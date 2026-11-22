// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    // CLR dictionaries reject null keys. Python's None is stored here and restored at the mapping boundary.
    internal sealed class PyNoneDictKey : IPyRepr
    {
        internal static readonly PyNoneDictKey Value = new();
        private PyNoneDictKey() { }

        internal static K Store<K>(K key) => key is null && typeof(K) == typeof(object) ? (K)(object)Value : key;
        internal static K Read<K>(K key) => typeof(K) == typeof(object) && ReferenceEquals(key, Value) ? default! : key;
        public string __repr__() => "None";
    }

    internal sealed class PyDictKeys<K>(ICollection<K> keys) : ICollection<K>, IPyLen
    {
        public int Count => keys.Count;
        public bool IsReadOnly => true;
        public long __len__() => Count;
        public IEnumerator<K> GetEnumerator() => keys.Select(PyNoneDictKey.Read).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Contains(K item) => keys.Contains(PyNoneDictKey.Store(item));
        public void CopyTo(K[] array, int arrayIndex)
        {
            foreach (var key in this) array[arrayIndex++] = key;
        }
        public void Add(K item) => throw new NotSupportedException();
        public bool Remove(K item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    internal sealed class PyDictItems<K, V>(IEnumerable<KeyValuePair<K, V>> entries, Func<int> count)
        : IEnumerable<(K, V)>, IPyLen, IPyRepr
    {
        public long __len__() => count();
        public IEnumerator<(K, V)> GetEnumerator() => entries.Select(entry => (PyNoneDictKey.Read(entry.Key), entry.Value)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public string __repr__() => PyDictViews.Repr(this, "dict_items");
        public override string ToString() => __repr__();
    }

    internal static class PyDictViews
    {
        internal static string? Name(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PyDictItems<,>)) return "dict_items";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PyDictKeys<>)) return "dict_keys";
            if (type.DeclaringType is not { IsGenericType: true } declaring) return null;
            Type owner = declaring.GetGenericTypeDefinition();
            if (owner != typeof(OrderedDictionary<,>) && owner != typeof(Dictionary<,>)) return null;
            return type.Name switch
            {
                "KeyCollection" => "dict_keys",
                "ValueCollection" => "dict_values",
                _ => null,
            };
        }

        internal static Type? CanonicalType(Type type) => Name(type) switch
        {
            "dict_keys" => typeof(OrderedDictionary<object, object>.KeyCollection),
            "dict_values" => typeof(OrderedDictionary<object, object>.ValueCollection),
            "dict_items" => typeof(PyDictItems<object, object>),
            _ => null,
        };

        internal static string Repr(IEnumerable values, string name) =>
            name + "([" + string.Join(", ", values.Cast<object?>().Select(Builtins.repr)) + "])";
    }
}
