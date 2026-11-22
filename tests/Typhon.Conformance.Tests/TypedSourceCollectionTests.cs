// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class TypedSourceCollectionTests
{
    [Fact]
    public void CollectsTypedValuesFromAnotherModule()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"TypedSourceCollections skipped: {reason}");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-collections-").FullName;
        try
        {
            const string module = """
                class Item:
                    def __init__(self, label: str):
                        self.label = label

                class Store:
                    def __init__(self):
                        self.items: list[Item] = [Item("beta"), Item("alpha")]

                names: dict[str, int] = {"alpha": 1, "beta": 2}

                class Form:
                    def __getitem__(self, argument):
                        return "subscripted", argument.__name__

                Optional = Form()
                """;
            File.WriteAllText(Path.Combine(directory, "data.py"), module);
            File.WriteAllText(Path.Combine(directory, "data.ty"), module);
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, """
                import data as t
                from data import Item, Store, names, Optional

                class View:
                    def __init__(self):
                        self.store: Store = Store()

                    @property
                    def loaded(self) -> list[Item]:
                        return self.store.items

                    def first(self) -> list[Item]:
                        return [value for value in self.store.items]

                    def labels(self) -> list[str]:
                        return [value.label for value in self.loaded]

                def keys() -> list[str]:
                    return list(names)

                view = View()
                print([item.label for item in view.first()])
                print(view.labels(), keys())
                print(t.Optional[int], Optional[str])
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
