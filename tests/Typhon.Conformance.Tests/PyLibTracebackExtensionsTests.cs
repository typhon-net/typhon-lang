// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTracebackExtensionsTests
{
    [Fact]
    public void PyLibTracebackExtensions()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTracebackExtensions skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTracebackExtensions requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-traceback-extensions-").FullName;
        try
        {
            string script = Path.Combine(dir, "traceback_extensions.ty");
            const string extract = """
                import ast
                from pathlib import Path
                import sys

                source = Path(sys.argv[1]).read_text(encoding="utf-8")
                function = next(node for node in ast.parse(source).body
                                if isinstance(node, ast.FunctionDef) and node.name == "_find_incompatible_extension_module")
                imports = "import os\nimport pathlib\nimport sys\nimport tempfile\n\n"
                Path(sys.argv[2]).write_text(imports + ast.get_source_segment(source, function) + "\n\n", encoding="utf-8")
                """;
            var selection = Suite.Run(python, ["-c", extract, Path.Combine(lib, "traceback.py"), script], TimeSpan.FromSeconds(30));
            Assert.True(selection.ExitCode == 0, selection.StdErr);
            const string witness = """
                with tempfile.TemporaryDirectory() as tmp:
                    directory = pathlib.Path(tmp)
                    import importlib.machinery
                    suffix = importlib.machinery.EXTENSION_SUFFIXES[-1]
                    filename = "witness_incompatible.incompatible" + suffix
                    directory.joinpath(filename).write_text("")
                    package = directory.joinpath("extension_witness")
                    package.mkdir()
                    package.joinpath("__init__.py").write_text("")
                    package.joinpath(filename).write_text("")
                    sys.path.insert(0, str(directory))
                    try:
                        print(_find_incompatible_extension_module("witness_incompatible") == filename)
                        print(_find_incompatible_extension_module("extension_witness.witness_incompatible") == filename)
                        print(_find_incompatible_extension_module("typhon_missing_extension_witness") is None)
                    finally:
                        sys.path.pop(0)
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
