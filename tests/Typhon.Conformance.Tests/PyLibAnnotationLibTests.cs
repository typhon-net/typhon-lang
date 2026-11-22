// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibAnnotationLibTests
{
    [Fact]
    public void AnnotationLibFromPythonSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"AnnotationLibFromPythonSource skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("AnnotationLibFromPythonSource requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-annotationlib-").FullName;
        try
        {
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, $"#:pylib {library}\n" + """
                import annotationlib as notes
                import sys

                count: int = 4

                def format_names():
                    Format = notes.Format
                    return Format.VALUE.name, Format.STRING.name

                print(format_names())

                class Row:
                    field: int
                    title: 'str'
                    children: list[int]
                    number: int | None

                class Empty(Row):
                    pass

                def label(row: int, name: 'str') -> bool:
                    return bool(row and name)

                print(notes.get_annotations(label))
                print(notes.get_annotations(label, format=notes.Format.STRING))
                print(notes.get_annotations(label, eval_str=True))
                print(notes.get_annotations(Row))
                print(notes.get_annotations(Empty))
                print(notes.get_annotations(sys.modules[__name__]))
                first = notes.get_annotations(label)
                first['other'] = 'ignored'
                print(first is notes.get_annotations(label), 'other' in notes.get_annotations(label))

                known = notes.ForwardRef('Alias')
                print(known, known.evaluate(globals={'Alias': Row}) is Row)
                print(known.evaluate(format=notes.Format.STRING))
                missing = notes.ForwardRef('Missing')
                print(missing.evaluate(format=notes.Format.FORWARDREF) is missing)
                try:
                    missing.evaluate()
                except NameError as error:
                    print(error.name, str(error))
                print(notes.ForwardRef('int').evaluate() is int)
                print(notes.ForwardRef('1 + 2').evaluate())
                print(notes.ForwardRef('list[Alias]').evaluate(globals={'Alias': Row}))
                print(notes.ForwardRef('Alias | None').evaluate(globals={'Alias': Row}))
                print(notes.type_repr(int), notes.type_repr(Row))

                def annotate(format):
                    if format == notes.Format.VALUE:
                        return {'known': int}
                    raise NotImplementedError

                print(notes.call_annotate_function(annotate, notes.Format.STRING))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
