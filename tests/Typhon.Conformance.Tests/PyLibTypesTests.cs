// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTypesTests
{
    [Fact]
    public void PyLibTypes()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTypes skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTypes requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-types-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_types.ty");
            const string source = """
                import types
                from types import FunctionType, MappingProxyType

                print(types.__file__.endswith((".ty", ".py")))

                print(types.LambdaType is FunctionType, types.BuiltinMethodType is types.BuiltinFunctionType)
                def plain():
                    return 4

                def iter_values():
                    yield 8

                async def async_work():
                    return 9

                async def async_values():
                    yield 10

                print(type(plain) is FunctionType, type(plain.__code__) is types.CodeType)
                print(type(type.__dict__) is MappingProxyType, type(None) is types.NoneType)
                print(type(iter_values()) is types.GeneratorType, isinstance(iter_values(), types.GeneratorType))
                coro = async_work()
                print(type(coro) is types.CoroutineType, isinstance(coro, types.CoroutineType))
                try:
                    coro.send(None)
                except StopIteration as e:
                    print(e.value)
                ag = async_values()
                print(type(ag) is types.AsyncGeneratorType, isinstance(ag, types.AsyncGeneratorType))

                print("GeneratorType" in types.__all__, "CellType" in types.__all__, "new_class" in types.__all__)
                print(types.SimpleNamespace(x=2))
                bases = (object,)
                print(types.resolve_bases(bases) is bases)
                class Placeholder:
                    def __mro_entries__(self, bases):
                        return (object,)
                print(types.resolve_bases((Placeholder(),)) == (object,))

                class Factory:
                    def __prepare__(self, name, bases, **kwargs):
                        return {"name": name, "option": kwargs.get("option")}
                    def __call__(self, name, bases, ns, **kwargs):
                        return ns["name"], ns["option"]
                factory = Factory()
                meta, namespace, keywords = types.prepare_class("Example", (), {"metaclass": factory, "option": 3})
                print(meta is factory, namespace["name"], namespace["option"], keywords["option"])
                print(types.new_class("Created", (), {"metaclass": factory, "option": 5}))
                """;
            File.WriteAllText(script, source);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            File.Copy(Path.Combine(lib, "types.py"), Path.Combine(dir, "types.ty"));
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
