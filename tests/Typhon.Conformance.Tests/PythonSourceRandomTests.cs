// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceRandomTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomFromPythonSource(bool bigint)
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"RandomFromPythonSource skipped: {reason}"); return; }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0) { Console.WriteLine("RandomFromPythonSource requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-random-").FullName;
        try
        {
            File.Copy(Path.Combine(library, "random.py"), Path.Combine(directory, "random.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, bigint ? """
                import random

                for value in ("tiger", b"tiger", bytearray(b"tiger")):
                    rng = random.Random(value)
                    print(rng.random(), rng.randbytes(12))
                rng = random.Random()
                rng.seed("tiger", version=1)
                print(rng.random(), rng.getrandbits(65))
                """ : """
                import random

                rng = random.Random(42)
                print(rng.random(), rng.getrandbits(5), rng.randbytes(2), rng.randrange(10, 50, 3))
                checkpoint = rng.getstate()
                print(checkpoint[0], len(checkpoint[1]), checkpoint[2])
                values = [1, 2, 3, 4, 5]
                rng.shuffle(values)
                print(values, rng.sample(range(20), 4))
                rng.setstate(checkpoint)
                repeated = [1, 2, 3, 4, 5]
                rng.shuffle(repeated)
                print(values == repeated, rng.sample(range(20), 4))
                print(random.Random(-31).random() == random.Random(31).random())

                class Fixed(random.Random):
                    def random(self) -> float:
                        return 0.125

                fixed = Fixed(42)
                print(fixed._randbelow.__name__, fixed.randrange(8))
                secure = random.SystemRandom()
                draw = secure.random()
                print(0 <= draw < 1, len(secure.randbytes(16)), secure.getrandbits(0))
                print(secure.seed("ignored", supplied=True) is None)
                try:
                    secure.getstate()
                except NotImplementedError as error:
                    print(str(error))
                random.seed(42)
                print(random.random(), random.getrandbits(8))
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            string[] arguments = [Suite.Cli, "run", probe, "--no-cache", "--no-server"];
            if (bigint) arguments = [..arguments, "--bigint"];
            var actual = Suite.Run("dotnet", arguments, TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
