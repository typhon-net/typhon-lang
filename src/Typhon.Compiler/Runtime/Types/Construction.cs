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
            var constructor = cls.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, [typeof(NoInit)], modifiers: null);
            if (constructor == null)
                throw new InvalidCastException($"dict.__new__ cannot allocate '{PyTypeName(cls)}'");
            return constructor.Invoke([default(NoInit)]);
        }

        /// <summary>Binds forwarded arguments to the selected base class's allocation-independent initializer.</summary>
        public static void __base_init(object instance, Type parent, object?[] args, IDictionary<string, object?>? kwargs)
        {
            var candidates = DeclaredInitializers(parent);
            InvokeInitializer(instance, parent, args, kwargs, candidates);
        }

        static List<MethodInfo> DeclaredInitializers(Type parent) =>
            parent.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "__init__" && !method.IsGenericMethodDefinition && !method.IsVirtual).ToList();

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

        static void InitializeAllocated(object instance, object?[] args, IDictionary<string, object?>? kwargs)
        {
            for (Type? owner = instance.GetType(); owner != null; owner = owner.BaseType)
            {
                var candidates = DeclaredInitializers(owner);
                if (candidates.Count == 0) continue;
                InvokeInitializer(instance, owner, args, kwargs, candidates);
                return;
            }
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
