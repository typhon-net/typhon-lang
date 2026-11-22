// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class ProjectClassImportTests
{
    [Fact]
    public void ExplicitPythonClassImportWinsOverClrName()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"Project class import comparison skipped: {why}"); return; }
        string directory = Directory.CreateTempSubdirectory("typhon-class-import-").FullName;
        try
        {
            const string exported = """
                class Decimal:
                    __slots__ = ("value",)

                    def __new__(cls, value: int):
                        instance = object.__new__(cls)
                        instance.value = value
                        return instance
                """;
            File.WriteAllText(Path.Combine(directory, "arithmetic_objects.py"), exported);
            File.WriteAllText(Path.Combine(directory, "arithmetic_objects.ty"), exported);
            string script = Path.Combine(directory, "class_import_probe.ty");
            File.WriteAllText(script, """
                from arithmetic_objects import Decimal

                def check(value: Decimal):
                    return isinstance(value, Decimal), value.value

                print(check(Decimal(37)))
                print(Decimal.__module__, Decimal.__name__)
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(2));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
