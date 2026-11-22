// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceWeakrefTests
{
    [Fact]
    public void WeakrefFromPythonSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"WeakrefFromPythonSource skipped: {reason}"); return; }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0) { Console.WriteLine("WeakrefFromPythonSource requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-weakref-").FullName;
        try
        {
            string source = Path.Combine(library, "weakref.py");
            File.Copy(source, Path.Combine(directory, "stock_weakref.py"));
            File.Copy(source, Path.Combine(directory, "stock_weakref.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import stock_weakref as weakref

                class Node:
                    def __init__(self, name: str):
                        self.name = name

                    def label(self) -> str:
                        return self.name

                key, value = Node("key"), Node("value")
                ref = weakref.ref(key)
                print(ref() is key, hash(ref) == hash(key))
                tagged = weakref.KeyedRef(key, None, "tag")
                print(tagged() is key, tagged.key, tagged == ref)
                method = weakref.WeakMethod(value.label)
                print(method()(), method == weakref.WeakMethod(value.label))
                values = weakref.WeakValueDictionary()
                values["item"] = value
                print(len(values), values["item"] is value, list(values.keys()))
                keys = weakref.WeakKeyDictionary()
                keys[key] = "payload"
                print(len(keys), keys[key], next(iter(keys)) is key)
                calls: list[str] = []
                def record(message: str):
                    calls.append(message)

                cleanup = weakref.finalize(value, record, "done")
                print(cleanup.alive, cleanup(), cleanup.alive, calls)
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", probe, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
