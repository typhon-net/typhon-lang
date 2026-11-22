// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibLocaleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PyLibLocaleSource(bool bigint)
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibLocaleSource skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibLocaleSource requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-locale-").FullName;
        try
        {
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import locale

                print(locale.setlocale(locale.LC_ALL, 'C'))
                print(locale.getlocale())
                for name in ('C', 'POSIX', 'EN_us.UTF-8', 'de_DE.utf8', 'sr_RS.UTF-8@latin', 'ja_JP.SJIS', 'ru_RU.KOI8-R', 'unknown.locale', 'ja_JP.sjis_2004', 'fr_FR:Latin-1@euro'):
                    print(locale.normalize(name))
                print(locale.format_string('%d', 1234, grouping=True))
                print(locale.format_string('%s %.2f %%', ('sample', 12.345)))
                print(locale.format_string('%(qty)d %(name)s', {'qty': 3, 'name': 'x'}))
                print(locale.format_string('%*.*f', (8, 2, 12.5)))
                print(locale.atof('12.5'), locale.atoi('12'))
                locale._override_localeconv = {'grouping': [3, 0], 'thousands_sep': '_', 'decimal_point': ','}
                print(locale.format_string('%.2f', 12345.75, grouping=True))
                print(locale.delocalize('12_345,75'), locale.atof('12_345,75'))
                try:
                    locale.currency(12)
                except ValueError as error:
                    print(str(error))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            string[] arguments = [Suite.Cli, "run", script, "--no-cache", "--no-server"];
            if (bigint) arguments = [.. arguments, "--bigint"];
            var actual = Suite.Run("dotnet", arguments, TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
