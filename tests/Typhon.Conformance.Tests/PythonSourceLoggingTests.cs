// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceLoggingTests
{
    [Fact]
    public void OriginalLoggingPackage()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"OriginalLoggingPackage skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("OriginalLoggingPackage requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-logging-").FullName;
        try
        {
            foreach (string name in new[] { "logging", "string" })
            {
                string package = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
                File.Copy(Path.Combine(library, name, "__init__.py"), Path.Combine(package, "__init__.ty"));
            }

            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import io
                import logging

                stream = io.StringIO()
                handler = logging.StreamHandler(stream)
                handler.setFormatter(logging.Formatter('%(levelname)s:%(name)s:%(message)s'))
                log = logging.getLogger('typhon.probe')
                log.propagate = False
                log.addHandler(handler)
                log.setLevel(logging.WARNING)
                log.info('hidden')
                log.warning('record %s', 'value')
                log.error('count=%d', 5)
                print(stream.getvalue().splitlines())
                log.removeHandler(handler)
                print(log.hasHandlers())
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--bigint", "--no-cache", "--no-server", probe], TimeSpan.FromMinutes(4));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
