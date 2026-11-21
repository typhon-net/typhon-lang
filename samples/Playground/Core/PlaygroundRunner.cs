// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Typhon.Playground;

/// <summary>How a run ended: the program's exit code (1 after an uncaught exception) and how long it ran.</summary>
public sealed record RunResult(int ExitCode, TimeSpan Elapsed);

/// <summary>
/// Runs a compiled playground program in this process, like <c>typhon run</c> (src/Typhon.Cli/ProgramRunner.cs): its
/// stdout and stderr go to the writers given, stdin comes from a string, and an uncaught exception becomes a Python-style
/// traceback on stderr. Every run loads the program with a fresh copy of Typhon.Runtime (from the same image the compiler
/// references) in its own load context, so no state of the runtime's modules (logging's handlers, random's generator,
/// sys.argv) carries over from one run to the next, as with a new Python process.
/// </summary>
public sealed class PlaygroundRunner
{
    /// <summary>The console is one per process: one program runs at a time.</summary>
    static readonly SemaphoreSlim Gate = new(1, 1);

    readonly byte[] _runtimeImage;

    /// <param name="runtimeImage">Typhon.Runtime.dll, the assembly the compiler references.</param>
    public PlaygroundRunner(byte[] runtimeImage) { _runtimeImage = runtimeImage; }

    public Task<RunResult> RunAsync(CompileResult compiled, TextWriter stdout, TextWriter stderr, string? stdin = null) =>
        compiled.Pe == null ? throw new ArgumentException("the program did not compile", nameof(compiled))
                            : RunAsync(compiled.Pe, compiled.Pdb, stdout, stderr, stdin);

    public async Task<RunResult> RunAsync(byte[] pe, byte[]? pdb, TextWriter stdout, TextWriter stderr, string? stdin = null)
    {
        await Gate.WaitAsync();
        var (savedOut, savedErr, savedExitCode) = (Console.Out, Console.Error, Environment.ExitCode);
        TextReader? savedIn = null;
        var sw = new Stopwatch();
        try
        {
            try { savedIn = Console.In; }
            catch (PlatformNotSupportedException) { } // a browser has no console to read from
            var context = new RunContext(_runtimeImage);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(new StringReader(stdin ?? ""));
            Environment.ExitCode = 0;
            AppContext.SetData("Typhon.Argv0", PlaygroundCompiler.FileName); // sys.argv[0], as `typhon run` sets it

            var asm = context.LoadFromStream(new MemoryStream(pe));
            var entry = asm.EntryPoint;
            if (entry == null)
            {
                stderr.WriteLine("typhon: no entry point (add top-level statements or an `if __name__ == \"__main__\":` block)");
                return new RunResult(1, TimeSpan.Zero);
            }
            int exitCode;
            sw.Start();
            try
            {
                var ret = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() });
                if (ret is Task task) await task; // never block: in a browser the one thread is the page's
                exitCode = Environment.ExitCode;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { exitCode = Uncaught(tie.InnerException, asm, pdb, stderr); }
            catch (Exception e) { exitCode = Uncaught(e, asm, pdb, stderr); } // from an awaited entry point
            sw.Stop();
            Console.Out.Flush();
            Console.Error.Flush();
            return new RunResult(exitCode, sw.Elapsed);
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Console.SetIn(savedIn ?? TextReader.Null);
            Environment.ExitCode = savedExitCode;
            Gate.Release();
        }
    }

    /// <summary>The exit code of an uncaught exception: sys.exit's code, else 1 after a traceback.</summary>
    static int Uncaught(Exception ex, Assembly program, byte[]? pdb, TextWriter stderr)
    {
        if (ex.GetType().FullName == PlaygroundCompiler.SystemExitTypeName)
            return ex.GetType().GetField("Code")?.GetValue(ex) is int code ? code : 1;
        WriteTraceback(ex, program, pdb, stderr);
        return 1;
    }

    /// <summary>
    /// The traceback <c>typhon run</c> prints: the program's frames, outermost first, with their Typhon lines, then the
    /// exception. The lines come from the program's portable PDB, read here rather than by the runtime (a browser's
    /// runtime may not load symbols), with the same lookup the runtime does: the last sequence point at or before the IL offset.
    /// </summary>
    public static void WriteTraceback(Exception ex, Assembly program, byte[]? pdb, TextWriter stderr)
    {
        // a sort's comparison failed: Python reports the comparison's error itself (TypeError: '<' not supported ...)
        while (ex is InvalidOperationException && ex.InnerException != null && ex.Message.StartsWith("Failed to compare")) ex = ex.InnerException;
        stderr.WriteLine("Traceback (most recent call last):");
        using var symbols = pdb == null ? null : MetadataReaderProvider.FromPortablePdbImage(ImmutableCollectionsMarshal.AsImmutableArray(pdb));
        var reader = symbols?.GetMetadataReader();
        foreach (var frame in (new StackTrace(ex, false).GetFrames() ?? []).Reverse())
        {
            var method = frame.GetMethod();
            if (reader == null || method == null || method.Module.Assembly != program) continue;
            var location = SourceLine(reader, method.MetadataToken, frame.GetILOffset());
            if (location is not var (file, line) || !file.EndsWith(".ty", StringComparison.OrdinalIgnoreCase)) continue;
            string fn = method.Name;
            // async/iterator state machines: <Main>d__12.MoveNext → Main
            if (fn == "MoveNext" && method.DeclaringType?.Name is string sm && sm.StartsWith('<')) fn = sm[1..sm.IndexOf('>')];
            if (fn == "Main") fn = "<module>";
            stderr.WriteLine($"  File \"{file}\", line {line}, in {fn}");
        }
        stderr.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    }

    static (string File, int Line)? SourceLine(MetadataReader pdb, int methodToken, int ilOffset)
    {
        if (ilOffset < 0 || (methodToken >> 24) != 0x06) return null;
        var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken);
        if (MetadataTokens.GetRowNumber(handle) > pdb.MethodDebugInformation.Count) return null;
        SequencePoint? best = null;
        foreach (var sp in pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle()).GetSequencePoints())
        {
            if (sp.Offset > ilOffset) break;
            if (!sp.IsHidden) best = sp;
        }
        if (best is not { } point) return null;
        return (pdb.GetString(pdb.GetDocument(point.Document).Name), point.StartLine);
    }

    /// <summary>A run's load context: the program, and its own Typhon.Runtime; everything else comes from the default context.</summary>
    /// <remarks>
    /// Not collectible: on CoreCLR the code of a collectible assembly is compiled fully optimized at once (no tiered
    /// compilation), and the JIT's tail calls and inlining would drop frames from tracebacks (`return f(x)` loses the
    /// caller). A run's copy of the runtime stays loaded: about 600 KB per run.
    /// </remarks>
    sealed class RunContext(byte[] runtimeImage) : AssemblyLoadContext("typhon-run", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == "Typhon.Runtime" ? LoadFromStream(new MemoryStream(runtimeImage)) : null;
    }
}
