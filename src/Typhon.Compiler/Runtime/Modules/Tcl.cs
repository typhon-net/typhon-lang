// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Concurrent;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Typhon.Runtime;

    internal static class _tkinter
    {
        public const long READABLE = 1 << 1;
        public const long WRITABLE = 1 << 2;
        public const long EXCEPTION = 1 << 3;

        public sealed class TclError(string message) : PyExceptionBase(message);

        public static TkappType create(string? screenName = null, string baseName = "", string className = "Tk",
            bool interactive = false, long wantobjects = 0, bool wantTk = true, bool sync = false, string? use = null)
        {
            if (screenName != null || sync || use != null) throw new NotSupportedException("nondefault Tk display options are unavailable");
            return new TkappType(baseName, interactive, wantobjects, wantTk);
        }

        public static string TCL_VERSION => TclApi.Instance.Version;
        public static string TK_VERSION => TclApi.Instance.Version;

        public sealed class TkappType : IDisposable
        {
            IntPtr _interpreter;
            long _wantobjects;

            internal TkappType(string baseName, bool interactive, long wantobjects, bool wantTk)
            {
                _wantobjects = wantobjects;
                _interpreter = TclThread.Call(() =>
                {
                    var native = TclApi.Instance;
                    IntPtr handle = native.Create();
                    if (handle == IntPtr.Zero) throw new TclError("could not create a Tcl interpreter");
                    try
                    {
                        Ensure(native.Init(handle), handle);
                        native.SetVar(handle, "argv0", baseName, 0);
                        native.SetVar(handle, "tcl_interactive", interactive ? "1" : "0", 0);
                        if (wantTk) Ensure(native.TkInit.Value(handle), handle);
                        return handle;
                    }
                    catch { native.Delete(handle); throw; }
                });
            }

            static void Ensure(int status, IntPtr handle)
            {
                if (status != 0) throw new TclError(Text(TclApi.Instance.Result(handle)));
            }

            static string Text(IntPtr ptr) => Marshal.PtrToStringUTF8(ptr) ?? "";
            IntPtr Handle => _interpreter != IntPtr.Zero ? _interpreter : throw new ObjectDisposedException("Tcl interpreter");
            public long wantobjects() => _wantobjects;
            public void wantobjects(long value) => _wantobjects = value;

            public string eval(string script) => TclThread.Call(() =>
            {
                IntPtr interpreter = Handle;
                Ensure(TclApi.Instance.Evaluate(interpreter, script), interpreter);
                return Text(TclApi.Instance.Result(interpreter));
            });

            public string getvar(string name) => TclThread.Call(() =>
            {
                IntPtr value = TclApi.Instance.GetVar(Handle, name, 0);
                return value != IntPtr.Zero ? Text(value) : throw new TclError($"can't read \"{name}\": no such variable");
            });

            public void setvar(string name, object? value) => TclThread.Call(() =>
                TclApi.Instance.SetVar(Handle, name, Builtins.str(value), 0) != IntPtr.Zero ? 0 :
                    throw new TclError($"can't set \"{name}\""));

            public void unsetvar(string name) => TclThread.Call(() =>
                TclApi.Instance.UnsetVar(Handle, name, 0) == 0 ? 0 : throw new TclError($"can't unset \"{name}\": no such variable"));

            public void loadtk() => TclThread.Call(() =>
            {
                IntPtr interpreter = Handle;
                Ensure(TclApi.Instance.TkInit.Value(interpreter), interpreter);
                return 0;
            });

            void Release()
            {
                IntPtr handle = Interlocked.Exchange(ref _interpreter, IntPtr.Zero);
                if (handle != IntPtr.Zero) TclThread.Queue.Add(() => TclApi.Instance.Delete(handle));
            }
            public void Dispose() { Release(); GC.SuppressFinalize(this); }
            ~TkappType() => Release();
        }

        static class TclThread
        {
            internal static readonly BlockingCollection<Action> Queue = new();
            static readonly Thread Worker = Start();

            static Thread Start()
            {
                var thread = new Thread(() =>
                {
                    foreach (var request in Queue.GetConsumingEnumerable()) request();
                }) { IsBackground = true, Name = "Tcl interpreter" };
                thread.Start();
                return thread;
            }

            internal static T Call<T>(Func<T> action)
            {
                if (Thread.CurrentThread == Worker) return action();
                var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Queue.Add(() =>
                {
                    try { result.SetResult(action()); }
                    catch (Exception error) { result.SetException(error); }
                });
                return result.Task.GetAwaiter().GetResult();
            }
        }

        sealed class TclApi
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Initializer(IntPtr interpreter);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr NewInterpreter();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DeleteInterpreter(IntPtr interpreter);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetResult(IntPtr interpreter);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EvaluateScript(IntPtr interpreter, [MarshalAs(UnmanagedType.LPUTF8Str)] string script);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ReadVariable(IntPtr interpreter, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr WriteVariable(IntPtr interpreter, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DeleteVariable(IntPtr interpreter, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Executable([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VersionNumbers(out int major, out int minor, out int patch, out int type);

            static readonly Lazy<TclApi> Native = new(() => new TclApi());
            internal static TclApi Instance => Native.Value;
            internal readonly string Version;
            internal readonly NewInterpreter Create;
            internal readonly Initializer Init;
            internal readonly DeleteInterpreter Delete;
            internal readonly GetResult Result;
            internal readonly EvaluateScript Evaluate;
            internal readonly ReadVariable GetVar;
            internal readonly WriteVariable SetVar;
            internal readonly DeleteVariable UnsetVar;
            internal readonly Lazy<Initializer> TkInit;

            TclApi()
            {
                IntPtr library = Load("TYPHON_TCL_LIBRARY", OperatingSystem.IsWindows() ? ["tcl90.dll", "tcl86t.dll"]
                    : OperatingSystem.IsMacOS() ? ["libtcl9.0.dylib", "libtcl8.6.dylib"] : ["libtcl9.0.so", "libtcl8.6.so"]);
                Symbol<Executable>(library, "Tcl_FindExecutable")(Environment.ProcessPath ?? "typhon");
                Symbol<VersionNumbers>(library, "Tcl_GetVersion")(out int major, out int minor, out _, out _);
                Version = $"{major}.{minor}";
                Create = Symbol<NewInterpreter>(library, "Tcl_CreateInterp");
                Init = Symbol<Initializer>(library, "Tcl_Init");
                Delete = Symbol<DeleteInterpreter>(library, "Tcl_DeleteInterp");
                Result = Symbol<GetResult>(library, "Tcl_GetStringResult");
                Evaluate = Symbol<EvaluateScript>(library, "Tcl_Eval");
                GetVar = Symbol<ReadVariable>(library, "Tcl_GetVar");
                SetVar = Symbol<WriteVariable>(library, "Tcl_SetVar");
                UnsetVar = Symbol<DeleteVariable>(library, "Tcl_UnsetVar");
                TkInit = new Lazy<Initializer>(() => Symbol<Initializer>(Load("TYPHON_TK_LIBRARY", OperatingSystem.IsWindows()
                    ? major >= 9 ? ["tk90.dll"] : ["tk86t.dll"] : OperatingSystem.IsMacOS() ? [$"libtk{Version}.dylib"] : [$"libtk{Version}.so"]), "Tk_Init"));
            }

            static T Symbol<T>(IntPtr library, string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

            static IntPtr Load(string variable, string[] names)
            {
                string? explicitPath = Environment.GetEnvironmentVariable(variable);
                foreach (string name in explicitPath == null ? names : new[] { explicitPath })
                    if (NativeLibrary.TryLoad(name, out var library)) return library;
                throw new ImportError("a compatible Tcl/Tk native library is unavailable");
            }
        }
    }
}
