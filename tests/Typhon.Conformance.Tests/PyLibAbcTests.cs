// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibAbcTests
{
    [Fact]
    public void OriginalAbc()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibAbc skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string stdlib = oracle.StdOut.Trim();
        if (stdlib.Length == 0) { Console.WriteLine("PyLibAbc requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-abc-").FullName;
        try
        {
            // Import the unmodified abc source under an alias to exercise the source implementation.
            string library = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            File.Copy(Path.Combine(stdlib, "abc.py"), Path.Combine(library, "stock_abc.py"));
            string script = Path.Combine(directory, "uses_abc.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                import stock_abc as abc

                class Contract(abc.ABC):
                    @abc.abstractmethod
                    def read(self):
                        return "abstract base"

                class Implemented(Contract):
                    def read(self):
                        return "implemented"

                class Virtual:
                    pass

                class DerivedVirtual(Virtual):
                    pass

                print(sorted(Contract.__abstractmethods__), sorted(Implemented.__abstractmethods__))
                try:
                    Contract()
                except TypeError as error:
                    print("abstract", "read" in str(error))
                print(Implemented().read(), issubclass(Implemented, Contract), isinstance(Implemented(), Contract))
                before = abc.get_cache_token()
                print(issubclass(Virtual, Contract), isinstance(Virtual(), Contract))
                print(Contract.register(Virtual) is Virtual, before != abc.get_cache_token())
                registered = abc.get_cache_token()
                print(issubclass(DerivedVirtual, Contract), isinstance(Virtual(), Contract))
                Contract.register(Virtual)
                print(registered == abc.get_cache_token())
                Contract._abc_registry_clear()
                Contract._abc_caches_clear()
                print(issubclass(Virtual, Contract), registered == abc.get_cache_token())

                def legacy(receiver):
                    return receiver.__name__

                descriptor = abc.abstractclassmethod(legacy)
                print(getattr(descriptor, "__isabstractmethod__"), getattr(legacy, "__isabstractmethod__"))
                print(descriptor.__get__(None, Implemented)())

                class Structural(abc.ABC):
                    @classmethod
                    def __subclasshook__(cls, candidate):
                        if cls is Structural and hasattr(candidate, "read"):
                            return True
                        return NotImplemented

                print(issubclass(Implemented, Structural), isinstance(Implemented(), Structural))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1),
                new Dictionary<string, string> { ["PYTHONPATH"] = library });
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
