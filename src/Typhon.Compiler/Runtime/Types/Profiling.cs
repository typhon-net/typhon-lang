// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using Typhon.Stdlib;

    internal static class PyProfiling
    {
        [ThreadStatic] public static object? Callback;
        [ThreadStatic] internal static IProfileObserver? Observer;
        [ThreadStatic] static Scope? _current;
        [ThreadStatic] static bool _dispatching;
        internal static bool Active => Callback != null || Observer != null;

        internal interface IProfileObserver
        {
            object? Enter(sys_.frame frame);
            void Leave(object? state);
        }

        internal sealed class Scope : IDisposable
        {
            internal readonly Scope? Parent;
            internal readonly sys_.frame Frame;
            internal readonly IProfileObserver? Observer;
            internal object? Sample, Result;

            internal Scope(sys_.frame frame, Scope? parent, IProfileObserver? observer)
            { Frame = frame; Parent = parent; Observer = observer; }

            public void Dispose()
            {
                try { Dispatch(this, entering: false); }
                finally { _current = Parent; }
            }
        }

        public static Scope? Enter(string name, string qualifier, string path, int line, string module)
        {
            if (_dispatching || !Active) return null;
            var parent = _current;
            var frame = new sys_.frame(new types.CodeType(name, path, line, qualname: qualifier), module, parent?.Frame);
            var scope = new Scope(frame, parent, Observer);
            _current = scope;
            try { Dispatch(scope, entering: true); return scope; }
            catch { _current = parent; throw; }
        }

        public static T Result<T>(Scope? scope, T value)
        {
            if (scope != null) scope.Result = value;
            return value;
        }

        static void Dispatch(Scope scope, bool entering)
        {
            _dispatching = true;
            try
            {
                if (entering) scope.Sample = scope.Observer?.Enter(scope.Frame);
                else scope.Observer?.Leave(scope.Sample);
                if (Callback is object callback)
                {
                    try { Builtins.__invoke(callback, [scope.Frame, entering ? "call" : "return", scope.Result]); }
                    catch { Callback = null; throw; }
                }
            }
            finally { _dispatching = false; }
        }
    }
}
