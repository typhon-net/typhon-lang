// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceCollectionsAbcTests
{
    [Fact]
    public void CollectionsAbcFromPythonSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"CollectionsAbcFromPythonSource skipped: {reason}");
            return;
        }

        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("CollectionsAbcFromPythonSource requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-collections-abc-").FullName;
        try
        {
            foreach (string module in new[] { "_collections_abc", "abc", "annotationlib" })
                File.Copy(Path.Combine(library, module + ".py"), Path.Combine(directory, module + ".ty"));

            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                from _collections_abc import Callable, Collection, Iterable, Iterator, Mapping, Sequence

                for owner in (Callable, Collection, Iterable, Iterator, Mapping, Sequence):
                    print(owner.__name__, isinstance([], owner), isinstance(iter([]), owner))

                from _collections_abc import Callable, Generator, Mapping, MutableSequence

                class Table(Mapping):
                    def __init__(self):
                        self.data = {"name": "Typhon", "kind": "compiler"}

                    def __getitem__(self, key):
                        return self.data[key]

                    def __len__(self):
                        return len(self.data)

                    def __iter__(self):
                        return iter(self.data)

                table = Table()
                print(isinstance({}, Mapping), isinstance([], MutableSequence))
                print(isinstance(lambda: None, Callable), isinstance((x for x in [1]), Generator))
                print(list(table.keys()), list(table.values()), list(table.items()))
                print(table.get("name"), table.get("absent", "default"))
                print("name" in table.keys(), ("kind", "compiler") in table.items())
                """);

            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", "--bigint", probe],
                TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
