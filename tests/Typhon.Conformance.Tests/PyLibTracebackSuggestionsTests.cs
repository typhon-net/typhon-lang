// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTracebackSuggestionsTests
{
    [Fact]
    public void PyLibTracebackSuggestions()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTracebackSuggestions skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTracebackSuggestions requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-traceback-suggestions-").FullName;
        try
        {
            string script = Path.Combine(dir, "traceback_suggestions.ty");
            const string extract = """
                import ast
                from pathlib import Path
                import sys

                source = Path(sys.argv[1]).read_text(encoding="utf-8")
                selected = ["import importlib.util\nimport sys\nimport types\nfrom contextlib import suppress"]
                functions = {"_get_safe___dir__", "_compute_suggestion_error", "_levenshtein_distance",
                             "_substitution_cost", "_is_lazy_import", "_check_for_nested_attribute"}
                fields = {"_MAX_CANDIDATE_ITEMS", "_MAX_STRING_SIZE", "_MOVE_COST", "_CASE_COST", "_sentinel"}
                for node in ast.parse(source).body:
                    if (
                        isinstance(node, ast.FunctionDef) and node.name in functions
                        or isinstance(node, ast.ClassDef) and node.name == "_Sentinel"
                        or isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in fields for t in node.targets)
                    ):
                        selected.append(ast.get_source_segment(source, node))
                Path(sys.argv[2]).write_text("\n\n".join(selected) + "\n\n", encoding="utf-8")
                """;
            var selection = Suite.Run(python, ["-c", extract, Path.Combine(lib, "traceback.py"), script], TimeSpan.FromSeconds(30));
            Assert.True(selection.ExitCode == 0, selection.StdErr);
            const string witness = """
                def suggest(error: object, tb: object, name: str):
                    return _compute_suggestion_error(error, tb, name)

                try:
                    getattr(["entry"], "apend")
                except AttributeError as exc:
                    print(suggest(exc, None, "apend"))

                name = NameError("missing name", name="countr")
                frame = types.SimpleNamespace(f_locals={"counter": 1}, f_globals={}, f_builtins={})
                trace = types.SimpleNamespace(tb_next=None, tb_frame=frame)
                print(suggest(name, trace, "countr"))

                error = ImportError("cannot import name", name="math")
                print(suggest(error, None, "sqr"))
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
