// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

/// <summary>
/// Python's int is a 64-bit long in Typhon. These cases cannot be conformance cases (CPython never overflows and cannot
/// import .NET), so their expected output is written here.
/// </summary>
public sealed class IntegerTests
{
    internal static string RunScript(string source, out int exitCode, out string stdErr)
    {
        string dir = Path.Combine(Suite.RepoRoot, "artifacts", "integer-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "main.ty");
            File.WriteAllText(path, source);
            var env = new Dictionary<string, string> { ["TYPHON_CACHE"] = "0", ["TYPHON_SERVER"] = "0" };
            var run = Suite.Run("dotnet", [Suite.Cli, "run", "--strict", path], TimeSpan.FromMinutes(2), env);
            exitCode = run.ExitCode;
            stdErr = run.StdErr;
            return Suite.Normalize(run.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Beyond 64 bits Typhon raises OverflowError (CPython would switch to arbitrary precision); it never wraps around.</summary>
    [Fact]
    public void OverflowBeyond64BitsRaises()
    {
        const string source = """
            x = 2 ** 62
            try:
                print("no error", x * 4)
            except OverflowError:
                print("OverflowError")
            try:
                print(1 << 64)
            except OverflowError:
                print("OverflowError")
            total = 0
            try:
                for i in range(10):
                    total += x
            except OverflowError:
                print("OverflowError after", total)
            print(x - 1 + x)
            print(2 ** 64)
            """;
        string stdout = RunScript(source, out int exit, out string stderr);
        Assert.Equal("OverflowError\nOverflowError\nOverflowError after 4611686018427387904\n9223372036854775807\n", stdout);
        Assert.NotEqual(0, exit);
        Assert.Contains("OverflowException", stderr);
    }

    /// <summary>
    /// .NET APIs that take an int (String.PadLeft, List.Insert, the DateTime constructor, StringBuilder.Capacity, ...) get
    /// Python ints narrowed (checked); APIs with a long overload (Math.Max) get the long. .NET ints read into Python
    /// variables widen to int (long), so arithmetic on them does not overflow at 32 bits.
    /// </summary>
    [Fact]
    public void DotNetApisReceivePythonInts()
    {
        const string source = """
            from System import Math, DateTime
            from System.Text import StringBuilder
            from System.Collections.Generic import List

            n = 3
            width = 8
            s = "abc"
            print(s.PadLeft(width) + "|", s.Substring(1, n - 2))
            sb = StringBuilder()
            sb.Append(s)
            sb.Insert(n, "X")
            sb.Capacity = width * 4
            print(sb.ToString(), sb.Capacity >= 32, sb.Length)
            print(Math.Max(2 ** 40, n), Math.Abs(-(2 ** 40)))
            xs = List[int]()
            xs.Add(2 ** 40)
            xs.Add(n)
            xs.Insert(n - 2, 7)
            print(xs.Count, xs.IndexOf(7), xs[n - 1], xs[0] * 2)
            d = DateTime(2026, n, 20)
            print(d.Month, d.AddDays(n).Day)
            length = sb.Length
            idx = s.IndexOf("c")
            print(length * 2 ** 40, idx * 2 ** 40)
            print(int32(2 ** 31 - 1))
            """;
        string stdout = RunScript(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal(
            "     abc| b\n" +
            "abcX True 4\n" +
            "1099511627776 1099511627776\n" +
            "3 1 3 2199023255552\n" +
            "3 23\n" +
            "4398046511104 2199023255552\n" +
            "2147483647\n", stdout);
    }
}
