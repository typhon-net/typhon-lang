// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceGettextTests
{
    [Fact]
    public void PythonSourceGettextCatalogues()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PythonSourceGettextCatalogues skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PythonSourceGettextCatalogues requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-gettext-").FullName;
        try
        {
            string source = Path.Combine(lib, "gettext.py");
            File.Copy(source, Path.Combine(directory, "stock_gettext.py"));
            File.Copy(source, Path.Combine(directory, "stock_gettext.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            string script = File.ReadAllText(Suite.CasePath("gettext_catalogues")).ReplaceLineEndings("\n")
                .Replace("import gettext\n", "import stock_gettext as gettext\n");
            script += "\n" + """
                import builtins
                direct.install(["pgettext", "ngettext"])
                print(getattr(builtins, "_")("Hello"), getattr(builtins, "pgettext")("menu", "Open"))
                print(getattr(builtins, "ngettext")("file", "files", 2))

                formula = gettext.c2py("(n%10==1&&n%100!=11)?0:(n>1?1:2)")
                print([formula(value) for value in (0, 1, 2, 11, 21, 22)])
                try:
                    gettext.c2py("n ? 1")
                except ValueError as error:
                    print(str(error))
                """;
            File.WriteAllText(probe, script);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            File.WriteAllText(probe, $"#:pylib {lib}\n" + script);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", probe, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
