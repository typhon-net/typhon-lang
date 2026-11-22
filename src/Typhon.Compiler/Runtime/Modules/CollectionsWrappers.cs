// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static partial class collections
    {
        public static Type defaultdict => typeof(Typhon.Runtime.defaultdict<object, object?>);

        public class UserDict : abc.Mapping, IPyLen, IPyRepr
        {
            public OrderedDictionary<object, object?> data = new();

            public UserDict() { }
            public UserDict(object? source) { if (source != null) update(source); }

            public dynamic? this[object? key] { get => __getitem__(key); set => __setitem__(key, value); }
            public virtual object? __getitem__(object? key) => Builtins.__getitem(data, key);
            public virtual void __setitem__(object? key, object? value) => Builtins.__setitem(data, key, value);
            public virtual void __delitem__(object? key) => Builtins.delitem((object)data, key);
            public long __len__() => data.Count;
            public IEnumerable<object?> __iter__() => Builtins.__iter_dyn(data);

            public virtual void update(object source)
            {
                var entries = Builtins.frozendict(source is abc.Mapping mapping ? mapping.items() : source,
                    new Dictionary<string, object?>());
                foreach (var entry in entries) __setitem__(entry.Key, entry.Value);
            }

            public void clear() => data.Clear();
            public UserDict copy() => new UserDict(data);
            public string __repr__() => Builtins.repr(data);
            public override string ToString() => __repr__();
        }

        public class ChainMap : abc.Mapping, IPyLen, IPyRepr
        {
            public List<object> maps;

            public ChainMap(params object[] mappings) =>
                maps = mappings.Length == 0 ? new() { new OrderedDictionary<object, object?>() } : new(mappings);

            public dynamic? this[object? key] { get => __getitem__(key); set => __setitem__(key, value); }

            public object? __getitem__(object? key)
            {
                foreach (object mapping in maps)
                {
                    try { return Builtins.__getitem(mapping, key); }
                    catch (KeyNotFoundException) { }
                }
                throw new KeyNotFoundException(Builtins.repr(key));
            }

            public void __setitem__(object? key, object? value) => Builtins.__setitem(maps[0], key, value);
            public void __delitem__(object? key) => Builtins.delitem(maps[0], key);
            public override bool __contains__(object? key) => maps.Any(mapping => Builtins.contains(mapping, key!));

            public IEnumerable<object?> __iter__()
            {
                var seen = new HashSet<object?>();
                foreach (object mapping in maps.AsEnumerable().Reverse())
                    foreach (object? key in Builtins.__iter_dyn(mapping))
                        if (seen.Add(key)) yield return key;
            }

            public long __len__() => __iter__().LongCount();
            public ChainMap parents => new ChainMap(maps.Skip(1).ToArray());
            public ChainMap new_child(object? mapping = null) =>
                new ChainMap(new[] { mapping ?? new OrderedDictionary<object, object?>() }.Concat(maps).ToArray());

            public string __repr__() => "ChainMap(" + string.Join(", ", maps.Select(Builtins.repr)) + ")";
            public override string ToString() => __repr__();
        }

        public class UserList : IEnumerable<object?>, IPyLen, IPyRepr
        {
            public List<object?> data;

            public UserList(object? source = null) =>
                data = source == null ? new() : Builtins.__iter_dyn(source).Cast<object?>().ToList();

            public dynamic? this[object? key] { get => __getitem__(key); set => __setitem__(key, value); }

            public object? __getitem__(object? key)
            {
                object? value = Builtins.__getitem(data, key);
                return key is PySlice ? new UserList(value) : value;
            }

            public void __setitem__(object? key, object? value) => Builtins.__setitem(data, key, value);
            public void __delitem__(object? key) => Builtins.delitem(data, key);
            public bool __contains__(object? item) => data.Any(value => Builtins.eq(value, item));
            public long __len__() => data.Count;
            public IEnumerator<object?> GetEnumerator() => data.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void append(object? item) => data.Add(item);
            public void extend(object source) => data.AddRange(Builtins.__iter_dyn(source).ToArray());
            public void clear() => data.Clear();
            public object? pop(long index = -1) => Builtins.pop(data, index);
            public void reverse() => data.Reverse();
            public UserList copy() => new UserList(data);
            public string __repr__() => Builtins.repr(data);
            public override string ToString() => __repr__();
        }

        public class UserString : IEnumerable<string>, IPyLen, IPyRepr
        {
            public string data;

            public UserString() : this("") { }
            public UserString(object? source) => data = source is UserString text ? text.data : Builtins.str(source);

            public UserString this[object? key] => __getitem__(key);
            public UserString __getitem__(object? key) => new UserString((object?)Builtins.__getitem(data, key));
            public bool __contains__(object? value) =>
                value is UserString text ? data.Contains(text.data, StringComparison.Ordinal) :
                value is string plain ? data.Contains(plain, StringComparison.Ordinal) :
                throw new InvalidCastException("'in <string>' requires string as left operand");
            public long __len__() => data.Length;
            public IEnumerator<string> GetEnumerator() => Builtins.chars(data).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public UserString lower() => new UserString(data.ToLowerInvariant());
            public UserString upper() => new UserString(data.ToUpperInvariant());
            public string __repr__() => Builtins.repr(data);
            public override string ToString() => data;
        }
    }
}
