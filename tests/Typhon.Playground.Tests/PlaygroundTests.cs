// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Typhon.Playground;
using Xunit;

namespace Typhon.Playground.Tests;

/// <summary>The reference files the playground serves (copied to refs/ by the project file), read as byte arrays.</summary>
public sealed class PlaygroundFixture
{
    public readonly Dictionary<string, byte[]> Images;
    public readonly PlaygroundCompiler Compiler;
    public readonly PlaygroundRunner Runner;

    public PlaygroundFixture()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "refs");
        Images = Directory.GetFiles(dir, "*.dll").ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        Compiler = PlaygroundCompiler.FromImages(Images);
        Runner = new PlaygroundRunner(Images[PlaygroundCompiler.RuntimeFileName]);
    }
}

/// <summary>
/// The playground's compile-and-run path (samples/Playground/Core) as the browser runs it, on regular .NET: source
/// strings in, references from byte arrays, the program's output captured. One class: the compiler is not thread-safe.
/// </summary>
public sealed class PlaygroundTests(PlaygroundFixture fixture) : IClassFixture<PlaygroundFixture>
{
    sealed record Ran(CompileResult Compiled, OutputCapture Output, RunResult? Result)
    {
        public string Stdout => Output.Stdout;
        public string Stderr => Output.Stderr;
        public int? ExitCode => Result?.ExitCode;
    }

    async Task<Ran> RunAsync(string source, string stdin = "", PlaygroundOptions? options = null)
    {
        var compiled = fixture.Compiler.Compile(source.ReplaceLineEndings("\n"), options);
        var output = new OutputCapture();
        var result = compiled.Success ? await fixture.Runner.RunAsync(compiled, output.Out, output.Err, stdin) : null;
        return new Ran(compiled, output, result);
    }

    static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    public static TheoryData<string> ExampleNames => new(Examples.All.Select(e => e.Name));

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public async Task EveryExampleCompilesAndRuns(string name)
    {
        var example = Examples.All.Single(e => e.Name == name);
        var ran = await RunAsync(example.Source, example.Stdin);
        Assert.True(ran.Compiled.Success, string.Join("\n", ran.Compiled.Diagnostics));
        Assert.Contains("public static partial class __main__", ran.Compiled.CSharp);
        Assert.NotEqual("", ran.Stdout);
        if (name.StartsWith("Exceptions"))
        {
            Assert.Equal(1, ran.ExitCode);
            Assert.StartsWith("Traceback (most recent call last):\n", ran.Stderr);
        }
        else
        {
            Assert.Equal("", ran.Stderr);
            Assert.Equal(0, ran.ExitCode);
        }
    }

    [Fact]
    public async Task ExamplesPrintWhatTheyShould()
    {
        Example Get(string prefix) => Examples.All.Single(e => e.Name.StartsWith(prefix));
        Assert.Equal(Lines("Hello, world!", "0 squared is 0", "1 squared is 1", "2 squared is 4"), (await RunAsync(Get("Hello").Source)).Stdout);
        Assert.Equal(Lines("[0, 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89]", "5... 4... 3... 2... 1... liftoff!", "sum of even squares: 1140"),
                     (await RunAsync(Get("Generators").Source)).Stdout);
        Assert.StartsWith("50! = 30414093201713378043612608166064768844377641568960512000000000000\n", (await RunAsync(Get("Big").Source)).Stdout);
        var input = Get("Reading");
        Assert.Equal(Lines("Your name: ", "Hello, Ada!", "Some numbers: ", "sum=31 min=1 max=9"), (await RunAsync(input.Source, input.Stdin)).Stdout);
        Assert.Contains("ab doubled is abab", (await RunAsync(Get("Duck").Source)).Stdout); // the C# runtime binder at work
    }

    [Fact]
    public async Task AnUncaughtExceptionPrintsAPythonTraceback()
    {
        var ran = await RunAsync("""
            def divide(a: int, b: int) -> float:
                return a / b

            def main() -> None:
                print("start")
                print(divide(1, 0))

            main()
            """);
        Assert.Equal(1, ran.ExitCode);
        Assert.Equal("start\n", ran.Stdout);
        Assert.Equal(Lines("Traceback (most recent call last):",
                           "  File \"main.ty\", line 8, in <module>",
                           "  File \"main.ty\", line 6, in main",
                           "  File \"main.ty\", line 2, in divide",
                           "DivideByZeroException: division by zero"), ran.Stderr);
    }

