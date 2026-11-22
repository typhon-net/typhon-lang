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

            /// <summary>The read-only mapping mixin implemented by __getitem__, __iter__, and __len__.</summary>
            public abstract class Mapping : IEnumerable<object?>
            {
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
