// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibLzmaTests
{
    [Fact]
    public void OriginalLzmaUsesNativeCompression()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"LZMA oracle skipped: {reason}");
            return;
        }

        string script = Suite.CasePath("source_lzma_api");
        var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--", "native"], TimeSpan.FromMinutes(2));
        Assert.True(actual.ExitCode == 0, actual.StdErr);
        string output = Suite.Normalize(actual.StdOut);
        if (output == "native liblzma unavailable\n")
        {
            Console.WriteLine("The fixture verified that lzma reports its missing _lzma dependency.");
            return;
        }

        var expected = Suite.Run(python, [script, "native"], TimeSpan.FromSeconds(30));
        Assert.True(expected.ExitCode == 0, expected.StdErr);
        Assert.Equal(Suite.Normalize(expected.StdOut), output);
    }
}
