// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibCsvTests
{
    [Fact]
    public void PyLibCsv()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibCsv skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibCsv requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-csv-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_csv.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import csv
                from io import StringIO

                sample = 'id;description\r\n1;"first line\nsecond ""quoted"" line"\r\n2;"x;y"\r\n'
                reader = csv.DictReader(StringIO(sample), delimiter=';')
                print(reader.fieldnames)
                print(next(reader))
                print(next(reader))
                print(reader.line_num)

                stream = StringIO()
                writer = csv.DictWriter(stream, ["id", "description"], delimiter=';')
                writer.writeheader()
                writer.writerow({"id": "3", "description": 'embedded; delimiter, "quotes"\nand newline'})
                print(writer.writerows([{"id": "4", "description": "plain"}]))
                print(repr(stream.getvalue()))
                try:
                    writer.writerow({"id": "5", "description": "extra", "other": "value"})
                except ValueError as error:
                    print(str(error))

                sniffer = csv.Sniffer()
                first = sniffer.sniff(sample)
                second = sniffer.sniff('name,count\nalpha,10\nbeta,20\n')
                print(first.delimiter, first.quotechar, first.doublequote)
                print(second.delimiter, second.quotechar, second.doublequote)
                print(first is second, issubclass(first, csv.Dialect))
                print(list(csv.reader(StringIO(sample), first)))
                print(sniffer.has_header('name,count\nalpha,10\nbeta,20\n'))
                print(sniffer.has_header('1;2\n3;4\n5;6\n'))
                chosen = sniffer.sniff('first;second\nleft;right\n', delimiters=';')
                print(chosen.delimiter)
                try:
                    sniffer.sniff('')
                except csv.Error as error:
                    print(str(error))

                excel = csv.excel()
                print(excel.delimiter, repr(excel.lineterminator), list(csv.reader(['1,2'], excel)))

                class CustomDialect(csv.excel):
                    delimiter = '|'
                csv.register_dialect("custom", CustomDialect)
                print(csv.get_dialect("custom").delimiter, next(csv.reader(['left|right'], "custom")))
                csv.unregister_dialect("custom")
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
