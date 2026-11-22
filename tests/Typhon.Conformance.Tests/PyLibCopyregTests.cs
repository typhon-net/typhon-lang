// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibCopyregTests
{
    /// <summary>Run CPython's registered reducers and extension registry from its unmodified Lib source.</summary>
    [Fact]
    public void PyLibCopyreg()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibCopyreg skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibCopyreg requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-copyreg-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_copyreg.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import copyreg
                import types

                class Item:
                    def __init__(self, value: int):
                        self.value = value

                class Empty:
                    pass

                class Base:
                    def greet(self) -> str:
                        return "base"

                class Derived(Base):
                    pass

                u = int | str
                union_reducer = copyreg.dispatch_table[type(u)]
                rebuild, args = union_reducer(u)
                print(rebuild(args[0], args[1]) == u)
                s = super(Derived, Derived())
                super_reducer = copyreg.dispatch_table[type(s)]
                rebuild_super, sa = super_reducer(s)
                print(rebuild_super(sa[0], sa[1]).greet())
                complex_value = complex(2.0, -3.0)
                complex_reducer = copyreg.dispatch_table[type(complex_value)]
                rebuild_complex, ca = complex_reducer(complex_value)
                print(rebuild_complex(ca[0], ca[1]) == complex_value)

                def reduce_item(value):
                    return Item, (value.value,)

                copyreg.pickle(Item, reduce_item)
                item_factory, ia = copyreg.dispatch_table[Item](Item(42))
                print(item_factory(ia[0]).value)
                print(int.__new__(int), int.__new__(int, "10", 8), type(int.__new__) is types.BuiltinFunctionType)
                print(object.__getstate__(Item(7)), object.__getstate__(Empty()))
                print(type(Item(7)).__getstate__ is object.__getstate__)
                copyreg.add_extension("sample", "Item", 205)
                copyreg.remove_extension("sample", "Item", 205)
                print(copyreg._extension_registry)
                print(getattr(int, "__new__").__self__ is int)
                make_item, constructor_args, state = copyreg._reduce_ex(Item(13), 0)
                reconstructed = make_item(constructor_args[0], constructor_args[1], constructor_args[2])
                setattr(reconstructed, "value", state["value"])
                print(type(reconstructed).__name__, reconstructed.value)
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
