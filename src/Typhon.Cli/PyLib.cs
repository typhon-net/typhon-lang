// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Typhon.Cli;

/// <summary>
/// "#:pylib" (opt-in): a module the program imports that neither the program's folder nor Typhon's runtime has is compiled
/// from CPython's standard library source (Lib/name.py), with the program, so its functions are typed by the program's
/// calls. "#:pylib DIR" names the Lib folder; without it, TYPHON_PYLIB or the stdlib of the python3 on the PATH.
/// </summary>
internal static class PyLib
{
    static readonly Regex Directive = new(@"^#:pylib(?:[ \t]+(?<dir>[^\r\n]*?))?[ \t]*$", RegexOptions.Multiline);

    /// <summary>The typing-only modules (Typhon's compiler handles their names itself): never compiled from Lib.</summary>
    static readonly HashSet<string> TypingOnly = new() { "typing", "abc", "enum", "__future__", "collections.abc", "typing_extensions" };

    /// <summary>Does the source ask for #:pylib; the folder it gives (or null), relative to the source's folder.</summary>
    public static bool Requested(string source, string sourceDir, out string? dir)
    {
        dir = null;
        var m = Directive.Match(source);
        if (!m.Success) return false;
        string given = m.Groups["dir"].Value.Trim();
        if (given.Length > 0) dir = Path.GetFullPath(Path.Combine(sourceDir, given));
        return true;
    }

    static string? _discovered;
    static bool _searched;

    /// <summary>The Lib folder: the one given, TYPHON_PYLIB, or that of the python3 (python on Windows) on the PATH; null if none.</summary>
    public static string? Resolve(string? given)
    {
        if (given != null) return Directory.Exists(given) ? given : null;
        if (Environment.GetEnvironmentVariable("TYPHON_PYLIB") is string env && env.Length > 0) return Directory.Exists(env) ? env : null;
        lock (Directive)
        {
            if (_searched) return _discovered;
            _searched = true;
            foreach (var exe in OperatingSystem.IsWindows() ? new[] { "python", "py" } : new[] { "python3", "python" })
            {
                try
                {
                    var psi = new ProcessStartInfo(exe, new[] { "-c", "import sysconfig; print(sysconfig.get_path('stdlib'))" })
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using var p = Process.Start(psi)!;
                    string path = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(10_000);
                    if (p.ExitCode == 0 && Directory.Exists(path)) return _discovered = path;
                }
                catch (Exception) { /* not installed */ }
            }
            return null;
        }
    }

    static readonly Lazy<Assembly?> Runtime = new(() =>
    {
        try { return Assembly.Load(new AssemblyName("Typhon.Runtime")); } catch (Exception) { return null; }
    });

    /// <summary>Is the module one Typhon provides itself (a runtime module, a typing-only one)? Those are never taken from Lib.</summary>
    public static bool ProvidedByTyphon(string module)
    {
        if (TypingOnly.Contains(module)) return true;
        string head = module.Split('.')[0];
        var asm = Runtime.Value;
        return asm != null && (asm.GetType("Typhon.Stdlib." + head) != null || asm.GetType("Typhon.Stdlib." + head + "_") != null);
    }

    /// <summary>Lib/name.py for a single-file module of the standard library (packages are not taken yet), or null.</summary>
    public static string? SourceOf(string lib, string module)
    {
        if (module.Contains('.') || module.Length == 0) return null;
        string path = Path.Combine(lib, module + ".py");
        return File.Exists(path) ? path : null;
    }
}
