// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Typhon.Conformance.Tests;

/// <summary>
/// Godot is the oracle for the GDScript front end: every tests/gdscript/cases/*.gd program (a SceneTree script that only
/// prints) must print exactly what Godot printed for it (tests/gdscript/expected/*.out, regenerated with
/// tests/gdscript/update_expected.py, which needs Godot). Running these tests does not.
/// </summary>
public sealed class GdScriptTests(ITestOutputHelper output)
{
    static readonly string Dir = Path.Combine(Suite.RepoRoot, "tests", "gdscript");

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Dir, "cases"), "*.gd").Order(StringComparer.Ordinal)) data.Add(Path.GetFileNameWithoutExtension(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesGodot(string name)
    {
        string expectedPath = Path.Combine(Dir, "expected", name + ".out");
        Assert.True(File.Exists(expectedPath), $"missing {expectedPath}: run tests/gdscript/update_expected.py (needs Godot)");
        string expected = Suite.Normalize(File.ReadAllText(expectedPath, Encoding.UTF8));
        var run = Suite.Run("dotnet", [Suite.Cli, "run", Path.Combine(Dir, "cases", name + ".gd")], TimeSpan.FromMinutes(2));
        if (!string.IsNullOrWhiteSpace(run.StdErr)) output.WriteLine("stderr:\n" + run.StdErr);
        Assert.True(run.ExitCode == 0, $"typhon run exited with {run.ExitCode}\n{run.StdErr}");
        string actual = Suite.Normalize(run.StdOut);
        Assert.True(actual == expected, Suite.Diff(expected, actual).Replace("CPython", "Godot"));
    }

    /// <summary>
    /// TyphonEmitToDisk's tool (what a Godot project's build runs): a .gd module's C# goes to a file named after its script
    /// class, as a top-level partial class (Godot's generators need both), next to the runtime; an unchanged second run
    /// rewrites nothing, and a file no longer produced is deleted.
    /// </summary>
    [Fact]
    public void EmitToDisk()
    {
        string dir = Path.Combine(Suite.RepoRoot, "artifacts", "gdscript-emit", Guid.NewGuid().ToString("N")[..8]);
        string outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "player.gd"),
                "extends RefCounted\nclass_name Player\n\nvar hp := 3\n\nfunc hit(n: int) -> int:\n\thp -= n\n\treturn hp\n");
            File.WriteAllText(Path.Combine(outDir, "Stale.cs"), "// from an earlier build\n");
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(p => p.EndsWith(".dll"));
            string rsp = Path.Combine(dir, "emit.rsp");
            File.WriteAllLines(rsp, new[] { "out=" + outDir, "project-dir=" + dir, "root-namespace=Game", "source=" + Path.Combine(dir, "player.gd") }
                .Concat(refs.Select(r => "ref=" + r)));

            var first = Suite.Run("dotnet", [Suite.Emit, "@" + rsp], TimeSpan.FromMinutes(2));
            Assert.True(first.ExitCode == 0, $"typhon-emit failed\n{first.StdOut}{first.StdErr}");
            string player = Path.Combine(outDir, "Player.cs");
            Assert.True(File.Exists(player) && File.Exists(Path.Combine(outDir, "Typhon.Runtime.g.cs")), string.Join(", ", Directory.GetFiles(outDir)));
            Assert.False(File.Exists(Path.Combine(outDir, "Stale.cs")));
            string code = File.ReadAllText(player);
            Assert.Contains("namespace Game", code);
            Assert.Contains("public partial class Player", code);
            Assert.Contains("public static partial class player_gd", code);
            Assert.Contains("player.gd", code); // #line directives point at the GDScript source

            var stamp = File.GetLastWriteTimeUtc(player);
            var second = Suite.Run("dotnet", [Suite.Emit, "@" + rsp], TimeSpan.FromMinutes(2));
            Assert.True(second.ExitCode == 0, second.StdOut + second.StdErr);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(player));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
