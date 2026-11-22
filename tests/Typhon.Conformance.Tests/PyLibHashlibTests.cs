// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibHashlibTests
{
    [Fact]
    public void PyLibHashlib()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibHashlib skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibHashlib requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-hashlib-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_hashlib.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import hashlib
                import os
                from hashlib import sha256 as selected
                from io import BytesIO

                print(hashlib.__file__ is not None)
                print(hashlib.md5(b'abc').hexdigest())
                print(hashlib.sha1(data=b'abc', usedforsecurity=False).hexdigest())
                print(selected(b'abc').hexdigest())
                print(hashlib.new('SHA256', b'abc', usedforsecurity=False).hexdigest())
                print(hashlib.sha384(b'abc').digest().hex())
                print(hashlib.sha512(b'abc').hexdigest())
                h = hashlib.sha256(b'a')
                fork = h.copy()
                print(h.update(b'bc'))
                fork.update(bytearray(b'bd'))
                print(h.digest_size, h.block_size, h.name, h.hexdigest())
                print(fork.name, fork.hexdigest())
                print(hashlib.file_digest(BytesIO(b'abc'), 'sha256').hexdigest())
                print(hashlib.file_digest(BytesIO(b'abc'), hashlib.sha256).hexdigest())

                print(hashlib.sha256().hexdigest())
                print({'sha1', 'sha256', 'sha512'}.issubset(hashlib.algorithms_available))
                path = os.path.join(os.path.dirname(__file__), 'payload.bin')
                with open(path, 'wb') as stream:
                    stream.write(b'abc')
                with open(path, 'rb') as stream:
                    print(hashlib.file_digest(stream, 'sha256', _bufsize=2).hexdigest())

                try:
                    hashlib.sha256('abc')
                except TypeError as error:
                    print(str(error))
                try:
                    hashlib.sha256(None)
                except TypeError as error:
                    print(str(error))
                try:
                    hashlib.new('typhon-missing-hash')
                except ValueError as error:
                    print(str(error))
                try:
                    from hashlib import typhon_missing_registered_constructor
                except ImportError:
                    print('missing constructor import')
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
