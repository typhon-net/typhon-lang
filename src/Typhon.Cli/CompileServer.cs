// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Cli;

/// <summary>
/// A background process that keeps the compiler loaded and warm, like Roslyn's own compiler server (VBCSCompiler).
/// `typhon run` / `check` send it the path of the script over a named pipe that only the current user can open; it
/// compiles (reusing loaded, JIT-optimized Roslyn and cached references) and returns the assembly, which the client runs
/// in its own process. The first `typhon run` without a server compiles locally and starts one for next time. The
/// server exits after 10 minutes without requests (TYPHON_SERVER_IDLE_SECONDS); each CLI build gets its own server.
/// Disable with TYPHON_SERVER=0 or --no-server.
/// </summary>
internal static class CompileServer
{
    const int Magic = 0x54595048; // "TYPH"
    const int ProtocolVersion = 4;
    const byte KindCompile = 1, KindPing = 2, KindStop = 3;

    public static bool Enabled => Environment.GetEnvironmentVariable("TYPHON_SERVER") != "0";

    /// <summary>One server per user and CLI build; short, because Unix domain socket paths are limited.</summary>
    static string PipeName => "typhon-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserName + "|" + BuildStamp.Value)))[..16].ToLowerInvariant();

    // ───────────────────────────── client ─────────────────────────────

    public sealed class Reply
    {
        public bool Success;
        public List<string> Messages = new();
        public byte[]? Pe, Pdb;
        public List<SourceProbe> Probes = new();
        public List<string> Frameworks = new();
        public List<string> PackageAssemblies = new();
        public long CompileMs;
    }

    /// <summary>Compiles through a running server; null if there is none (or it failed), then the caller compiles itself.</summary>
    public static Reply? TryCompile(string entryPath, int mode, int connectTimeoutMs = 50)
    {
        try
        {
            using var pipe = Connect(connectTimeoutMs);
            if (pipe == null) return null;
            using var w = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            using var r = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            w.Write(Magic); w.Write(ProtocolVersion); w.Write(KindCompile);
            w.Write(entryPath); w.Write(mode);
            w.Flush();
            if (r.ReadByte() != 0) return null;
            var reply = new Reply { Success = r.ReadBoolean() };
            for (int i = r.ReadInt32(); i > 0; i--) reply.Messages.Add(r.ReadString());
            int peLen = r.ReadInt32(); if (peLen > 0) reply.Pe = r.ReadBytes(peLen);
            int pdbLen = r.ReadInt32(); if (pdbLen > 0) reply.Pdb = r.ReadBytes(pdbLen);
            for (int i = r.ReadInt32(); i > 0; i--) { string path = r.ReadString(); string sha = r.ReadString(); reply.Probes.Add(new SourceProbe(path, sha.Length == 0 ? null : sha)); }
            for (int i = r.ReadInt32(); i > 0; i--) reply.Frameworks.Add(r.ReadString());
            for (int i = r.ReadInt32(); i > 0; i--) reply.PackageAssemblies.Add(r.ReadString());
            reply.CompileMs = r.ReadInt64();
            return reply;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (TimeoutException) { return null; }
    }

    static NamedPipeClientStream? Connect(int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try { pipe.Connect(timeoutMs); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); return null; }
        catch (IOException) { pipe.Dispose(); return null; }
    }

