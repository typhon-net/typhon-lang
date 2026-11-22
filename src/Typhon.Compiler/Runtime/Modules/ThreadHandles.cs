// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Typhon.Runtime;

    internal static partial class _thread
    {
        static readonly object HandlesLock = new object();
        static readonly HashSet<_ThreadHandle> NonDaemonHandles = new();
        static Thread? MainThread;

        internal static void SetMainThread() => Interlocked.CompareExchange(ref MainThread, Thread.CurrentThread, null);
        public static long _get_main_thread_ident() => (MainThread ?? Thread.CurrentThread).ManagedThreadId;
        public static bool _is_main_interpreter() => true;
        public static void set_name(string name) => Thread.CurrentThread.Name = name;

        public static _ThreadHandle _make_thread_handle(long ident)
        {
            Thread? attached = ident == Environment.CurrentManagedThreadId ? Thread.CurrentThread :
                ident == MainThread?.ManagedThreadId ? MainThread : null;
            return new _ThreadHandle(ident, attached);
        }

        public static _ThreadHandle start_joinable_thread(object? function, _ThreadHandle? handle = null, bool daemon = true)
        {
            if (!Builtins.callable(function)) throw new InvalidCastException("first arg must be callable");
            var selected = handle ?? new _ThreadHandle();
            selected.Start(function, daemon);
            return selected;
        }

        public static object? _shutdown()
        {
            while (true)
            {
                _ThreadHandle? pending;
                lock (HandlesLock)
                    pending = NonDaemonHandles.FirstOrDefault(handle => handle.ident != Environment.CurrentManagedThreadId);
                if (pending == null) return null;
                pending.join();
            }
        }

        internal sealed class _ThreadHandle
        {
            readonly object _gate = new object();
            Thread? _attached;
            bool _started;
            bool _done;
            long _ident;

            public _ThreadHandle() { }

            internal _ThreadHandle(long ident, Thread? attached)
            {
                _ident = ident;
                _attached = attached;
                _started = true;
            }

            public long ident => Interlocked.Read(ref _ident);

            internal void Start(object? function, bool daemon)
            {
                lock (_gate)
                {
                    if (_started) throw new InvalidOperationException("thread already started");
                    var worker = new Thread(() => Run(function)) { IsBackground = daemon };
                    Interlocked.Exchange(ref _ident, worker.ManagedThreadId);
                    _started = true;
                    if (!daemon)
                        lock (HandlesLock) NonDaemonHandles.Add(this);
                    try { worker.Start(); }
                    catch
                    {
                        lock (HandlesLock) NonDaemonHandles.Remove(this);
                        _started = false;
                        Interlocked.Exchange(ref _ident, 0);
                        throw;
                    }
                }
            }

            void Run(object? function)
            {
                Interlocked.Increment(ref _running);
                try
                {
                    contextvars.EnterThread();
                    PyWait.Call(function, Array.Empty<object?>());
                }
                catch (SystemExit) { }
                catch (Exception error) { PyWait.Unraisable($"Exception ignored in thread started by: {Builtins.repr(function)}", error); }
                finally
                {
                    Interlocked.Decrement(ref _running);
                    _set_done();
                }
            }

            public bool is_done()
            {
                lock (_gate) return _done || _attached != null && !_attached.IsAlive;
            }

            public void join(double? timeout = null)
            {
                lock (_gate)
                {
                    if (!_started) throw new InvalidOperationException("thread not started");
                    if (_done) return;
                    if (ident == Environment.CurrentManagedThreadId) throw new InvalidOperationException("Cannot join current thread");
                    if (_attached == null)
                    {
                        PyWait.Until(_gate, () => _done, timeout);
                        return;
                    }
                    // An attached thread does not signal its natural exit through this handle. Check its liveness
                    // between monitor waits so _set_done can also wake a joiner before the attached thread exits.
                    long? deadline = timeout.HasValue ? PyWait.Deadline(timeout.Value) : null;
                    while (!_done && _attached.IsAlive)
                    {
                        double remaining = deadline.HasValue ? PyWait.Left(deadline.Value) : 0.01;
                        if (remaining <= 0) return;
                        Monitor.Wait(_gate, (int)Math.Clamp(Math.Ceiling(remaining * 1000), 1, 10));
                    }
                    _done = true;
                }
            }

            public void _set_done()
            {
                lock (_gate)
                {
                    if (!_started) throw new InvalidOperationException("thread not started");
                    _done = true;
                    Monitor.PulseAll(_gate);
                }
                lock (HandlesLock) NonDaemonHandles.Remove(this);
            }
        }
    }
}
