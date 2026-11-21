// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Typhon.Conformance.Tests;

/// <summary>
/// CPython is the oracle: every tests/conformance/cases/*.ty program must print exactly the bytes CPython printed
/// (tests/conformance/expected/*.out, regenerated with tests/conformance/update_expected.py).
/// </summary>
public sealed class ConformanceTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Suite.CaseNames()) data.Add(name);
        return data;
    }

    /// <summary>`typhon run` (Roslyn → IL → JIT) prints what CPython prints. Known failures must still fail.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Jit(string name)
    {
        var run = Suite.Run("dotnet", [Suite.Cli, "run", Suite.CasePath(name)], TimeSpan.FromMinutes(2));
        string expected = Suite.Expected(name);
        bool matches = run.ExitCode == 0 && Suite.Normalize(run.StdOut) == expected;
        if (!string.IsNullOrWhiteSpace(run.StdErr)) output.WriteLine("stderr:\n" + run.StdErr);

        if (Suite.KnownFailures.TryGetValue(name, out var reason))
        {
            output.WriteLine($"known failure: {reason}");
            Assert.False(matches, $"'{name}' is listed in tests/conformance/known-failures.txt but now matches CPython: remove it from the list.");
            return;
        }
        Assert.True(run.ExitCode == 0, $"typhon run exited with {run.ExitCode}\n{run.StdErr}");
        Assert.True(matches, Suite.Diff(expected, Suite.Normalize(run.StdOut)));
    }

    /// <summary>
    /// The compile server produces the same programs as compiling in-process: every case (except known failures) through
    /// a warm server. The test's own server exits 60 s after its last request.
    /// </summary>
    [Fact]
    public void CompileServerMatches()
    {
        var env = new Dictionary<string, string> { ["TYPHON_SERVER"] = "1", ["TYPHON_SERVER_IDLE_SECONDS"] = "60" };
        var start = Suite.Run("dotnet", [Suite.Cli, "server", "start"], TimeSpan.FromMinutes(1), env);
        Assert.True(start.ExitCode == 0, "typhon server start failed\n" + start.StdErr);
        var failures = new List<string>();
        foreach (var name in Suite.CaseNames().Where(n => !Suite.KnownFailures.ContainsKey(n)))
        {
            var run = Suite.Run("dotnet", [Suite.Cli, "run", "--time", Suite.CasePath(name)], TimeSpan.FromMinutes(2), env);
            if (!run.StdErr.Contains("compiled by the compile server")) failures.Add($"{name}: not compiled by the server\n{run.StdErr}");
            else if (run.ExitCode != 0 || Suite.Normalize(run.StdOut) != Suite.Expected(name)) failures.Add($"{name}:\n{Suite.Diff(Suite.Expected(name), Suite.Normalize(run.StdOut))}");
            else output.WriteLine($"{name}: ok");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// #:package Name@Version: the package is restored (once, into the cache), referenced when compiling and loaded when
    /// the program runs.
    /// </summary>
    [Fact]
    public void PackageDirective()
    {
        string dir = Directory.CreateTempSubdirectory("typhon-package-").FullName;
        string script = Path.Combine(dir, "uses_package.ty");
        File.WriteAllText(script, "#:package Newtonsoft.Json@13.0.3\nfrom Newtonsoft.Json import JsonConvert\n\nprint(JsonConvert.SerializeObject([1, 2, 3]))\n");
        try
        {
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal("[1,2,3]\n", Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// #:pylib: modules Typhon's runtime lacks (graphlib, colorsys) are compiled from CPython's standard library source, with
    /// the program, and print what CPython prints.
    /// </summary>
    [Fact]
    public void PyLibDirective()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibDirective skipped: {why}"); return; }
        var lib = Suite.Run(python, ["-c", "import sysconfig; print(sysconfig.get_path('stdlib'))"], TimeSpan.FromSeconds(30)).StdOut.Trim();
        Assert.True(Directory.Exists(Path.Combine(lib)), $"no stdlib folder: {lib}");
        string dir = Directory.CreateTempSubdirectory("typhon-pylib-").FullName;
        string script = Path.Combine(dir, "uses_lib.ty");
        File.WriteAllText(script,
            $"#:pylib {lib}\n" +
            "import graphlib\nimport colorsys\n\n" +
            "ts = graphlib.TopologicalSorter({\"b\": {\"a\"}, \"c\": {\"b\"}})\n" +
            "print(list(ts.static_order()))\n" +
            "h, s, v = colorsys.rgb_to_hsv(0.2, 0.4, 0.4)\n" +
            "print(round(h, 3), round(s, 3), round(v, 3))\n" +
            "print(colorsys.hls_to_rgb(0.5, 0.5, 0.5))\n");
        try
        {
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// typhon test runs a test module's tests like pytest: fixtures (also yield fixtures), parametrize, raises, approx,
    /// skip and test classes pass, the failing test is reported with its assert line, the values compared and its output.
    /// </summary>
    [Fact]
    public void PytestStyleTests()
    {
        var run = Suite.Run("dotnet", [Suite.Cli, "test", Path.Combine(Suite.RepoRoot, "tests", "pytest", "test_sample.ty")], TimeSpan.FromMinutes(2));
        string stdout = Suite.Normalize(run.StdOut);
        output.WriteLine(stdout);
        Assert.True(run.ExitCode == 1, $"exit code {run.ExitCode}\n{run.StdErr}");
        Assert.Contains("test_sample.ty ........Fs.. [100%]", stdout);
        Assert.Contains(">   assert add(2, 2) == 5\nE   assert 4 == 5", stdout);
        Assert.Contains("Captured stdout call", stdout);
        Assert.Contains("FAILED test_sample.ty::test_fails - assert 4 == 5", stdout);
        Assert.Matches(@"=+ 1 failed, 10 passed, 1 skipped in \d+\.\d\ds =+", stdout);
    }

    /// <summary>
    /// A cached build is used only while every source file is unchanged: run twice (the second from the cache), edit an
    /// imported module, and the next run must compile again and print the new result.
    /// </summary>
    [Fact]
    public void ScriptCacheTracksSources()
    {
        string dir = Path.Combine(Suite.RepoRoot, "artifacts", "conformance-cache", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.ty"), "from helper import greet\nprint(greet(\"cache\"))\n");
            File.WriteAllText(Path.Combine(dir, "helper.ty"), "def greet(n: str) -> str:\n    return \"hi \" + n\n");
            var env = new Dictionary<string, string> { ["TYPHON_CACHE"] = "1", ["TYPHON_CACHE_DIR"] = Path.Combine(dir, "cache") };
            string[] cmd = [Suite.Cli, "run", "--time", Path.Combine(dir, "main.ty")];

            var first = Suite.Run("dotnet", cmd, TimeSpan.FromMinutes(2), env);
            Assert.Equal("hi cache\n", Suite.Normalize(first.StdOut));
            Assert.Contains("compiled in", first.StdErr);

            var second = Suite.Run("dotnet", cmd, TimeSpan.FromMinutes(2), env);
            Assert.Equal("hi cache\n", Suite.Normalize(second.StdOut));
            Assert.Contains("cached build", second.StdErr);

            File.WriteAllText(Path.Combine(dir, "helper.ty"), "def greet(n: str) -> str:\n    return \"hello \" + n\n");
            var third = Suite.Run("dotnet", cmd, TimeSpan.FromMinutes(2), env);
            Assert.Equal("hello cache\n", Suite.Normalize(third.StdOut));
            Assert.Contains("compiled in", third.StdErr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    public static TheoryData<string> UntypedCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Suite.CaseNames().Where(n => n.StartsWith("untyped", StringComparison.Ordinal))) data.Add(name);
        return data;
    }

    /// <summary>
    /// Cases named untyped*.ty have no annotations at all; whole-program inference must type every parameter, return value
    /// and field, so they compile with --strict (where any implicit dynamic is an error).
    /// </summary>
    [Theory]
    [MemberData(nameof(UntypedCases))]
    public void UntypedCompilesStrict(string name)
    {
        var run = Suite.Run("dotnet", [Suite.Cli, "check", "--strict", Suite.CasePath(name)], TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0, $"typhon check --strict failed: inference left something dynamic\n{run.StdErr}{run.StdOut}");
    }

    /// <summary>
    /// The checked-in expected outputs are what the CPython on this machine prints today (guards against stale .out files).
    /// Uses TYPHON_CPYTHON, the repo's .venv, or python3/python on PATH; passes with a note when none is found,
    /// unless TYPHON_REQUIRE_CPYTHON=1 (set in CI).
    /// </summary>
    [Fact]
    public void ExpectedOutputsMatchCPython()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null)
        {
            Assert.True(Environment.GetEnvironmentVariable("TYPHON_REQUIRE_CPYTHON") != "1", $"CPython 3.12+ is required: {why}");
            output.WriteLine($"skipped: {why}");
            return;
        }
        bool hasNumpy = Suite.Run(python, ["-c", "import numpy"], TimeSpan.FromMinutes(1)).ExitCode == 0;
        var stale = new List<string>();
        foreach (var name in Suite.CaseNames())
        {
            if (name.StartsWith("numpy", StringComparison.Ordinal) && !hasNumpy) { output.WriteLine($"{name}: skipped (numpy not installed)"); continue; }
            var run = Suite.Run(python, ["-X", "utf8", Suite.CasePath(name)], TimeSpan.FromMinutes(2));
            if (run.ExitCode != 0) { stale.Add($"{name}: CPython exited with {run.ExitCode}\n{run.StdErr}"); continue; }
            string actual = Suite.Normalize(run.StdOut);
            if (actual != Suite.Expected(name)) stale.Add($"{name}:\n{Suite.Diff(Suite.Expected(name), actual)}");
            else output.WriteLine($"{name}: ok");
        }
        Assert.True(stale.Count == 0, "expected/*.out differs from CPython (run tests/conformance/update_expected.py):\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// `typhon build --aot` produces a native executable with the same output. Opt-in (TYPHON_CONFORMANCE_AOT=1): it needs the
    /// platform's native toolchain and takes ~30 s per case. Cases that use dynamic values (TY2001) are reported and skipped.
    /// </summary>
    [Fact]
    public void NativeAot()
    {
        if (Environment.GetEnvironmentVariable("TYPHON_CONFORMANCE_AOT") != "1") { output.WriteLine("skipped: set TYPHON_CONFORMANCE_AOT=1"); return; }
        var failures = new List<string>();
        string outRoot = Path.Combine(Suite.RepoRoot, "artifacts", "conformance-aot");
        foreach (var name in Suite.CaseNames())
        {
            if (Suite.KnownFailures.ContainsKey(name)) continue;
            string outDir = Path.Combine(outRoot, name);
            var build = Suite.Run("dotnet", [Suite.Cli, "build", Suite.CasePath(name), "--aot", "-o", outDir], TimeSpan.FromMinutes(10));
            if (build.ExitCode != 0)
            {
                if (build.StdErr.Contains("TY2001")) { output.WriteLine($"{name}: not AOT-compatible (dynamic code), skipped"); continue; }
                failures.Add($"{name}: build --aot failed\n{build.StdErr}{build.StdOut}");
                continue;
            }
            string exe = Path.Combine(outDir, "typhon_" + name + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
            var run = Suite.Run(exe, [], TimeSpan.FromMinutes(2));
            string actual = Suite.Normalize(run.StdOut);
            if (run.ExitCode != 0 || actual != Suite.Expected(name)) failures.Add($"{name}: exit {run.ExitCode}\n{run.StdErr}{Suite.Diff(Suite.Expected(name), actual)}");
            else output.WriteLine($"{name}: ok");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}

/// <summary>Paths, the known-failures list and process helpers shared by the tests.</summary>
static class Suite
{
    static string Metadata(string key) =>
        typeof(Suite).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"assembly metadata '{key}' missing");

    public static readonly string RepoRoot = Path.GetFullPath(Metadata("TyphonRepoRoot"));
    public static readonly string Cli = Path.GetFullPath(Metadata("TyphonCli"));
    public static readonly string Emit = Path.GetFullPath(Metadata("TyphonEmit"));
    static readonly string ConformanceDir = Path.Combine(RepoRoot, "tests", "conformance");

    public static IEnumerable<string> CaseNames() =>
        Directory.GetFiles(Path.Combine(ConformanceDir, "cases"), "*.ty").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!;

    public static string CasePath(string name) => Path.Combine(ConformanceDir, "cases", name + ".ty");

    public static string Expected(string name)
    {
        string path = Path.Combine(ConformanceDir, "expected", name + ".out");
        Assert.True(File.Exists(path), $"missing {path}: run tests/conformance/update_expected.py");
        return Normalize(File.ReadAllText(path, Encoding.UTF8));
    }

    public static readonly IReadOnlyDictionary<string, string> KnownFailures = File.ReadAllLines(Path.Combine(ConformanceDir, "known-failures.txt"))
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => { int hash = l.IndexOf('#'); return (Name: (hash < 0 ? l : l[..hash]).Trim(), Reason: hash < 0 ? "" : l[(hash + 1)..].Trim()); })
        .ToDictionary(x => x.Name, x => x.Reason);

    public static string Normalize(string s) => s.Replace("\r\n", "\n");

    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static Result Run(string fileName, IEnumerable<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = Path.Combine(ConformanceDir, "cases"),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONHASHSEED"] = "0";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        // by default every test compiles in its own process, without the script cache: it tests the compiler itself
        psi.Environment["TYPHON_SERVER"] = "0";
        psi.Environment["TYPHON_CACHE"] = "0";
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new Result(-1, stdout.IsCompleted ? stdout.Result : "", $"timed out after {timeout}");
        }
        p.WaitForExit();
        return new Result(p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string? FindPython(out string why)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("TYPHON_CPYTHON") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(RepoRoot, ".venv", "Scripts", "python.exe"));
        candidates.Add(Path.Combine(RepoRoot, ".venv", "bin", "python"));
        candidates.Add("python3");
        candidates.Add("python");
        foreach (var c in candidates)
        {
            if (Path.IsPathRooted(c) && !File.Exists(c)) continue;
            try
            {
                // PEP 695 generics (def first[T]) need 3.12+
                var r = Run(c, ["-c", "import sys; print(sys.version_info >= (3, 12))"], TimeSpan.FromSeconds(30));
                if (r.ExitCode == 0 && r.StdOut.Trim() == "True") { why = ""; return c; }
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        why = "no CPython 3.12+ found (set TYPHON_CPYTHON)";
        return null;
    }

    /// <summary>A small line diff for assertion messages: each differing line as -expected / +actual.</summary>
    public static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder("output differs from CPython (- CPython, + Typhon):\n");
        int shown = 0;
        for (int i = 0; i < Math.Max(e.Length, a.Length) && shown < 20; i++)
        {
            string? el = i < e.Length ? e[i] : null, al = i < a.Length ? a[i] : null;
            if (el == al) continue;
            shown++;
            if (el != null) sb.Append($"  line {i + 1} - {el}\n");
            if (al != null) sb.Append($"  line {i + 1} + {al}\n");
        }
        return sb.ToString();
    }
}
