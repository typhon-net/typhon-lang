// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceOpcodeTests
{
    static string? Python315(out string lib)
    {
        lib = "";
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"CPython opcode tests skipped: {reason}");
            return null;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        lib = stdlib.StdOut.Trim();
        if (lib.Length == 0) Console.WriteLine("CPython opcode tests require the CPython 3.15 library");
        return lib.Length == 0 ? null : python;
    }

    [Fact]
    public void PythonSourceOpcodeMetadata()
    {
        string? python = Python315(out string lib);
        if (python == null) return;

        string directory = Directory.CreateTempSubdirectory("typhon-opcode-").FullName;
        try
        {
            string source = Path.Combine(lib, "opcode.py");
            File.Copy(source, Path.Combine(directory, "stock_opcode.py"));
            File.Copy(source, Path.Combine(directory, "stock_opcode.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            var script = """
                import stock_opcode as opcode

                for name in ("LOAD_CONST", "LOAD_GLOBAL", "BINARY_OP", "FOR_ITER", "RETURN_VALUE"):
                    number = opcode.opmap[name]
                    print(name, number, opcode.opname[number], number in opcode.hasarg)
                print(opcode.stack_effect(opcode.opmap["BUILD_LIST"], 3), opcode.stack_effect(opcode.opmap["LOAD_GLOBAL"], 1))
                print(opcode.stack_effect(opcode.opmap["UNPACK_EX"], 2 | (3 << 8)), opcode.stack_effect(opcode.opmap["FOR_ITER"], 7, jump=True))
                print(opcode.EXTENDED_ARG, opcode.opmap["JUMP_FORWARD"] in opcode.hasjump, len(opcode._inline_cache_entries))
                try:
                    opcode.stack_effect(999)
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

    [Fact]
    public void PythonSourceOpcodeMatchesManagedApi()
    {
        string? python = Python315(out string lib);
        if (python == null) return;

        string directory = Directory.CreateTempSubdirectory("typhon-opcode-backends-").FullName;
        try
        {
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + File.ReadAllText(Suite.CasePath("native_opcode_api")));
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CPythonMarshalCodeLocations()
    {
        string? python = Python315(out string lib);
        if (python == null) return;

        string directory = Directory.CreateTempSubdirectory("typhon-code-locations-").FullName;
        try
        {
            string generate = Path.Combine(directory, "generate.py");
            File.WriteAllText(generate, """
                import marshal
                import sys

                def sample(value):
                    try:
                        total = (
                            value * 200
                            + value ** 3
                        )
                        very_long_identifier_reserved_for_extended_source_columns = total + value + 1
                        return very_long_identifier_reserved_for_extended_source_columns
                    except ValueError:
                        return -1

                with open(sys.argv[1], "wb") as fixture:
                    marshal.dump(sample.__code__, fixture)
                """);
            string detailed = Path.Combine(directory, "with-columns.bin");
            string linesOnly = Path.Combine(directory, "no-columns.bin");
            var withColumns = Suite.Run(python, [generate, detailed], TimeSpan.FromMinutes(1));
            Assert.True(withColumns.ExitCode == 0, withColumns.StdErr);
            var withoutColumns = Suite.Run(python, ["-X", "no_debug_ranges", generate, linesOnly], TimeSpan.FromMinutes(1));
            Assert.True(withoutColumns.ExitCode == 0, withoutColumns.StdErr);

            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import dis
                import marshal
                import sys

                for path in sys.argv[1:]:
                    with open(path, "rb") as fixture:
                        code = marshal.load(fixture)
                    print(code.co_name, code.co_firstlineno)
                    print(list(code.co_lines()))
                    print(list(code.co_positions()))
                    print(list(dis.findlinestarts(code)))
                """);
            var expected = Suite.Run(python, [probe, detailed, linesOnly], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", probe, detailed, linesOnly], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));

            foreach (string module in new[] { "dis", "opcode", "_opcode_metadata" })
                File.Copy(Path.Combine(lib, module + ".py"), Path.Combine(directory, module + ".ty"));

            string disProbe = Path.Combine(directory, "original_dis.ty");
            File.WriteAllText(disProbe, """
                import dis
                import marshal
                import sys

                with open(sys.argv[1], "rb") as fixture:
                    code = marshal.load(fixture)
                print(code.co_name, len(code.co_code), len(list(dis.findlinestarts(code))))
                print(dis.findlabels(code.co_code))
                print([(item.opname, item.offset, item.argrepr) for item in dis.get_instructions(code)][:12])
                print(dis.code_info(code).splitlines()[-3:])
                """);
            var expectedDis = Suite.Run(python, [disProbe, detailed], TimeSpan.FromMinutes(1));
            Assert.True(expectedDis.ExitCode == 0, expectedDis.StdErr);
            var actualDis = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", "--bigint",
                disProbe, detailed], TimeSpan.FromMinutes(3));
            Assert.True(actualDis.ExitCode == 0, actualDis.StdErr);
            Assert.Equal(Suite.Normalize(expectedDis.StdOut), Suite.Normalize(actualDis.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
