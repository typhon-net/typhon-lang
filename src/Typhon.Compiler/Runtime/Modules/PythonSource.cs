// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Typhon.Runtime;

    internal static partial class tokenize
    {
        static readonly Regex Cookie = new Regex(@"\A[ \t\f]*#.*?coding[=:][ \t]*([-\w.]+)", RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
        static readonly Regex CommentOrBlank = new Regex(@"\A[ \t\f]*(?:#|[\r\n]|\z)", RegexOptions.CultureInvariant);
        static readonly byte[] Utf8Bom = { 0xef, 0xbb, 0xbf };

        public static PyFile open(object filename)
        {
            string path = os_.__path(filename);
            var stream = StreamOps.OpenFile(path, "r");
            try
            {
                string encoding = SourceEncoding(stream);
                stream.Position = 0;
                return new PyFile(stream, path, "r", encoding, null, null, true);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal static string SourceEncoding(Stream stream)
        {
            byte[] first = ReadLine(stream);
            bool bom = first.AsSpan().StartsWith(Utf8Bom);
            if (bom) first = first[Utf8Bom.Length..];
            string header = Encoding.Latin1.GetString(first);
            string? cookie = DeclaredEncoding(header);
            byte[] second = Array.Empty<byte>();
            if (cookie == null && first.Length != 0 && CommentOrBlank.IsMatch(header))
            {
                second = ReadLine(stream);
                cookie = DeclaredEncoding(Encoding.Latin1.GetString(second));
            }
            string codec = cookie ?? "utf-8";
            if (bom && codec != "utf-8") throw new SyntaxError("encoding problem: utf-8");
            if (first.Contains((byte)0) || second.Contains((byte)0))
                throw new SyntaxError("source code cannot contain null bytes");
            Encoding encoding;
            try { encoding = StrictEncoding(codec); }
            catch (ArgumentException) { throw new SyntaxError($"unknown encoding: {codec}"); }
            try
            {
                encoding.GetCharCount(first);
                encoding.GetCharCount(second);
            }
            catch (DecoderFallbackException) { throw new SyntaxError("invalid or missing encoding declaration"); }
            return bom ? "utf-8-sig" : codec;
        }

        internal static Encoding StrictEncoding(string name)
        {
            var encoding = (Encoding)ByteOps.EncodingOf(name, "strict").Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            return encoding;
        }

        static string? DeclaredEncoding(string line)
        {
            var match = Cookie.Match(line);
            if (!match.Success) return null;
            string name = match.Groups[1].Value;
            string normalized = name.ToLowerInvariant().Replace('_', '-');
            if (normalized == "utf-8" || normalized.StartsWith("utf-8-", StringComparison.Ordinal)) return "utf-8";
            foreach (string alias in new[] { "latin-1", "iso-8859-1", "iso-latin-1" })
                if (normalized == alias || normalized.StartsWith(alias + "-", StringComparison.Ordinal)) return "iso-8859-1";
            return name;
        }

        static byte[] ReadLine(Stream stream)
        {
            using var line = new MemoryStream();
            for (int b; (b = stream.ReadByte()) != -1;)
            {
                line.WriteByte((byte)b);
                if (b == '\n') break;
            }
            return line.ToArray();
        }
    }

    internal static partial class importlib
    {
        public static dynamic import_module(string name, string? package = null)
        {
            if (name.StartsWith('.'))
            {
                if (string.IsNullOrEmpty(package)) throw new InvalidCastException("the 'package' argument is required to perform a relative import");
                int level = 0;
                while (level < name.Length && name[level] == '.') level++;
                name = SourceImport.ResolveRelative(name[level..], package, level);
            }
            return SourceImport.ImportModule(name);
        }

        internal static partial class machinery
        {
            public static readonly List<string> SOURCE_SUFFIXES = OperatingSystem.IsWindows() ? new() { ".py", ".pyw" } : new() { ".py" };
            public static readonly List<string> BYTECODE_SUFFIXES = new() { ".pyc" };

            internal static class BuiltinImporter
            {
                internal static bool IsBuiltin(string name) => sys_.builtin_module_names.Contains(name);

                public static ModuleSpec? find_spec(string fullname, object? path = null, object? target = null) =>
                    IsBuiltin(fullname) ? new ModuleSpec(fullname, typeof(BuiltinImporter), origin: "built-in") : null;

                public static PyModule create_module(ModuleSpec spec)
                {
                    RequireBuiltin(spec.name);
                    var stdlib = typeof(sys_).Assembly;
                    string name = "Typhon.Stdlib." + spec.name;
                    Type type = stdlib.GetType(name) ?? stdlib.GetType(name + "_") ??
                        throw new ImportError($"No built-in module named '{spec.name}'", name: spec.name);
                    return PyModule.Of(type, spec.name);
                }

                public static object? exec_module(object? module)
                {
                    if (module is PyModule { SourceType: Type type })
                        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    return null;
                }

                public static object? get_code(string fullname) { RequireBuiltin(fullname); return null; }
                public static string? get_source(string fullname) { RequireBuiltin(fullname); return null; }
                public static bool is_package(string fullname) { RequireBuiltin(fullname); return false; }

                static void RequireBuiltin(string fullname)
                {
                    if (!IsBuiltin(fullname))
                        throw new ImportError($"'{fullname}' is not a built-in module", name: fullname);
                }
            }

            internal sealed class ModuleSpec : IPyRepr, IEquatable<ModuleSpec>
            {
                public string name;
                public dynamic? loader;
                public object? loader_state;
                public string? origin;
                public IList<string>? submodule_search_locations;
                string? _cached;

                public ModuleSpec(string name, object? loader, string? origin = null, object? loader_state = null, bool? is_package = null)
                {
                    this.name = name;
                    this.loader = loader;
                    this.origin = origin;
                    this.loader_state = loader_state;
                    submodule_search_locations = is_package == true ? new List<string>() : null;
                }

                public bool has_location { get; set; }
                public string parent
                {
                    get
                    {
                        if (submodule_search_locations != null) return name;
                        int dot = name.LastIndexOf('.');
                        return dot >= 0 ? name[..dot] : "";
                    }
                }
                public string? cached
                {
                    get
                    {
                        if (_cached == null && has_location && origin?.EndsWith(".pyc", StringComparison.Ordinal) == true)
                            _cached = origin;
                        return _cached;
                    }
                    set => _cached = value;
                }

                public bool Equals(ModuleSpec? other) => other != null && name == other.name &&
                    Builtins.eq(loader, other.loader) && origin == other.origin &&
                    Builtins.eq(submodule_search_locations, other.submodule_search_locations) &&
                    cached == other.cached && has_location == other.has_location;
                public override bool Equals(object? other) => other is ModuleSpec spec && Equals(spec);
                public override int GetHashCode() => throw new InvalidCastException("unhashable type: 'ModuleSpec'");
                public string __repr__() => $"ModuleSpec(name={Builtins.repr(name)}, loader={Builtins.repr(loader)}" +
                    (origin == null ? "" : $", origin={Builtins.repr(origin)}") +
                    (submodule_search_locations == null ? "" : $", submodule_search_locations={Builtins.repr(submodule_search_locations)}") + ")";
                public override string ToString() => __repr__();
            }
        }

        internal static class _bootstrap
        {
            public static object _load(machinery.ModuleSpec spec) => SourceImport.Load(spec);
        }

        internal static class util
        {
            public static readonly PyBytes MAGIC_NUMBER = new PyBytes(new byte[] { 0x52, 0x0e, 0x0d, 0x0a });

            public static machinery.ModuleSpec? find_spec(string name, string? package = null)
            {
                if (name.StartsWith('.'))
                {
                    if (string.IsNullOrEmpty(package)) throw new ImportError($"no package specified for {Builtins.repr(name)} (required for relative module names)");
                    int level = 0;
                    while (level < name.Length && name[level] == '.') level++;
                    name = SourceImport.ResolveRelative(name[level..], package, level);
                }
                if (!IsModuleName(name)) return null;
                if (TryLoadedSpec(name, out var spec)) return (machinery.ModuleSpec?)spec;
                int parentDelimiter = name.LastIndexOf('.');
                if (parentDelimiter < 0) return FindOnPath(name, null);
                string parent = name[..parentDelimiter];
                object owner = SourceImport.ImportModule(parent);
                object? search = Builtins.getattr(owner, "__path__", null);
                if (search == null) throw new ModuleNotFoundError($"__path__ attribute not found on '{parent}' while trying to find '{name}'", name);
                return FindOnPath(name, search);
            }

            public static dynamic? _find_spec_from_path(string name, object? path = null)
            {
                if (TryLoadedSpec(name, out var spec)) return spec;
                return IsModuleName(name) ? FindOnPath(name, path) : null;
            }

            static bool IsModuleName(string name) => name.Length > 0 &&
                name.IndexOfAny(new[] { '/', '\\', '\0' }) < 0 && name.Split('.').All(part => part.Length > 0);

            static bool TryLoadedSpec(string name, out object? spec)
            {
                spec = null;
                if (sys_.modules.TryGetValue(name, out var module))
                {
                    if (module == null) return true;
                    spec = Builtins.getattr(module, "__spec__", null);
                    if (spec == null) throw new ArgumentException(Builtins.hasattr(module, "__spec__") ? $"{name}.__spec__ is None" : $"{name}.__spec__ is not set");
                    return true;
                }
                return false;
            }

            static machinery.ModuleSpec? FindOnPath(string name, object? path)
            {
                foreach (object? finder in sys_.meta_path.ToArray())
                {
                    object? find = Builtins.getattr(finder, "find_spec", null);
                    if (find != null && (object?)Builtins.__invoke(find, new object?[] { name, path, null }) is machinery.ModuleSpec spec)
                        return spec;
                }
                return machinery.PathFinder.find_spec(name, path);
            }

            public static machinery.ModuleSpec? spec_from_file_location(
                string name,
                [System.Runtime.InteropServices.Optional] object? location,
                [PyKeywordOnly, System.Runtime.InteropServices.Optional] object? loader,
                [PyKeywordOnly, System.Runtime.InteropServices.Optional] object? submodule_search_locations)
            {
                if (ReferenceEquals(loader, Type.Missing)) loader = null;
                string origin;
                if (location == null || ReferenceEquals(location, Type.Missing))
                {
                    origin = "<unknown>";
                    object? filename = loader == null ? Type.Missing : Builtins.getattr(loader, "get_filename", Type.Missing);
                    if (!ReferenceEquals(filename, Type.Missing))
                    {
                        try { origin = os_.__path((object?)Builtins.__invoke(filename, [name])); }
                        catch (ImportError) { }
                    }
                }
                else origin = global::System.IO.Path.GetFullPath(os_.__path(location));

                if (loader == null)
                {
                    if (!machinery.SOURCE_SUFFIXES.Any(suffix => origin.EndsWith(suffix, StringComparison.Ordinal))) return null;
                    loader = new machinery.SourceFileLoader(name, origin);
                }

                List<string>? locations = null;
                if (ReferenceEquals(submodule_search_locations, Type.Missing))
                {
                    object? isPackage = Builtins.getattr(loader, "is_package", Type.Missing);
                    if (!ReferenceEquals(isPackage, Type.Missing))
                    {
                        try { if (Builtins.truthy((object?)Builtins.__invoke(isPackage, [name]))) locations = []; }
                        catch (ImportError) { }
                    }
                }
                else if (submodule_search_locations != null)
                    locations = submodule_search_locations as List<string> ??
                        Builtins.__iter_dyn(submodule_search_locations).Select(path => os_.__path((object?)path)).ToList();

                if (locations?.Count == 0) locations.Add(global::System.IO.Path.GetDirectoryName(origin) ?? "");
                return new machinery.ModuleSpec(name, loader, origin)
                {
                    has_location = true,
                    submodule_search_locations = locations,
                };
            }

            public static dynamic module_from_spec(machinery.ModuleSpec spec)
            {
                object? module = null;
                if (spec.loader != null)
                {
                    var create = (object?)Builtins.getattr(spec.loader, "create_module", null);
                    if (create != null) module = Builtins.__invoke(create, [spec]);
                    else if (Builtins.hasattr(spec.loader, "exec_module"))
                        throw new ImportError("loaders that define exec_module() must also define create_module()");
                }
                module ??= new PyModule(spec.name, null);
                SetMissing("__name__", spec.name);
                SetMissing("__loader__", spec.loader);
                SetMissing("__package__", spec.parent);
                Builtins.setattr(module, "__spec__", spec);
                if (spec.submodule_search_locations != null) SetMissing("__path__", spec.submodule_search_locations);
                if (spec.has_location) SetMissing("__file__", spec.origin);
                return module;

                void SetMissing(string name, object? value)
                {
                    if ((object?)Builtins.getattr(module, name, null) == null) Builtins.setattr(module, name, value);
                }
            }

            public static string decode_source(PyBytes source_bytes)
            {
                byte[] bytes = source_bytes.Data;
                using var stream = new MemoryStream(bytes, writable: false);
                string codec = tokenize.SourceEncoding(stream);
                int start = codec == "utf-8-sig" ? 3 : 0;
                try
                {
                    string text = tokenize.StrictEncoding(start == 0 ? codec : "utf-8").GetString(bytes, start, bytes.Length - start);
                    return text.Replace("\r\n", "\n").Replace('\r', '\n');
                }
                catch (DecoderFallbackException e)
                {
                    int index = e.Index + start;
                    throw new UnicodeDecodeError(codec, source_bytes, index, index + e.BytesUnknown.Length, "invalid byte sequence");
                }
            }
        }
    }
}
