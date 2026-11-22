// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibPprintTests
{
    [Fact]
    public void PyLibPprintSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibPprintSource skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibPprintSource requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-pprint-").FullName;
        try
        {
            string source = Path.Combine(lib, "pprint.py");
            File.Copy(source, Path.Combine(directory, "stock_pprint.py"));
            File.Copy(source, Path.Combine(directory, "stock_pprint.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                from dataclasses import dataclass, field
                import stock_pprint

                data = {"zulu": [1, 2, 3], "alpha": {"count": 1234567}}
                print(stock_pprint.pformat(data, width=22, underscore_numbers=True))
                print(stock_pprint.pformat([["alpha", "beta"], ["gamma", "delta"]], width=22, compact=True))
                print(stock_pprint.pformat({17, 1, 9}, width=10))
                print(stock_pprint.pformat(data.items(), width=27))
                print(stock_pprint.isreadable(data), stock_pprint.isrecursive(data))
                print(stock_pprint.saferepr(({"k": 7}, ["leaf"])))
                cycle = []
                cycle.append(cycle)
                print(stock_pprint.isrecursive(cycle), stock_pprint.isreadable(cycle), "Recursion on list" in stock_pprint.saferepr(cycle))

                @dataclass
                class Record:
                    name: str
                    values: list[int]
                    hidden: int = field(repr=False)

                print(stock_pprint.pformat(Record("description", [4, 3, 2, 1], 11), width=20))
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
