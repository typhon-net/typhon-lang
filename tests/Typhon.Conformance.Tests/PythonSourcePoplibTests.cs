// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourcePoplibTests
{
    [Fact]
    public void PoplibFromPythonSource()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PoplibFromPythonSource skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PoplibFromPythonSource requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-poplib-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, "poplib.py"), Path.Combine(dir, "poplib.ty"));
            string source = Path.Combine(dir, "uses_poplib.ty");
            File.WriteAllText(source, """
                import poplib
                import socket
                import threading

                def server(listener):
                    peer, _ = listener.accept()
                    with peer:
                        with peer.makefile('rb') as reader:
                            peer.sendall(b'+OK ready\r\n')
                            for request, answer in (
                                (b'USER alice\r\n', b'+OK user\r\n'),
                                (b'PASS sample\r\n', b'+OK signed in\r\n'),
                                (b'STAT\r\n', b'+OK 1 12\r\n'),
                                (b'LIST\r\n', b'+OK listing\r\n1 12\r\n.\r\n'),
                                (b'RETR 1\r\n', b'+OK message\r\nfirst\r\n..dot\r\n.\r\n'),
                                (b'DELE 2\r\n', b'-ERR no such message\r\n'),
                                (b'QUIT\r\n', b'+OK bye\r\n'),
                            ):
                                actual = reader.readline()
                                assert actual == request
                                peer.sendall(answer)

                with socket.socket() as listener:
                    listener.bind(('127.0.0.1', 0))
                    listener.listen()
                    worker = threading.Thread(target=server, args=(listener,), daemon=True)
                    worker.start()
                    address = listener.getsockname()
                    client = poplib.POP3('127.0.0.1', address[1], timeout=2)
                    print(client.getwelcome(), client.user('alice'), client.pass_('sample'))
                    print(client.stat())
                    print(client.list())
                    print(client.retr(1))
                    try:
                        client.dele(2)
                    except poplib.error_proto as err:
                        print(err.args)
                    print(client.quit())
                    worker.join()
                """);
            var expected = Suite.Run(python, [source], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", source], TimeSpan.FromMinutes(2));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
