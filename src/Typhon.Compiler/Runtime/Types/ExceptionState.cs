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
        object? _attributeName;
        object? _attributeObject;

        internal static Exception? Handling => Active.Value;

        internal static bool IsAttributeErrorMember(string name) => name is "name" or "obj";

        internal static bool HasAttribute(Exception error, string name) =>
            name is "args" or "__cause__" or "__context__" or "__suppress_context__" or "__traceback__" ||
            error is MissingMemberException && IsAttributeErrorMember(name);

        internal static MissingMemberException CreateAttributeError(object?[] args, object? name, object? obj)
        {
            args = PyExc.Norm(args);
            var error = new MissingMemberException(PyExc.Text(args));
            error.Data["__args__"] = args;
            var state = States.GetOrCreateValue(error);
            state._attributeName = name;
            state._attributeObject = obj;
            return error;
        }

        internal static object? GetAttribute(Exception error, string name)
        {
            States.TryGetValue(error, out var state);
            return name switch
            {
                "args" => PyExc.Args(error),
                "__cause__" => state?._cause,
                "__context__" => state?._context,
                "__suppress_context__" => state?._suppressContext ?? false,
                "__traceback__" => GetTraceback(error),
                "name" => state?._attributeName,
                "obj" => state?._attributeObject,
                _ => throw new MissingMemberException(name),
            };
        }

        internal static Traceback? GetTraceback(Exception error) =>
            States.TryGetValue(error, out var state) && state._tracebackAssigned ? state._traceback : Traceback.Of(error);

        internal static void SetAttribute(Exception error, string name, object? value)
        {
            if (name == "args")
            {
                PyExc.SetArgs(error, value);
                return;
            }
            var state = States.GetOrCreateValue(error);
            switch (name)
            {
                case "name":
                    state._attributeName = value;
                    return;
                case "obj":
                    state._attributeObject = value;
                    return;
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
        public static MissingMemberException __attribute_error(object?[] args, IDictionary<string, object?>? keywords = null)
        {
            object? name = null, obj = null;
            if (keywords != null)
                foreach (var (key, value) in keywords)
                    switch (key)
                    {
                        case "name": name = value; break;
                        case "obj": obj = value; break;
                        default: throw new InvalidCastException($"AttributeError() got an unexpected keyword argument '{key}'");
                    }
            return PyExceptionState.CreateAttributeError(args, name, obj);
        }

        public static void add_note(this Exception error, object? note)
        {
            if (note is not (string or PyString or PyStrEnum))
                throw new InvalidCastException($"add_note() argument must be str, not {(note == null ? "NoneType" : PyTypeName(note.GetType()))}");
            if (!TryGetAttr(error, "__notes__", out object? notes))
            {
                setattr(error, "__notes__", new List<object?> { note });
                return;
            }
            if (notes is not System.Collections.IList list || list.IsFixedSize || list.IsReadOnly)
                throw new InvalidCastException("Cannot add note: __notes__ is not a list");
            list.Add(list is List<string> ? str(note) : note);
        }

        public static IDisposable __handling_exception(Exception error) => new PyExceptionState.Handler(error);

        public static Exception __reraise()
        {
            if (PyExceptionState.Handling is not Exception error)
                return new InvalidOperationException("No active exception to reraise");
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw new InvalidOperationException();
        }

        public static Exception __prepare_exception(object? value, object? cause, bool explicitCause)
        {
            var error = ExceptionInstance(value);
            PyExceptionState.Chain(error, PyExceptionState.Handling);
            if (explicitCause)
                PyExceptionState.SetAttribute(error, "__cause__", cause == null ? null : ExceptionInstance(cause));
            return error;
        }

        public static T __exception_args<T>(object?[] args) where T : Exception
        {
            var error = (T)Activator.CreateInstance(typeof(T), [PyExc.Text(args)])!;
            error.Data["__args__"] = args;
            return error;
        }

        static Exception ExceptionInstance(object? value)
        {
            if (value is Type type && typeof(Exception).IsAssignableFrom(type)) value = __invoke(type, []);
            return value as Exception ?? throw new InvalidCastException("exceptions must derive from BaseException");
        }
    }
}
