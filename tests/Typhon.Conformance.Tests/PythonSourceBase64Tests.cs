// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceBase64Tests
{
    [Fact]
    public void Base64FromPythonSource()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"Base64FromPythonSource skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("Base64FromPythonSource requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-base64-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, "base64.py"), Path.Combine(dir, "base64.ty"));
            string source = Path.Combine(dir, "uses_base64.ty");
            File.WriteAllText(source, """
                import base64
                import binascii

                packet = b'a\xffz\x00b'
                print(base64.b64encode(packet, b'-_', padded=False, wrapcol=5))
                print(base64.b64decode(b'Yf*96AGI=', ignorechars=b'*', canonical=True))
                print(base64.urlsafe_b64encode(packet, padded=False), base64.urlsafe_b64decode(b'Yf96AGI'))
                print(base64.b32encode(packet, padded=False), base64.b32decode(base64.b32encode(packet, padded=False), padded=False))
                print(base64.b32hexencode(packet), base64.b32hexdecode(base64.b32hexencode(packet).lower(), casefold=True))
                print(base64.b16encode(packet), base64.b16decode(b'61 FF7A-0062', ignorechars=b' -'))
                print(base64.b85encode(packet), base64.b85decode(base64.b85encode(packet)))
                print(base64.z85encode(packet), base64.z85decode(base64.z85encode(packet)))
                data = b'\0\0\0\0    abc'
                encoded = base64.a85encode(data, foldspaces=True, adobe=True, wrapcol=5)
                print(encoded, base64.a85decode(encoded, foldspaces=True, adobe=True))
                print(base64.decodebytes(base64.encodebytes(packet)))
                for decoder, bad in ((base64.b64decode, b'TR=='), (base64.b32decode, b'MZ======')):
                    try:
                        decoder(bad, canonical=True)
                    except binascii.Error as error:
                        print(str(error))
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
