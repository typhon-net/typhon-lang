// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibPathTests
{
    [Fact]
    public void OriginalPathModules()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"OriginalPathModules skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("OriginalPathModules requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-paths-").FullName;
        try
        {
            string script = Path.Combine(directory, "path_probe.ty");
            const string source = """
                import upstream_ntpath as win
                import upstream_posixpath as posix

                print(win.splitdrive(r"C:\dir\leaf"))
                print(win.splitext(b"logs\\data.tar.gz"))
                print(win.commonpath([r"C:\one\two", r"C:\one\three"]))
                print(win.normpath(r"C:\one\..\Two//three"))
                print(posix.split("/a/b//c.ext"))
                print(posix.splitext(b"a/b.test.tar.gz"))
                print(posix.normpath("//a/../b///./c/"))
                print(posix.commonpath(["/project/lib/a", "/project/lib/b", "/project/lib/c"]))
                print(posix.commonpath([b"left/other/a", b"left/other", b"left/other/a/b"]))
                print(posix.relpath("/a/b/c", "/a/other"))
                try:
                    posix.commonpath(["/absolute", "relative"])
                except ValueError as error:
                    print(str(error))
                """;
            File.WriteAllText(script, source);
            foreach (string module in new[] { "ntpath", "posixpath" })
            {
                string upstream = Path.Combine(lib, module + ".py");
                File.Copy(upstream, Path.Combine(directory, "upstream_" + module + ".py"));
                File.Copy(upstream, Path.Combine(directory, "upstream_" + module + ".ty"));
            }
            File.Copy(Path.Combine(lib, "genericpath.py"), Path.Combine(directory, "genericpath.ty"));

            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
