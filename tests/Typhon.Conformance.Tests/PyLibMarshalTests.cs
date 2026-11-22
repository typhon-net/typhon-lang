// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibMarshalTests
{
    [Fact]
    public void PyLibPstatsCodeRecords()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibPstatsCodeRecords skipped: {reason}");
            return;
        }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) return;

        string directory = Directory.CreateTempSubdirectory("typhon-pstats-code-").FullName;
        try
        {
            string workload = Path.Combine(directory, "profile_payload.py");
            File.WriteAllText(workload, """
                def multiply(value: int) -> int:
                    return value * 3

                def workload() -> int:
                    return multiply(1) + multiply(2) + multiply(3)

                items = [0, 1, 2, 3, 4]
                literals = (1 + 2j, ..., items[1:4], 2 in {1, 2, 3})
                workload()
                """);
            string source = Path.Combine(directory, "profile.bin");
            var profile = Suite.Run(python, [Path.Combine(lib, "profile.py"), "-o", source, workload], TimeSpan.FromMinutes(1));
            Assert.True(profile.ExitCode == 0, profile.StdErr);

            string entry = Path.Combine(directory, "reads_pstats.ty");
            File.WriteAllText(entry, $"#:pylib {lib}\n" + """
                import inspect
                import marshal
                import pstats
                import sys

                original, output = sys.argv[1:]
                stats = pstats.Stats(original)
                for function, values in stats.stats.items():
                    if inspect.iscode(function[2]):
                        code = function[2]
                        print(type(code).__name__, code.co_name, code.co_qualname, code.co_firstlineno)
                        print(code.co_filename.endswith("profile_payload.py"), len(code.co_code), len(code.co_consts))
                    elif function[2] in ("workload", "multiply"):
                        print(function[2], values[0], values[1])
                try:
                    with open(original, "rb") as stream:
                        marshal.load(stream, allow_code=False)
                except ValueError as error:
                    print(str(error))
                try:
                    marshal.dumps(stats.stats, allow_code=False)
                except ValueError as error:
                    print(str(error))
                stats.dump_stats(output)
                """);
            string expectedOutput = Path.Combine(directory, "expected.bin");
            var expected = Suite.Run(python, [entry, source, expectedOutput], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            string actualOutput = Path.Combine(directory, "actual.bin");
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", entry, "--no-cache", "--no-server", source, actualOutput], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));

            const string compare = """
                import marshal, sys

                with open(sys.argv[1], 'rb') as before, open(sys.argv[2], 'rb') as after:
                    assert marshal.load(before) == marshal.load(after)
                """;
            var reread = Suite.Run(python, ["-c", compare, expectedOutput, actualOutput], TimeSpan.FromMinutes(1));
            Assert.True(reread.ExitCode == 0, reread.StdErr);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
