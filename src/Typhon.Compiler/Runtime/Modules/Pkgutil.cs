// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using Typhon.Runtime;
    using Path = System.IO.Path;

    internal static class pkgutil
    {
        public static dynamic? read_code(object stream)
        {
            object? read = Builtins.getattr(stream, "read");
            if ((object?)Builtins.__invoke(read, new object?[] { 4L }) is not PyBytes header ||
                !header.Data.AsSpan().SequenceEqual(importlib.util.MAGIC_NUMBER.Data))
                return null;
            _ = Builtins.__invoke(read, [12L]);
            return marshal.load(stream);
        }

        public static object? get_importer(object path_item)
        {
            string path = os_.fsdecode(path_item);
            if (sys_.path_importer_cache.TryGetValue(path, out var cached)) return cached;
            foreach (var hook in sys_.path_hooks)
            {
                object? importer;
                try { importer = Builtins.__invoke(hook, new object?[] { path }); }
                catch (ImportError) { continue; }
                return sys_.path_importer_cache[path] = importer;
            }
            return null;
        }

        internal static List<object?> DefaultPathHooks() => new()
        {
            new PyClosure("zipimporter", (args, _) => ZipSourceFinder.Open((string)args[0]!)),
            new PyClosure("path_hook_for_FileFinder", (args, _) => importlib.machinery.FileFinder.FromSourcePath((string)args[0]!)),
        };
    }

    internal static partial class importlib
    {
        internal static partial class machinery
        {
            internal sealed class SourceFileLoader(string fullname, string path) : SourceLoader(fullname, path)
            {
                public OrderedDictionary<string, object?> path_stats(string path)
                {
                    var stats = os_.stat(path);
                    return new() { ["mtime"] = stats.st_mtime, ["size"] = stats.st_size };
                }
            }

            internal static class PathFinder
            {
                public static ModuleSpec? find_spec(string fullname, object? path = null, object? target = null)
                {
                    var entries = path == null ? sys_.path : Builtins.__iter_dyn(path).OfType<string>();
                    var spec = FindOnPaths(fullname, entries, target);
                    if (spec is { loader: null, submodule_search_locations: { Count: > 0 } portions })
                        spec.submodule_search_locations = new NamespacePath(fullname, portions, (name, paths) => FindOnPaths(name, paths, null));
                    return spec;
                }

                static ModuleSpec? FindOnPaths(string fullname, IEnumerable<string> entries, object? target) =>
                    SourceImport.Find(fullname, entries.Select(entry => entry.Length == 0 ? Directory.GetCurrentDirectory() : entry), target);

                public static void invalidate_caches()
                {
                    foreach (var entry in sys_.path_importer_cache.ToArray())
                    {
                        if (entry.Value == null || !Path.IsPathFullyQualified(entry.Key))
                            sys_.path_importer_cache.Remove(entry.Key);
                        else if (Builtins.getattr(entry.Value, "invalidate_caches", null) is object invalidate)
                            Builtins.__invoke(invalidate, []);
                    }
                    NamespacePath.Invalidate();
                }
            }

            internal sealed class FileFinder
            {
                public string path { get; }

                public FileFinder(string path)
                {
                    this.path = Path.GetFullPath(path.Length == 0 ? "." : path);
                }

                internal static FileFinder FromSourcePath(string path)
                {
                    var finder = new FileFinder(path);
                    if (!Directory.Exists(finder.path)) throw new ImportError("only directories are supported");
                    return finder;
                }

                public importlib.machinery.ModuleSpec? find_spec(string fullname, object? target = null)
                {
                    string leaf = SourceImport.Leaf(fullname);
                    string directory = Path.Combine(path, leaf);
                    string initializer = Path.Combine(directory, "__init__.py");
                    if (File.Exists(initializer))
                        return SourceImport.Spec(fullname, initializer, new SourceFileLoader(fullname, initializer), directory);
                    string script = Path.Combine(path, leaf + ".py");
                    if (File.Exists(script)) return SourceImport.Spec(fullname, script, new SourceFileLoader(fullname, script));
                    string bytecode = Path.Combine(path, leaf + ".pyc");
                    if (File.Exists(bytecode)) return SourceImport.Spec(fullname, bytecode, new SourcelessFileLoader(fullname, bytecode));
                    if (!Directory.Exists(directory)) return null;
                    var spec = new importlib.machinery.ModuleSpec(fullname, null, is_package: true);
                    spec.submodule_search_locations!.Add(directory);
                    return spec;
                }
            }
        }
    }

    internal sealed class ZipSourceFinder
    {
        readonly string _archive;
        readonly string _prefix;

        ZipSourceFinder(string archive, string prefix) { _archive = archive; _prefix = prefix; }

        internal static ZipSourceFinder Open(string path)
        {
            if (path.Length == 0) throw new ImportError("not a Zip file");
            string candidate = Path.GetFullPath(path);
            var directories = new Stack<string>();
            while (!File.Exists(candidate))
            {
                string? parent = Path.GetDirectoryName(candidate);
                if (parent == null || Directory.Exists(candidate)) throw new ImportError("not a Zip file");
                directories.Push(Path.GetFileName(candidate));
                candidate = parent;
            }
            try
            {
                using var stream = File.OpenRead(candidate);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                _ = archive.Entries.Count;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { throw new ImportError("not a Zip file"); }
            return new ZipSourceFinder(candidate, directories.Count == 0 ? "" : string.Join("/", directories) + "/");
        }

        public importlib.machinery.ModuleSpec? find_spec(string fullname, object? target = null)
        {
            string entry = _prefix + SourceImport.Leaf(fullname);
            using var stream = File.OpenRead(_archive);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            string initializer = entry + "/__init__.py";
            if (zip.GetEntry(initializer) != null)
            {
                string origin = ArchivePath(initializer);
                return SourceImport.Spec(fullname, origin, new SourceLoader(fullname, origin, _archive, initializer), ArchivePath(entry));
            }
            string script = entry + ".py";
            if (zip.GetEntry(script) != null)
            {
                string origin = ArchivePath(script);
                return SourceImport.Spec(fullname, origin, new SourceLoader(fullname, origin, _archive, script));
            }
            return null;
        }

        public IEnumerable<(string, bool)> iter_modules(string prefix = "")
        {
            using var stream = File.OpenRead(_archive);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var yielded = new HashSet<string>(StringComparer.Ordinal);
            foreach (string member in zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal))
            {
                if (!member.StartsWith(_prefix, StringComparison.Ordinal)) continue;
                string relative = member[_prefix.Length..];
                int slash = relative.IndexOf('/');
                if (slash >= 0)
                {
                    // A member directly inside a package identifies the package at this prefix.
                    string initializer = relative[(slash + 1)..];
                    if (initializer.StartsWith("__init__.py", StringComparison.Ordinal) && !initializer.Contains('/'))
                    {
                        string package = relative[..slash];
                        if (yielded.Add(package)) yield return (prefix + package, true);
                    }
                    continue;
                }
                if (inspect.getmodulename(relative) is string name && name != "__init__" &&
                    !name.Contains('.') && yielded.Add(name))
                    yield return (prefix + name, false);
            }
        }

        string ArchivePath(string entry) => Path.Combine(_archive, entry.Replace('/', Path.DirectorySeparatorChar));
    }

    internal class SourceLoader(string moduleName, string origin, string? archive = null, string? member = null)
    {
        public string name => moduleName;

        public bool is_package(string fullname) =>
            Path.GetFileNameWithoutExtension(get_filename(fullname)) == "__init__" && SourceImport.Leaf(fullname) != "__init__";

        public string get_filename(string? fullname = null)
        {
            if (fullname != null && fullname != name)
                throw new ImportError($"loader for {name} cannot handle {fullname}", fullname);
            return origin;
        }

        public string get_source(string fullname)
        {
            string filename = get_filename(fullname);
            PyBytes contents;
            try { contents = get_data(filename); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new ImportError("source not available through get_data()", fullname); }
            return archive == null ? importlib.util.decode_source(contents) : ByteOps.Decode(contents.Data, "utf-8", "strict");
        }

        public RuntimeCode get_code(string fullname)
        {
            string filename = get_filename(fullname);
            try { return source_to_code(get_data(filename), filename); }
            catch (IOException error) { throw new ImportError(error.Message, fullname); }
        }

        public RuntimeCode source_to_code(object data, string path, long _optimize = -1) =>
            (RuntimeCode)Builtins.compile(data, path, "exec", dont_inherit: true, optimize: _optimize, module: name);

        public PyBytes get_data(string path)
        {
            if (archive == null) return new PyBytes(File.ReadAllBytes(path));
            string entry = path == origin ? member! : Path.GetRelativePath(archive, path).Replace(Path.DirectorySeparatorChar, '/');
            using var file = File.OpenRead(archive);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            using var input = (zip.GetEntry(entry) ?? throw new System.IO.FileNotFoundException($"No entry {entry} in archive", path)).Open();
            using var data = new MemoryStream();
            input.CopyTo(data);
            return new PyBytes(data.ToArray());
        }

        public string path => origin;

        public object get_resource_reader(string fullname)
        {
            _ = get_filename(fullname);
            if (archive == null) return new importlib.resources.readers.FileReader(this);
            string entry = member!;
            return new importlib.resources.readers.ZipReader(archive, entry[..(entry.LastIndexOf('/') + 1)]);
        }

        public object? create_module(importlib.machinery.ModuleSpec spec) => null;

        public void exec_module(object module)
        {
            string name = (string?)Builtins.getattr(module, "__name__") ?? throw new ImportError("module has no name");
            Builtins.exec(get_code(name), Builtins.getattr(module, "__dict__"));
        }
    }

    internal static class SourceImport
    {
        internal static string Leaf(string fullname) => fullname[(fullname.LastIndexOf('.') + 1)..];

        internal static importlib.machinery.ModuleSpec Spec(string fullname, string origin, object loader, string? package = null)
        {
            var spec = new importlib.machinery.ModuleSpec(fullname, loader, origin, is_package: package != null) { has_location = true };
            if (package != null) spec.submodule_search_locations!.Add(package);
            return spec;
        }

        internal static importlib.machinery.ModuleSpec? Find(string fullname, IEnumerable<string> entries, object? target = null)
        {
            importlib.machinery.ModuleSpec? portions = null;
            foreach (string entry in entries)
            {
                object? finder = pkgutil.get_importer(entry);
                object? find = Builtins.getattr(finder, "find_spec", null);
                object?[] arguments = target == null ? [fullname] : [fullname, target];
                if (find == null || (object?)Builtins.__invoke(find, arguments) is not importlib.machinery.ModuleSpec spec)
                    continue;
                if (spec.loader != null || spec.submodule_search_locations == null) return spec;
                portions ??= new importlib.machinery.ModuleSpec(fullname, null, is_package: true);
                foreach (string portion in spec.submodule_search_locations)
                    portions.submodule_search_locations!.Add(portion);
            }
            return portions;
        }

        internal static string ResolveRelative(string module, string package, int level)
        {
            var parts = package.Split('.');
            if (level > parts.Length) throw new ImportError("attempted relative import beyond top-level package");
            string parent = string.Join(".", parts.Take(parts.Length - level + 1));
            return module.Length == 0 ? parent : parent + "." + module;
        }

        internal static object ImportModule(string name)
        {
            if (sys_.modules.TryGetValue(name, out object? existing) && existing != null) return existing;
            var spec = importlib.util.find_spec(name) ?? throw new ModuleNotFoundError($"No module named '{name}'", name);
            return Load(spec);
        }

        internal static object Load(importlib.machinery.ModuleSpec spec)
        {
            string name = spec.name;
            object module = importlib.util.module_from_spec(spec);
            sys_.modules[name] = module;
            try
            {
                if (spec.loader is SourceLoader source) source.exec_module(module);
                else if (spec.loader != null) Builtins.__invoke(Builtins.getattr(spec.loader, "exec_module"), new[] { module });
                int separator = name.LastIndexOf('.');
                if (separator >= 0 && sys_.modules.TryGetValue(name[..separator], out object? parent) && parent != null)
                    Builtins.setattr(parent, name[(separator + 1)..], module);
                return module;
            }
            catch
            {
                sys_.modules.Remove(name);
                throw;
            }
        }
    }
}
