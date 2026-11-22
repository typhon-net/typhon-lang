// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTracebackTests
{
    [Fact]
    public void PyLibTraceback()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTraceback skipped: {why}"); return; }

        var oracle = Suite.Run(python,
            ["-I", "-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTraceback requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-original-traceback-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, "traceback.py"), Path.Combine(dir, "traceback.ty"));
            File.Copy(Path.Combine(lib, "codeop.py"), Path.Combine(dir, "codeop.ty"));
            string script = Path.Combine(dir, "portable_traceback.ty");
            File.WriteAllText(script, """
                import traceback

                print(traceback.format_exception_only(ValueError("problem")))
                print(traceback.format_exception_only(TypeError("bad call\nsecond line")))
                syntax = SyntaxError("invalid token", ("example.py", 4, 2, " x ++", 4, 5))
                print(traceback.format_exception_only(syntax))
                group = ExceptionGroup("group", [ValueError("first"), TypeError("second")])
                print(traceback.format_exception_only(group, show_group=True))
                print(traceback.format_list([("example.py", 4, "build", " result = value")]))

                def fail():
                    raise ValueError("with a frame")

                try:
                    fail()
                except ValueError as error:
                    print("".join(traceback.format_exception(error)).replace(__file__, "example.ty"), end="")
                """);

            var expected = Suite.Run(python, ["-I", "-X", "no_debug_ranges", script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
