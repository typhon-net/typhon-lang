// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceTypingTests
{
    [Fact]
    public void OriginalTypingModule()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"OriginalTypingModule skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("OriginalTypingModule requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-typing-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "collections"));
            foreach (string module in new[] { "typing", "abc", "annotationlib", "_collections_abc" })
                File.Copy(Path.Combine(library, module + ".py"), Path.Combine(directory, module + ".ty"));
            // CPython exposes its _collections_abc source under both import names.
            File.Copy(Path.Combine(library, "_collections_abc.py"), Path.Combine(directory, "collections", "abc.ty"));

            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import typing as t

                class Payload:
                    pass

                def decode(item: 'Payload | None') -> t.Annotated[list[Payload], 'wire']:
                    return []

                hints = t.get_type_hints(decode, include_extras=True)
                print(hints['item'], hints['return'])
                print(t.get_args(hints['item']))
                print(t.get_origin(hints['return']).__name__)
                print(t.get_args(t.Optional[int]))
                T = t.TypeVar('T', bound=int, default=bool)
                print(T, T.__bound__, T.__default__, T.has_default())
                P = t.ParamSpec('P')
                print(P.args, P.kwargs, t.get_origin(P.args) is P)
                print(t.ForwardRef('Payload').evaluate(globals=globals()) is Payload)

                @t.runtime_checkable
                class Readable(t.Protocol):
                    def read(self) -> int: ...

                class Reader:
                    def read(self) -> int:
                        return 1

                print(t.is_protocol(Readable), t.get_protocol_members(Readable), isinstance(Reader(), Readable))
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--bigint", "--no-cache", "--no-server", probe], TimeSpan.FromMinutes(4));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
