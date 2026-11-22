// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibDecimalTests
{
    [Fact]
    public void PyLibDecimalFallback()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibDecimalFallback skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibDecimalFallback requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-decimal-").FullName;
        try
        {
            string script = Path.Combine(directory, "decimal_probe.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import decimal
                from decimal import Decimal, localcontext, ROUND_HALF_UP
                import sys

                print(decimal is sys.modules['decimal'], decimal.__name__, Decimal.__module__)
                print(Decimal('1.33') + Decimal('1.27'))
                print(Decimal((0, (3, 1, 4), -2)))
                print(Decimal('3.14159').quantize(Decimal('0.01'), rounding=ROUND_HALF_UP))
                with localcontext() as context:
                    context.prec = 9
                    print(Decimal(1) / Decimal(7))
                print(Decimal('123456789123456789123456789') + Decimal(1))
                print(Decimal('4.50').as_integer_ratio())
                print(format(Decimal('-1234.125'), ',.2f'))
                print(pow(Decimal(2), Decimal(7), Decimal(13)))
                """);
            var expected = Suite.Run(python,
                ["-c", "import runpy, sys; sys.modules['_decimal'] = None; runpy.run_path(sys.argv[1], run_name='__main__')", script],
                TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--bigint", "--no-cache", "--no-server"],
                TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
