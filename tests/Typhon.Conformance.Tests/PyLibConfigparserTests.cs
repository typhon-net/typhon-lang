// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibConfigparserTests
{
    [Fact]
    public void PyLibConfigparserSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibConfigparserSource skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibConfigparserSource requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-configparser-").FullName;
        try
        {
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """"
                import configparser
                import io

                parser = configparser.ConfigParser()
                parser.read_string("""[DEFAULT]
                root = /home/example
                sound = yes
                [main]
                Path = %(root)s/data
                Count = 3
                multiline = one
                  two

                  three
                """)
                print(parser.sections(), parser.options('main'))
                print(parser.get('main', 'path'), parser.getint('main', 'count'), parser.getboolean('main', 'sound'))
                print(repr(parser['main']['multiline']), parser['main'].get('missing', 'fallback'))
                print(list(parser['main'].values()))

                buffer = io.StringIO()
                parser.write(buffer)
                print(repr(buffer.getvalue()))
                restored = configparser.ConfigParser()
                restored.read_file(io.StringIO(buffer.getvalue()))
                print(restored.get('main', 'path'), restored.getint('main', 'count'))

                extended = configparser.ConfigParser(interpolation=configparser.ExtendedInterpolation())
                extended.read_dict({'paths': {'root': '/root'}, 'app': {'file': '${paths:root}/a'}})
                print(extended.get('app', 'file'))

                def split_csv(value):
                    return value.split(',')

                custom = configparser.ConfigParser(converters={'csv': split_csv})
                custom.read_string('[section]\nitems = x,y\n')
                print(getattr(custom, 'getcsv')('section', 'items'), getattr(custom['section'], 'getcsv')('items'))

                try:
                    parser.read_string('[dup]\na=1\na=2\n', source='sample.ini')
                except configparser.DuplicateOptionError as error:
                    print(error.section, error.option, error.source, error.lineno)
                try:
                    parser.read_string('no header\n', source='sample.ini')
                except configparser.MissingSectionHeaderError as error:
                    print(error.source, error.lineno, repr(error.line))
                """");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
