// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using Typhon.Stdlib;

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class PyNamedTupleLayoutAttribute(bool inlinedBase, bool fieldConstructor) : Attribute
    {
        internal bool InlinedBase => inlinedBase;
        internal bool FieldConstructor => fieldConstructor;
    }

    internal static class PyNamedTupleAllocator
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly ConditionalWeakTable<Type, PyClosure> Allocators = new();

        internal static bool TryGet(Type owner, bool inlinedBase, out object? result)
        {
            result = null;
            if (owner.GetCustomAttribute<PyNamedTupleLayoutAttribute>() is not { } layout || layout.InlinedBase != inlinedBase)
                return false;
            result = Allocators.GetValue(owner, type => Create(type, layout.FieldConstructor));
            return true;
        }

        static PyClosure Create(Type owner, bool fieldConstructor)
        {
            var properties = owner.GetProperties(Declared | BindingFlags.Instance)
                .Select(property => (Property: property, Descriptor: property.GetCustomAttribute<PyDescriptorAttribute>()))
                .Where(member => member.Descriptor?.Kind == PyDescriptorKind.NamedTupleField)
                .OrderBy(member => member.Descriptor!.Index).Select(member => member.Property).ToArray();
            string[] names = ["_cls", .. properties.Select(property => property.Name)];
            object?[] defaults = Enumerable.Repeat<object?>(PyClosure.RequiredArgument, names.Length).ToArray();
            if (fieldConstructor && PySignatureAttribute.Find(owner, PySignatureAttribute.ConstructorProvider) is { } signature)
            {
                foreach (var parameter in signature().Arguments)
                {
                    int index = Array.IndexOf(names, parameter.name, 1);
                    object? value = parameter.@default;
                    if (index > 0 && !ReferenceEquals(value, inspect.Parameter.empty)) defaults[index] = value;
                }
            }
            else if (owner.GetField("__typhon_namedtuple_defaults", Declared | BindingFlags.Static)?.GetValue(null) is object?[] factoryDefaults)
                factoryDefaults.CopyTo(defaults, defaults.Length - factoryDefaults.Length);

            return new PyClosure("__new__", (args, keywords) =>
            {
                var values = PyClosure.BindArguments(Builtins.PyTypeName(owner) + ".__new__", names, defaults,
                    names.Length, 0, args, keywords, varargs: false, kwargs: false, out _);
                if (values[0] is not Type type || !owner.IsAssignableFrom(type))
                    throw new InvalidCastException($"{Builtins.PyTypeName(owner)}.__new__ requires a subtype");
                _abc.CheckAbstract(type);
                object instance = RuntimeHelpers.GetUninitializedObject(type);
                for (int index = 0; index < properties.Length; index++)
                    properties[index].SetValue(instance, PyArgs.ConvertArgument(values[index + 1], properties[index].PropertyType));
                return instance;
            });
        }
    }
}
