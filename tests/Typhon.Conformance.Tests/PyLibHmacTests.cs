// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibHmacTests
{
    [Fact]
    public void PyLibHmac()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibHmac skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibHmac requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-hmac-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_hmac.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import hashlib
                import hmac
                from hmac import compare_digest

                print(hmac.__file__ is not None)
                key = b'\x0b' * 20
                print(hmac.digest(key, b'Hi There', 'sha256').hex())
                print(hmac.digest(key, b'Hi There', hashlib.sha384).hex())

                original = hmac.new(b'key', b'hello', digestmod='sha256')
                copy = original.copy()
                print(original.name, original.digest_size, original.block_size)
                print(original.digest().hex())
                print(original.update(b' world'))
                copy.update(bytearray(b' there'))
                print(original.hexdigest())
                print(copy.hexdigest())


                def make_hash(data=b''):
                    return hashlib.sha256(data)

                long_key = bytearray(b'k' * 130)
                with_callback = hmac.new(long_key, b'message', digestmod=make_hash)
                print(with_callback.hexdigest())
                print(hmac.digest(long_key, b'message', make_hash).hex())
                other_callback = with_callback.copy()
                with_callback.update(b' original')
                other_callback.update(b' copy')
                print(with_callback.hexdigest())
                print(other_callback.hexdigest())
                print(compare_digest('known', 'known'), hmac.compare_digest(b'abc', b'abd'))
                print(compare_digest(memoryview(b'abc'), bytearray(b'abc')))

                try:
                    hmac.new(b'key')
                except TypeError as error:
                    print(str(error))
                try:
                    hmac.new(b'key', b'data', 'typhon-missing-hash')
                except ValueError as error:
                    print(str(error))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
