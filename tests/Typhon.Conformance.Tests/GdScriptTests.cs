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
}
