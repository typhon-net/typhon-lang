// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    internal static partial class Builtins
    {
        public static Typhon.Stdlib.builtins.property property(object? fget = null, object? fset = null, object? fdel = null, object? doc = null) => new(fget, fset, fdel, doc);
        public static dynamic staticmethod(object? function) => new Typhon.Stdlib.builtins.staticmethod(function);
        public static Typhon.Stdlib.builtins.classmethod classmethod(object? function) => new(function);
    }
}

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Reflection;
    using Typhon.Runtime;

    internal static partial class builtins
    {
        internal class property
        {
            public dynamic? fget { get; }
            public dynamic? fset { get; }
            public dynamic? fdel { get; }
            public object? __doc__ { get; set; }
            readonly bool _usesGetterDoc;
            string? _name;

            public property(object? fget = null, object? fset = null, object? fdel = null, object? doc = null)
            {
                this.fget = fget;
                this.fset = fset;
                this.fdel = fdel;
                _usesGetterDoc = doc == null;
                __doc__ = doc ?? Builtins.getattr(fget, "__doc__", null);
            }

            public string __name__
            {
                get => _name ?? (string?)Builtins.getattr((object?)fget, "__name__", null) ?? throw new MissingMemberException("property has no __name__");
                set => _name = value;
            }

            public void __set_name__(object owner, string name) => _name = name;

            public dynamic? __get__(object? instance, object? owner = null)
            {
                if (instance == null) return owner == null ? throw UnboundDescriptor() : this;
                if (fget == null) throw MissingAccessor(instance, "getter");
                return Builtins.__invoke((object?)fget, [instance]);
            }

            public void __set__(object? instance, object? value)
            {
                if (fset == null) throw MissingAccessor(instance, "setter");
                Builtins.__invoke((object?)fset, [instance, value]);
            }

            public void __delete__(object? instance)
            {
                if (fdel == null) throw MissingAccessor(instance, "deleter");
                Builtins.__invoke((object?)fdel, [instance]);
            }

            MissingMemberException MissingAccessor(object? instance, string accessor)
            {
                string name = _name == null ? "" : " '" + _name + "'";
                string type = instance == null ? "NoneType" : Builtins.PyTypeName(instance.GetType());
                return new MissingMemberException($"property{name} of '{type}' object has no {accessor}");
            }

            property Copy(object? get, object? set, object? del) =>
                new property(get, set, del, _usesGetterDoc ? null : __doc__) { _name = _name };

            public property getter(object? get) => Copy(get ?? (object?)fget, (object?)fset, (object?)fdel);
            public property setter(object? set) => Copy((object?)fget, set ?? (object?)fset, (object?)fdel);
            public property deleter(object? del) => Copy((object?)fget, (object?)fset, del ?? (object?)fdel);
        }

        internal abstract class MethodDescriptor : DynamicObject
        {
            public dynamic? __func__ { get; }
            public dynamic? __wrapped__ => __func__;
            public object? __doc__ { get; set; }
            public string __name__ { get; set; }
            public string __qualname__ { get; set; }
            public string __module__ { get; set; }

            protected MethodDescriptor(object? function)
            {
                __func__ = function;
                __doc__ = Builtins.getattr(function, "__doc__", null);
                __name__ = (string?)Builtins.getattr(function, "__name__", null) ?? Builtins.__name_of(function);
                __qualname__ = (string?)Builtins.getattr(function, "__qualname__", null) ?? __name__;
                __module__ = (string?)Builtins.getattr(function, "__module__", null) ?? "builtins";
            }
        }

        internal class staticmethod : MethodDescriptor, IPyKeywordCallable
        {
            public staticmethod(object? function) : base(function) { }

            public dynamic? __get__(object? instance, object? owner = null) =>
                instance == null && owner == null ? throw UnboundDescriptor() : __func__;

            public dynamic? __call__(params object?[] args) => Builtins.__invoke((object?)__func__, args);

            public object? __call_kw__(object?[] args, IDictionary<string, object?>? kwargs) =>
                Builtins.__invoke_kw((object?)__func__, args, kwargs);

            public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result)
            {
                args ??= [];
                var names = binder.CallInfo.ArgumentNames;
                if (names.Count == 0) { result = __call__(args); return true; }
                int positional = args.Length - names.Count;
                var keywords = new Dictionary<string, object?>();
                for (int i = 0; i < names.Count; i++) keywords[names[i]] = args[positional + i];
                result = __call_kw__(args[..positional], keywords);
                return true;
            }
        }

        internal class classmethod : MethodDescriptor
        {
            public classmethod(object? function) : base(function) { }

            public dynamic __get__(object? instance, object? owner = null) =>
                new types.MethodType((object?)__func__, owner ?? instance?.GetType() ?? throw UnboundDescriptor());
        }

        static InvalidCastException UnboundDescriptor() => new("__get__(None, None) is invalid");
    }

    internal static partial class functools
    {
        internal class cached_property
        {
            public dynamic func { get; }
            public string? attrname { get; private set; }
            public object? __doc__ { get; set; }
            public string? __module__ { get; set; }

            public cached_property(object func)
            {
                this.func = func;
                __doc__ = Builtins.getattr(func, "__doc__", null);
                __module__ = (string?)Builtins.getattr(func, "__module__", null);
            }

            public void __set_name__(object owner, string name)
            {
                if (attrname != null && attrname != name)
                    throw new InvalidCastException($"Cannot assign the same cached_property to two different names ({Builtins.repr(attrname)} and {Builtins.repr(name)}).");
                attrname = name;
            }

            public dynamic? __get__(object? instance, object? owner = null)
            {
                if (instance == null) return this;
                if (attrname == null) throw new InvalidCastException("Cannot use cached_property instance without calling __set_name__ on it.");
                var type = instance.GetType();
                if (!AllowsInstanceDict(type))
                    throw new InvalidCastException($"No '__dict__' attribute on '{Builtins.PyTypeName(type)}' instance to cache {Builtins.repr(attrname)} property.");
                var cache = new PyAttrDict(instance);
                if (!cache.TryGetValue(attrname, out var value))
                {
                    value = Builtins.__invoke((object)func, [instance]);
                    cache[attrname] = value;
                }
                return value;
            }

            static bool AllowsInstanceDict(Type type)
            {
                // A subclass with no __slots__ declaration acquires its own dictionary. A slotted
                // subclass keeps the dictionary if a base has one or if its slots include __dict__.
                for (Type? current = type; current != null; current = current.BaseType)
                {
                    if (current.Assembly == typeof(object).Assembly || current.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true ||
                        current == typeof(PyBytes) || current == typeof(PyByteArray)) return false;
                    var slots = current.GetField("__slots__", BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (slots == null) return true;
                    var values = slots.GetValue(null);
                    if (values is string name ? name == "__dict__" : Builtins.__iter_dyn(values).Any(slot => slot is string key && key == "__dict__")) return true;
                }
                return false;
            }
        }
    }
}
