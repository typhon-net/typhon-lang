// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibSocketserverTests
{
    [Fact]
    public void InheritedClassMembersAcrossModules()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"InheritedClassMembersAcrossModules skipped: {reason}");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-inherited-members-").FullName;
        try
        {
            const string ancestor = """
                class Original:
                    def __init__(self, request: str, code: int = 4):
                        self.request = request
                        self.code = code
                    def render(self):
                        print("original")
                    def show(self):
                        self.render()
                """;
            const string intermediate = """
                from ancestor import Original as ImportedOriginal

                class Intermediate(ImportedOriginal):
                    pass
                """;
            foreach (var (name, source) in new[] { ("ancestor", ancestor), ("intermediate", intermediate) })
            {
                File.WriteAllText(Path.Combine(directory, name + ".py"), source);
                File.WriteAllText(Path.Combine(directory, name + ".ty"), source);
            }
            string probe = Path.Combine(directory, "inherited.ty");
            File.WriteAllText(probe, """
                import intermediate as shared

                class ImportedOriginal:
                    def __init__(self):
                        raise RuntimeError("wrong constructor")

                class Derived(shared.Intermediate):
                    def render(self):
                        print("derived", self.request)

                one = Derived("request")
                two = Derived("client", code=8)
                print(one.request, one.code)
                print(two.request, two.code)
                one.show()
                """);
            Compare(python, probe);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ConditionalModuleClasses()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"ConditionalModuleClasses skipped: {reason}");
            return;
        }
        string directory = Directory.CreateTempSubdirectory("typhon-conditional-classes-").FullName;
        try
        {
            const string module = """
                available = False

                if available:
                    class Missing:
                        print('unexpected class body')
                else:
                    class Present:
                        print('active class body')
                        answer = 7

                try:
                    unused = Missing
                except NameError:
                    missing_inside = True

                from enum import Enum

                if available:
                    class MissingEnum(Enum):
                        first = 1
                else:
                    class PresentEnum(Enum):
                        first = 1
                """;
            File.WriteAllText(Path.Combine(directory, "branch_defs.py"), module);
            File.WriteAllText(Path.Combine(directory, "branch_defs.ty"), module);
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import branch_defs

                print(hasattr(branch_defs, "Missing"), hasattr(branch_defs, "Present"))
                print("Missing" in branch_defs.__dict__, "Present" in branch_defs.__dict__)
                from branch_defs import Present
                print(Present.__name__, Present.answer, Present is branch_defs.Present)
                try:
                    from branch_defs import Missing
                except ImportError:
                    print("missing class")
                print(branch_defs.missing_inside, Present().answer)
                try:
                    unused = branch_defs.Missing
                except AttributeError:
                    print("missing attribute")
                print(hasattr(branch_defs, "MissingEnum"), hasattr(branch_defs, "PresentEnum"))
                """);
            Compare(python, probe);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PyLibSocketserver()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibSocketserver skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibSocketserver requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-socketserver-").FullName;
        try
        {
            foreach (string name in new[] { "socketserver", "selectors" })
            {
                string source = Path.Combine(lib, name + ".py");
                File.Copy(source, Path.Combine(directory, name + ".py"));
                File.Copy(source, Path.Combine(directory, name + ".ty"));
            }
            string tcp = Path.Combine(directory, "tcp_probe.ty");
            File.WriteAllText(tcp, """
                import socket
                import socketserver
                import threading

                class Echo(socketserver.BaseRequestHandler):
                    def handle(self):
                        self.request.sendall(self.request.recv(128).upper())

                listener = socketserver.TCPServer(("127.0.0.1", 0), Echo)
                listener.timeout = 2.0
                worker = threading.Thread(target=listener.handle_request)
                worker.start()
                try:
                    with socket.create_connection(listener.server_address, timeout=2.0) as client:
                        client.sendall(b"echo")
                        print(client.recv(128))
                finally:
                    worker.join(3.0)
                    listener.server_close()
                print(worker.is_alive())
                """);
            Compare(python, tcp);
            string udp = Path.Combine(directory, "udp_probe.ty");
            File.WriteAllText(udp, """
                import socket
                import socketserver

                class EchoDatagram(socketserver.BaseRequestHandler):
                    def handle(self):
                        message, endpoint = self.request
                        endpoint.sendto(message.upper(), self.client_address)

                listener = socketserver.UDPServer(("127.0.0.1", 0), EchoDatagram)
                listener.timeout = 2.0
                try:
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                        client.settimeout(2.0)
                        client.sendto(b"datagram", listener.server_address)
                        listener.handle_request()
                        result, peer = client.recvfrom(128)
                        print(result, peer == listener.server_address)
                finally:
                    listener.server_close()
                """);
            Compare(python, udp);
            string stream = Path.Combine(directory, "threaded_stream_probe.ty");
            File.WriteAllText(stream, """
                import socket
                import socketserver

                class EchoStream(socketserver.StreamRequestHandler):
                    def handle(self):
                        self.wfile.write(self.rfile.readline().upper())

                server = socketserver.ThreadingTCPServer(("127.0.0.1", 0), EchoStream)
                server.timeout = 2.0
                try:
                    with socket.create_connection(server.server_address, timeout=2.0) as client:
                        client.sendall(b"stream\n")
                        server.handle_request()
                        data = b""
                        while len(data) < 7:
                            chunk = client.recv(7 - len(data))
                            if not chunk:
                                break
                            data += chunk
                        print(data)
                finally:
                    server.server_close()
                """);
            Compare(python, stream);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static void Compare(string python, string probe)
    {
        var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
        Assert.True(expected.ExitCode == 0, expected.StdErr);
        var actual = Suite.Run("dotnet", [Suite.Cli, "run", probe, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
        Assert.True(actual.ExitCode == 0, actual.StdErr);
        Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
    }
}
