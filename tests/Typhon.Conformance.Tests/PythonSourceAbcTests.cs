// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceAbcTests
{
    [Fact]
    public void ExplicitAbcSourceImports()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"ExplicitAbcSourceImports skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("ExplicitAbcSourceImports requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-abc-").FullName;
        try
        {
            File.Copy(Path.Combine(library, "abc.py"), Path.Combine(directory, "abc.ty"));
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, """
                import abc
                from abc import ABCMeta, abstractmethod

                class Reader(metaclass=ABCMeta):
                    @abstractmethod
                    def read(self) -> int:
                        raise NotImplementedError

                try:
                    Reader()
                except TypeError:
                    print('abstract')

                class Concrete(Reader):
                    def read(self) -> int:
                        return 7

                class Virtual:
                    pass

                reader = Concrete()
                print(reader.read(), isinstance(reader, Reader), abc.ABCMeta is ABCMeta)
                Reader.register(Virtual)
                print(issubclass(Virtual, Reader), isinstance(Virtual(), Reader))
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
