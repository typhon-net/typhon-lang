// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibUrllibTests
{
    [Fact]
    public void UrlParsingAndMimeTypesUseTheInstalledSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibUrllib skipped: {reason}");
            return;
        }

        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("PyLibUrllib requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-urllib-").FullName;
        try
        {
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, $"#:pylib {library}\n" + """
                # typhon: int=bigint
                from urllib import parse
                from urllib.parse import urlparse
                import mimetypes

                address = 'https://user:pass@[2001:db8::1]:8443/a/b;p?q=one#end'
                url = urlparse(address)
                print(url.scheme, url.hostname, url.port, url.path, url.params)
                print(url.username, url.password, url.query, url.fragment)
                print(url.encode().decode() == url)
                print(parse.urlsplit(b'HTTP://Example.TEST/a?b#c').geturl())
                print(parse.urljoin('https://example.test/a/b', '../c?x'))
                print(parse.parse_qs('k=a+b&k=%C3%A9&empty=', keep_blank_values=True))
                print(parse.urlencode({'names': ['one two', 'é']}, doseq=True))

                mimetypes.inited = True
                database = mimetypes.MimeTypes(filenames=())
                print(database.guess_type('https://example.test/a.svgz?q=one#end'))
                print(database.guess_type('data:text/plain;charset=utf-8,hello'))
                database.add_type('application/x-sample', '.sample')
                print(database.guess_type('https://example.test/doc.sample'))
                print(database.guess_extension('application/x-sample'))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(4));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
