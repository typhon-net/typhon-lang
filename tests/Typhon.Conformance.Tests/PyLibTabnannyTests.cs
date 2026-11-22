// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTabnannyTests
{
    [Fact]
    public void PyLibTabnanny()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTabnanny skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTabnanny requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-tabnanny-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_tabnanny.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import io
                import os
                import tabnanny
                import tokenize

                print(tabnanny.__file__ is not None)
                for source in (
                    '# comment\nif ready:\n    value = (1\n + 2)\n    pass\n',
                    'if ready:\n\tpass\n        pass\n',
                    'if ready:\n \tpass\n\t pass\n',
                    'if ready:\n  \t  \tpass\n    \t\tpass\n',
                ):
                    try:
                        tabnanny.process_tokens(tokenize.generate_tokens(io.StringIO(source).readline))
                        print('clean')
                    except tabnanny.NannyNag as problem:
                        print(problem.get_lineno(), problem.get_msg(), repr(problem.get_line()))

                os.chdir(os.path.dirname(__file__))
                with open('clean.py', 'w', encoding='utf-8') as f:
                    f.write('if ready:\n    pass\n')
                with open('ambiguous.py', 'w', encoding='utf-8') as f:
                    f.write('if ready:\n\tpass\n        pass\n')
                tabnanny.verbose = 1
                tabnanny.check('clean.py')
                tabnanny.check('ambiguous.py')
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
