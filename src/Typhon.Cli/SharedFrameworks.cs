// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Typhon.Cli;

/// <summary>
/// The .NET shared frameworks a program compiled by the typhon command can use besides the base one (Microsoft.NETCore.App,
/// which the CLI itself runs on). ASP.NET Core (Microsoft.AspNetCore.App) is referenced only when a module imports one of
/// its namespaces (Microsoft.AspNetCore.*, Microsoft.Extensions.*) or asks for it like a .NET file-based app does, with
/// "#:sdk Microsoft.NET.Sdk.Web" (a comment to Python), so other programs compile as fast as before. The framework is the one installed next to the running runtime,
/// with the same major and minor version; this file uses no Roslyn types (the cache and server paths load it too).
/// </summary>
internal static class SharedFrameworks
{
    public const string AspNetCore = "Microsoft.AspNetCore.App";
    static readonly string[] AspNetCoreNamespaces = { "Microsoft.AspNetCore", "Microsoft.Extensions", "Microsoft.Net.Http.Headers" };
    /// <summary>"#:sdk Microsoft.NET.Sdk.Web", the directive of .NET's file-based apps (a comment to Python).</summary>
    static readonly Regex WebSdkDirective = new(@"^#:sdk\s+Microsoft\.NET\.Sdk\.Web(@\S+)?\s*$", RegexOptions.Multiline);

    /// <summary>Does importing this module name need ASP.NET Core?</summary>
    public static bool IsAspNetCoreNamespace(string module) =>
        AspNetCoreNamespaces.Any(ns => module == ns || module.StartsWith(ns + ".", StringComparison.Ordinal));

    public static bool HasWebSdkDirective(string source) => WebSdkDirective.IsMatch(source);

    /// <summary>The dotnet command of the installation the CLI runs on (for dotnet restore), else the one on PATH.</summary>
    public static string DotnetExecutable
    {
        get
        {
            string exe = Path.Combine(DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            return File.Exists(exe) ? exe : "dotnet";
        }
    }

    /// <summary>The dotnet installation the CLI runs on (the folder that holds shared/).</summary>
    static string DotnetRoot => Path.GetFullPath(Path.Combine(RuntimeDirectory, "..", "..", ".."));

    /// <summary>shared/Microsoft.NETCore.App/&lt;version&gt; of the running runtime.</summary>
    static string RuntimeDirectory => Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    static readonly Dictionary<string, string?> _dirs = new();

    /// <summary>
    /// The installed framework's folder for the running runtime: the same version folder name if present (11.0.0 next to
    /// 11.0.0), else the highest patch with the same major and minor version; null when it is not installed.
    /// </summary>
    public static string? Directory(string name)
    {
        lock (_dirs)
        {
            if (_dirs.TryGetValue(name, out var known)) return known;
            string? found = null;
            string parent = Path.Combine(DotnetRoot, "shared", name);
            if (System.IO.Directory.Exists(parent))
            {
                string runtimeVersion = Path.GetFileName(RuntimeDirectory);
                var v = Environment.Version;
                var candidates = System.IO.Directory.GetDirectories(parent)
                    .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
                    .Where(c => c.Version != null && c.Version.Major == v.Major && c.Version.Minor == v.Minor)
                    .OrderByDescending(c => c.Version)
                    .ToList();
                found = candidates.FirstOrDefault(c => Path.GetFileName(c.Dir) == runtimeVersion).Dir ?? candidates.FirstOrDefault().Dir;
            }
            return _dirs[name] = found;
        }
    }

    static Version? ParseVersion(string folder)
    {
        int dash = folder.IndexOf('-');
        return Version.TryParse(dash >= 0 ? folder[..dash] : folder, out var v) ? v : null;
    }

    /// <summary>The framework's assemblies (reference and implementation are the same files in a shared framework).</summary>
    public static IEnumerable<string> Assemblies(string name) =>
        Directory(name) is string dir ? System.IO.Directory.GetFiles(dir, "*.dll") : Enumerable.Empty<string>();

    /// <summary>The message for a program that needs a framework that is not installed.</summary>
    public static string NotInstalled(string name, string reason) =>
        $"typhon: this program uses {(name == AspNetCore ? "ASP.NET Core" : name)} ({reason}), but the {name} " +
        $"{Environment.Version.Major}.{Environment.Version.Minor} shared framework is not installed in {Path.Combine(DotnetRoot, "shared")}";

    static readonly Dictionary<string, string> _packageAssemblies = new(StringComparer.OrdinalIgnoreCase);
    static bool _packagesHooked;

    /// <summary>Lets a program run in this process load the assemblies of its #:package packages.</summary>
    public static void EnablePackages(IEnumerable<string> runtimeAssemblies)
    {
        lock (_packageAssemblies)
        {
            foreach (var path in runtimeAssemblies) _packageAssemblies[Path.GetFileNameWithoutExtension(path)] = path;
            if (_packagesHooked || _packageAssemblies.Count == 0) return;
            _packagesHooked = true;
        }
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string? path;
            lock (_packageAssemblies) _packageAssemblies.TryGetValue(assemblyName.Name ?? "", out path);
            return path != null && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    static readonly HashSet<string> _enabled = new();

    /// <summary>Lets a program run in this process load the frameworks' assemblies (the CLI's own list has only the base one).</summary>
    public static void EnableAtRunTime(IEnumerable<string> frameworks)
    {
        foreach (var name in frameworks)
        {
            lock (_enabled) { if (!_enabled.Add(name)) continue; }
            string dir = Directory(name) ?? throw new InvalidOperationException(NotInstalled(name, "a cached build references it"));
            AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                string path = Path.Combine(dir, assemblyName.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            };
        }
    }
}
