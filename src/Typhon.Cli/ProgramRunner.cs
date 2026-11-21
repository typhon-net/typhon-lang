// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Typhon.Cli;

/// <summary>Runs a compiled Typhon program in this process and prints Python-style tracebacks (no Roslyn needed).</summary>
internal static class ProgramRunner
{
    public static int Run(byte[] pe, byte[]? pdb, string[] programArgs, bool timing, IReadOnlyCollection<string> frameworks, IReadOnlyCollection<string>? packageAssemblies = null)
    {
        if (packageAssemblies != null) SharedFrameworks.EnablePackages(packageAssemblies);
        // ASP.NET Core and the like are not on this process's list of platform assemblies: they load from their folder
        try { SharedFrameworks.EnableAtRunTime(frameworks); }
        catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); return 1; }
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(pe), pdb == null ? null : new MemoryStream(pdb));
        var entry = asm.EntryPoint;
        if (entry == null) { Console.Error.WriteLine("typhon: no entry point (add top-level statements or an `if __name__ == \"__main__\":` block)"); return 1; }
        var sw = Stopwatch.StartNew();
        try
        {
            var ret = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { programArgs });
            if (ret is Task t) t.GetAwaiter().GetResult();
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // sys.exit while a module initializes (outside Main's own handler): the program ends with its code
            var inner = tie.InnerException;
            while (inner is TypeInitializationException && inner.InnerException != null) inner = inner.InnerException;
            if (inner.GetType().FullName == "Typhon.Runtime.SystemExit" && inner.GetType().GetMethod("Exit") is MethodInfo exit)
            {
                exit.Invoke(inner, null);
                return Environment.ExitCode;
            }
            PrintTraceback(tie.InnerException);
            return 1;
        }
        if (timing) Console.Error.WriteLine($"[typhon] ran in {sw.ElapsedMilliseconds} ms");
        return Environment.ExitCode;
    }

    static void PrintTraceback(Exception ex)
    {
        // a sort's comparison failed: Python reports the comparison's error itself (TypeError: '<' not supported ...)
        while (ex is InvalidOperationException && ex.InnerException != null && ex.Message.StartsWith("Failed to compare")) ex = ex.InnerException;
        Console.Error.WriteLine("Traceback (most recent call last):");
        foreach (var frame in new StackTrace(ex, true).GetFrames().Reverse())
        {
            string? file = frame.GetFileName();
            if (file == null || !(file.EndsWith(".ty") || file.EndsWith(".gd"))) continue;
            var method = frame.GetMethod();
            string fn = method?.Name ?? "?";
            // async/iterator state machines: <Main>d__12.MoveNext → Main
            if (fn == "MoveNext" && method?.DeclaringType?.Name is string sm && sm.StartsWith('<')) fn = sm[1..sm.IndexOf('>')];
            if (fn == "Main") fn = "<module>";
            Console.Error.WriteLine($"  File \"{file}\", line {frame.GetFileLineNumber()}, in {fn}");
        }
        Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    }
}
