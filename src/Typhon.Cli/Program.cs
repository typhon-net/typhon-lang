// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Typhon.Cli;

// typhon — command line front end for Typhon.
//   typhon run   <file.ty> [args...]   compile in memory and run (like `python file.py`)
//   typhon emit  <file.ty>             print the generated C#
//   typhon check <file.ty>             report diagnostics only
//   typhon build <file.ty> [-o dir]    compile to disk: dir/<name>.dll, run it with `dotnet dir/<name>.dll`
//   typhon build <file.ty> --aot       Native AOT: a self-contained native executable (needs the C++ build tools)
//   typhon server start|stop|status    the background compile server (started automatically by `typhon run`)
// Options: --strict | --permissive (type mode), --bigint (Python ints are unbounded BigIntegers instead of checked 64-bit
//          longs; also TyphonIntMode=bigint in a project or a "# typhon: int=bigint" comment), --time (timings to stderr),
//          --no-cache / TYPHON_CACHE=0 (always compile), --no-server / TYPHON_SERVER=0 (compile in this process)
//
// `typhon run` takes the fastest way that is still exact: a cached build of the unchanged script (ScriptCache), else
// the warm compile server (CompileServer), else compiling here (LocalCommand, which also starts a server for next
// time). This file must not touch Roslyn types, so the first two paths never load the compiler.

var total = Stopwatch.StartNew();
var argList = args.ToList();
if (argList.Count >= 1 && argList[0] == "server") return CompileServer.Command(argList.Skip(1).ToArray());

int mode = 1; // Typhon.Compiler.TypeMode: 0 = Strict, 1 = Warn, 2 = Permissive
string modeName = "warn";
if (argList.Remove("--strict")) { mode = 0; modeName = "strict"; }
if (argList.Remove("--permissive")) { mode = 2; modeName = "permissive"; }
// big-int mode travels with the type mode (one int to the compile server, one cache slot per combination)
if (argList.Remove("--bigint")) { mode |= 0x10; modeName += "+bigint"; }
bool timing = argList.Remove("--time");
bool aot = argList.Remove("--aot");
bool useCache = !argList.Remove("--no-cache") && ScriptCache.Enabled;
bool useServer = !argList.Remove("--no-server") && CompileServer.Enabled;
string? outDir = null;
int oIndex = argList.IndexOf("-o");
if (oIndex >= 0 && oIndex + 1 < argList.Count) { outDir = Path.GetFullPath(argList[oIndex + 1]); argList.RemoveRange(oIndex, 2); }

if (argList.Count < 2 || argList[0] is not ("run" or "emit" or "check" or "build"))
{
    Console.Error.WriteLine("usage: typhon (run|emit|check|build) <file.ty> [args...] [-o dir] [--strict|--permissive] [--bigint] [--time] [--no-cache] [--no-server]");
    Console.Error.WriteLine("       typhon server (start|stop|status)");
    return 2;
}

string command = argList[0];
string entryPath = Path.GetFullPath(argList[1]);
string[] programArgs = argList.Skip(2).ToArray();
if (!File.Exists(entryPath)) { Console.Error.WriteLine($"typhon: can't open file '{entryPath}'"); return 2; }

// 1. an unchanged script that was compiled before: run the cached assembly
if (command == "run" && useCache && ScriptCache.TryLoad(entryPath, modeName) is { } cached)
{
    foreach (var m in cached.Messages) Console.Error.WriteLine(m);
    if (timing) Console.Error.WriteLine($"[typhon] cached build, ready in {total.ElapsedMilliseconds} ms");
    return ProgramRunner.Run(cached.Pe, cached.Pdb, programArgs, timing, cached.Frameworks);
}

// 2. the warm compile server
if (command is "run" or "check" && useServer && CompileServer.TryCompile(entryPath, mode) is { } reply)
{
    foreach (var m in reply.Messages) Console.Error.WriteLine(m);
    if (!reply.Success) return 1;
    if (timing) Console.Error.WriteLine($"[typhon] compiled by the compile server in {reply.CompileMs} ms, ready in {total.ElapsedMilliseconds} ms");
    if (command == "check") return 0;
    if (useCache) ScriptCache.Store(entryPath, modeName, reply.Pe!, reply.Pdb, reply.Probes, reply.Messages, reply.Frameworks);
    return ProgramRunner.Run(reply.Pe!, reply.Pdb, programArgs, timing, reply.Frameworks);
}

// 3. compile in this process
return LocalCommand.Execute(command, entryPath, mode, modeName, programArgs, outDir, aot, timing, useCache,
                            startServer: useServer && command is "run" or "check", total);
