// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    internal static partial class Builtins
    {
        static readonly ConditionalWeakTable<Type, ReprDescriptor> ReprDescriptors = new();

        static ReprDescriptor ReprSlot(Type receiver) => ReprDescriptors.GetValue(ReprOwner(receiver), owner => new ReprDescriptor(owner));

        static Type ReprOwner(Type receiver)
        {
            if (PyDictViews.CanonicalType(receiver) is Type view) return view;
            if (IsIntType(receiver)) return typeof(long);
            if (receiver == typeof(float)) return typeof(double);
            if (receiver == typeof(ITuple) || receiver == typeof(ValueTuple)) return typeof(ITuple);
            foreach (Type candidate in Ancestors(receiver))
            {
                Type definition = candidate.IsGenericType ? candidate.GetGenericTypeDefinition() : candidate;
                if (definition == typeof(List<>)) return typeof(List<object>);
                if (definition == typeof(Dictionary<,>) || definition == typeof(OrderedDictionary<,>))
                    return typeof(OrderedDictionary<object, object>);
                if (definition == typeof(HashSet<>)) return typeof(HashSet<object>);
                if (definition == typeof(frozenset<>)) return typeof(frozenset<object>);
                if (definition == typeof(frozendict<,>)) return typeof(frozendict<object, object>);
                if (definition == typeof(PyTuple<>) || candidate.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true)
                    return typeof(ITuple);
                if (candidate == typeof(string) || candidate == typeof(bool) || candidate == typeof(double) ||
                    candidate == typeof(Complex) || candidate == typeof(PyBytes) || candidate == typeof(PyByteArray) ||
                    candidate == typeof(Typhon.Stdlib.types.NoneType))
                    return candidate;
                if (candidate.GetMethod("__repr__", InstanceMembers | BindingFlags.DeclaredOnly, binder: null,
                    types: Type.EmptyTypes, modifiers: null) != null)
                    return definition;
            }
            return typeof(object);
        }

        internal sealed class ReprDescriptor : PyFunction, IPyRepr
        {
            internal ReprDescriptor(Type owner) : base("__repr__")
            {
                __objclass__ = owner;
                __qualname__ = PyTypeName(owner) + ".__repr__";
                __module__ = __module_of(owner);
                __doc__ = "Return repr(self).";
            }

            public Type __objclass__ { get; }

            public object __get__(object? instance, Type? owner = null)
            {
                if (instance == null)
                {
                    if (owner == null) throw new InvalidCastException("__get__(None, None) is invalid");
                    return this;
                }
                if (!IsSubclass(instance.GetType(), __objclass__))
                    throw new InvalidCastException($"descriptor '__repr__' for '{PyTypeName(__objclass__)}' objects doesn't apply to a '{PyTypeName(instance.GetType())}' object");
                return new Typhon.Stdlib.types.MethodType(this, instance);
            }

            public override object? __call__(object?[] args)
            {
                Type owner = __objclass__;
                if (args.Length == 0)
                    throw new InvalidCastException($"descriptor '__repr__' of '{PyTypeName(owner)}' object needs an argument");
                object? self = args[0];
                Type receiver = self?.GetType() ?? typeof(Typhon.Stdlib.types.NoneType);
                if (!IsSubclass(receiver, owner))
                    throw new InvalidCastException($"descriptor '__repr__' requires a '{PyTypeName(owner)}' object but received a '{PyTypeName(receiver)}'");
                if (args.Length != 1) throw new InvalidCastException($"expected 0 arguments, got {args.Length - 1}");
                if (owner == typeof(object))
                    return $"<{PythonQualifiedTypeName(receiver)} object at 0x{id(self!):x}>";
                if (owner == typeof(long) && self is bool flag) return flag ? "1" : "0";
                if (owner == typeof(ITuple) || owner == typeof(List<object>) || owner == typeof(HashSet<object>) ||
                    owner == typeof(OrderedDictionary<object, object>))
                    return CollectionRepr(self!)!;
                return repr(self);
            }

            public string __repr__() => $"<slot wrapper '__repr__' of '{PyTypeName(__objclass__)}' objects>";
            public override string ToString() => __repr__();
        }
    }
}
