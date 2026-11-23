// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceCollectionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalCollectionsMatchesPython(bool originalAbcs)
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PythonSourceCollections skipped: {reason}");
            return;
        }

        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("PythonSourceCollections requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-collections-").FullName;
        try
        {
            string package = Directory.CreateDirectory(Path.Combine(directory, "collections")).FullName;
            File.Copy(Path.Combine(library, "collections", "__init__.py"), Path.Combine(package, "__init__.ty"));
            if (originalAbcs)
            {
                foreach (string module in new[] { "abc", "_collections_abc" })
                    File.Copy(Path.Combine(library, module + ".py"), Path.Combine(directory, module + ".ty"));
            }
            string script = Path.Combine(directory, "probe.ty");
            string program = """
                # typhon: int=bigint
                from collections import ChainMap, Counter, OrderedDict, UserDict, namedtuple

                data = Counter('banana')
                print(sorted(data.items()), data.most_common(2), ''.join(sorted(data.elements())))
                data.update({'n': 1, 'a': 2})
                data.subtract('na')
                print(sorted(data.items()), data['missing'])

                ordered = OrderedDict([('b', 2), ('a', 1)])
                ordered.update({'c': 3}, d=4)
                ordered.move_to_end('a')
                print(list(ordered.items()), ordered.popitem(last=False))
                print(ordered == dict(ordered), ordered != dict(ordered), ordered != 2)

                point = namedtuple('Point', 'x y', defaults=(3,))
                print(point(8), point(1, 6)._replace(y=7))

                chain = ChainMap({'a': 1}, {'a': 2, 'b': 3})
                print(chain['a'], chain.get('b'), sorted(chain))
                bag = UserDict({'old': 1})
                bag |= UserDict({'new': 2})
                print(dict(bag))
                """;
            if (originalAbcs)
                program += "\n" + """


                    from collections import UserList, UserString
                    from _collections_abc import MutableMapping, Sequence

                    def mapping_pop(mapping: MutableMapping):
                        print(mapping.pop('missing', 'fallback'), mapping.pop('a'))
                        try:
                            mapping.pop('missing')
                        except KeyError:
                            print('missing key')

                    def sequence_index(sequence: Sequence):
                        print(sequence.index('a'), sequence.index('a', 1), sequence.index('b', 1, 3))

                    mapping_pop(chain)
                    sequence_index(UserList(['a', 'b', 'a']))
                    print(UserString('ababa').count('a', 1), UserString('ababa').endswith('ba'))
                    """;
            File.WriteAllText(script, $"#:pylib {library}\n" + program);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(4));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
