// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Stdlib;

    internal static class PyMonitoring
    {
        public const long DEBUGGER_ID = 0, COVERAGE_ID = 1, PROFILER_ID = 2, OPTIMIZER_ID = 5;
        public static readonly object DISABLE = new(), MISSING = new();
        internal const long Start = 1L << 0, Return = 1L << 2, Line = 1L << 5, Raise = 1L << 11, Unwind = 1L << 13;
        const long AllEvents = (1L << 19) - 1;
        const long GlobalOnly = (1L << 12) | (1L << 14) | Raise | Unwind;


        public static readonly types.SimpleNamespace events = new(new OrderedDictionary<string, object?>
        {
            ["NO_EVENTS"] = 0L,
            ["PY_START"] = Start, ["PY_RESUME"] = 1L << 1, ["PY_RETURN"] = Return, ["PY_YIELD"] = 1L << 3,
            ["CALL"] = 1L << 4, ["LINE"] = Line, ["INSTRUCTION"] = 1L << 6, ["JUMP"] = 1L << 7,
            ["BRANCH_LEFT"] = 1L << 8, ["BRANCH_RIGHT"] = 1L << 9, ["STOP_ITERATION"] = 1L << 10,
            ["RAISE"] = Raise, ["EXCEPTION_HANDLED"] = 1L << 12, ["PY_UNWIND"] = Unwind,
            ["PY_THROW"] = 1L << 14, ["RERAISE"] = 1L << 15, ["C_RETURN"] = 1L << 16, ["C_RAISE"] = 1L << 17,
            ["BRANCH"] = 1L << 18,
        });

        sealed class LocalMask { internal long Events; }

        sealed class Tool
        {
            internal string? Name;
            internal long Events;
            internal bool HasLocalEvents;
            internal readonly Dictionary<long, object> Callbacks = new();
            internal readonly ConditionalWeakTable<types.CodeType, LocalMask> Local = new();
            internal ConditionalWeakTable<types.CodeType, HashSet<(long Event, long Offset)>> Disabled = new();
        }

        static readonly object Sync = new();
        static readonly Tool?[] Tools = new Tool?[6];

        internal static bool Active
        {
            get
            {
                lock (Sync) return Tools.Any(tool => tool != null && (tool.Events != 0 || tool.HasLocalEvents));
            }
        }

        static int ToolIndex(long toolId) => toolId >= 0 && toolId < Tools.Length ? (int)toolId :
            throw new ArgumentException($"invalid tool {toolId} (must be between 0 and 5)");

        static Tool ToolInUse(long toolId) => Tools[ToolIndex(toolId)] is { Name: not null } tool ? tool :
            throw new ArgumentException($"tool {toolId} is not in use");

        static long EventMask(long mask) => (mask & ~AllEvents) == 0 ? mask :
            throw new ArgumentException($"invalid event set 0x{mask:x}");

        public static void use_tool_id(long tool_id, string name)
        {
            lock (Sync)
            {
                var tool = Tools[ToolIndex(tool_id)] ??= new Tool();
                if (tool.Name != null) throw new ArgumentException($"tool {tool_id} is already in use");
                tool.Name = name;
            }
        }

        public static void clear_tool_id(long tool_id)
        {
            lock (Sync)
            {
                int index = ToolIndex(tool_id);
                if (Tools[index] is { } tool) Tools[index] = new Tool { Name = tool.Name };
            }
        }

        public static void free_tool_id(long tool_id)
        {
            lock (Sync) Tools[ToolIndex(tool_id)] = null;
        }

        public static string? get_tool(long tool_id)
        {
            lock (Sync) return Tools[ToolIndex(tool_id)]?.Name;
        }

        public static long get_events(long tool_id)
        {
            lock (Sync) return Tools[ToolIndex(tool_id)]?.Events ?? 0;
        }

        public static void set_events(long tool_id, long @event_set)
        {
            lock (Sync) ToolInUse(tool_id).Events = EventMask(event_set);
        }

        public static long get_local_events(long tool_id, object code)
        {
            if (code is not types.CodeType pythonCode) throw new InvalidCastException("code must be a code object");
            lock (Sync)
                return Tools[ToolIndex(tool_id)] is { } tool && tool.Local.TryGetValue(pythonCode, out var local)
                    ? local.Events : 0;
        }

        public static void set_local_events(long tool_id, object code, long @event_set)
        {
            if (code is not types.CodeType pythonCode) throw new InvalidCastException("code must be a code object");
            lock (Sync)
            {
                var tool = ToolInUse(tool_id);
                long local = EventMask(event_set) & ~GlobalOnly;
                tool.Local.GetOrCreateValue(pythonCode).Events = local;
                tool.HasLocalEvents |= local != 0;
            }
        }

        public static object? register_callback(long tool_id, long @event, object? func)
        {
            if (@event == 0 || (@event & (@event - 1)) != 0)
                throw new ArgumentException("The callback can only be set for one event at a time");
            if ((@event & ~AllEvents) != 0) throw new ArgumentException($"invalid event {@event}");
            lock (Sync)
            {
                var tool = Tools[ToolIndex(tool_id)] ??= new Tool();
                tool.Callbacks.TryGetValue(@event, out var previous);
                if (func == null) tool.Callbacks.Remove(@event);
                else tool.Callbacks[@event] = func;
                return previous;
            }
        }

        public static void restart_events()
        {
            lock (Sync)
                foreach (var tool in Tools)
                    if (tool != null) tool.Disabled = new();
        }

        internal static void Notify(sys_.frame frame, long @event, Func<object?[]> arguments)
        {
            if (PyTracing.InCallback) return;
            types.CodeType code = frame.f_code;
            var point = (@event, frame.f_lasti);
            List<(Tool Tool, object Callback)>? observers = null;
            lock (Sync)
            {
                foreach (var tool in Tools)
                {
                    if (tool == null || !tool.Callbacks.TryGetValue(@event, out var callback)) continue;
                    long mask = tool.Events | (tool.Local.TryGetValue(code, out var local) ? local.Events : 0);
                    if ((mask & @event) == 0 || tool.Disabled.TryGetValue(code, out var disabled) && disabled.Contains(point)) continue;
                    (observers ??= new()).Add((tool, callback));
                }
            }
            if (observers == null) return;
            var args = arguments();
            foreach (var (tool, callback) in observers)
            {
                if (!ReferenceEquals(PyTracing.InvokeObserver(callback, args), DISABLE)) continue;
                if ((@event & GlobalOnly) != 0) throw new ArgumentException("cannot disable this event");
                lock (Sync) tool.Disabled.GetOrCreateValue(code).Add(point);
            }
        }
    }
}
