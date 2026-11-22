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
            public static object _check_methods(Type C, params string[] methods)
            {
                foreach (var method in methods)
                    if ((object?)Builtins.getattr(C, method, null) == null) return Builtins.NotImplemented;
                return true;
            }

            /// <summary>The read-only mapping mixin implemented by __getitem__, __iter__, and __len__.</summary>
            public abstract class Mapping : IEnumerable<object?>
            {
                object? Item(object? key) => Builtins.__invoke(Builtins.getattr(this, "__getitem__"), new[] { key });

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

                public virtual IEnumerable<object?> keys() => new View<object?>(this, Keys);
                public virtual IEnumerable<object?> values() => new View<object?>(this, () => Keys().Select(Item));
                public virtual IEnumerable<(object?, object?)> items() =>
                    new View<(object?, object?)>(this, () => Keys().Select(key => (key, Item(key))));

                IEnumerator<object?> IEnumerable<object?>.GetEnumerator() => Keys().GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => Keys().GetEnumerator();

                sealed class View<T>(Mapping mapping, Func<IEnumerable<T>> values) : IEnumerable<T>, IPyLen
                {
                    public long __len__() => Builtins.len(mapping);
                    public IEnumerator<T> GetEnumerator() => values().GetEnumerator();
                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                }
            }
        }
    }
}
