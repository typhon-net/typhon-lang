// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTracebackAnchorsTests
{
    [Fact]
    public void PyLibTracebackAnchors()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTracebackAnchors skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTracebackAnchors requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-traceback-anchors-").FullName;
        try
        {
            string script = Path.Combine(dir, "traceback_anchors.ty");
            const string extract = """
                import ast
                from pathlib import Path
                import sys

                lines = Path(sys.argv[1]).read_text(encoding="utf-8").splitlines(keepends=True)
                selected = ["import collections\nfrom contextlib import suppress\n\n"]
                for node in ast.parse("".join(lines)).body:
                    if isinstance(node, ast.ClassDef) and node.name == "StackSummary":
                        method = next(n for n in node.body if isinstance(n, ast.FunctionDef) and n.name == "_should_show_carets")
                        selected.append("class StackSummary:\n")
                        selected.extend(lines[method.lineno - 1:method.end_lineno])
                    elif isinstance(node, ast.FunctionDef) and node.name in ("_byte_offset_to_character_offset", "_extract_caret_anchors_from_line_segment"):
                        selected.extend(lines[node.lineno - 1:node.end_lineno])
                    elif isinstance(node, ast.Assign) and any(isinstance(n, ast.Name) and n.id == "_Anchors" for n in node.targets):
                        selected.extend(lines[node.lineno - 1:node.end_lineno])
                    else:
                        continue
                    selected.append("\n\n")
                Path(sys.argv[2]).write_text("".join(selected), encoding="utf-8")
                """;
            var selection = Suite.Run(python, ["-c", extract, Path.Combine(lib, "traceback.py"), script], TimeSpan.FromSeconds(30));
            Assert.True(selection.ExitCode == 0, selection.StdErr);
            const string witness = """
                for segment in ("left + right", "a ** b", "items[0]", "name()", "a +\n b", "abc", "a && b"):
                    print(_extract_caret_anchors_from_line_segment(segment))
                tester = StackSummary()
                print(tester._should_show_carets(7, 10, ["return f()"], True))
                print(tester._should_show_carets(9, 12, ["result = f()"], True))
                print(tester._should_show_carets(0, 4, ["f(1) + 2"], None))
                """;
            File.AppendAllText(script, witness);

            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
