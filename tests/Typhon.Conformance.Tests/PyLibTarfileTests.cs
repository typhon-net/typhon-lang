// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTarfileTests
{
    [Fact]
    public void PyLibTarfile()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTarfile skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string stdlib = oracle.StdOut.Trim();
        if (stdlib.Length == 0) { Console.WriteLine("PyLibTarfile requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-tarfile-source-").FullName;
        try
        {
            string sources = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            File.Copy(Path.Combine(stdlib, "tarfile.py"), Path.Combine(sources, "tarfile.py"));
            string script = Path.Combine(directory, "archive.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                import io
                import sys
                import tarfile

                payload = b"first line\nsecond line\n"
                for filename, write_mode, read_mode in ((sys.argv[1], "w:", "r:"), (sys.argv[2], "w|gz", "r|gz")):
                    if sys.argv[3] == "write":
                        with tarfile.open(filename, mode=write_mode) as archive:
                            info = tarfile.TarInfo("nested/café.txt")
                            info.size = len(payload)
                            info.mtime = 17
                            archive.addfile(info, io.BytesIO(payload))
                            empty = tarfile.TarInfo("empty")
                            archive.addfile(empty, io.BytesIO())
                    print(read_mode)
                    with tarfile.open(filename, mode=read_mode) as archive:
                        for member in archive:
                            with archive.extractfile(member) as file:
                                print(member.name, member.size, member.mtime, file.read())
                """);

            string cpythonTar = Path.Combine(directory, "cpython.tar");
            string cpythonGzip = Path.Combine(directory, "cpython.tar.gz");
            string typhonTar = Path.Combine(directory, "typhon.tar");
            string typhonGzip = Path.Combine(directory, "typhon.tar.gz");
            var environment = new Dictionary<string, string> { ["PYTHONPATH"] = sources, ["PYTHONUTF8"] = "1" };
            var expected = Suite.Run(python, [script, cpythonTar, cpythonGzip, "write"], TimeSpan.FromMinutes(1), environment);
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var create = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", script, typhonTar, typhonGzip, "write"],
                TimeSpan.FromMinutes(3), environment);
            Assert.True(create.ExitCode == 0, create.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(create.StdOut));

            var cpythonReadsTyphon = Suite.Run(python, [script, typhonTar, typhonGzip, "read"], TimeSpan.FromMinutes(1), environment);
            Assert.True(cpythonReadsTyphon.ExitCode == 0, cpythonReadsTyphon.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(cpythonReadsTyphon.StdOut));
            var typhonReadsCpython = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", script, cpythonTar, cpythonGzip, "read"],
                TimeSpan.FromMinutes(3), environment);
            Assert.True(typhonReadsCpython.ExitCode == 0, typhonReadsCpython.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(typhonReadsCpython.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
