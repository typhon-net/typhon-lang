// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibCodeTests
{
    [Fact]
    public void OriginalInteractiveInterpreter()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibCode skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string library = oracle.StdOut.Trim();
        if (library.Length == 0) { Console.WriteLine("PyLibCode requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-interactive-").FullName;
        try
        {
            string script = Path.Combine(directory, "uses_code.ty");
            File.WriteAllText(script, $"#:pylib {library}\n" + """
                import builtins
                import code
                import sys


                class Interpreter(code.InteractiveInterpreter):
                    def _excepthook(self, typ, value, tb):
                        if isinstance(value, SyntaxError):
                            print("syntax", value.filename, value.text.strip(), tb is None)
                        else:
                            print("runtime error", str(value), tb is value.__traceback__)


                interpreter = Interpreter({})
                print(interpreter.runsource("if True:"))
                print(interpreter.runsource("answer = 21 * 2"))
                print(interpreter.locals["answer"])
                print(interpreter.runsource("answer + 1"))
                print(interpreter.runsource("if True", "session.py"))
                print(interpreter.runsource("raise ValueError('boom')", "session.py"))


                class Console(code.InteractiveConsole):
                    def __init__(self, lines):
                        super().__init__(local_exit=True)
                        self.lines = iter(lines)

                    def raw_input(self, prompt=""):
                        print("prompt", repr(prompt))
                        return next(self.lines)

                    def write(self, data):
                        print("write", repr(data))


                old_exit = builtins.exit
                old_quit = builtins.quit
                print(hasattr(sys, "ps1"), hasattr(sys, "ps2"))
                console = Console(["if True:", "    answer = 7", "", "quit(3)"])
                console.interact(banner="", exitmsg="")
                print(console.locals["answer"])
                print(hasattr(sys, "ps1"), hasattr(sys, "ps2"))
                print(builtins.exit is old_exit, builtins.quit is old_quit)
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
