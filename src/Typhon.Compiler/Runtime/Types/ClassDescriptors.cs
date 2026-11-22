// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.ExceptionServices;
    using Typhon.Stdlib;

    internal enum PyDescriptorKind { Property, StaticMethod, ClassMethod, CachedProperty, NamedTupleField, ConditionalMethod }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
    internal sealed class PyDescriptorAttribute(PyDescriptorKind kind, string? getter = null, int index = -1) : Attribute
    {
        public PyDescriptorKind Kind { get; } = kind;
        public string? Getter { get; } = getter;
        public int Index { get; } = index;
    }

    internal sealed class PyTupleGetter(int index)
    {
        public object? __doc__ { get; set; }

        public object? __get__(object? instance, object? owner = null)
        {
            if (instance == null) return owner != null ? this : throw new InvalidCastException("__get__(None, None) is invalid");
            if (instance is not ITuple tuple) throw new InvalidCastException("descriptor for tuple requires a tuple");
            return tuple[index];
        }

        public void __set__(object? instance, object? value) => throw new MissingMemberException("can't set attribute");
        public void __delete__(object? instance) => throw new MissingMemberException("can't delete attribute");
    }

    /// <summary>A compiled class's declared Python members and their stable descriptor objects.</summary>
    internal static class PyClassDescriptors
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        static readonly ConditionalWeakTable<Type, ClassNamespace> Namespaces = new();

        static ClassNamespace Namespace(Type type) => Namespaces.GetValue(type, t => new ClassNamespace(t));

        internal static types.MappingProxyType Dictionary(Type type) => new(Namespace(type));

        internal static bool TryGetDeclaredDescriptor(Type type, string name, out object? value) =>
            Namespace(type).Descriptors.TryGetValue(name, out value);

        internal static bool TryGetAttribute(Type type, string name, out object? value, bool instance = false)
        {
            for (Type? declaration = type; declaration != null; declaration = declaration.BaseType)
            {
                if (!declaration.IsDefined(typeof(PySourceAttribute))) continue;
                var ns = Namespace(declaration);
                if (!ns.Contains(name)) continue;
                if (ns.Descriptors.TryGetValue(name, out value))
                {
                    if (instance && value is not builtins.MethodDescriptor) break;
                    value = value switch
                    {
                        builtins.staticmethod method => method.__get__(null, type),
                        builtins.classmethod method => method.__get__(null, type),
                        _ => value,
                    };
                    return true;
                }
                if (instance) break;
                value = ns[name];
                return true;
            }
            value = null;
            return false;
        }

        internal static functools.cached_property CachedProperty(Type type, string name) =>
            (functools.cached_property)Namespace(type).Descriptors[name]!;

        static PyClosure Callable(Type type, string name, PySourceAttribute? source, MethodInfo? method,
            Func<object?[], IDictionary<string, object?>?, object?> invoke) =>
            new PyClosure(name, invoke)
            {
                __module__ = source?.Module ?? Builtins.__module_of(type),
                __doc__ = source?.DocString,
                _signature = method != null ? PySignatureAttribute.Of(method) : null,
                _code = source is { Line: > 0 } ? new sys_.code(name, source.Filename, source.Line) : null,
            };

        static object? Invoke(IEnumerable<MethodInfo> methods, string name, object? target, object?[] args, IDictionary<string, object?>? keywords)
        {
            var method = PyArgs.Best(methods, args, keywords, out var call) ?? throw PyArgs.NoFit(name, keywords, methods);
            try { return method.Invoke(target, call); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        static object Receiver(string name, object?[] args) => args.Length > 0 && args[0] != null ? args[0]! :
            throw new InvalidCastException($"{name}() requires an instance");

        static PyClosure InstanceFunction(Type type, string name, MethodInfo[] methods, PySourceAttribute? source) =>
            Callable(type, name, source, methods.FirstOrDefault(), (args, keywords) => Invoke(methods, name, Receiver(name, args), args[1..], keywords));

        internal static PyClosure ConditionalFunction(Type type, string name, string implementation)
        {
            var methods = type.GetMethods(Declared).Where(m => m.Name == implementation).ToArray();
            return InstanceFunction(type, name, methods, methods.FirstOrDefault()?.GetCustomAttribute<PySourceAttribute>());
        }

        internal static object? BindConditionalMethod(object? value, object instance, string name)
        {
            if (PyAttrDict.TryGetExtra(instance, name, out var local)) return local;
            return value is PyFunction function ? new types.MethodType(function, instance) : value;
        }

        sealed class ClassNamespace : IDictionary
        {
            readonly Dictionary<string, Func<object?>> _bindings = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, object?> Descriptors = new(StringComparer.Ordinal);

            internal ClassNamespace(Type type)
            {
                var source = type.GetCustomAttribute<PySourceAttribute>();
                _bindings["__module__"] = () => Builtins.__module_of(type);
                _bindings["__qualname__"] = () => Builtins.PyTypeName(type);
                _bindings["__doc__"] = () => source?.DocString;
                foreach (var field in type.GetFields(Declared).Where(f => f.IsStatic && !f.Name.StartsWith("<", StringComparison.Ordinal) &&
                    !f.Name.StartsWith("__typhon_branch_", StringComparison.Ordinal)))
                    _bindings[field.Name] = () => field.GetValue(null);
                foreach (var nested in type.GetNestedTypes(Declared))
                    if (nested.IsDefined(typeof(PySourceAttribute))) _bindings[nested.Name] = () => nested;
                foreach (var member in type.GetProperties(Declared))
                {
                    var spec = member.GetCustomAttribute<PyDescriptorAttribute>();
                    var declaration = member.GetCustomAttribute<PySourceAttribute>();
                    if (spec?.Kind == PyDescriptorKind.NamedTupleField)
                    {
                        AddDescriptor(member.Name, new PyTupleGetter(spec.Index));
                    }
                    else if (spec?.Kind == PyDescriptorKind.ConditionalMethod)
                    {
                        object? value = type.GetField(spec.Getter!, Declared)?.GetValue(null);
                        Descriptors[member.Name] = value;
                        _bindings[member.Name] = () => value;
                    }
                    else if (spec?.Kind == PyDescriptorKind.Property)
                    {
                        var getter = InstanceFunction(type, member.Name, [member.GetGetMethod(true)!], declaration);
                        var setter = member.GetSetMethod(true) is { } set
                            ? InstanceFunction(type, member.Name, [set], set.GetCustomAttribute<PySourceAttribute>()) : null;
                        var property = new builtins.property(getter, setter);
                        property.__set_name__(type, member.Name);
                        AddDescriptor(member.Name, property);
                    }
                    else if (spec?.Kind == PyDescriptorKind.CachedProperty)
                    {
                        MethodInfo[] getters = type.GetMethods(Declared).Where(m => m.Name == spec.Getter).ToArray();
                        var property = new functools.cached_property(InstanceFunction(type, member.Name, getters, declaration));
                        property.__set_name__(type, member.Name);
                        AddDescriptor(member.Name, property);
                    }
                }
                foreach (var overloads in type.GetMethods(Declared).Where(m => m.IsDefined(typeof(PySourceAttribute)) && !m.IsSpecialName &&
                    !m.Name.StartsWith("__typhon_branch_", StringComparison.Ordinal)).GroupBy(m => m.Name))
                {
                    var methods = overloads.ToArray();
                    var method = methods[0];
                    var metadata = method.GetCustomAttribute<PySourceAttribute>();
                    var spec = method.GetCustomAttribute<PyDescriptorAttribute>();
                    if (spec?.Kind == PyDescriptorKind.StaticMethod)
                        AddDescriptor(method.Name, new builtins.staticmethod(Callable(type, method.Name, metadata, method, (a, kw) => Invoke(methods, method.Name, null, a, kw))));
                    else if (spec?.Kind == PyDescriptorKind.ClassMethod)
                    {
                        var implementations = type.GetMethods(Declared).Where(m => m.Name == spec.Getter).ToArray();
                        AddDescriptor(method.Name, new builtins.classmethod(Callable(type, method.Name, metadata, method,
                            (a, kw) => Invoke(implementations, method.Name, null, a, kw))));
                    }
                    else
                        _bindings[method.Name] = Bind(InstanceFunction(type, method.Name, methods, metadata));
                }
            }

            static Func<object?> Bind(object value) => () => value;
            void AddDescriptor(string name, object value) { Descriptors[name] = value; _bindings[name] = Bind(value); }

            public object? this[object key]
            {
                get => key is string name && _bindings.TryGetValue(name, out var binding) ? binding() : null;
                set => throw new NotSupportedException();
            }

            public ICollection Keys => _bindings.Keys;
            public ICollection Values => _bindings.Values.Select(read => read()).ToArray();
            public int Count => _bindings.Count;
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public bool Contains(object key) => key is string name && _bindings.ContainsKey(name);
            public void Add(object key, object? value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public void Remove(object key) => throw new NotSupportedException();

            IDictionary Snapshot()
            {
                var result = new OrderedDictionary<string, object?>();
                foreach (var (name, read) in _bindings) result[name] = read();
                return result;
            }

            public void CopyTo(Array array, int index) => Snapshot().CopyTo(array, index);
            public IDictionaryEnumerator GetEnumerator() => Snapshot().GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    internal static partial class Builtins
    {
        public static dynamic? __cached_property_get__(object instance, Type owner, string name) =>
            PyAttrDict.TryGetExtra(instance, name, out var value) ? value : PyClassDescriptors.CachedProperty(owner, name).__get__(instance);

        public static void __cached_property_set__(object instance, string name, object? value) => new PyAttrDict(instance)[name] = value;
    }
}
