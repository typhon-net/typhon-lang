// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Traceback = Typhon.Stdlib.types.TracebackType;

    internal sealed class PyExceptionState
    {
        static readonly ConditionalWeakTable<Exception, PyExceptionState> States = new();
        static readonly AsyncLocal<Exception?> Active = new();

        Exception? _cause;
        Exception? _context;
        bool _suppressContext;
        Traceback? _traceback;
        bool _tracebackAssigned;

        internal static Exception? Handling => Active.Value;

        internal static bool HasAttribute(string name) =>
            name is "__cause__" or "__context__" or "__suppress_context__" or "__traceback__";

        internal static object? GetAttribute(Exception error, string name)
        {
            States.TryGetValue(error, out var state);
            return name switch
            {
                "__cause__" => state?._cause,
                "__context__" => state?._context,
                "__suppress_context__" => state?._suppressContext ?? false,
                "__traceback__" => GetTraceback(error),
                _ => throw new MissingMemberException(name),
            };
        }

        internal static Traceback? GetTraceback(Exception error) =>
            States.TryGetValue(error, out var state) && state._tracebackAssigned ? state._traceback : Traceback.Of(error);

        internal static void SetAttribute(Exception error, string name, object? value)
        {
            var state = States.GetOrCreateValue(error);
            switch (name)
            {
                case "__cause__":
                    state._cause = ExceptionOrNone(value, "cause");
                    state._suppressContext = true;
                    return;
                case "__context__":
                    state._context = ExceptionOrNone(value, "context");
                    return;
                case "__suppress_context__":
                    state._suppressContext = value is bool suppress ? suppress :
                        throw new InvalidCastException("attribute value type must be bool");
                    return;
                case "__traceback__":
                    state._traceback = value == null ? null : value as Traceback ??
                        throw new InvalidCastException("__traceback__ must be a traceback or None");
                    state._tracebackAssigned = true;
                    return;
            }
        }

        static Exception? ExceptionOrNone(object? value, string name) => value == null ? null : value as Exception ??
            throw new InvalidCastException($"exception {name} must be None or derive from BaseException");

        internal static void Chain(Exception error, Exception? context)
        {
            if (context == null || ReferenceEquals(error, context)) return;
            var seen = new HashSet<Exception>();
            for (var previous = context; seen.Add(previous) && States.TryGetValue(previous, out var state);)
            {
                if (ReferenceEquals(state._context, error)) { state._context = null; break; }
                if (state._context is not Exception next) break;
                previous = next;
            }
            States.GetOrCreateValue(error)._context = context;
        }

        internal sealed class Handler : IDisposable
        {
            readonly Exception? _outer = Handling;

            internal Handler(Exception error)
            {
                if (Active.Value is { } outer &&
                    (!States.TryGetValue(error, out var state) || state._context == null))
                    Chain(error, outer);
                Active.Value = error;
            }

            public void Dispose() => Active.Value = _outer;
        }
    }

    internal static partial class Builtins
    {
        public static IDisposable __handling_exception(Exception error) => new PyExceptionState.Handler(error);

        public static Exception __prepare_exception(object? value, object? cause, bool explicitCause)
        {
            var error = ExceptionInstance(value);
            PyExceptionState.Chain(error, PyExceptionState.Handling);
            if (explicitCause)
                PyExceptionState.SetAttribute(error, "__cause__", cause == null ? null : ExceptionInstance(cause));
            return error;
        }

        static Exception ExceptionInstance(object? value)
        {
            if (value is Type type && typeof(Exception).IsAssignableFrom(type)) value = __invoke(type, []);
            return value as Exception ?? throw new InvalidCastException("exceptions must derive from BaseException");
        }
    }
}
