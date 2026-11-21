// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Typhon.Compiler;

namespace Typhon.Cli;

/// <summary>Compiling in this process (the slow path for `run`; the only path for `emit` and `build`).</summary>
internal static class LocalCommand
{
    public static int Execute(string command, string entryPath, int mode, string modeName, string[] programArgs, string? outDir, bool aot,
                              bool timing, bool useCache, bool startServer, Stopwatch total)
    {
        // start the server now: it warms up while this process compiles, and the next run uses it
        if (startServer) CompileServer.StartInBackground();

        var target = command is "run" or "check" ? CompileTarget.Script : CompileTarget.Build;
        var output = CliCompiler.Compile(entryPath, CliCompiler.TypeModeOf(mode), target, csharpOnly: command == "emit", bigInts: CliCompiler.BigIntsOf(mode));
        if (command == "emit")
        {
            Console.WriteLine(output.EntryCode);
            foreach (var m in output.Messages) Console.Error.WriteLine(m);
            return output.Success ? 0 : 1;
        }
        foreach (var m in output.Messages) Console.Error.WriteLine(m);
        if (!output.Success) return 1;
        if (timing)
            Console.Error.WriteLine($"[typhon] compiled in {total.ElapsedMilliseconds} ms: references {output.ReferencesMs} ms, " +
                                    $"Typhon parse + inference + C# emission {output.FrontMs} ms, Roslyn C# to IL {output.RoslynMs} ms " +
                                    $"(process start before Main: {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - total.ElapsedMilliseconds:F0} ms)");
        if (command == "check") return 0;

        if (command == "build" && aot && output.Frameworks.Count > 0)
        {
            // Native AOT for ASP.NET Core needs the request delegate generator and trimming annotations of a full project
            Console.Error.WriteLine($"typhon: build --aot does not support programs that use {string.Join(", ", output.Frameworks)}; " +
                                    "build without --aot (a framework-dependent app), or publish a project with PublishAot");
            return 1;
        }
        if (command == "build" && aot) return NativeAotBuilder.Build(output.Compilation!, output.Trees, entryPath, outDir);
        if (command == "build") return WriteBuild(output, entryPath, outDir);

        if (useCache) ScriptCache.Store(entryPath, modeName, output.Pe!, output.Pdb, output.Probes, output.Messages, output.Frameworks);
        return ProgramRunner.Run(output.Pe!, output.Pdb, programArgs, timing, output.Frameworks);
    }

    /// <summary>A framework-dependent app: name.dll + .pdb + .runtimeconfig.json + the Typhon libraries it may use.</summary>
    static int WriteBuild(CompileOutput output, string entryPath, string? outDir)
    {
        outDir ??= Path.Combine(Path.GetDirectoryName(entryPath)!, "bin", Path.GetFileNameWithoutExtension(entryPath));
        Directory.CreateDirectory(outDir);
        string asmName = output.Compilation!.AssemblyName!;
        string dllPath = Path.Combine(outDir, asmName + ".dll");
        File.WriteAllBytes(dllPath, output.Pe!);
        File.WriteAllBytes(Path.Combine(outDir, asmName + ".pdb"), output.Pdb!);
        var v = Environment.Version;
        // the base framework, plus ASP.NET Core when the program uses it
        var frameworks = new[] { "Microsoft.NETCore.App" }.Concat(output.Frameworks)
            .Select(f => $$"""      { "name": "{{f}}", "version": "{{v.Major}}.{{v.Minor}}.0" }""");
        File.WriteAllText(Path.Combine(outDir, asmName + ".runtimeconfig.json"), $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{v.Major}}.{{v.Minor}}",
                "frameworks": [
            {{string.Join(",\n", frameworks)}}
                ]
              }
            }
            """);
        foreach (var lib in LibrariesToShip()) File.Copy(lib, Path.Combine(outDir, Path.GetFileName(lib)), overwrite: true);
        Console.Error.WriteLine($"[typhon] built {dllPath} — run it with: dotnet \"{dllPath}\"");
        return 0;
    }

    /// <summary>Typhon libraries a built program may reference (not the compiler, not the precompiled runtime: it is embedded).</summary>
    public static IEnumerable<string> LibrariesToShip() =>
        Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Where(p => Path.GetFileName(p) is var n &&
            ((n.StartsWith("Typhon.") && !n.StartsWith("Typhon.Compiler") && n != "Typhon.Runtime.dll") || n == "System.Numerics.Tensors.dll"));
}
