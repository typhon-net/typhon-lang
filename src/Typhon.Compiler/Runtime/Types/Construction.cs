// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;

    internal static partial class Builtins
    {
        public static dynamic __dict_new(Type cls)
        {
            if (cls == typeof(OrderedDictionary<object, object>)) return new OrderedDictionary<object, object>();
            if (!typeof(System.Collections.IDictionary).IsAssignableFrom(cls))
                throw new InvalidCastException("dict.__new__ requires a dictionary subtype");
            Typhon.Stdlib._abc.CheckAbstract(cls);
            return AllocateBuiltinSubclass(cls, "dict");
        }

        static Type? ListLayout(Type? type)
        {
            for (Type? current = type; current != null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(List<>))
                    return current;
            return null;
        }

        public static object __list_new([PyPositionalOnly] object? cls, [PyPackedArguments] object?[] args,
            [PyKeywordArguments] OrderedDictionary<string, object?> keywords)
        {
            if (cls is not Type type) throw new InvalidCastException("list.__new__ requires a type");
            Type? layout = ListLayout(type);
            if (layout == null) throw new InvalidCastException($"list.__new__({PyTypeName(type)}): not a subtype of list");
            return type == layout ? Activator.CreateInstance(layout)! : AllocateBuiltinSubclass(type, "list");
        }

        static object AllocateBuiltinSubclass(Type type, string builtin)
        {
            var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, [typeof(NoInit)], modifiers: null);
            if (constructor == null)
                throw new InvalidCastException($"{builtin}.__new__ cannot allocate '{PyTypeName(type)}'");
            return Unwrapped(() => constructor.Invoke([default(NoInit)]))!;
        }

        public static object? __list_initialize(object? receiver, object?[] args, IDictionary<string, object?>? keywords)
        {
            if (receiver is not System.Collections.IList list || ListLayout(receiver.GetType()) is not Type layout)
                throw new InvalidCastException("descriptor '__init__' requires a 'list' object");
            if (keywords?.Count > 0) throw new InvalidCastException("list() takes no keyword arguments");
            if (args.Length > 1) throw new InvalidCastException($"list.__init__ expected at most 1 argument, got {args.Length}");
            list.Clear();
            if (args.Length == 1)
            {
                Type element = layout.GetGenericArguments()[0];
                foreach (object? item in __iter_dyn(args[0]))
                    list.Add(PyArgs.ConvertArgument(item, element));
            }
            return null;
        }

        /// <summary>Binds forwarded arguments to the selected base class's allocation-independent initializer.</summary>
        public static void __base_init(object instance, Type parent, object?[] args, IDictionary<string, object?>? kwargs)
        {
            if (!InitializeAllocated(instance, args, kwargs, parent))
                throw PyArgs.NoFit(parent.Name + ".__init__", kwargs, []);
        }

        static List<MethodInfo> DeclaredInitializers(Type parent) =>
            parent.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "__init__" && !method.IsGenericMethodDefinition && !method.IsVirtual).ToList();

        // A source base whose allocation and initialization were split has an unbound Python initializer.
        static PyClosure? CompiledInitializer(Type type)
        {
            for (Type? owner = type; owner != null && owner.IsDefined(typeof(PySourceAttribute), false); owner = owner.BaseType)
            {
                var candidates = DeclaredInitializers(owner);
                if (candidates.Count == 0) continue;

                var declaring = owner;
                return new PyClosure("__init__", (args, keywords) =>
                {
                    if (args.Length == 0)
                        throw new InvalidCastException("__init__() missing 1 required positional argument: 'self'");
                    if (args[0] is not object instance || !declaring.IsInstanceOfType(instance))
                        throw new InvalidCastException($"__init__() requires an instance of '{PyTypeName(declaring)}'");
                    InvokeInitializer(instance, declaring, args[1..], keywords, candidates);
                    return null;
                });
            }
            return null;
        }

        static bool TryAddedInitializer(Type type, object?[] args, IDictionary<string, object?>? keywords, out object? result)
        {
            result = null;
            if (!PyClassDescriptors.HasWritableNamespace(type)) return false;
            foreach (Type owner in PyMetaclasses.MethodOrder(type))
            {
                if (PyClassDescriptors.TryGetDeclaredAddedAttribute(owner, "__init__", out var initializer))
                {
                    result = __object_new(type);
                    InvokeAddedInitializer(result, initializer, type, args, keywords);
                    return true;
                }
                if (DeclaredInitializers(owner).Count != 0) return false;
            }
            return false;
        }

        static void InvokeAddedInitializer(object instance, object? initializer, Type type, object?[] args,
            IDictionary<string, object?>? keywords)
        {
            if ((object?)__invoke_kw(PyMetaclasses.Bind(initializer, instance, type), args, keywords) != null)
                throw new InvalidCastException("__init__() should return None");
        }

        static bool InitializeAllocated(object instance, object?[] args, IDictionary<string, object?>? kwargs,
            Type? start = null)
        {
            for (Type? owner = start ?? instance.GetType(); owner != null; owner = owner.BaseType)
            {
                var candidates = DeclaredInitializers(owner);
                if (owner == ListLayout(owner))
                {
                    __list_initialize(instance, args, kwargs);
                    return true;
                }
                if (candidates.Count == 0) continue;
                InvokeInitializer(instance, owner, args, kwargs, candidates);
                return true;
            }
            return false;
        }

        static void InvokeInitializer(object instance, Type parent, object?[] args, IDictionary<string, object?>? kwargs,
            List<MethodInfo> candidates)
        {
            var selected = PyArgs.Best(candidates, args, kwargs, out var bound)
                ?? throw PyArgs.NoFit(parent.Name + ".__init__", kwargs, candidates);
            try { selected.Invoke(instance, bound); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
