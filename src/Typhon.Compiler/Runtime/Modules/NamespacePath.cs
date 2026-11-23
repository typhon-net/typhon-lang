// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Typhon.Runtime;

    internal static partial class importlib
    {
        internal static partial class machinery
        {
            internal sealed class NamespacePath : IList<string>, IPyLen, IPyRepr
            {
                static int _epoch;
                readonly string _name;
                readonly Func<string, string[], ModuleSpec?> _find;
                IList<string> _portions;
                string[] _parent;
                int _seenEpoch;

                internal NamespacePath(string name, IList<string> portions, Func<string, string[], ModuleSpec?> find)
                {
                    _name = name;
                    _portions = portions;
                    _find = find;
                    _parent = ParentPath();
                    _seenEpoch = Volatile.Read(ref _epoch);
                }

                public NamespacePath(string name, object path, object path_finder)
                    : this(name, Paths(path).ToList(),
                        (fullname, parent) => (ModuleSpec?)Builtins.__invoke(path_finder, [fullname, new PyTuple<string>(parent)])) { }

                internal static void Invalidate() => Interlocked.Increment(ref _epoch);

                string[] ParentPath()
                {
                    int dot = _name.LastIndexOf('.');
                    if (dot < 0) return sys_.path.ToArray();
                    string parent = _name[..dot];
                    if (!sys_.modules.TryGetValue(parent, out var loaded))
                        throw new ModuleNotFoundError($"'{parent}' must be imported before finding '{_name}'.", parent);
                    return Paths(Builtins.getattr((object?)loaded, "__path__")).ToArray();
                }

                static IEnumerable<string> Paths(object? value) => Builtins.__iter_dyn(value).OfType<string>();

                IList<string> Current()
                {
                    var parent = ParentPath();
                    int epoch = Volatile.Read(ref _epoch);
                    if (epoch != _seenEpoch || !_parent.SequenceEqual(parent, StringComparer.Ordinal))
                    {
                        var spec = _find(_name, parent);
                        if (spec is { loader: null, submodule_search_locations.Count: > 0 })
                            _portions = spec.submodule_search_locations;
                        _parent = parent;
                        _seenEpoch = epoch;
                    }
                    return _portions;
                }

                public long __len__() => Current().Count;
                public bool __contains__(string item) => Current().Contains(item);
                public string __repr__() => $"NamespacePath({Builtins.repr(_portions)})";
                public void append(string item) => _portions.Add(item);

                public string __getitem__(long index)
                {
                    var path = Current();
                    return path[Offset(index, path.Count)];
                }

                public void __setitem__(long index, string item) => _portions[Offset(index, _portions.Count)] = item;

                static int Offset(long index, int length)
                {
                    if (index < 0) index += length;
                    if (index < 0 || index >= length) throw new IndexOutOfRangeException("list index out of range");
                    return (int)index;
                }

                public IEnumerator<string> GetEnumerator() => Current().GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                int ICollection<string>.Count => Current().Count;
                bool ICollection<string>.IsReadOnly => _portions.IsReadOnly;
                string IList<string>.this[int index] { get => Current()[index]; set => _portions[index] = value; }
                int IList<string>.IndexOf(string item) => Current().IndexOf(item);
                void IList<string>.Insert(int index, string item) => _portions.Insert(index, item);
                void IList<string>.RemoveAt(int index) => _portions.RemoveAt(index);
                void ICollection<string>.Add(string item) => _portions.Add(item);
                void ICollection<string>.Clear() => _portions.Clear();
                bool ICollection<string>.Contains(string item) => Current().Contains(item);
                void ICollection<string>.CopyTo(string[] array, int offset) => Current().CopyTo(array, offset);
                bool ICollection<string>.Remove(string item) => _portions.Remove(item);
            }
        }
    }
}
