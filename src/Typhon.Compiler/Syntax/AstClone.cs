// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Typhon.Compiler.Syntax
{
    /// <summary>Deep copies of syntax trees (a method copied into another class gets its own nodes).</summary>
    public static class AstClone
    {
        public static T Clone<T>(T node) where T : class => (T)CloneObject(node);

        static object CloneObject(object o)
        {
            if (o == null || o is string) return o;
            var type = o.GetType();
            if (type.IsValueType || type.IsEnum) return o;
            if (o is IList list && type.IsGenericType)
            {
                var copy = (IList)System.Activator.CreateInstance(type);
                foreach (var item in list) copy.Add(CloneObject(item));
                return copy;
            }
            if (type.Namespace != typeof(Node).Namespace) return o; // (not a syntax node: shared)
            var clone = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    f.SetValue(clone, CloneObject(f.GetValue(o)));
            return clone;
        }
    }
}
