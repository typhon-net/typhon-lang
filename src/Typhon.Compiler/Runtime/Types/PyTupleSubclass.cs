// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;

    /// <summary>A compiled tuple subtype keeps the tuple contents beside its Python instance attributes.</summary>
    internal class PyTupleSubclass : IReadOnlyList<object?>, ITuple, IPyTuple, IComparable
    {
        PyTuple<object?> _items = PyTuple<object?>.Empty;

        internal static PyTupleSubclass Allocate(Type cls, PyTuple<object?> items)
        {
            if (!typeof(PyTupleSubclass).IsAssignableFrom(cls))
                throw new InvalidCastException("tuple.__new__ requires a tuple subtype");
            Typhon.Stdlib._abc.CheckAbstract(cls);
            var result = (PyTupleSubclass)RuntimeHelpers.GetUninitializedObject(cls);
            result._items = items;
            return result;
        }

        public static object __new__(Type cls) => Allocate(cls, PyTuple<object?>.Empty);
        public static object __new__(Type cls, object? iterable) =>
            Allocate(cls, new PyTuple<object?>(Builtins.__iter_dyn(iterable)));

        public int Count => _items.Count;
        int ITuple.Length => _items.Count;
        object? ITuple.this[int index] => _items[index];
        object? IPyTuple.this[int index] => _items[index];
        object? IReadOnlyList<object?>.this[int index] => _items[index];

        public dynamic? this[long index] => Builtins.__getitem(_items, index);
        public dynamic? this[PySlice slice] => Builtins.__getitem(_items, slice);
        public IEnumerator<object?> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override bool Equals(object? other) => other is ITuple or IPyTuple && Builtins.eq(this, other);
        public override int GetHashCode() => _items.GetHashCode();
        public int CompareTo(object? other) => other is ITuple or IPyTuple ? Builtins.compare(this, other) :
            throw new InvalidCastException("tuples can only be ordered with other tuples");
        public override string ToString() => Builtins.repr(this);
    }

    internal static partial class Builtins
    {
        public static dynamic __tuple_new(Type cls) => cls == typeof(ITuple) ? PyTuple<object?>.Empty : PyTupleSubclass.__new__(cls);
        public static dynamic __tuple_new(Type cls, object? iterable) => cls == typeof(ITuple)
            ? new PyTuple<object?>(__iter_dyn(iterable)) : PyTupleSubclass.__new__(cls, iterable);
    }
}
