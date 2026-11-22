// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibZipappTests
{
    /// <summary>Run zipapp from Lib and inspect the resulting archives with CPython's reader.</summary>
    [Fact]
    public void PyLibZipapp()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibZipapp skipped: {why}"); return; }
        var oracle = Suite.Run(python, ["-c", "import sysconfig; print(sysconfig.get_path('stdlib'))"], TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        string dir = Directory.CreateTempSubdirectory("typhon-zipapp-").FullName;
        try
        {
            string oracleDir = Directory.CreateDirectory(Path.Combine(dir, "oracle")).FullName;
            string outputDir = Directory.CreateDirectory(Path.Combine(dir, "output")).FullName;
            string expectedScript = Path.Combine(oracleDir, "uses_zipapp.ty");
            string actualScript = Path.Combine(outputDir, "uses_zipapp.ty");
            const string source = """
                import os
                os.chdir(os.path.dirname(__file__))

                import io
                import pathlib
                import zipapp

                app = pathlib.Path("app")
                app.mkdir()
                (app / "__main__.py").write_text("print('hello')\n", encoding="utf-8")
                (app / "empty").mkdir()
                (app / "files").mkdir()
                (app / "files" / "bulk.txt").write_text("repeat line\n" * 256, encoding="utf-8")
                (app / "skip.txt").write_text("exclude this entry\n", encoding="utf-8")

                zipapp.create_archive(app, "plain.pyz")
                print(zipapp.get_interpreter("plain.pyz"))
                zipapp.create_archive(app, "compressed.pyz", interpreter="/usr/bin/env python3",
                                      filter=lambda member: member.name != "skip.txt", compressed=True)
                print(zipapp.get_interpreter("compressed.pyz"))
                zipapp.create_archive("compressed.pyz", "copied.pyz", interpreter="/custom/python")
                print(zipapp.get_interpreter(pathlib.Path("copied.pyz")))

                code = pathlib.Path("code")
                code.mkdir()
                (code / "entry.py").write_text("def start():\n    return 42\n", encoding="utf-8")
                stream = io.BytesIO()
                zipapp.create_archive(code, stream, interpreter="/memory/python", main="entry:start")
                print(stream.getvalue().startswith(b"#!/memory/python\nPK"))
                stream.seek(0)
                print(zipapp.get_interpreter(stream))
                with open("buffer.pyz", "wb") as output:
                    output.write(stream.getvalue())

                try:
                    zipapp.create_archive("missing")
                except zipapp.ZipAppError as error:
                    print(str(error))
                try:
                    zipapp.create_archive(code)
                except zipapp.ZipAppError as error:
                    print(str(error))
                try:
                    zipapp.create_archive(app, main="entry:start")
                except zipapp.ZipAppError as error:
                    print(str(error))
                try:
                    zipapp.create_archive(code, main="invalid-format")
                except zipapp.ZipAppError as error:
                    print(str(error))
                try:
                    zipapp.create_archive(app, app / "skip.txt")
                except zipapp.ZipAppError:
                    print("existing target rejected")
                """;
            File.WriteAllText(expectedScript, source);
            File.WriteAllText(actualScript, $"#:pylib {lib}\n" + source);

            var expected = Suite.Run(python, [expectedScript], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", actualScript, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));

            string inspect = Path.Combine(dir, "inspect_archives.py");
            File.WriteAllText(inspect, """
                from pathlib import Path
                from zipfile import ZipFile
                import sys

                for name in ("plain.pyz", "compressed.pyz", "copied.pyz", "buffer.pyz"):
                    with ZipFile(Path(sys.argv[1]) / name) as archive:
                        assert archive.testzip() is None
                        print(name)
                        for entry in sorted(archive.infolist(), key=lambda info: info.filename):
                            print(entry.filename, entry.compress_type, entry.file_size, f"{entry.CRC:08x}")
                """);
            var expectedZip = Suite.Run(python, [inspect, oracleDir], TimeSpan.FromMinutes(1));
            Assert.True(expectedZip.ExitCode == 0, expectedZip.StdErr);
            var actualZip = Suite.Run(python, [inspect, outputDir], TimeSpan.FromMinutes(1));
            Assert.True(actualZip.ExitCode == 0, actualZip.StdErr);
            Assert.Equal(Suite.Normalize(expectedZip.StdOut), Suite.Normalize(actualZip.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
