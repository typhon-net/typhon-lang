// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibFtpTests
{
    [Fact]
    public async Task PyLibPlainFtp()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"PyLibPlainFtp skipped: {reason}"); return; }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibPlainFtp requires CPython 3.15"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-ftp-").FullName;
        try
        {
            string sourceLibrary = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            foreach (string name in new[] { "ftplib.py", "netrc.py" })
                File.Copy(Path.Combine(lib, name), Path.Combine(sourceLibrary, name));

            string script = Path.Combine(directory, "ftp_probe.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                import sys
                sys.modules["ssl"] = None
                import ftplib

                client = ftplib.FTP()
                print(client.connect("127.0.0.1", int(sys.argv[1]), timeout=10))
                print(client.login("example", "fixture-only"))
                print(client.pwd())
                print(client.voidcmd("NOOP"))
                print(client.quit())
                """);
            var environment = new Dictionary<string, string> { ["PYTHONPATH"] = sourceLibrary };
            string expected = await RunClient(python, [script], environment);
            string actual = await RunClient("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", script], environment);
            Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static async Task<string> RunClient(string executable, string[] arguments, Dictionary<string, string> environment)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task server = Reply(listener, timeout.Token);
        string port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = Suite.Run(executable, [.. arguments, port], TimeSpan.FromMinutes(3), environment);
        Assert.True(result.ExitCode == 0, result.StdErr);
        await server;
        return Suite.Normalize(result.StdOut);
    }

    static async Task Reply(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 FTP fixture");
        foreach (var (command, response) in new (string, string)[]
        {
            ("USER example", "331 Password required"),
            ("PASS fixture-only", "230 Logged in"),
            ("PWD", "257 \"/sample\" is the current directory"),
            ("NOOP", "200 OK"),
            ("QUIT", "221 Goodbye"),
        })
        {
            Assert.Equal(command, await reader.ReadLineAsync(cancellationToken));
            await writer.WriteLineAsync(response);
        }
    }
}
