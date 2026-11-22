// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceTokenizeTests
{
    [Fact]
    public void OriginalTokenAndTokenize()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"OriginalTokenAndTokenize skipped: {reason}");
            return;
        }

        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0)
        {
            Console.WriteLine("OriginalTokenAndTokenize requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-source-tokenize-").FullName;
        try
        {
            foreach (string module in new[] { "token", "tokenize" })
                File.Copy(Path.Combine(library, module + ".py"), Path.Combine(directory, module + ".ty"));

            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                from io import BytesIO, StringIO
                from token import tok_name as token_names
                from tokenize import TokenInfo, detect_encoding, generate_tokens, tokenize, untokenize, tok_name

                text = "def area(r):\n    return r ** 2  # square\n"
                tokens = list(generate_tokens(StringIO(text).readline))
                print([(tok_name[t.type], t.string, t.start, t.end) for t in tokens])
                print(untokenize(tokens) == text)

                binary = b"# coding: latin-1\n\xe9 = 1\n"
                print(detect_encoding(BytesIO(binary).readline))
                binary_tokens = list(tokenize(BytesIO(binary).readline))
                print(binary_tokens[0].string, binary_tokens[3].string)
                print(untokenize(binary_tokens) == binary)
                for item in (tokens[0], binary_tokens[0]):
                    print(type(item).__name__, isinstance(item, TokenInfo), item.exact_type, token_names[item.type])
                    print(repr(item))
                """);

            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", "--no-cache", "--no-server", "--bigint", probe],
                TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
