// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceIoTests
{
    [Fact]
    public void IoFromPythonSource()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"IoFromPythonSource skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("IoFromPythonSource requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-io-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, "io.py"), Path.Combine(dir, "io.ty"));
            string source = Path.Combine(dir, "uses_io.ty");
            File.WriteAllText(source, """
                import io

                with io.BytesIO(b'first\nsecond') as binary:
                    print(binary.readline(), binary.read())
                    print(isinstance(binary, io.BufferedIOBase), isinstance(binary, io.IOBase))

                with io.StringIO('one\ntwo') as text:
                    print(repr(text.readline()), repr(text.read()))
                    print(isinstance(text, io.TextIOBase), isinstance(text, io.IOBase))

                print(issubclass(io.FileIO, io.RawIOBase))
                print(io.SEEK_SET, io.SEEK_CUR, io.SEEK_END)
                """);
            var expected = Suite.Run(python, [source], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", source], TimeSpan.FromMinutes(2));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
