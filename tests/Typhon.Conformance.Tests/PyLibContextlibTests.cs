// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibContextlibTests
{
    [Fact]
    public void PyLibContextlib()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibContextlib skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string stdlib = oracle.StdOut.Trim();
        if (stdlib.Length == 0) { Console.WriteLine("PyLibContextlib requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-contextlib-").FullName;
        try
        {
            // An alias allows #:pylib to find the unmodified source beside Typhon's runtime contextlib.
            string sourceLibrary = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            File.Copy(Path.Combine(stdlib, "contextlib.py"), Path.Combine(sourceLibrary, "stock_contextlib.py"));
            string script = Path.Combine(directory, "uses_contextlib.ty");
            File.WriteAllText(script, $"#:pylib {sourceLibrary}\n" + """
                import asyncio
                import stock_contextlib as ctx

                print(ctx.__file__ is not None)


                @ctx.contextmanager
                def managed(tag):
                    print("enter", tag)
                    try:
                        yield tag.upper()
                    except ValueError as error:
                        print("suppressed", error)
                    finally:
                        print("exit", tag)


                @ctx.asynccontextmanager
                async def async_managed(tag):
                    print("async enter", tag)
                    try:
                        await asyncio.sleep(0)
                        yield tag.upper()
                    except ValueError as error:
                        print("async suppressed", error)
                    finally:
                        print("async exit", tag)


                class Manager:
                    def __enter__(self):
                        print("class enter")
                        return 5

                    def __exit__(self, *details):
                        print("class exit", details[0] is None)


                with ctx.nullcontext(3) as value:
                    print("null", value)
                with ctx.suppress(ValueError):
                    raise ValueError("hidden")
                print("continued")
                with managed("plain") as value:
                    print("body", value)
                with managed("error") as value:
                    print("body", value)
                    raise ValueError("inside")
                with ctx.ExitStack() as stack:
                    print(stack.enter_context(Manager()))
                    print(stack.enter_context(managed("stack")))
                    stack.callback(print, "callback")


                async def main():
                    async with async_managed("normal") as value:
                        print("async body", value)
                    async with async_managed("error") as value:
                        print("async body", value)
                        raise ValueError("async inside")
                    async with ctx.AsyncExitStack() as stack:
                        print(await stack.enter_async_context(async_managed("stack")))
                        stack.callback(print, "async callback")


                asyncio.run(main())
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1),
                new Dictionary<string, string> { ["PYTHONPATH"] = sourceLibrary });
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