    /// <summary>Ping: (warm, compiles served, uptime) or null when no server is running.</summary>
    static (bool Warm, int Compiles, long UptimeMs)? Ping(int timeoutMs = 200)
    {
        try
        {
            using var pipe = Connect(timeoutMs);
            if (pipe == null) return null;
            using var w = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            using var r = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            w.Write(Magic); w.Write(ProtocolVersion); w.Write(KindPing); w.Flush();
            if (r.ReadByte() != 0) return null;
            return (r.ReadBoolean(), r.ReadInt32(), r.ReadInt64());
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Starts `typhon server run` detached from this process. It must not inherit our handles: a test runner or benchmark
    /// that captures our output would otherwise wait for the server to exit.
    /// </summary>
    public static void StartInBackground()
    {
        try
        {
            string host = Environment.ProcessPath!;
            bool viaDotnet = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var args = new List<string>();
            if (viaDotnet) args.Add(typeof(CompileServer).Assembly.Location);
            args.Add("server"); args.Add("run");
            if (OperatingSystem.IsWindows()) StartDetachedWindows(host, args);
            else
            {
                var psi = new ProcessStartInfo(host)
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetTempPath(),
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                p?.StandardInput.Close();
            }
        }
        catch (Exception) { /* no server: compiling locally still works */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static void StartDetachedWindows(string host, List<string> args)
    {
        const uint CreateNoWindow = 0x08000000, CreateNewProcessGroup = 0x00000200, CreateBreakawayFromJob = 0x01000000;
        var cmd = new StringBuilder("\"" + host + "\"");
        foreach (var a in args) cmd.Append(" \"").Append(a).Append('"');
        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        // leave the caller's job object if allowed (a job that kills its processes on close would take the server along)
        if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow | CreateNewProcessGroup | CreateBreakawayFromJob,
                           IntPtr.Zero, Path.GetTempPath(), ref si, out var pi) &&
            !CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow | CreateNewProcessGroup,
                           IntPtr.Zero, Path.GetTempPath(), ref si, out pi))
            return;
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }

    // ───────────────────────────── `typhon server ...` ─────────────────────────────

    public static int Command(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "run":
                return Serve();
            case "start":
            {
                if (Ping() is { Warm: true }) { Console.Error.WriteLine("[typhon] compile server already running"); return 0; }
                if (Ping() == null) StartInBackground();
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (Ping() is { Warm: true }) { Console.Error.WriteLine($"[typhon] compile server ready ({sw.ElapsedMilliseconds} ms)"); return 0; }
                    Thread.Sleep(100);
                }
                Console.Error.WriteLine("[typhon] compile server did not start");
                return 1;
            }
            case "stop":
            {
                try
                {
                    using var pipe = Connect(200);
                    if (pipe == null) { Console.Error.WriteLine("[typhon] no compile server running"); return 0; }
                    using var w = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
                    w.Write(Magic); w.Write(ProtocolVersion); w.Write(KindStop); w.Flush();
                    pipe.ReadByte();
                }
                catch (IOException) { }
                Console.Error.WriteLine("[typhon] compile server stopped");
                return 0;
            }
            case "status":
            {
                var ping = Ping();
                Console.Error.WriteLine(ping is { } p
                    ? $"[typhon] compile server running ({(p.Warm ? "warm" : "warming up")}, {p.Compiles} compiles, up {p.UptimeMs / 1000} s)"
                    : "[typhon] no compile server running");
                return 0;
            }
            default:
                Console.Error.WriteLine("usage: typhon server (start|stop|status|run)");
                return 2;
        }
    }

    // ───────────────────────────── server ─────────────────────────────

    static volatile bool _warm;
    static int _compiles, _active;
    static long _lastActivityTicks = DateTime.UtcNow.Ticks;
    static readonly Stopwatch Uptime = Stopwatch.StartNew();

    static bool _staleRemoved;

    /// <summary>Removes this build's socket file once (on Unix a named pipe is a socket file in the temp folder).</summary>
    static bool TryRemoveStaleSocket()
    {
        if (_staleRemoved) return false;
        _staleRemoved = true;
        try
        {
            string socket = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName);
            if (!File.Exists(socket)) return false;
            File.Delete(socket);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static int Serve()
    {
        var idle = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("TYPHON_SERVER_IDLE_SECONDS"), out var s) && s > 0 ? s : 600);
        var refs = new ReferenceSet();
        var shutdown = new CancellationTokenSource();
        Task.Run(() => WarmUp(refs));
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, idle.TotalSeconds)));
                if (Volatile.Read(ref _active) == 0 && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc) > idle)
                {
                    shutdown.Cancel();
                    return;
                }
            }
        });

        bool first = true;
        while (true)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));
            }
            catch (Exception e) when (first && e is IOException or UnauthorizedAccessException)
            {
                // another server already serves this build, or a dead one left its socket file behind (Unix: a server
                // stopped with Environment.Exit, or killed): if nothing answers, the file is stale and is removed
                if (OperatingSystem.IsWindows() || Ping() != null || !TryRemoveStaleSocket()) return 0;
                continue;
            }
            first = false;
            try { pipe.WaitForConnectionAsync(shutdown.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { pipe.Dispose(); return 0; }
            Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            Task.Run(() =>
            {
                try { Handle(pipe, refs); }
                catch (Exception) { /* a broken client must not take the server down */ }
                finally
                {
                    pipe.Dispose();
                    Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Decrement(ref _active);
                }
            });
        }
    }

    static void Handle(NamedPipeServerStream pipe, ReferenceSet refs)
    {
        using var r = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var w = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != Magic || r.ReadInt32() != ProtocolVersion) return;
        switch (r.ReadByte())
        {
            case KindCompile:
            {
                string path = r.ReadString();
                int modeBits = r.ReadInt32();
                var sw = Stopwatch.StartNew();
                CompileOutput output;
                try { output = CliCompiler.Compile(path, CliCompiler.TypeModeOf(modeBits), CompileTarget.Script, refs, bigInts: CliCompiler.BigIntsOf(modeBits)); }
                catch (Exception) { w.Write((byte)1); w.Flush(); return; }
                Interlocked.Increment(ref _compiles);
                w.Write((byte)0);
                w.Write(output.Success);
                w.Write(output.Messages.Count);
                foreach (var m in output.Messages) w.Write(m);
                w.Write(output.Pe?.Length ?? 0); if (output.Pe != null) w.Write(output.Pe);
                w.Write(output.Pdb?.Length ?? 0); if (output.Pdb != null) w.Write(output.Pdb);
                w.Write(output.Probes.Count);
                foreach (var p in output.Probes) { w.Write(p.Path); w.Write(p.Sha256 ?? ""); }
                w.Write(output.Frameworks.Count);
                foreach (var f in output.Frameworks) w.Write(f);
                w.Write(output.PackageAssemblies.Count);
                foreach (var a in output.PackageAssemblies) w.Write(a);
                w.Write(sw.ElapsedMilliseconds);
                break;
            }
            case KindPing:
                w.Write((byte)0); w.Write(_warm); w.Write(Volatile.Read(ref _compiles)); w.Write(Uptime.ElapsedMilliseconds);
                break;
            case KindStop:
                w.Write((byte)0); w.Flush();
                Environment.Exit(0);
                break;
        }
        w.Flush();
        if (OperatingSystem.IsWindows()) pipe.WaitForPipeDrain();
    }

    /// <summary>
    /// A few compiles of a representative program right after start, so .NET's tiered JIT has optimized Roslyn's hot
    /// paths before the first real request (a cold compile takes ~1.5 s, a warm one ~0.2 s with the runtime as source).
    /// </summary>
    static void WarmUp(ReferenceSet refs)
    {
        string file = Path.Combine(Path.GetTempPath(), $"typhon-warmup-{Environment.ProcessId}.ty");
        try
        {
            File.WriteAllText(file, WarmUpProgram);
            for (int i = 0; i < 4; i++) CliCompiler.Compile(file, Typhon.Compiler.TypeMode.Permissive, CompileTarget.Script, refs);
        }
        catch (Exception) { }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
            _warm = true;
        }
    }

    const string WarmUpProgram = """
        from dataclasses import dataclass


        @dataclass
        class Point:
            x: float
            y: float


        class Shape:
            def __init__(self, name):
                self.name = name

            def area(self) -> float:
                return 0.0


        def scale(v, k=2):
            return v * k


        def total(points: list[Point]) -> float:
            return sum(p.x * p.y for p in points)


        words = "a b c a".split()
        counts: dict[str, int] = {}
        for w in words:
            counts[w] = counts.get(w, 0) + 1
        pts = [Point(float(i), i / 2) for i in range(10)]
        print(f"{total(pts):.2f}", sorted(counts), scale(3), scale(1.5), Shape("s").area())
        """;
}
