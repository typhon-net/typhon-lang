// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Stdlib;

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class PyMetaclassDefinitionAttribute : Attribute { }

    /// <summary>Python class-creation metadata for classes that keep their declared CLR type.</summary>
    internal static class PyMetaclasses
    {
        sealed record Definition(Type Meta, OrderedDictionary<string, object?> Namespace);
        sealed record Creation(Type Target, Type Meta, string Name, Type[] Bases, IDictionary Namespace);
        static readonly ConditionalWeakTable<Type, Definition> Definitions = new();
        [ThreadStatic] static Creation? _creating;

        internal static bool IsMetaclass(Type type) =>
            type.IsDefined(typeof(PyMetaclassDefinitionAttribute), false);

        internal static Type Of(Type type) =>
            Definitions.TryGetValue(type, out var definition) ? definition.Meta : typeof(Type);

        internal static IDictionary? Namespace(Type type) =>
            Definitions.TryGetValue(type, out var definition) ? definition.Namespace : null;

        internal static bool TryOwnValue(Type type, string name, out object? value)
        {
            value = null;
            return Definitions.TryGetValue(type, out var definition) && definition.Namespace.TryGetValue(name, out value);
        }

        internal static bool Set(Type type, string name, object? value)
        {
            if (!Definitions.TryGetValue(type, out var definition)) return false;
            definition.Namespace[name] = value;
            return true;
        }

        internal static object? Bind(object? value, object? instance, Type owner) => value switch
        {
            builtins.classmethod cm => cm.__get__(instance, owner),
            builtins.staticmethod sm => sm.__get__(instance, owner),
            builtins.property p => p.__get__(instance, owner),
            PyFunction function when instance != null => new types.MethodType(function, instance),
            _ => value,
        };

        static Type Select(Type? proposed, Type[] bases)
        {
            Type winner = proposed ?? typeof(Type);
            foreach (Type basis in bases)
            {
                Type candidate = Of(basis);
                if (Builtins.issubclass(winner, candidate)) continue;
                if (Builtins.issubclass(candidate, winner)) winner = candidate;
                else throw new InvalidCastException("metaclass conflict: the metaclass of a derived class must be a (non-strict) subclass of the metaclasses of all its bases");
            }
            return winner;
        }

        internal static readonly PyClosure New = new("__new__", (args, kwargs) =>
        {
            var creation = _creating ?? throw new NotSupportedException("type.__new__ requires a compiled class definition");
            if (args.Length != 4 || args[0] is not Type metaclass || args[1] is not string name ||
                args[2] is not IEnumerable bases || args[3] is not IDictionary members || kwargs?.Count > 0)
                throw new InvalidCastException("type.__new__ expects a metaclass, name, bases, and a namespace");
            if (metaclass != creation.Meta || name != creation.Name || !bases.Cast<object?>().SequenceEqual(creation.Bases))
                throw new NotSupportedException("type.__new__ cannot change a compiled class's metaclass, name, or bases");
            var owned = new OrderedDictionary<string, object?>();
            var entries = members.GetEnumerator();
            while (entries.MoveNext())
            {
                if (entries.Key is not string key) throw new InvalidCastException("type.__new__ namespace keys must be strings");
                owned[key] = entries.Value;
            }
            Definitions.Add(creation.Target, new Definition(metaclass, owned));
            return creation.Target;
        });

        internal static T Write<T>(Type type, string name, T value)
        {
            if (_creating is { } frame && frame.Target == type) frame.Namespace[name] = value;
            return value;
        }

        internal static object? Publish(Type type, string name)
        {
            var original = PyClassDescriptors.DeclaredNamespace(type);
            if (original.Contains(name)) Write(type, name, original[name]);
            return null;
        }

        internal static dynamic? Read(Type type, string name, Type module, string moduleName)
        {
            if (_creating is { } frame && frame.Target == type && frame.Namespace.Contains(name))
                return frame.Namespace[name];
            return PyClassDescriptors.ReadBodyName(PyClassDescriptors.Unassigned, name, module, moduleName);
        }

        internal static void Define(Type target, Type? declared, string name, string module, Type[] bases, string[] names,
            IDictionary<string, object?>? keywords)
        {
            Type chosen = Select(declared, bases);
            var basis = new PyTuple<Type>(bases);
            object? prepare = Builtins.getattr(chosen, "__prepare__", null);
            IDictionary ns = prepare == null ? new OrderedDictionary<string, object?>() :
                (object?)Builtins.__invoke_kw(prepare, [name, basis], keywords) as IDictionary ??
                throw new InvalidCastException("__prepare__ must return a mapping");
            ns["__module__"] = module;
            ns["__qualname__"] = name;
            object?[] create = [chosen, name, basis, ns];

            var previous = _creating;
            _creating = new Creation(target, chosen, name, bases, ns);
            try
            {
                RuntimeHelpers.RunClassConstructor(target.TypeHandle);
                IDictionary original = PyClassDescriptors.DeclaredNamespace(target);
                foreach (string member in names)
                    if (!ns.Contains(member) && original.Contains(member)) ns[member] = original[member];
                object? construct = Builtins.getattr(chosen, "__new__", New);
                var result = (object?)Builtins.__invoke_kw(construct, create, keywords);
                if (!ReferenceEquals(result, target) || !Definitions.TryGetValue(target, out _))
                    throw new NotSupportedException("a metaclass must initialize and return its compiled class");
                object? initialize = Builtins.getattr(chosen, "__init__", null);
                if (initialize != null) Builtins.__invoke_kw(initialize, [target, name, basis, ns], keywords);
            }
            catch
            {
                Definitions.Remove(target);
                throw;
            }
            finally { _creating = previous; }
        }
    }

    internal static partial class Builtins
    {
        public static void __define_metaclass_type(Type target, Type? declared, string name, string module, Type[] bases,
            string[] names, IDictionary<string, object?>? keywords) =>
            PyMetaclasses.Define(target, declared, name, module, bases, names, keywords);

        public static T __class_namespace_set<T>(Type type, string name, T value) => PyMetaclasses.Write(type, name, value);
        public static object? __class_namespace_publish(Type type, string name) => PyMetaclasses.Publish(type, name);
        public static dynamic? __class_namespace_read(Type type, string name, Type module, string moduleName) =>
            PyMetaclasses.Read(type, name, module, moduleName);
    }
}
