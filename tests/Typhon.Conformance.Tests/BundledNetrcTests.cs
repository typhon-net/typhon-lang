// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class BundledNetrcTests
{
    [Fact]
    public void ReadsBundledNetrcWithoutExternalLibrary()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"CPython netrc tests skipped: {reason}"); return; }
        string directory = Directory.CreateTempSubdirectory("typhon-netrc-").FullName;
        try
        {
            string input = Path.Combine(directory, "fixture.txt");
            File.WriteAllText(input, """
                # Test data
                machine example.test login example account test password fixture-only
                default login anonymous password fallback
                macdef log
                line 1
                line 2


                """);
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, """
                import netrc
                import sys

                record = netrc.netrc(sys.argv[1])
                print(record.authenticators("example.test"))
                print(record.authenticators("unknown.test"))
                print(record.macros["log"])
                """);

            var expected = Suite.Run(python, ["-I", script, input], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", script, input], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
