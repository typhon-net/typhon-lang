// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PythonSourceArgparseTests
{
    [Fact]
    public void ArgparseFromPythonSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null) { Console.WriteLine($"ArgparseFromPythonSource skipped: {reason}"); return; }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string library = stdlib.StdOut.Trim();
        if (library.Length == 0) { Console.WriteLine("ArgparseFromPythonSource requires the CPython 3.15 library"); return; }

        string directory = Directory.CreateTempSubdirectory("typhon-argparse-").FullName;
        try
        {
            string source = Path.Combine(library, "argparse.py");
            File.Copy(source, Path.Combine(directory, "stock_argparse.py"));
            File.Copy(source, Path.Combine(directory, "stock_argparse.ty"));
            string probe = Path.Combine(directory, "probe.ty");
            File.WriteAllText(probe, """
                import stock_argparse as argparse

                parser = argparse.ArgumentParser(prog="example", description="Read a record.", color=False)
                parser.add_argument("--count", type=int, default=2, help="number of calls (default=%(default)s)")
                group = parser.add_mutually_exclusive_group()
                group.add_argument("--red", action="store_true")
                group.add_argument("--blue", action="store_true")
                parser.add_argument("record", choices=["alpha", "beta"])
                parsed = parser.parse_args(["--count", "3", "--red", "alpha"])
                print(parsed.count, parsed.red, parsed.blue, parsed.record)
                other, rest = parser.parse_known_args(["beta", "--tail", "end"])
                print(other.count, other.record, rest)
                help_text = parser.format_help()
                print("Read a record." in help_text, "default=2" in help_text, "{alpha,beta}" in help_text)

                error_parser = argparse.ArgumentParser(prog="example", add_help=False, color=False, exit_on_error=False)
                error_parser.add_argument("--count", type=int)
                for argv in (["--count"], ["--count", "bad"]):
                    try:
                        error_parser.parse_args(argv)
                    except argparse.ArgumentError as error:
                        print(str(error))

                subcommand_parser = argparse.ArgumentParser(prog="example", color=False)
                subparsers = subcommand_parser.add_subparsers(dest="command", required=True)
                listing = subparsers.add_parser("list", aliases=["ls"], help="Show records.")
                listing.add_argument("--all", action="store_true")
                result = subcommand_parser.parse_args(["ls", "--all"])
                print(result.command, result.all)
                text = subcommand_parser.format_help()
                print("{list,ls}" in text, "Show records." in text)
                """);
            var expected = Suite.Run(python, [probe], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", probe, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
