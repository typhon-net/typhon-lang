// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceDifflibTests
{
    [Fact]
    public void OriginalDifflibModule()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"OriginalDifflibModule skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("OriginalDifflibModule requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-difflib-").FullName;
        try
        {
            File.Copy(Path.Combine(library, "difflib.py"), Path.Combine(directory, "difflib.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import difflib

                matcher = difflib.SequenceMatcher(None, 'abracadabra', 'alakazam')
                print(matcher.get_matching_blocks(), matcher.get_opcodes())
                print(list(difflib.unified_diff(['a\n', 'b\n'], ['a\n', 'c\n'], fromfile='before', tofile='after')))
                print(list(difflib.ndiff(['a\n', 'b\n'], ['a\n', 'c\n'])))
                print(difflib.get_close_matches('alpha', ['alpha', 'aloha', 'beta']))
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
