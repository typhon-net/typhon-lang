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
        static readonly byte[] Python315Magic = { 0x52, 0x0e, 0x0d, 0x0a };

        public static RuntimeCode? read_code(object stream)
        {
            object? read = Builtins.getattr(stream, "read");
            if ((object?)Builtins.__invoke(read, new object?[] { 4L }) is not PyBytes header || !header.Data.AsSpan().SequenceEqual(Python315Magic))
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
            new PyClosure("path_hook_for_FileFinder", (args, _) => new DirectorySourceFinder((string)args[0]!)),
        };
    }

    internal sealed class DirectorySourceFinder
    {
        readonly string _path;

        internal DirectorySourceFinder(string path)
        {
            _path = Path.GetFullPath(path.Length == 0 ? "." : path);
            if (!Directory.Exists(_path)) throw new ImportError("only directories are supported");
        }

        public importlib.machinery.ModuleSpec? find_spec(string fullname)
        {
            string leaf = SourceImport.Leaf(fullname);
            string directory = Path.Combine(_path, leaf);
            string initializer = Path.Combine(directory, "__init__.py");
            if (File.Exists(initializer))
                return SourceImport.Spec(fullname, initializer, new SourceLoader(initializer), directory);
            string script = Path.Combine(_path, leaf + ".py");
            if (File.Exists(script)) return SourceImport.Spec(fullname, script, new SourceLoader(script));
            if (!Directory.Exists(directory)) return null;
            var spec = new importlib.machinery.ModuleSpec(fullname, null, is_package: true);
            spec.submodule_search_locations!.Add(directory);
            return spec;
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
                return SourceImport.Spec(fullname, origin, new SourceLoader(origin, _archive, initializer), ArchivePath(entry));
            }
            string script = entry + ".py";
            if (zip.GetEntry(script) != null)
            {
                string origin = ArchivePath(script);
                return SourceImport.Spec(fullname, origin, new SourceLoader(origin, _archive, script));
            }
            return null;
        }

        string ArchivePath(string entry) => Path.Combine(_archive, entry.Replace('/', Path.DirectorySeparatorChar));
    }

    internal sealed class SourceLoader(string origin, string? archive = null, string? member = null)
    {
        public RuntimeCode get_code(string fullname)
        {
            try
            {
                if (archive == null) return Builtins.compile(new PyBytes(File.ReadAllBytes(origin)), origin, "exec", module: fullname);
                using var file = File.OpenRead(archive);
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                using var input = (zip.GetEntry(member!) ?? throw new ImportError($"No code object available for {fullname}", fullname)).Open();
                using var data = new MemoryStream();
                input.CopyTo(data);
                return Builtins.compile(new PyBytes(data.ToArray()), origin, "exec", module: fullname);
            }
            catch (IOException error) { throw new ImportError(error.Message, fullname); }
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
            if (sys_.modules.TryGetValue(name, out var existing) && existing != null) return existing;
            var spec = importlib.util.find_spec(name) ?? throw new ModuleNotFoundError($"No module named '{name}'", name);
            object module = importlib.util.module_from_spec(spec);
            sys_.modules[name] = module;
            try
            {
                if (spec.loader is SourceLoader source) source.exec_module(module);
                else if (spec.loader != null) Builtins.__invoke(Builtins.getattr(spec.loader, "exec_module"), new[] { module });
                int separator = name.LastIndexOf('.');
                if (separator >= 0 && sys_.modules.TryGetValue(name[..separator], out var parent) && parent != null)
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
