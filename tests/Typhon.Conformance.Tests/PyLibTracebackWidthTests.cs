// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTracebackWidthTests
{
    [Fact]
    public void PyLibTracebackWidth()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTracebackWidth skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTracebackWidth requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-traceback-width-").FullName;
        try
        {
            string script = Path.Combine(dir, "traceback_width.ty");
            const string extract = """
                import ast
                from pathlib import Path
                import sys

                lines = Path(sys.argv[1]).read_text(encoding="utf-8").splitlines(keepends=True)
                selected = ["import functools\nimport itertools\nimport re\n\n"]
                for node in ast.parse("".join(lines)).body:
                    if isinstance(node, ast.FunctionDef) and node.name in ("_zip_display_width", "_str_width", "_wlen", "_display_width"):
                        start = min([node.lineno] + [item.lineno for item in node.decorator_list])
                        selected.extend(lines[start - 1:node.end_lineno])
                        selected.append("\n\n")
                    elif isinstance(node, ast.Assign) and any(isinstance(name, ast.Name) and name.id == "_ANSI_ESCAPE_SEQUENCE" for name in node.targets):
                        selected.extend(lines[node.lineno - 1:node.end_lineno])
                        selected.append("\n\n")
                Path(sys.argv[2]).write_text("".join(selected), encoding="utf-8")
                """;
            var selection = Suite.Run(python, ["-c", extract, Path.Combine(lib, "traceback.py"), script], TimeSpan.FromSeconds(30));
            Assert.True(selection.ExitCode == 0, selection.StdErr);
            const string witness = """
                for line in ["simple", "e\u0301漢", "💚\u0300", "🇦🇧", "क्\u200dष"]:
                    print([(ascii(ch), carets) for ch, carets in _zip_display_width(line, list("0123456789"))])
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
