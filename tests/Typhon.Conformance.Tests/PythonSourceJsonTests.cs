// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceJsonTests
{
    [Fact]
    public void OriginalJsonPackage()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"OriginalJsonPackage skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("OriginalJsonPackage requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-json-").FullName;
        try
        {
            string package = Directory.CreateDirectory(Path.Combine(directory, "json")).FullName;
            foreach (string source in Directory.EnumerateFiles(Path.Combine(library, "json"), "*.py"))
                File.Copy(source, Path.Combine(package, Path.GetFileNameWithoutExtension(source) + ".ty"));

            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, $"#:pylib {library}\n" + """
                # typhon: int=bigint
                import json

                print(json.dumps(json.loads('{"numbers":[1,2],"yes":true}'), sort_keys=True))
                print(json.loads('[123456789012345678901234567890, 0.1, null]'))
                try:
                    json.loads('{\n  "a": }')
                except json.JSONDecodeError as error:
                    print(error.msg, error.lineno, error.colno)
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", probe], TimeSpan.FromMinutes(4));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
