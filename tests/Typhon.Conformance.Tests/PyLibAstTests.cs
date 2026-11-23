// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibAstTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AstUnparserWithManagedOrOriginalAst(bool source)
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"CPython AST tests skipped: {reason}"); return; }
        var oracle = Suite.Run(python,
            ["-I", "-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("CPython AST tests require the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-ast-backends-").FullName;
        try
        {
            string script = Path.Combine(directory, "probe.ty");
            string directive = source ? $"#:pylib {lib}\n" : "";
            File.WriteAllText(script, directive + """
                import ast
                from _ast_unparse import Unparser

                examples = [
                    "def f(x: int = 2):\n    return x**2\n",
                    "class C:\n    x = 1\n    def f(self):\n        return self.x\n",
                    "xs = [x * 2 for x in range(4) if x > 0]",
                    "if ok:\n    f(1, key=True)\nelse:\n    f(2)\n",
                ]
                for text in examples:
                    tree = ast.parse(text)
                    print(type(tree.body[0]).__name__, len(list(ast.walk(tree))))
                    print(Unparser().visit(tree))

                for text in ["(3, {'a': 1}, [2])", "set()", "3+2j", "{None: b'bytes'}", "-17"]:
                    print(ast.literal_eval(text))
                """);
            if (source)
                File.AppendAllText(script, "\nprint(ast.__file__ is not None, ast.unparse(ast.parse('f(3)', mode='eval')))\n");

            var expected = Suite.Run(python, ["-I", script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
