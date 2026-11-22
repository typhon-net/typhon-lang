// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceBdbTests
{
    [Fact]
    public void PythonSourceBdbBreakpoints()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PythonSourceBdbBreakpoints skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PythonSourceBdbBreakpoints requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-bdb-").FullName;
        try
        {
            string source = Path.Combine(lib, "bdb.py");
            File.Copy(source, Path.Combine(directory, "stock_bdb.py"));
            File.Copy(source, Path.Combine(directory, "stock_bdb.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import stock_bdb as bdb

                bdb.Breakpoint.clearBreakpoints()
                debugger = bdb.Bdb(skip=["ignored.*"])
                debugger.reset()
                print(debugger.canonic("<stdin>"), debugger.is_skipped_module("ignored.child"), debugger.is_skipped_module("other"))
                print(debugger.set_break(__file__, 5))
                first = debugger.get_breaks(__file__, 5)[0]
                print(first.number, first.enabled, debugger.get_file_breaks(__file__))
                first.disable()
                print(first.enabled)
                first.enable()
                print(first.enabled, debugger.get_break(__file__, 5))
                print(debugger.clear_bpbynumber(str(first.number)), debugger.get_break(__file__, 5))
                print(debugger.set_break(__file__, 700).endswith(":700 does not exist"))
                try:
                    bdb.Bdb(backend="unknown")
                except ValueError as error:
                    print(str(error))


                class Recorder(bdb.Bdb):
                    def __init__(self, backend: str, run_to_breakpoint: bool = False):
                        super().__init__(backend=backend)
                        self.run_to_breakpoint = run_to_breakpoint
                        self.entries: list[tuple[str, int]] = []

                    def user_line(self, frame):
                        if frame.f_code.co_name == "work":
                            self.entries.append(("line", int(frame.f_lineno - frame.f_code.co_firstlineno)))
                            if self.run_to_breakpoint:
                                self.set_continue()

                    def user_return(self, frame, value):
                        if frame.f_code.co_name == "work":
                            self.entries.append(("return", int(value)))

                def work(value: int) -> int:
                    adjusted = value + 4
                    return adjusted * 2

                for backend in ("settrace", "monitoring"):
                    recorder = Recorder(backend)
                    print(recorder.runcall(work, 3), recorder.entries)
                    bdb.Breakpoint.clearBreakpoints()
                    recorder = Recorder(backend, True)
                    breakpoint_line = work.__code__.co_firstlineno + 2
                    print(recorder.set_break(__file__, breakpoint_line))
                    print(recorder.runcall(work, 3), recorder.entries)
                    print(recorder.get_breaks(__file__, breakpoint_line)[0].hits)
                    print(recorder.clear_all_breaks(), recorder.get_all_breaks())
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", probe, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
