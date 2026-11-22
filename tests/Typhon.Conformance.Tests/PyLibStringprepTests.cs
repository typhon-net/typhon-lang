// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibStringprepTests
{
    [Fact]
    public void PyLibStringprep()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibStringprep skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibStringprep requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-stringprep-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_stringprep.ty");
            const string source = """
                import stringprep

                for point in [0x20, 0xa0, 0x200b, 0x5d0, 0x661, 0x61, 0xfdd0, 0xe000, 0x1f600]:
                    c = chr(point)
                    print(hex(point), stringprep.in_table_a1(c), stringprep.in_table_b1(c),
                          stringprep.in_table_c11_c12(c), stringprep.in_table_c3(c),
                          stringprep.in_table_c4(c), stringprep.in_table_d1(c), stringprep.in_table_d2(c))

                for c in ["ß", "K", "ℌ", "Ⅰ", "①", "Ⅷ", "Ａ", "b", "ŉ", "Σ", "ᾈ", chr(0x2f868)]:
                    print(ascii(c), ascii(stringprep.map_table_b2(c)), ascii(stringprep.map_table_b3(c)))
                """;
            File.WriteAllText(script, source);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            File.WriteAllText(script, $"#:pylib {lib}\n" + source);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
