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
        public static class abc
        {
            public abstract class Generator : IEnumerable<object?>
            {
                public Generator __iter__() => this;
                public virtual dynamic? __next__() => Builtins.__invoke(Builtins.getattr(this, "send"), new object?[] { null });
                public virtual void close() => CloseWithGeneratorExit(this, "generator");

                public IEnumerator<object?> GetEnumerator()
                {
                    while (true)
                    {
                        object? value;
                        try { value = __next__(); }
                        catch (StopIteration) { yield break; }
                        yield return value;
                    }
                }

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public abstract class Coroutine
            {
                public virtual void close() => CloseWithGeneratorExit(this, "coroutine");
            }

            static void CloseWithGeneratorExit(object generator, string kind)
            {
                try { Builtins.__invoke(Builtins.getattr(generator, "throw"), new object?[] { new GeneratorExit() }); }
                catch (Exception error) when (error is GeneratorExit or StopIteration) { return; }
                throw new InvalidOperationException($"{kind} ignored GeneratorExit");
            }

            public static object _check_methods(Type C, params string[] methods)
            {
                foreach (var method in methods)
                    if ((object?)Builtins.getattr(C, method, null) == null) return Builtins.NotImplemented;
                return true;
            }

            public abstract class Sequence : IPyLen, IEnumerable<object?>
            {
                public Sequence() { }
                public abstract long __len__();
                public abstract object? __getitem__(object? index);

                IEnumerable<object?> Items(long start, long? stop)
                {
                    for (long position = start; !stop.HasValue || position < stop.Value; position++)
                    {
                        object? value;
                        try { value = __getitem__(position); }
                        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException) { yield break; }
                        yield return value;
                    }
                }

                public IEnumerator<object?> GetEnumerator() => Items(0, null).GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                public IEnumerator<object?> __iter__() => GetEnumerator();
                public bool __contains__(object? value) => this.Any(item => ReferenceEquals(item, value) || Builtins.eq(item, value));
                public long count(object? value) => this.LongCount(item => ReferenceEquals(item, value) || Builtins.eq(item, value));

                public IEnumerable<object?> __reversed__()
                {
                    for (long position = __len__(); position > 0; position--) yield return __getitem__(position - 1);
                }

                public long index(object? value, long start = 0, long? stop = null)
                {
                    if (start < 0) start = Math.Max(0, start + __len__());
                    if (stop < 0) stop += __len__();
                    long position = start;
                    foreach (var item in Items(start, stop))
                    {
                        if (ReferenceEquals(item, value) || Builtins.eq(item, value)) return position;
                        position++;
                    }
                    throw new ArgumentException("");
                }
            }

            /// <summary>Mutable sequence operations supplied by the positional read, write, delete and insert hooks.</summary>
            public abstract class MutableSequence : Sequence
            {
                public abstract void __setitem__(object? index, object? value);
                public abstract void __delitem__(object? index);
                public abstract void insert(object? index, object? value);

                public void append(object? value) => insert(__len__(), value);

                public void clear()
                {
                    while (true)
                    {
                        try { pop(); }
                        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException) { return; }
                    }
                }

                public void reverse()
                {
                    long count = __len__();
                    for (long first = 0; first < count / 2; first++)
                    {
                        long last = count - first - 1;
                        object? right = __getitem__(last), left = __getitem__(first);
                        __setitem__(first, right);
                        __setitem__(last, left);
                    }
                }

                public void extend(object? values)
                {
                    if (ReferenceEquals(values, this)) values = this.ToArray();
                    foreach (object? item in Builtins.__iter_dyn(values)) append(item);
                }

                public dynamic? pop(long index = -1)
                {
                    object? value = __getitem__(index);
                    __delitem__(index);
                    return value;
                }

                public void remove(object? value) => __delitem__(index(value));
                public MutableSequence __iadd__(object? values) { extend(values); return this; }
            }

            /// <summary>The read-only mapping mixin implemented by __getitem__, __iter__, and __len__.</summary>
            public abstract class Mapping : IEnumerable<object?>
            {
                public Mapping() { }

                internal object? Item(object? key) => Builtins.__invoke(Builtins.getattr(this, "__getitem__"), new[] { key });

                IEnumerable<object?> Keys() =>
                    Builtins.__iter_dyn(Builtins.__invoke(Builtins.getattr(this, "__iter__"), Array.Empty<object?>()));

                public virtual object? get(object? key, object? @default = null)
                {
                    try { return Item(key); }
                    catch (KeyNotFoundException) { return @default; }
                }

                public virtual bool __contains__(object? key)
                {
                    try { Item(key); return true; }
                    catch (KeyNotFoundException) { return false; }
                }

                public virtual IEnumerable<object?> keys() => new KeysView(this);
                public virtual IEnumerable<object?> values() => new ValuesView(this);
                public virtual IEnumerable<(object?, object?)> items() => new ItemsView(this);

                IEnumerator<object?> IEnumerable<object?>.GetEnumerator() => Keys().GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => Keys().GetEnumerator();

            }

            /// <summary>Mutable mapping operations supplied by the item's read, write and delete hooks.</summary>
            public abstract class MutableMapping : Mapping
            {
                public MutableMapping() { }

                public static PyFunction __ne__ => Builtins.ObjectNotEqual;

                public dynamic? this[object? key] { get => Item(key); set => Put(key, value); }

                internal void Put(object? key, object? value) =>
                    Builtins.__invoke((object?)Builtins.getattr(this, "__setitem__"), [key, value]);

                internal void Delete(object? key) =>
                    Builtins.__invoke((object?)Builtins.getattr(this, "__delitem__"), [key]);

                public object? pop(object? key, params object?[] defaults)
                {
                    if (defaults.Length > 1) throw new InvalidCastException("pop expected at most 2 arguments");
                    object? value;
                    try { value = Item(key); }
                    catch (KeyNotFoundException) when (defaults.Length == 1) { return defaults[0]; }
                    Delete(key);
                    return value;
                }

                public (object?, object?) popitem()
                {
                    using var keys = ((IEnumerable<object?>)this).GetEnumerator();
                    if (!keys.MoveNext()) throw new KeyNotFoundException();
                    object? key = keys.Current;
                    object? value = Item(key);
                    Delete(key);
                    return (key, value);
                }

                public void clear()
                {
                    while (true)
                    {
                        try { popitem(); }
                        catch (KeyNotFoundException) { return; }
                    }
                }

                public object? setdefault(object? key, object? @default = null)
                {
                    try { return Item(key); }
                    catch (KeyNotFoundException) { Put(key, @default); return @default; }
                }

                public void update([System.Runtime.InteropServices.Optional, PyPositionalOnly] object? other,
                    [PyKeywordArguments] IDictionary<string, object?>? __kwargs = null) => Update(this, other, __kwargs);

                // The ABC supplies a Python mixin, so the unbound function can also be attached to an unrelated class.
                internal static PyFunction UpdateFunction { get; } = new PyClosure("update", (args, keywords) =>
                {
                    if (args.Length is < 1 or > 2) throw PyClosure.ArgCountError("update", 1, 2, args.Length);
                    Update(args[0], args.Length == 2 ? args[1] : Type.Missing, keywords);
                    return null;
                });

                static void Update(object? receiver, object? other, IDictionary<string, object?>? keywords)
                {
                    if (!ReferenceEquals(other, Type.Missing))
                    {
                        if (other == null) throw new InvalidCastException("'NoneType' object is not iterable");
                        foreach (var (key, value) in Builtins.ReadDictItems(other)) Builtins.__setitem(receiver, key, value);
                    }
                    if (keywords != null)
                        foreach (var (key, value) in keywords) Builtins.__setitem(receiver, key, value);
                }
            }

            public class MappingView(object mapping) : IPyLen, IPyRepr
            {
                public object _mapping = mapping;

                public long __len__() => Builtins.len(_mapping);
                protected IEnumerable<object?> Keys() => Builtins.__iter_dyn(_mapping);
                protected object? Item(object? key) => Builtins.__getitem(_mapping, key);
                public string __repr__() => $"{Builtins.PyTypeName(GetType())}({Builtins.repr(_mapping)})";
                public override string ToString() => __repr__();
            }

            public class KeysView(object mapping) : MappingView(mapping), IEnumerable<object?>
            {
                public bool __contains__(object? key) => _mapping is Mapping mapping
                    ? mapping.__contains__(key) : Builtins.contains(_mapping, key!);
                public IEnumerator<object?> GetEnumerator() => Keys().GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public class ValuesView(object mapping) : MappingView(mapping), IEnumerable<object?>
            {
                public bool __contains__(object? value) => Keys().Any(key => Builtins.eq(Item(key), value));
                public IEnumerator<object?> GetEnumerator() => Keys().Select(Item).GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public class ItemsView(object mapping) : MappingView(mapping), IEnumerable<(object?, object?)>
            {
                public bool __contains__(object? item)
                {
                    var pair = Builtins.__iter_dyn(item).Take(3).ToArray();
                    if (pair.Length < 2) throw new ArgumentException($"not enough values to unpack (expected 2, got {pair.Length})");
                    if (pair.Length > 2) throw new ArgumentException("too many values to unpack (expected 2)");
                    try { return Builtins.eq(Item((object?)pair[0]), (object?)pair[1]); }
                    catch (KeyNotFoundException) { return false; }
                }

                public IEnumerator<(object?, object?)> GetEnumerator() => Keys().Select(key => (key, Item(key))).GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
        }
    }
}
