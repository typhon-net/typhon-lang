// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibShelveTests
{
    [Fact]
    public void PyLibShelve()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibShelve skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string stdlib = oracle.StdOut.Trim();
        if (stdlib.Length == 0) { Console.WriteLine("PyLibShelve requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-shelve-source-").FullName;
        try
        {
            string sources = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            File.Copy(Path.Combine(stdlib, "shelve.py"), Path.Combine(sources, "shelve.py"));
            string dbm = Directory.CreateDirectory(Path.Combine(sources, "dbm")).FullName;
            foreach (string name in new[] { "__init__.py", "dumb.py" })
                File.Copy(Path.Combine(stdlib, "dbm", name), Path.Combine(dbm, name));

            string cpythonData = Path.Combine(directory, "cpython-data");
            string typhonData = Path.Combine(directory, "typhon-data");
            string script = Path.Combine(directory, "shelf.ty");
            File.WriteAllText(script, $"#:pylib {sources}\n" + """
                import dbm.dumb
                import shelve
                import sys


                with shelve.Shelf(dbm.dumb.open(sys.argv[1], "n"), writeback=True) as data:
                    data["clé"] = [1, b"binary", {"enabled": True}]
                    data["empty"] = None
                    data["clé"].append("change")
                    data.sync()
                    print(data["clé"], len(data), data["empty"])


                with shelve.open(sys.argv[1], "w") as data:
                    print(data["clé"])
                    del data["empty"]
                    data["tuple"] = (2, "two")
                    print(sorted(data.keys()), "empty" in data)


                with shelve.open(sys.argv[2], "r") as data:
                    print(data["tuple"], len(data))
                """);

            var environment = new Dictionary<string, string> { ["PYTHONPATH"] = sources };
            var expected = Suite.Run(python, [script, cpythonData, cpythonData], TimeSpan.FromMinutes(1), environment);
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var result = Suite.Run("dotnet", [Suite.Cli, "run", script, typhonData, cpythonData, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(result.ExitCode == 0, result.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(result.StdOut));

            var readBack = Suite.Run(python, ["-c", """
                import dbm
                import shelve
                import sys

                target = sys.argv[1]
                assert dbm.whichdb(target) == "dbm.dumb"
                with shelve.open(target, "r") as data:
                    assert dict(data) == {"clé": [1, b"binary", {"enabled": True}, "change"], "tuple": (2, "two")}
                """, typhonData], TimeSpan.FromMinutes(1), environment);
            Assert.True(readBack.ExitCode == 0, readBack.StdErr);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
