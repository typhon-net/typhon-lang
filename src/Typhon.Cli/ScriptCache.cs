// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;

namespace Typhon.Cli;

/// <summary>Identifies this exact build of the CLI, compiler and runtime: caches and servers are only shared within one.</summary>
internal static class BuildStamp
{
    public static readonly string Value = Compute();

    static string Compute()
    {
        var sb = new StringBuilder(AppContext.BaseDirectory).Append('|').Append(Environment.Version);
        foreach (var name in new[] { "typhon.dll", "Typhon.Compiler.dll", "Typhon.Runtime.dll", "Typhon.NumPy.dll" })
        {
            var file = new FileInfo(Path.Combine(AppContext.BaseDirectory, name));
            if (file.Exists) sb.Append('|').Append(name).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Compiled scripts for `typhon run`, like CPython's __pycache__ but in the user's cache directory. Each script has one
/// slot (keyed by its path and the type mode). The slot records the hash of every source file the compilation read and
/// every module path it probed that did not exist, plus the compiler build; if any of that differs, the script is
/// compiled again and the slot overwritten, so stale versions never pile up. Slots unused for 30 days are removed.
/// Disable with TYPHON_CACHE=0 or --no-cache; move it with TYPHON_CACHE_DIR.
/// </summary>
internal static class ScriptCache
{
    const string Header = "typhon-cache 2";
    static readonly TimeSpan MaxUnused = TimeSpan.FromDays(30);

    public sealed record Entry(byte[] Pe, byte[]? Pdb, List<string> Messages, List<string> Frameworks);

    public static bool Enabled => Environment.GetEnvironmentVariable("TYPHON_CACHE") != "0";

    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable("TYPHON_CACHE_DIR") is { Length: > 0 } dir) return dir;
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typhon", "cache");
            string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(xdg, "typhon");
        }
    }

    static string SlotDir(string entryPath, string mode)
    {
        string key = (OperatingSystem.IsWindows() ? entryPath.ToLowerInvariant() : entryPath) + "|" + mode;
        return Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant());
    }

    /// <summary>The cached build of this script, if it is still exactly what compiling it now would produce.</summary>
    public static Entry? TryLoad(string entryPath, string mode)
    {
        try
        {
            string dir = SlotDir(entryPath, mode);
            string manifest = Path.Combine(dir, "manifest.txt");
            if (!File.Exists(manifest)) return null;
            var lines = File.ReadAllLines(manifest);
            if (lines.Length < 2 || lines[0] != Header || lines[1] != "build " + BuildStamp.Value) return null;
            var messages = new List<string>();
            var frameworks = new List<string>();
            foreach (var line in lines.Skip(2))
            {
                // "framework <name> <folder>": built against that framework folder; another one (an update) recompiles
                if (line.StartsWith("framework "))
                {
                    int space = line.IndexOf(' ', 10);
                    string name = line[10..space], folder = line[(space + 1)..];
                    if (SharedFrameworks.Directory(name) != folder) return null;
                    frameworks.Add(name);
                    continue;
                }
                if (line.StartsWith("file "))
                {
                    // "file <sha256 | -> <path>"
                    int space = line.IndexOf(' ', 5);
                    string sha = line[5..space], path = line[(space + 1)..];
                    if (sha == "-") { if (File.Exists(path)) return null; }
                    else if (!File.Exists(path) || CliCompiler.Sha256(File.ReadAllBytes(path)) != sha) return null;
                }
                else if (line.StartsWith("message ")) messages.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line[8..])));
            }
            string pdb = Path.Combine(dir, "program.pdb");
            var entry = new Entry(File.ReadAllBytes(Path.Combine(dir, "program.dll")), File.Exists(pdb) ? File.ReadAllBytes(pdb) : null, messages, frameworks);
            File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow); // last use, for pruning
            return entry;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Stores a successful build, replacing whatever this script's slot held before. Best effort: errors are ignored.</summary>
    public static void Store(string entryPath, string mode, byte[] pe, byte[]? pdb, IEnumerable<SourceProbe> probes, IEnumerable<string> messages,
                             IEnumerable<string> frameworks)
    {
        string dir = SlotDir(entryPath, mode);
        string tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(tmp);
            File.WriteAllBytes(Path.Combine(tmp, "program.dll"), pe);
            if (pdb != null) File.WriteAllBytes(Path.Combine(tmp, "program.pdb"), pdb);
            var sb = new StringBuilder().AppendLine(Header).AppendLine("build " + BuildStamp.Value);
            foreach (var p in probes) sb.Append("file ").Append(p.Sha256 ?? "-").Append(' ').AppendLine(p.Path);
            foreach (var f in frameworks) sb.Append("framework ").Append(f).Append(' ').AppendLine(SharedFrameworks.Directory(f));
            foreach (var m in messages) sb.Append("message ").AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(m)));
            File.WriteAllText(Path.Combine(tmp, "manifest.txt"), sb.ToString());
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // the stale version of this script
            Directory.Move(tmp, dir);
            PruneOccasionally();
        }
        catch (IOException) { TryDelete(tmp); }
        catch (UnauthorizedAccessException) { TryDelete(tmp); }
    }

    /// <summary>At most once a day: remove slots unused for 30 days (deleted or forgotten scripts) and abandoned temp folders.</summary>
    static void PruneOccasionally()
    {
        string marker = Path.Combine(Root, "last-prune");
        if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1)) return;
        File.WriteAllText(marker, "");
        foreach (var dir in Directory.GetDirectories(Root))
        {
            string manifest = Path.Combine(dir, "manifest.txt");
            var lastUse = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : Directory.GetLastWriteTimeUtc(dir);
            var maxAge = dir.Contains(".tmp-") ? TimeSpan.FromHours(1) : MaxUnused;
            if (DateTime.UtcNow - lastUse > maxAge) TryDelete(dir);
        }
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
