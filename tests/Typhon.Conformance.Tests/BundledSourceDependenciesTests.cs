// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class BundledSourceDependenciesTests
{
    [Fact]
    public void OriginalProfileUsesBundledPstats() => CompareOriginal("profile", """
        import profile
        import pstats

        def add(x, y):
            return x + y

        profiler = profile.Profile()
        print(profiler.runcall(add, 2, 3))
        profiler.create_stats()
        print(any(key[2] == "add" for key in profiler.stats))
        report = pstats.Stats(profiler).strip_dirs().sort_stats("name")
        print(report.total_calls > 0, any(key[2] == "add" for key in report.stats))
        """);

    [Fact]
    public void OriginalSocketServerUsesBundledSelectors() => CompareOriginal("socketserver", """
        import socket
        import socketserver

        class Handler(socketserver.BaseRequestHandler):
            def handle(self):
                message = self.request.recv(128)
                self.request.sendall(message.upper())

        with socketserver.TCPServer(("127.0.0.1", 0), Handler) as server:
            server.timeout = 4
            with socket.create_connection(server.server_address, timeout=4) as client:
                client.sendall(b"typhon")
                server.handle_request()
                print(client.recv(128))
            print(server.server_address[0])
        """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalInspectReadsCompiledSource(bool postponed) => CompareOriginal("inspect",
        (postponed ? "from __future__ import annotations\n" : "") + """
        import inspect
        from annotationlib import Format

        def compute(x: int, /, label: str = "a", *, enabled: bool = True) -> str:
            return label

        signature = inspect.signature(compute)
        print(signature)
        print(signature.bind(7, enabled=False))
        print(inspect.get_annotations(compute, format=Format.STRING))
        print(inspect.isfunction(compute), inspect.ismethod(compute), inspect.isroutine(compute))
        print(inspect.getsource(compute).splitlines()[0])
        """);

    static void CompareOriginal(string module, string source)
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"Original library tests skipped: {reason}"); return; }
        var stdlib = Suite.Run(python,
            ["-I", "-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("Original library tests require the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory($"typhon-{module}-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, module + ".py"), Path.Combine(directory, module + ".ty"));
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, source);
            var expected = Suite.Run(python, ["-I", script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
