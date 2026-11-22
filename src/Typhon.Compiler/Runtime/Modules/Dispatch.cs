// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static partial class functools
    {
        public static dynamic singledispatch(object function) => new SingleDispatch(function);

        internal sealed class SingleDispatch : PyFunction
        {
            readonly OrderedDictionary<Type, object?> _registry = new();

            internal SingleDispatch(object function) : base(Builtins.__name_of(function))
            {
                _registry[typeof(object)] = function;
                registry = new types.MappingProxyType(_registry);
                update_wrapper(this, function);
                _attrs = new Dictionary<string, object?>
                {
                    ["register"] = new PyBoundMethod(this, "register"),
                    ["dispatch"] = new PyBoundMethod(this, "dispatch"),
                    ["registry"] = registry,
                };
            }

            public types.MappingProxyType registry { get; }

            public dynamic register(Type cls, object? function = null)
            {
                if (function == null)
                    return new PyClosure("register", (args, keywords) =>
                        args.Length == 1 && (keywords == null || keywords.Count == 0) ? register(cls, args[0]) :
                        throw PyClosure.ArgCountError("register", 1, 1, args.Length));
                if (!Builtins.callable(function)) throw new InvalidCastException("functools.singledispatch expected a callable");
                lock (_registry) _registry[cls] = function;
                return function;
            }

            public object? dispatch(Type cls)
            {
                lock (_registry)
                {
                    for (Type? ancestor = cls; ancestor != null && ancestor != typeof(object); ancestor = ancestor.BaseType)
                        if (_registry.TryGetValue(ancestor, out var match)) return match;
                    var candidates = _registry.Keys.Where(t => t != typeof(object) && Builtins.issubclass(cls, t)).ToList();
                    var mostSpecific = candidates.Where(t => !candidates.Any(u => u != t && Builtins.issubclass(u, t) &&
                        !Builtins.issubclass(t, u))).ToList();
                    if (mostSpecific.Count > 1) throw new InvalidOperationException("ambiguous dispatch");
                    return _registry[mostSpecific.FirstOrDefault() ?? typeof(object)];
                }
            }

            public override object? __call__(object?[] args) => __call_kw__(args, null);

            public override object? __call_kw__(object?[] args, IDictionary<string, object?>? kw)
            {
                if (args.Length == 0) throw new InvalidCastException($"{__name__} requires at least 1 positional argument");
                return Builtins.__invoke_kw(dispatch(Builtins.__type_of(args[0])), args, kw);
            }
        }
    }
}
