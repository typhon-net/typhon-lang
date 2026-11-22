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
        public static RuntimeCode? read_code(object stream)
        {
            object? read = Builtins.getattr(stream, "read");
            if ((object?)Builtins.__invoke(read, new object?[] { 4L }) is not PyBytes header || !header.Data.AsSpan().SequenceEqual(importlib.util.MAGIC_NUMBER.Data))
                return null;
            throw new NotSupportedException("CPython bytecode files cannot be executed by Typhon");
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

                public importlib.machinery.ModuleSpec? find_spec(string fullname)
                {
                    string leaf = SourceImport.Leaf(fullname);
                    string directory = Path.Combine(path, leaf);
                    string initializer = Path.Combine(directory, "__init__.py");
                    if (File.Exists(initializer))
                        return SourceImport.Spec(fullname, initializer, new SourceLoader(fullname, initializer), directory);
                    string script = Path.Combine(path, leaf + ".py");
                    if (File.Exists(script)) return SourceImport.Spec(fullname, script, new SourceLoader(fullname, script));
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

        public importlib.machinery.ModuleSpec? find_spec(string fullname)
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

    internal sealed class SourceLoader(string name, string origin, string? archive = null, string? member = null)
    {
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
            try { return (RuntimeCode)Builtins.compile(get_data(filename), filename, "exec", module: fullname); }
            catch (IOException error) { throw new ImportError(error.Message, fullname); }
        }

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

        internal static importlib.machinery.ModuleSpec Spec(string fullname, string origin, SourceLoader loader, string? package = null)
        {
            var spec = new importlib.machinery.ModuleSpec(fullname, loader, origin, is_package: package != null) { has_location = true };
            if (package != null) spec.submodule_search_locations!.Add(package);
            return spec;
        }

        internal static importlib.machinery.ModuleSpec? Find(string fullname, IEnumerable<string> entries)
        {
            importlib.machinery.ModuleSpec? portions = null;
            foreach (string entry in entries)
            {
                object? finder = pkgutil.get_importer(entry);
                object? find = Builtins.getattr(finder, "find_spec", null);
                if (find == null || (object?)Builtins.__invoke(find, new object?[] { fullname }) is not importlib.machinery.ModuleSpec spec)
                    continue;
                if (spec.loader != null || spec.submodule_search_locations == null) return spec;
                portions ??= new importlib.machinery.ModuleSpec(fullname, null, is_package: true);
                portions.submodule_search_locations!.AddRange(spec.submodule_search_locations!);
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
