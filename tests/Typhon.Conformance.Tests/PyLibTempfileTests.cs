// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTempfileTests
{
    [Fact]
    public void PyLibTempfile()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTempfile skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.implementation.name == 'cpython' and sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string stdlib = oracle.StdOut.Trim();
        if (stdlib.Length == 0) { Console.WriteLine("PyLibTempfile requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-tempfile-source-").FullName;
        try
        {
            string sourceLibrary = Directory.CreateDirectory(Path.Combine(directory, "Lib")).FullName;
            File.Copy(Path.Combine(stdlib, "tempfile.py"), Path.Combine(sourceLibrary, "stock_tempfile.py"));
            string files = Directory.CreateDirectory(Path.Combine(directory, "files")).FullName;
            string script = Path.Combine(directory, "uses_tempfile.ty");
            File.WriteAllText(script, $"#:pylib {sourceLibrary}\n" + """
                import os
                import stock_tempfile as tempfile
                import sys

                directory = sys.argv[1]
                with tempfile.NamedTemporaryFile(mode="w+t", encoding="utf-8", newline="", dir=directory) as named:
                    filename = named.name
                    print("named", os.path.isfile(filename), named.closed)
                    print(named.write("alpha\n"))
                    named.seek(0)
                    print(repr(named.readline()))
                print("removed", named.closed, not os.path.exists(filename))

                with tempfile.NamedTemporaryFile(dir=directory, delete=False) as retained:
                    retained_path = retained.name
                    retained.write(b"data")
                try:
                    print("retained", os.path.isfile(retained_path))
                    with open(retained_path, "rb") as reader:
                        print(reader.read())
                finally:
                    os.unlink(retained_path)

                with tempfile.TemporaryFile(dir=directory) as ordinary:
                    ordinary.write(b"items")
                    ordinary.seek(1)
                    print("ordinary", ordinary.read(), isinstance(ordinary.fileno(), int))
                print("ordinary closed", ordinary.closed)

                with tempfile.SpooledTemporaryFile(max_size=4, dir=directory) as spool:
                    print("spool", spool.mode, spool._rolled)
                    print(spool.write(b"ab"), spool._rolled, spool.tell())
                    print(spool.write(b"cdef"), spool._rolled, spool.tell())
                    spool.seek(1)
                    print(spool.read(3), isinstance(spool.fileno(), int))
                print("spool closed", spool.closed)

                with tempfile.SpooledTemporaryFile(max_size=6, mode="w+t", encoding="utf-8", newline="", dir=directory) as spool:
                    print("text", spool.mode, spool.encoding, spool._rolled)
                    print(spool.write("aé"), spool._rolled)
                    print(spool.write("b\ncd"), spool._rolled)
                    spool.seek(0)
                    print(spool.readlines())
                print("text closed", spool.closed)

                with tempfile.NamedTemporaryFile(dir=directory, delete_on_close=False) as deferred:
                    deferred_path = deferred.name
                    deferred.close()
                    print("deferred", os.path.isfile(deferred_path))
                print("deferred removed", not os.path.exists(deferred_path))

                with tempfile.SpooledTemporaryFile(max_size=100, dir=directory) as spool:
                    print("forced", spool.write(b"memory"), spool._rolled, isinstance(spool.fileno(), int), spool._rolled)
                    spool.seek(0)
                    print(spool.read())

                with tempfile.TemporaryDirectory(dir=directory) as nested_directory:
                    nested_path = os.path.join(nested_directory, "data")
                    with open(nested_path, "wb") as writer:
                        writer.write(b"nested")
                    print("directory", os.path.isdir(nested_directory), os.path.isfile(nested_path))
                print("directory removed", not os.path.exists(nested_directory))

                path_bytes = os.fsencode(directory)

                descriptor, filename = tempfile.mkstemp(prefix=b"pref", suffix=b".tmp", dir=path_bytes)
                try:
                    print(isinstance(filename, bytes), os.path.basename(filename).startswith(b"pref"), filename.endswith(b".tmp"))
                    print(os.write(descriptor, b"written"))
                finally:
                    os.close(descriptor)
                    os.unlink(filename)

                nested = tempfile.mkdtemp(prefix=b"dir", dir=path_bytes)
                try:
                    print(isinstance(nested, bytes), os.path.isdir(nested), os.path.basename(nested).startswith(b"dir"))
                finally:
                    os.rmdir(nested)

                print(os.path.isdir(tempfile.gettempdir()), tempfile.gettempdirb() == os.fsencode(tempfile.gettempdir()))
                """);
            var expected = Suite.Run(python, [script, files], TimeSpan.FromMinutes(1),
                new Dictionary<string, string> { ["PYTHONPATH"] = sourceLibrary });
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            Assert.Empty(Directory.EnumerateFileSystemEntries(files));
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, files, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
            Assert.Empty(Directory.EnumerateFileSystemEntries(files));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
