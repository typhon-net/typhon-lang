// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class PyAbstractMethodAttribute : Attribute { }
}

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    internal static class _abc
    {
        sealed class Cache
        {
            internal readonly ConditionalWeakTable<Type, object> Positive = new();
            internal readonly ConditionalWeakTable<Type, object> Negative = new();
            internal long Version = get_cache_token();
        }

        static readonly object Marker = new();
        static readonly ConditionalWeakTable<Type, Cache> Caches = new();

        static Cache GetCache(Type cls) => Caches.GetValue(cls, _ => new Cache());

        public static long get_cache_token() => Builtins.AbcCacheToken;

        internal static bool IsAbstract(object? member)
        {
            if (member is types.MethodType method) member = method.__func__;
            return Builtins.truthy((object?)Builtins.getattr(member, "__isabstractmethod__", false));
        }

        public static void _abc_init(Type cls)
        {
            var names = new HashSet<string>();
            foreach (Type basis in PyMetaclasses.Bases(cls))
                if ((object?)Builtins.getattr(basis, "__abstractmethods__", null) is IEnumerable inherited)
                    foreach (string name in inherited)
                        if (IsAbstract(Builtins.getattr(cls, name, null))) names.Add(name);

            IDictionary own = PyMetaclasses.Namespace(cls) ?? PyClassDescriptors.DeclaredNamespace(cls);
            var members = own.GetEnumerator();
            while (members.MoveNext())
                if (members.Key is string name && IsAbstract(members.Value)) names.Add(name);

            Builtins.setattr(cls, "__abstractmethods__", new frozenset<string>(names));
            Caches.Remove(cls);
            Caches.Add(cls, new Cache());
        }

        static IEnumerable<object?> AbstractMethods(Type cls) =>
            PyMetaclasses.TryOwnValue(cls, "__abstractmethods__", out var value) && value is IEnumerable methods
                ? methods.Cast<object?>() : Enumerable.Empty<object?>();

        internal static bool HasAbstractMethods(Type cls) => AbstractMethods(cls).Any();

        internal static void CheckAbstract(Type cls)
        {
            string[] names = AbstractMethods(cls).Select(Builtins.str).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (names.Length == 0) return;
            string members = string.Join(", ", names.Select(name => "'" + name + "'"));
            throw new InvalidCastException($"Can't instantiate abstract class {Builtins.PyTypeName(cls)} without an implementation for abstract method{(names.Length == 1 ? "" : "s")} {members}");
        }

        public static Type _abc_register(Type cls, object? candidate)
        {
            if (candidate is not Type subclass) throw new InvalidCastException("Can only register classes");
            if (Builtins.issubclass(subclass, cls)) return subclass;
            if (Builtins.issubclass(cls, subclass))
                throw new InvalidOperationException("Refusing to create an inheritance cycle");
            return Builtins.__register_abc(cls, subclass);
        }

        public static bool _abc_instancecheck(Type cls, object? instance)
        {
            Type actual = Builtins.__type_of(instance);
            object? reported = Builtins.getattr(instance, "__class__", actual);
            return reported is Type other && other != actual && _abc_subclasscheck(cls, other) || _abc_subclasscheck(cls, actual);
        }

        public static bool _abc_subclasscheck(Type cls, object? candidate)
        {
            if (candidate is not Type subclass) throw new InvalidCastException("issubclass() arg 1 must be a class");
            var cache = GetCache(cls);
            long version = get_cache_token();
            lock (cache)
            {
                if (cache.Positive.TryGetValue(subclass, out _)) return true;
                if (cache.Version != version)
                {
                    cache.Negative.Clear();
                    cache.Version = version;
                }
                if (cache.Negative.TryGetValue(subclass, out _)) return false;
            }

            object? hook = Builtins.getattr(cls, "__subclasshook__", null);
            object? result = hook == null ? Builtins.NotImplemented : Builtins.__invoke(hook, [subclass]);
            bool matches = result is bool decision ? decision : ReferenceEquals(result, Builtins.NotImplemented)
                ? Builtins.IsSubclass(subclass, cls) : throw new InvalidCastException("__subclasshook__ must return either False, True or NotImplemented");

            lock (cache)
            {
                if (matches) cache.Positive.GetValue(subclass, _ => Marker);
                else if (cache.Version == version) cache.Negative.GetValue(subclass, _ => Marker);
            }
            return matches;
        }

        public static (HashSet<weakref.@ref>, HashSet<weakref.@ref>, HashSet<weakref.@ref>, long) _get_dump(Type cls)
        {
            var cache = GetCache(cls);
            lock (cache)
                return (References(Builtins.AbcRegistry(cls)), References(cache.Positive.Select(pair => pair.Key)),
                    References(cache.Negative.Select(pair => pair.Key)), cache.Version);
        }

        static HashSet<weakref.@ref> References(IEnumerable<Type> classes) =>
            classes.Select(type => new weakref.@ref(type)).ToHashSet();

        public static void _reset_registry(Type cls) => Builtins.ClearAbcRegistry(cls);

        public static void _reset_caches(Type cls)
        {
            var cache = GetCache(cls);
            lock (cache)
            {
                cache.Positive.Clear();
                cache.Negative.Clear();
            }
        }
    }
}
