// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Typhon.Stdlib;

    internal static class PyTracing
    {
        [ThreadStatic] static Scope? _current;
        [ThreadStatic] static bool _dispatching;
        [ThreadStatic] static object? _callback;
        [ThreadStatic] static long _version;
        static object? _allThreads;
        static long _allVersion;
        static readonly object AllThreadsLock = new();

        internal static object? Callback
        {
            get
            {
                long version = Interlocked.Read(ref _allVersion);
                if (_version != version)
                {
                    lock (AllThreadsLock)
                    {
                        _callback = _allThreads;
                        _version = _allVersion;
                    }
                }
                return _callback;
            }
            set
            {
                _version = Interlocked.Read(ref _allVersion);
                _callback = value;
            }
        }

        internal static bool Active => Callback != null;
        internal static bool InCallback => _dispatching;
        static readonly ConditionalWeakTable<Assembly, ConcurrentDictionary<(string Path, string Name, int Line), TraceCode>> Code = new();

        internal static void SetAll(object? callback)
        {
            lock (AllThreadsLock)
            {
                _allThreads = callback;
                Interlocked.Increment(ref _allVersion);
            }
        }

        internal static sys_.frame? GetFrame(long depth)
        {
            if (depth < 0) return null;
            sys_.frame? frame = _current?.Frame;
            while (depth-- > 0 && frame != null) frame = frame.f_back;
            return frame;
        }

        public static Scope Enter(Assembly assembly, string name, string qualifier, string path, int line, string module, int[] lines)
        {
            var parent = _current;
            var code = Code.GetOrCreateValue(assembly).GetOrAdd((path, qualifier, line), _ => new TraceCode(name, qualifier, path, line, lines));
            var frame = new sys_.frame(code, module, parent?.Frame);
            var scope = new Scope(frame, parent);
            _current = scope;
            try
            {
                if (Callback is object callback && !_dispatching)
                    frame.f_trace = Invoke(callback, frame, "call", null);
                PyMonitoring.Notify(frame, PyMonitoring.Start, () => [code, frame.f_lasti]);
                return scope;
            }
            catch
            {
                _current = parent;
                throw;
            }
        }

        static object? Invoke(object hook, sys_.frame frame, string name, object? value)
        {
            try { return InvokeObserver(hook, [frame, name, value]); }
            catch
            {
                Callback = null;
                frame.f_trace = null;
                throw;
            }
        }

        internal static object? InvokeObserver(object hook, object?[] args)
        {
            bool wasDispatching = _dispatching;
            _dispatching = true;
            try { return Builtins.__invoke(hook, args); }
            finally { _dispatching = wasDispatching; }
        }

        // The source statement reports the first header event. Report subsequent checks as iteration resumes.
        public static IEnumerable<T> Iterate<T>(IEnumerable<T> source, Scope scope, long line)
        {
            if (source is IList<T> list)
            {
                for (int index = 0; index < list.Count; index++)
                {
                    yield return list[index];
                    scope.Line(line);
                }
                yield break;
            }
            foreach (T value in source)
            {
                yield return value;
                scope.Line(line);
            }
        }

        internal sealed class TraceCode : types.CodeType
        {
            readonly int[] _lines;

            public TraceCode(string name, string qualifier, string file, int line, int[] lines)
                : base(name, file, line, qualname: qualifier) => _lines = lines;

            internal long LineOffset(long line)
            {
                int position = Array.BinarySearch(_lines, checked((int)line));
                return position >= 0 ? position : 0;
            }

            public override IEnumerable<(long, long, long?)> co_lines()
            {
                for (int offset = 0; offset < _lines.Length; offset++)
                    yield return (offset, offset + 1L, _lines[offset]);
            }
        }

        internal sealed class Scope : IDisposable
        {
            readonly Scope? _parent;
            internal readonly sys_.frame Frame;
            internal object? Result;
            bool _unwinding;

            internal Scope(sys_.frame frame, Scope? parent) { Frame = frame; _parent = parent; }

            public void Line(long line)
            {
                Frame.f_lineno = line;
                Frame.f_lasti = ((TraceCode)Frame.f_code).LineOffset(line);
                if (Frame.f_trace_lines) Dispatch("line", null);
                PyMonitoring.Notify(Frame, PyMonitoring.Line, () => [Frame.f_code, line]);
            }

            public void Unwind(Exception error)
            {
                Result = null;
                _unwinding = true;
                if (CanNotify())
                    Dispatch("exception", (error.GetType(), error, PyExceptionState.GetTraceback(error)));
                PyMonitoring.Notify(Frame, PyMonitoring.Raise, () => [Frame.f_code, Frame.f_lasti, error]);
                PyMonitoring.Notify(Frame, PyMonitoring.Unwind, () => [Frame.f_code, Frame.f_lasti, error]);
            }

            bool CanNotify() => !_dispatching && Active && Frame.f_trace != null;

            void Dispatch(string name, object? value)
            {
                if (!CanNotify()) return;
                object? next = Invoke(Frame.f_trace!, Frame, name, value);
                if (next != null) Frame.f_trace = next;
            }

            public void Dispose()
            {
                try
                {
                    Dispatch("return", Result);
                    if (!_unwinding)
                        PyMonitoring.Notify(Frame, PyMonitoring.Return, () => [Frame.f_code, Frame.f_lasti, Result]);
                }
                finally { _current = _parent; }
            }
        }

        public static T Return<T>(Scope scope, T value)
        {
            scope.Result = value;
            return value;
        }
    }
}
