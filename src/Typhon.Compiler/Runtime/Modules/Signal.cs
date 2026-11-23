// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Typhon.Runtime;

    internal static class _signal
    {
        static readonly bool Windows = OperatingSystem.IsWindows();
        static readonly bool Mac = OperatingSystem.IsMacOS();

        public const long SIG_DFL = 0, SIG_IGN = 1;
        public const long SIGINT = 2, SIGILL = 4, SIGFPE = 8, SIGSEGV = 11, SIGTERM = 15;
        public static readonly long SIGABRT = Windows ? 22 : 6;
        public static readonly long NSIG = Windows ? 23 : Mac ? 32 : 65;

        [PyOptionalModuleMember] public static object SIGBREAK = Windows ? (object)21L : PyModule.Unbound;
        [PyOptionalModuleMember] public static object CTRL_C_EVENT = Windows ? (object)0L : PyModule.Unbound;
        [PyOptionalModuleMember] public static object CTRL_BREAK_EVENT = Windows ? (object)1L : PyModule.Unbound;
        [PyOptionalModuleMember] public static object SIGHUP = Unix(1);
        [PyOptionalModuleMember] public static object SIGQUIT = Unix(3);
        [PyOptionalModuleMember] public static object SIGTRAP = Unix(5);
        [PyOptionalModuleMember] public static object SIGKILL = Unix(9);
        [PyOptionalModuleMember] public static object SIGPIPE = Unix(13);
        [PyOptionalModuleMember] public static object SIGALRM = Unix(14);
        [PyOptionalModuleMember] public static object SIGBUS = Unix(Mac ? 10 : 7);
        [PyOptionalModuleMember] public static object SIGUSR1 = Unix(Mac ? 30 : 10);
        [PyOptionalModuleMember] public static object SIGUSR2 = Unix(Mac ? 31 : 12);
        [PyOptionalModuleMember] public static object SIGCHLD = Unix(Mac ? 20 : 17);
        [PyOptionalModuleMember] public static object SIGCONT = Unix(Mac ? 19 : 18);
        [PyOptionalModuleMember] public static object SIGSTOP = Unix(Mac ? 17 : 19);
        [PyOptionalModuleMember] public static object SIGTSTP = Unix(Mac ? 18 : 20);
        [PyOptionalModuleMember] public static object SIGTTIN = Unix(21);
        [PyOptionalModuleMember] public static object SIGTTOU = Unix(22);
        [PyOptionalModuleMember] public static object SIGURG = Unix(Mac ? 16 : 23);
        [PyOptionalModuleMember] public static object SIGXCPU = Unix(24);
        [PyOptionalModuleMember] public static object SIGXFSZ = Unix(25);
        [PyOptionalModuleMember] public static object SIGVTALRM = Unix(26);
        [PyOptionalModuleMember] public static object SIGPROF = Unix(27);
        [PyOptionalModuleMember] public static object SIGWINCH = Unix(28);
        [PyOptionalModuleMember] public static object SIGIO = Unix(Mac ? 23 : 29);
        [PyOptionalModuleMember] public static object SIGSYS = Unix(Mac ? 12 : 31);
        [PyOptionalModuleMember] public static object SIGCLD = Linux(17);
        [PyOptionalModuleMember] public static object SIGIOT = Linux(6);
        [PyOptionalModuleMember] public static object SIGPOLL = Linux(29);
        [PyOptionalModuleMember] public static object SIGPWR = Linux(30);
        [PyOptionalModuleMember] public static object SIGSTKFLT = Linux(16);
        [PyOptionalModuleMember] public static object SIGRTMIN = Linux(34);
        [PyOptionalModuleMember] public static object SIGRTMAX = Linux(64);
        [PyOptionalModuleMember] public static object SIGINFO = Mac ? (object)29L : PyModule.Unbound;
        [PyOptionalModuleMember] public static object SIGEMT = Mac ? (object)7L : PyModule.Unbound;

        static object Unix(long number) => Windows ? PyModule.Unbound : number;
        static object Linux(long number) => OperatingSystem.IsLinux() ? number : PyModule.Unbound;

        static readonly object Gate = new();
        static readonly Dictionary<int, object> Handlers = new();
        static readonly Dictionary<int, PosixSignalRegistration> Registrations = new();
        static readonly SortedSet<int> Pending = new();
        static bool _hasPending;
        static bool _initialized;
        [ThreadStatic] static bool _dispatching;

        static void Initialize()
        {
            if (_initialized) return;
            var module = PyModule.Of(typeof(_signal), "_signal");
            Handlers[(int)SIGINT] = module.ImportAttribute(nameof(default_int_handler))!;
            if (!Windows)
            {
                Handlers[13] = SIG_IGN;
                Handlers[25] = SIG_IGN;
            }
            Register((int)SIGINT);
            _initialized = true;
        }

        static void Register(int number)
        {
            if (Registrations.ContainsKey(number)) return;
            if (Windows && number is not ((int)SIGINT or 21)) return;
            var native = Windows ? number == SIGINT ? PosixSignal.SIGINT : PosixSignal.SIGQUIT : (PosixSignal)number;
            Registrations[number] = PosixSignalRegistration.Create(native, context =>
            {
                lock (Gate)
                {
                    if (!Handlers.TryGetValue(number, out object? handler)) return;
                    context.Cancel = true;
                    if (handler is not SIG_IGN) Enqueue(number);
                }
            });
        }

        static void Enqueue(int number)
        {
            Pending.Add(number);
            Volatile.Write(ref _hasPending, true);
        }

        static bool OnMainThread => _thread._get_main_thread_ident() == Environment.CurrentManagedThreadId;

        public static void default_int_handler(object? signum, object? frame) => throw new KeyboardInterrupt();

        public static HashSet<long> valid_signals() => Windows
            ? new HashSet<long> { SIGINT, SIGILL, SIGFPE, SIGSEGV, SIGTERM, SIGABRT, 21 }
            : new HashSet<long>(Enumerable.Range(1, (int)NSIG - 1).Where(n => Mac || n is not (32 or 33)).Select(n => (long)n));

        public static object? getsignal(object? signalnum)
        {
            int number = SignalNumber(signalnum);
            lock (Gate)
            {
                Initialize();
                return Handlers.TryGetValue(number, out object? handler) ? handler :
                    Windows && !valid_signals().Contains(number) ? null : (object)SIG_DFL;
            }
        }

        public static object signal(object? signalnum, object? handler)
        {
            int number = SignalNumber(signalnum);
            if (!OnMainThread) throw new ArgumentException("signal only works in main thread of the main interpreter");
            bool restore = handler is SIG_DFL || handler is BigInteger defaultValue && defaultValue.IsZero;
            bool ignore = handler is SIG_IGN || handler is BigInteger ignoredValue && ignoredValue.IsOne;
            if (!restore && !ignore && !Builtins.callable(handler))
                throw new InvalidCastException("signal handler must be signal.SIG_IGN, signal.SIG_DFL, or a callable object");
            if (Windows && !valid_signals().Contains(number) || !Windows && number is 9 ||
                !Windows && number == (Mac ? 17 : 19))
                throw new ArgumentException("invalid signal value");
            PosixSignalRegistration? removed = null;
            object previous;
            lock (Gate)
            {
                Initialize();
                previous = Handlers.TryGetValue(number, out object? current) ? current : SIG_DFL;
                if (restore)
                {
                    Handlers.Remove(number);
                    Registrations.Remove(number, out removed);
                }
                else
                {
                    Register(number);
                    Handlers[number] = ignore ? SIG_IGN : handler!;
                }
            }
            removed?.Dispose();
            return previous;
        }

        public static object? raise_signal(object? signalnum)
        {
            int number = SignalNumber(signalnum);
            bool configured;
            lock (Gate)
            {
                Initialize();
                configured = Handlers.TryGetValue(number, out object? handler);
                if (configured && handler is not SIG_IGN) Enqueue(number);
            }
            if (!configured)
            {
                int result = Windows ? RaiseWindows(number) : RaiseUnix(number);
                if (result != 0) throw new ArgumentException("invalid signal value");
            }
            DispatchPending(allowReentry: true);
            return null;
        }

        internal static void Poll() => DispatchPending(allowReentry: false);

        static void DispatchPending(bool allowReentry)
        {
            if (_dispatching && !allowReentry || !Volatile.Read(ref _hasPending) || !OnMainThread) return;
            bool wasDispatching = _dispatching;
            _dispatching = true;
            try
            {
                while (true)
                {
                    int number;
                    object? handler;
                    lock (Gate)
                    {
                        if (Pending.Count == 0) break;
                        number = Pending.Min;
                        Pending.Remove(number);
                        Volatile.Write(ref _hasPending, Pending.Count != 0);
                        Handlers.TryGetValue(number, out handler);
                    }
                    if (handler != null && handler is not SIG_IGN)
                        Builtins.__invoke(handler, [(long)number, sys_._getframe()]);
                }
            }
            finally { _dispatching = wasDispatching; }
        }

        static int SignalNumber(object? value)
        {
            object index = Builtins.__index_value(value);
            BigInteger number = index is BigInteger wide ? wide : (long)index;
            if (number < int.MinValue || number > int.MaxValue) throw new OverflowException("Python int too large to convert to C int");
            if (number <= 0 || number >= NSIG) throw new ArgumentException("signal number out of range");
            return (int)number;
        }

        [DllImport("libc", EntryPoint = "raise")]
        static extern int RaiseUnix(int number);

        [DllImport("ucrtbase.dll", EntryPoint = "raise")]
        static extern int RaiseWindows(int number);
    }
}
