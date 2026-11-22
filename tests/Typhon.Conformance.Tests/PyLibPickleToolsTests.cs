// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibPickleToolsTests
{
    [Fact]
    public void PyLibPickleTools()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibPickleTools skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string stdlib = oracle.StdOut.Trim();
        if (stdlib.Length == 0) { Console.WriteLine("PyLibPickleTools requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-pickletools-source-").FullName;
        try
        {
            string sources = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            File.Copy(Path.Combine(stdlib, "pickletools.py"), Path.Combine(sources, "pickletools.py"));

            string input = Path.Combine(directory, "input.pkl");
            var fixture = Suite.Run(python, ["-c", """
                import pickle
                import sys

                shared = ["é", -4294967297, b"bytes"]
                with open(sys.argv[1], "wb") as out:
                    pickle.dump({"first": shared, "second": shared}, out, protocol=5)
                """, input], TimeSpan.FromSeconds(30));
            Assert.True(fixture.ExitCode == 0, fixture.StdErr);

            string script = Path.Combine(directory, "records.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                import io
                import pickle
                import pickletools
                import sys

                with open(sys.argv[1], "rb") as source:
                    record = source.read()

                for opcode, argument, position in pickletools.genops(record):
                    print(opcode.name, repr(argument), position)

                optimized = pickletools.optimize(record)
                recovered = pickle.loads(optimized)
                print("optimized", len(record), len(optimized), recovered == pickle.loads(record))
                assert recovered["first"] is recovered["second"]

                out = io.StringIO()
                pickletools.dis(optimized, out=out)
                print(out.getvalue(), end="")

                for opcode, argument, position in pickletools.genops(b"S'line\\nnext'\n."):
                    print("legacy", opcode.name, repr(argument), position)
                """);

            var environment = new Dictionary<string, string>
            {
                ["PYTHONPATH"] = sources,
                ["NO_COLOR"] = "1",
                ["PYTHON_COLORS"] = "0",
            };
            var expected = Suite.Run(python, [script, input], TimeSpan.FromMinutes(1), environment);
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var result = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", script, input],
                TimeSpan.FromMinutes(3), environment);
            Assert.True(result.ExitCode == 0, result.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(result.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
