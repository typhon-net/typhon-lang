// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Retain the Python module name.

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static partial class functools
    {
        public static dynamic lru_cache([Optional, DefaultParameterValue(128L)] object? maxsize, bool typed = false)
        {
            if (maxsize is long capacity) return CacheDecorator(Math.Max(0, capacity), typed);
            if (maxsize is int narrowed) return CacheDecorator(Math.Max(0, narrowed), typed);
            if (maxsize is BigInteger integer) return CacheDecorator(integer.Sign < 0 ? 0 : checked((long)integer), typed);
            if (maxsize is bool flag) return CacheDecorator(flag ? 1 : 0, typed);
            if (maxsize == null) return CacheDecorator(null, typed);
            if (Builtins.callable(maxsize)) return new LruWrapper(maxsize, 128, typed);
            throw new InvalidCastException("Expected first argument to be an integer, a callable, or None");
        }

        public static dynamic cache(object func) => new LruWrapper(func, null, false);

        static PyClosure CacheDecorator(long? capacity, bool typed) => new PyClosure("lru_cache", (args, keywords) =>
        {
            var bound = PyClosure.Args("lru_cache", new[] { "user_function" }, args, keywords, false);
            if (bound.Length != 1 || !Builtins.callable(bound[0])) throw new InvalidCastException("the first argument must be callable");
            return new LruWrapper(bound[0]!, capacity, typed);
        });

        internal sealed class CacheInfo : PyStructSeq
        {
            public readonly long hits, misses, currsize;
            public readonly long? maxsize;
            public CacheInfo(long hits, long misses, long? maxsize, long currsize)
            {
                this.hits = hits; this.misses = misses; this.maxsize = maxsize; this.currsize = currsize;
            }
            protected override string TypeName => "CacheInfo";
            protected override string[] Fields => new[] { "hits", "misses", "maxsize", "currsize" };
            protected override object?[] Values => new object?[] { hits, misses, maxsize, currsize };
        }

        internal sealed class LruWrapper : PyFunction
        {
            readonly object _func;
            readonly long? _capacity;
            readonly bool _typed;
            readonly object _lock = new object();
            readonly Dictionary<Key, LinkedListNode<(Key Key, object? Value)>> _values = new();
            readonly LinkedList<(Key Key, object? Value)> _recency = new();
            long _hits, _misses;

            public LruWrapper(object func, long? capacity, bool typed)
                : base((object?)Builtins.getattr(func, "__name__", null) as string ?? "lru_cache")
            {
                if (!Builtins.callable(func)) throw new InvalidCastException("the first argument must be callable");
                _func = func;
                _capacity = capacity;
                _typed = typed;
                __doc__ = Builtins.getattr(func, "__doc__", null);
                __module__ = (object?)Builtins.getattr(func, "__module__", null) as string ?? "__main__";
                __wrapped__ = func;
            }

            public override object? __call__(object?[] args) => __call_kw__(args, null);
            public override object? __call_kw__(object?[] args, IDictionary<string, object?>? kw)
            {
                if (_capacity == 0)
                {
                    lock (_lock) _misses++;
                    return Builtins.__invoke_kw(_func, args, kw);
                }
                var key = new Key(args, kw, _typed);
                lock (_lock)
                {
                    if (_values.TryGetValue(key, out var hit))
                    {
                        _hits++;
                        _recency.Remove(hit);
                        _recency.AddLast(hit);
                        return hit.Value.Value;
                    }
                    _misses++;
                }
                // Run the wrapped function outside the lock. A recursive call can fill the same cache.
                object? result = Builtins.__invoke_kw(_func, args, kw);
                lock (_lock)
                {
                    if (!_values.ContainsKey(key))
                    {
                        if (_capacity is long max && _values.Count >= max)
                        {
                            _values.Remove(_recency.First!.Value.Key);
                            _recency.RemoveFirst();
                        }
                        _values.Add(key, _recency.AddLast((key, result)));
                    }
                }
                return result;
            }
            public CacheInfo cache_info() { lock (_lock) return new CacheInfo(_hits, _misses, _capacity, _values.Count); }
            public void cache_clear()
            {
                lock (_lock) { _values.Clear(); _recency.Clear(); _hits = _misses = 0; }
            }
            public OrderedDictionary<string, object?> cache_parameters() => new() { ["maxsize"] = _capacity, ["typed"] = _typed };
        }

        sealed class Key : IEquatable<Key>
        {
            readonly object?[] _args;
            readonly KeyValuePair<string, object?>[] _keywords;
            readonly bool _typed, _fast;
            readonly int _hash;

            public Key(object?[] args, IDictionary<string, object?>? keywords, bool typed)
            {
                _args = args.ToArray();
                _keywords = keywords?.ToArray() ?? Array.Empty<KeyValuePair<string, object?>>();
                _typed = typed;
                // CPython uses the bare int or str as the key for a single argument of those exact types.
                _fast = !typed && _keywords.Length == 0 && args.Length == 1 && args[0] is (long or int or BigInteger or string);
                var hash = new HashCode();
                hash.Add(_fast);
                foreach (var value in _args) Add(value);
                foreach (var (name, value) in _keywords) { hash.Add(name); Add(value); }
                _hash = hash.ToHashCode();
                void Add(object? value)
                {
                    EnsureHashable(value);
                    hash.Add(PyKeyComparer.Instance.GetHashCode(value));
                    if (typed) hash.Add(ArgumentType(value));
                }
            }
            static Type? ArgumentType(object? value) => value is long or int or BigInteger ? typeof(long) : value?.GetType();
            bool Same(object? a, object? b) => (!_typed || ArgumentType(a) == ArgumentType(b)) &&
                (ReferenceEquals(a, b) || PyKeyComparer.Instance.Equals(a, b));
            public bool Equals(Key? other)
            {
                if (other == null || _hash != other._hash || _fast != other._fast || _typed != other._typed ||
                    _args.Length != other._args.Length || _keywords.Length != other._keywords.Length) return false;
                for (int i = 0; i < _args.Length; i++) if (!Same(_args[i], other._args[i])) return false;
                for (int i = 0; i < _keywords.Length; i++)
                    if (_keywords[i].Key != other._keywords[i].Key || !Same(_keywords[i].Value, other._keywords[i].Value)) return false;
                return true;
            }
            public override bool Equals(object? other) => Equals(other as Key);
            public override int GetHashCode() => _hash;

            static void EnsureHashable(object? value)
            {
                if (value is ITuple tuple)
                {
                    for (int i = 0; i < tuple.Length; i++) EnsureHashable(tuple[i]);
                    return;
                }
                if (value is IPyTuple items)
                {
                    for (int i = 0; i < items.Count; i++) EnsureHashable(items[i]);
                    return;
                }
                if (value is IList or IDictionary or PyByteArray || value?.GetType() is Type type &&
                    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
                    throw new InvalidCastException($"unhashable type: '{Builtins.PyTypeName(value!.GetType())}'");
            }
        }
    }
}