    [Fact]
    public async Task SysExitEndsTheProgramNotTheProcess()
    {
        // Typhon's sys.exit is Environment.Exit: unredirected, it would end this test run (and a browser tab's .NET runtime)
        var ran = await RunAsync("""
            import sys
            from sys import exit

            def check(n: int) -> None:
                if n > 2:
                    exit(n)

            print("before")
            try:
                check(1)
                check(4)
                sys.exit(3)
            finally:
                print("cleanup")
            print("after")
            """);
        Assert.Equal(4, ran.ExitCode);
        Assert.Equal("before\ncleanup\n", ran.Stdout);
        Assert.Equal("", ran.Stderr);
        Assert.Equal(0, (await RunAsync("import sys\nprint('done')\nsys.exit()\n")).ExitCode);
    }

    [Fact]
    public async Task DiagnosticsPointAtTheTyphonSource()
    {
        var syntax = await RunAsync("print('ok')\ndef broken(:\n    pass\n");
        var error = Assert.Single(syntax.Compiled.Diagnostics);
        Assert.Equal((DiagnosticOrigin.Typhon, "error", "TY0001", 2), (error.Origin, error.Severity, error.Id, error.Line));
        Assert.Null(syntax.Compiled.CSharp);
        Assert.Null(syntax.Result);

        var types = await RunAsync("def half(n: int) -> int:\n    return n // 2\n\n\nx: int = half(\"four\")\nprint(x)\n");
        var mismatch = Assert.Single(types.Compiled.Diagnostics, d => d.IsError);
        Assert.Equal((DiagnosticOrigin.CSharp, 5), (mismatch.Origin, mismatch.Line));
        Assert.True(mismatch.CSharpLine > 0);
        Assert.Contains("half(\"four\")", types.Compiled.CSharp!.Split('\n')[mismatch.CSharpLine - 1]);
        Assert.StartsWith("main.ty(5,", mismatch.ToString());
        Assert.False(types.Compiled.Success);
    }

    [Fact]
    public async Task EveryRunGetsAFreshRuntime()
    {
        // logging.basicConfig configures the root logger once per process: a second run sees a new one, as with a new python
        const string program = """
            import logging
            import sys

            logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
            logging.info("started")
            print("to stdout")
            print("to stderr", file=sys.stderr)
            print("back to stdout")
            """;
        foreach (var _ in new[] { 1, 2 })
        {
            var ran = await RunAsync(program);
            Assert.Equal(0, ran.ExitCode);
            // stdout and stderr in the order they were written
            Assert.Equal([(true, "INFO started\n"), (false, "to stdout\n"), (true, "to stderr\n"), (false, "back to stdout\n")], ran.Output.Segments);
        }
    }

    [Fact]
    public async Task OutputStopsAtTheLimit()
    {
        var ran = await RunAsync("for i in range(100000):\n    print('x' * 10)\n");
        Assert.Equal(0, ran.ExitCode);
        Assert.True(ran.Output.Truncated);
        Assert.Equal(OutputCapture.Limit, ran.Stdout.Length);
    }

    [Fact]
    public async Task OptionsSelectTheTypeModeAndBigInts()
    {
        const string untyped = "def f(x):\n    return x.upper()\n\nprint(f('a'))\n";
        Assert.Equal("A\n", (await RunAsync(untyped)).Stdout);
        Assert.Equal("1267650600228229401496703205376\n", (await RunAsync("print(2 ** 100)\n", options: new PlaygroundOptions(BigInts: true))).Stdout);
        var overflow = await RunAsync("print(2 ** 100)\n");
        Assert.Equal(1, overflow.ExitCode);
        Assert.Contains("Overflow", overflow.Stderr);
    }

    [Fact]
    public void TheReferencesCoverEveryAssemblyTheRuntimeReferences()
    {
        // otherwise Roslyn reports CS0012 as soon as a program uses a runtime member with such a type in its signature
        using var pe = new PEReader(new MemoryStream(fixture.Images[PlaygroundCompiler.RuntimeFileName]));
        var md = pe.GetMetadataReader();
        var missing = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                        .Where(name => !fixture.Images.ContainsKey(name + ".dll")).ToList();
        Assert.Empty(missing);
    }
}
