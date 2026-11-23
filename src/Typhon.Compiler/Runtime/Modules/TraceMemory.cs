// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    internal static class _tracemalloc
    {
        sealed class Allocation(object owner, long size, (string, long)[] stack, long frameCount)
        {
            internal readonly WeakReference<object> Owner = new(owner);
            internal readonly long Size = size;
            internal readonly (string, long)[] Stack = stack;
            internal readonly long FrameCount = frameCount;
        }

        static readonly object Gate = new();
        static readonly List<Allocation> Allocations = [];
        static ConditionalWeakTable<object, Allocation> _objects = new();
        static volatile bool _active;
        static long _limit = 1;
        static long _current, _peak;
        static int _generation;

        public static void start(long nframe = 1)
        {
            if (nframe < 1 || nframe > 65535) throw new ArgumentException("the number of frames must be in range [1; 65535]");
            lock (Gate)
            {
                if (_active) return;
                _limit = nframe;
                _active = true;
            }
        }

        public static bool is_tracing() => _active;
        public static long get_traceback_limit() { lock (Gate) return _limit; }

        public static void stop()
        {
            lock (Gate)
            {
                _active = false;
                Clear();
            }
        }

        public static void clear_traces() { lock (Gate) Clear(); }

        static void Clear()
        {
            Allocations.Clear();
            _objects = new ConditionalWeakTable<object, Allocation>();
            _current = _peak = 0;
        }

        static void Sweep()
        {
            Allocations.RemoveAll(allocation =>
            {
                if (allocation.Owner.TryGetTarget(out _)) return false;
                _current -= allocation.Size;
                return true;
            });
            _generation = GC.CollectionCount(0);
        }

        internal static void Record(object owner, long size)
        {
            if (!_active || size == 0) return;
            lock (Gate)
            {
                if (!_active) return;
                if (_generation != GC.CollectionCount(0)) Sweep();
                var frames = new List<(string, long)>();
                long total = 0;
                foreach (StackFrame frame in new StackTrace(fNeedFileInfo: true).GetFrames())
                    if (PyFrames.Of(frame) is { } python)
                    {
                        if (total < _limit) frames.Add((python.file, python.line));
                        total++;
                    }
                if (frames.Count == 0) frames.Add(("<unknown>", 0));
                var allocation = new Allocation(owner, size, frames.ToArray(), Math.Max(1, total));
                _objects.Add(owner, allocation);
                Allocations.Add(allocation);
                _current += size;
                _peak = Math.Max(_peak, _current);
            }
        }

        public static (long, long) get_traced_memory()
        {
            lock (Gate)
            {
                if (!_active) return (0, 0);
                Sweep();
                return (_current, _peak);
            }
        }

        public static void reset_peak()
        {
            lock (Gate)
            {
                if (!_active) return;
                Sweep();
                _peak = _current;
            }
        }

        public static long get_tracemalloc_memory()
        {
            lock (Gate)
                return Allocations.Capacity * (long)IntPtr.Size +
                    Allocations.Sum(entry => (entry.Stack.Length * 2L + 5) * IntPtr.Size);
        }

        public static PyTuple<(string, long)>? _get_object_traceback(object? value)
        {
            lock (Gate)
                return value != null && _objects.TryGetValue(value, out var allocation)
                    ? new PyTuple<(string, long)>(allocation.Stack) : null;
        }

        public static List<(long, long, PyTuple<(string, long)>, long)> _get_traces()
        {
            lock (Gate)
            {
                Sweep();
                return Allocations.Select(entry => (0L, entry.Size,
                    new PyTuple<(string, long)>(entry.Stack), entry.FrameCount)).ToList();
            }
        }
    }
}
