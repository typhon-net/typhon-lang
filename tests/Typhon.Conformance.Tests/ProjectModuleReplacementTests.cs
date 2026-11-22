// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class ProjectModuleReplacementTests
{
    [Fact]
    public void SourceModuleCanReplaceItsRegistryEntry()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"Module replacement comparison skipped: {why}"); return; }
        string directory = Directory.CreateTempSubdirectory("typhon-module-replacement-").FullName;
        try
        {
            void Define(string name, string code)
            {
                File.WriteAllText(Path.Combine(directory, name + ".ty"), code);
                File.WriteAllText(Path.Combine(directory, name + ".py"), code);
            }
            Define("backend", """
                '''Backend documentation.'''
                def value():
                    return 'backend'
                """);
            Define("facade", """
                import sys as system
                import backend

                def value():
                    return 'facade'

                system.modules[__name__] = backend
                """);
            Define("forwarded", """
                from sys import modules as registry
                import backend
                registry[__name__] = backend
                """);
            string script = Path.Combine(directory, "module_replacement_probe.ty");
            File.WriteAllText(script, """
                from facade import value
                from forwarded import value as forwarded_value
                import backend
                import facade
                import forwarded
                import sys

                print(value(), facade.value(), forwarded_value())
                print(facade is backend, forwarded is backend, sys.modules['facade'] is backend)
                print(facade.__name__, facade.__doc__)
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
