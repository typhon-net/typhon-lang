// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    internal static partial class Builtins
    {
        static readonly Dictionary<Type, ImmutableNewArgumentsDescriptor> NewArgumentsDescriptors =
            new[] { typeof(long), typeof(double), typeof(Complex), typeof(string), typeof(PyBytes), typeof(ITuple) }
                .ToDictionary(owner => owner, owner => new ImmutableNewArgumentsDescriptor(owner));

        static Type? NewArgumentsOwner(Type type)
        {
            if (type == typeof(bool) || IsIntType(type) || typeof(PyInteger).IsAssignableFrom(type)) return typeof(long);
            if (type == typeof(float) || type == typeof(double)) return typeof(double);
            if (type == typeof(Complex)) return typeof(Complex);
            if (type == typeof(string) || typeof(PyString).IsAssignableFrom(type) || typeof(PyStrEnum).IsAssignableFrom(type))
                return typeof(string);
            if (typeof(PyBytes).IsAssignableFrom(type)) return typeof(PyBytes);
            return typeof(ITuple).IsAssignableFrom(type) ? typeof(ITuple) : null;
        }

        static ImmutableNewArgumentsDescriptor? ImmutableNewArguments(Type type) =>
            NewArgumentsOwner(type) is { } owner ? NewArgumentsDescriptors[owner] : null;

        internal sealed class ImmutableNewArgumentsDescriptor : PyFunction
        {
            internal ImmutableNewArgumentsDescriptor(Type owner) : base("__getnewargs__")
            {
                __objclass__ = owner;
                __module__ = "builtins";
                __qualname__ = PyTypeName(owner) + ".__getnewargs__";
            }

            public Type __objclass__ { get; }

            public object __get__(object? instance, Type? owner = null)
            {
                if (instance == null)
                {
                    if (owner == null) throw new InvalidCastException("__get__(None, None) is invalid");
                    return this;
                }
                CheckReceiver(instance);
                return new BoundImmutableNewArguments(this, instance);
            }

            void CheckReceiver(object? self)
            {
                if (self == null || NewArgumentsOwner(self.GetType()) != __objclass__)
                    throw new InvalidCastException($"descriptor '__getnewargs__' requires a '{PyTypeName(__objclass__)}' object");
            }

            public override object? __call__(object?[] args)
            {
                if (args.Length != 1) throw new InvalidCastException("__getnewargs__ requires one receiver");
                CheckReceiver(args[0]);
                return Read(args[0]!);
            }

            public override object? __call_kw__(object?[] args, IDictionary<string, object?>? keywords) =>
                keywords?.Count > 0 ? throw new InvalidCastException("__getnewargs__ takes no keyword arguments") : __call__(args);

            internal static PyTuple<object?> Read(object self)
            {
                if (self is Complex complex) return new PyTuple<object?>([complex.Real, complex.Imaginary]);
                object value = self switch
                {
                    bool flag => flag ? 1L : 0L,
                    PyInteger integer => integer.PrimitiveValue,
                    int or short or byte => Convert.ToInt64(self),
                    float single => (double)single,
                    double number => number,
                    string text => new string(text.AsSpan()),
                    PyString text => new string(text.Text.AsSpan()),
                    PyStrEnum member => new string(member.value.AsSpan()),
                    PyBytes bytes => new PyBytes((byte[])bytes.Data.Clone()),
                    ITuple tuple when __type_of(self) != typeof(ITuple) =>
                        new PyTuple<object?>(Enumerable.Range(0, tuple.Length).Select(index => tuple[index])),
                    _ => self,
                };
                return new PyTuple<object?>([value]);
            }
        }

        internal sealed class BoundImmutableNewArguments : PyFunction
        {
            internal BoundImmutableNewArguments(ImmutableNewArgumentsDescriptor descriptor, object instance)
                : base(descriptor.__name__)
            {
                __self__ = instance;
                __qualname__ = descriptor.__qualname__;
                __module__ = descriptor.__module__;
            }

            public object __self__ { get; }

            public override object? __call__(object?[] args) =>
                args.Length == 0 ? ImmutableNewArgumentsDescriptor.Read(__self__) :
                    throw new InvalidCastException("__getnewargs__ takes no arguments");

            public override object? __call_kw__(object?[] args, IDictionary<string, object?>? keywords) =>
                keywords?.Count > 0 ? throw new InvalidCastException("__getnewargs__ takes no keyword arguments") : __call__(args);
        }
    }
}
