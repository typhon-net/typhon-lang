// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;
    using FsPath = Typhon.Runtime.Path;

    internal static partial class importlib
    {
        internal static partial class machinery
        {
            public static readonly List<string> EXTENSION_SUFFIXES = ExtensionSuffixes();

            static List<string> ExtensionSuffixes()
            {
                string version = sys_.version_info.major.ToString(CultureInfo.InvariantCulture) +
                    sys_.version_info.minor.ToString(CultureInfo.InvariantCulture);
                Architecture architecture = RuntimeInformation.ProcessArchitecture;
                if (OperatingSystem.IsWindows())
                {
                    string windows = architecture switch
                    {
                        Architecture.X64 => "win_amd64",
                        Architecture.Arm64 => "win_arm64",
                        _ => "win32",
                    };
                    return [$".cp{version}-{windows}.pyd", ".pyd"];
                }
                string platform = OperatingSystem.IsMacOS() ? "-darwin" : "";
                if (OperatingSystem.IsLinux())
                {
                    string cpu = architecture switch
                    {
                        Architecture.X64 => "x86_64",
                        Architecture.X86 => "i386",
                        Architecture.Arm64 => "aarch64",
                        Architecture.Arm => "arm",
                        Architecture.Ppc64le => "powerpc64le",
                        _ => architecture.ToString().ToLowerInvariant(),
                    };
                    string abi = RuntimeInformation.RuntimeIdentifier.Contains("-musl-", StringComparison.Ordinal) ? "musl"
                        : architecture == Architecture.Arm ? "gnueabihf" : "gnu";
                    platform = $"-{cpu}-linux-{abi}";
                }
                return platform.Length == 0
                    ? [$".cpython-{version}.so", ".abi3.so", ".abi3t.so", ".so"]
                    : [$".cpython-{version}{platform}.so", $".abi3{platform}.so", ".abi3.so", $".abi3t{platform}.so", ".abi3t.so", ".so"];
            }
        }

        internal static class resources
        {
            // Resource readers associated with a loader take precedence over the fallback for source modules.
            public static dynamic files(object anchor)
            {
                object module = anchor is string name ? SourceImport.ImportModule(name) : anchor;
                object? spec = Builtins.getattr(module, "__spec__", null);
                object? loader = Builtins.getattr(spec, "loader", null);
                object? getReader = Builtins.getattr(loader, "get_resource_reader", null);
                if (getReader != null && (object?)Builtins.__invoke(getReader, [(object?)Builtins.getattr(spec, "name")]) is object reader)
                    return Invoke(reader, "files")!;
                if ((object?)Builtins.getattr(module, "__path__", null) is object paths &&
                    Builtins.__iter_dyn(paths).Select(readers.Resolve).OfType<object>().ToArray() is { Length: > 0 } directories)
                    return directories.Length == 1 ? directories[0] : new readers.MultiplexedPath(directories);
                if ((object?)Builtins.getattr(module, "__file__", null) is string file)
                    return new FsPath(file).parent;
                throw new InvalidCastException($"Cannot access resources for '{Builtins.getattr(module, "__name__", null)}' as it does not appear to correspond to an importable module (its __spec__ is None).");
            }

            static object? Invoke(object target, string name, params object?[] args) =>
                Builtins.__invoke((object?)Builtins.getattr(target, name), args);

            static bool IsDir(object target) => Builtins.truthy(Invoke(target, "is_dir"));
            static string Name(object target) => (string?)Builtins.getattr(target, "name") ?? throw new InvalidCastException("resource name must be a string");
            static IEnumerable<object> Entries(object target) => Builtins.__iter_dyn(Invoke(target, "iterdir")).Cast<object>();
            static object Join(object target, params object?[] names) => Invoke(target, "joinpath", names)!;

            internal static class readers
            {
                internal abstract class TraversableReader
                {
                    public abstract dynamic files();
                    public virtual string resource_path(string resource) => throw new FileNotFoundException(resource);
                    public virtual dynamic open_resource(string resource)
                    {
                        object target = Join((object)files(), resource);
                        return target is FsPath path ? Builtins.open_binary(path) : Invoke(target, "open", "rb")!;
                    }
                    public virtual bool is_resource(string path) => Builtins.truthy(Invoke(Join((object)files(), path), "is_file"));
                    public IEnumerable<string> contents() => Entries((object)files()).Select(Name);
                }

                internal sealed class FileReader : TraversableReader
                {
                    public FsPath path { get; }
                    public FileReader(object loader) => path = new FsPath(os_.__path((object?)Builtins.getattr(loader, "path"))).parent;
                    public override string resource_path(string resource) => path.joinpath(resource).ToString();
                    public override dynamic files() => path;
                }

                internal sealed class ZipReader : TraversableReader
                {
                    public string prefix { get; }
                    public string archive { get; }
                    public ZipReader(object loader, string module)
                    {
                        archive = os_.__path((object?)Builtins.getattr(loader, "archive"));
                        string loaderPrefix = (string?)Builtins.getattr(loader, "prefix") ?? throw new InvalidCastException("archive prefix must be a string");
                        prefix = loaderPrefix.Replace('\\', '/');
                        if (Builtins.truthy(Invoke(loader, "is_package", module))) prefix += SourceImport.Leaf(module) + "/";
                    }
                    internal ZipReader(string archive, string prefix) { this.archive = archive; this.prefix = prefix; }
                    public override dynamic files() => new zipfile.Path(archive, prefix);
                }

                internal sealed class MultiplexedPath : IPyRepr
                {
                    readonly object[] _paths;

                    public MultiplexedPath(params object[] paths)
                    {
                        _paths = paths.Distinct().Select(p => p is string path ? new FsPath(path) : p).ToArray();
                        if (_paths.Length == 0) throw new FileNotFoundException("MultiplexedPath must contain at least one path");
                        if (!_paths.All(IsDir)) throw new NotADirectoryError("MultiplexedPath only supports directories");
                    }

                    public string name => Name(_paths[0]);
                    public bool is_dir() => true;
                    public bool is_file() => false;
                    public IEnumerable<dynamic> iterdir()
                    {
                        foreach (var group in _paths.SelectMany(Entries).GroupBy(Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                        {
                            object[] children = group.ToArray();
                            yield return children.Length > 1 && children.All(IsDir) ? new MultiplexedPath(children) : children[0];
                        }
                    }

                    public dynamic joinpath(params object[] descendants)
                    {
                        if (descendants.Length == 0) return this;
                        string[] parts = descendants.SelectMany(p => os_.__path(p).Split('/', StringSplitOptions.RemoveEmptyEntries))
                            .Where(p => p != ".").ToArray();
                        if (parts.Length == 0) throw new StopIteration();
                        foreach (object child in iterdir())
                            if (Name(child) == parts[0]) return Join(child, parts.Skip(1).Cast<object?>().ToArray());
                        return Join(_paths[0], descendants);
                    }

                    public static dynamic operator /(MultiplexedPath path, string child) => path.joinpath(child);
                    public PyBytes read_bytes() => throw Missing();
                    public string read_text(string? encoding = null, string? errors = null) => throw Missing();
                    public object open(string mode = "r", string? encoding = null, string? errors = null) => throw Missing();
                    FileNotFoundException Missing() => new FileNotFoundException($"{this} is not a file");
                    public string __repr__() => $"MultiplexedPath({string.Join(", ", _paths.Select(p => $"'{p}'"))})";
                    public override string ToString() => __repr__();
                }

                internal sealed class NamespaceReader : TraversableReader
                {
                    public MultiplexedPath path { get; }
                    public NamespaceReader(object namespace_path)
                    {
                        if (!Builtins.str(namespace_path).Contains("NamespacePath", StringComparison.Ordinal))
                            throw new ArgumentException("Invalid path");
                        path = new MultiplexedPath(Builtins.__iter_dyn(namespace_path).Select(Resolve).OfType<object>().ToArray());
                    }
                    public override dynamic files() => path;
                    public override string resource_path(string resource) => path.joinpath(resource).ToString();
                }

                internal static object? Resolve(object? location)
                {
                    if (location is not string path) return null;
                    if (Directory.Exists(path)) return new FsPath(path);
                    for (int index = path.Length - 1; index >= 0; index--)
                    {
                        if (path[index] is not ('/' or '\\') || !File.Exists(path[..index])) continue;
                        var candidate = new zipfile.Path(path[..index], path[(index + 1)..].Replace('\\', '/').TrimStart('/') + "/");
                        try { if (candidate.exists()) return candidate; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
                    return null;
                }
            }
        }
    }
}
