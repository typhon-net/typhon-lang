// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibIpAddressTests
{
    [Fact]
    public void PyLibIpAddressBigIntegers()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibIpAddressBigIntegers skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibIpAddressBigIntegers requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-ipaddress-").FullName;
        try
        {
            string source = Path.Combine(lib, "ipaddress.py");
            File.Copy(source, Path.Combine(directory, "stock_ipaddress.py"));
            File.Copy(source, Path.Combine(directory, "stock_ipaddress.ty"));
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, """
                import stock_ipaddress as ipaddress

                ipv4 = ipaddress.ip_address("192.0.2.7")
                print(str(ipv4), ipv4.version, int(ipv4), ipv4.is_private, ipv4.is_global)
                network = ipaddress.ip_network("192.0.2.7/30", strict=False)
                print(str(network), network.prefixlen, ipv4 in network, [str(host) for host in network.hosts()])
                print(format(ipv4, "x"), format(ipv4, "#_b"))
                print([str(value) for value in ipaddress.collapse_addresses([
                    ipaddress.ip_network("192.0.2.0/25"),
                    ipaddress.ip_network("192.0.2.128/25"),
                ])])
                ipv6 = ipaddress.ip_address("2001:db8::a")
                print(str(ipv6), ipv6.version, ipv6.compressed, ipv6.exploded, ipv6.is_private)
                packed = bytes.fromhex("20010db800000000000000000000000a")
                print(ipaddress.IPv6Address(packed) == ipv6, ipv6.packed == packed)
                v6network = ipaddress.ip_network("2001:db8::a/124", strict=False)
                print(str(v6network), v6network.prefixlen, ipv6 in v6network, v6network.num_addresses)
                short_network = ipaddress.ip_network("2001:db8::/126")
                print([str(host) for host in short_network.hosts()])
                print([str(address) for address in short_network])
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--bigint", "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
