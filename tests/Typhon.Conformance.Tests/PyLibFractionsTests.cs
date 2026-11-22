// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibFractionsTests
{
    [Fact]
    public void PyLibFractionsArithmetic()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibFractionsArithmetic skipped: {reason}");
            return;
        }

        var stdlib = Suite.Run(python,
            ["-I", "-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibFractionsArithmetic requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-fractions-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, "fractions.py"), Path.Combine(directory, "fractions.ty"));
            string dependencies = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            foreach (string module in new[] { "decimal.py", "_pydecimal.py" })
                File.Copy(Path.Combine(lib, module), Path.Combine(dependencies, module));

            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                from fractions import Fraction

                left = Fraction('1/7')
                right = Fraction('3/8')
                print(left + right, left - right, left * right, left / right)
                print(1 + left, 1 - left, 2 * left, 2 / left)
                print(Fraction('3/4') ** 3)
                print(format(Fraction(7, 3), '.6f'))
                print(divmod(Fraction(17, 3), Fraction(5, 2)))
                print(left == Fraction(2, 14), left < right)
                """);
            var expected = Suite.Run(python, ["-I", script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--bigint", "--no-cache", "--no-server"],
                TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
