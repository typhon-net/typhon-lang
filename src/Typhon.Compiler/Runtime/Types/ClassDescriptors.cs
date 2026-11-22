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

    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    internal sealed class PyClassSlotAttribute(string storageName) : Attribute
    {
        internal FieldInfo Storage(PropertyInfo property) => property.DeclaringType!.GetField(storageName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
    }

    internal enum PyDescriptorKind { Property, StaticMethod, ClassMethod, CachedProperty, NamedTupleField }

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

        internal static IDictionary DeclaredNamespace(Type type) => Namespace(type);

        internal static types.MappingProxyType Dictionary(Type type) => new(PyMetaclasses.Namespace(type) ?? Namespace(type));

        internal static bool TryGetAnnotations(Type type, out object? value)
        {
            if (type is PyLocalDataClass local && local.Members.TryGetValue("__annotations__", out value)) return true;
            var members = Namespace(type);
            if (members.TryRead("__annotations__", out value)) return true;
            if (type is not PyLocalDataClass && !type.IsDefined(typeof(PySourceAttribute), false)) return false;
            value = new OrderedDictionary<string, object?>();
            members.SetAdded("__annotations__", value);
            return true;
        }

        sealed class TypeAnnotations
        {
            public object? __get__(object? owner, object? ignored = null)
            {
                if (owner == null) return this;
                if (owner is not Type type) throw new InvalidCastException("type.__annotations__ requires a type object");
                if (TryGetAnnotations(type, out var value)) return value;
                throw new MissingMemberException($"type object '{Builtins.PyTypeName(type)}' has no attribute '__annotations__'");
            }
        }

        internal static readonly object Unassigned = new();

        internal static dynamic? ReadBodyName(object? value, string name, Type module, string moduleName)
        {
            if (!ReferenceEquals(value, Unassigned)) return value;
            if (PyModule.Of(module, moduleName).TryGetNamespaceMember(name, out object? global)) return global;
            object? builtin = Builtins.getattr(typeof(builtins), name, Unassigned);
            return !ReferenceEquals(builtin, Unassigned) ? builtin : Builtins.__unbound_import(name);
        }

        internal static void DeleteBodyName(ref object? value, string name)
        {
            if (ReferenceEquals(value, Unassigned)) Builtins.__unbound_import(name);
            value = Unassigned;
        }

        internal static bool TryReadSlot(PropertyInfo property, PyClassSlotAttribute slot, out object? value)
        {
            value = slot.Storage(property).GetValue(null);
            return !ReferenceEquals(value, Unassigned);
        }

        internal static bool DeleteSlot(Type type, string name)
        {
            if (type.GetProperty(name, Declared) is not { } property ||
                property.GetCustomAttribute<PyClassSlotAttribute>() is not { } slot ||
                !TryReadSlot(property, slot, out _)) return false;
            slot.Storage(property).SetValue(null, Unassigned);
            return true;
        }


        internal static bool TryGetDeclaredDescriptor(Type type, string name, out object? value) =>
            Namespace(type).Descriptors.TryGetValue(name, out value);

        internal static bool SetAddedAttribute(Type type, string name, object? value) =>
            !type.IsValueType && type.IsDefined(typeof(PySourceAttribute), false) && Namespace(type).SetAdded(name, value);

        internal static bool DeleteAddedAttribute(Type type, string name) =>
            Namespaces.TryGetValue(type, out var members) && members.RemoveAdded(name);

        internal static bool TryGetDeclaredAddedAttribute(Type type, string name, out object? value)
        {
            value = null;
            return Namespaces.TryGetValue(type, out var members) && members.TryReadAdded(name, out value);
        }

        internal static bool TryGetAddedInstanceAttribute(object instance, string name, out object? value)
        {
            Type type = instance.GetType();
            for (Type? declaration = type; declaration != null; declaration = declaration.BaseType)
            {
                if (!declaration.IsDefined(typeof(PySourceAttribute), false)) continue;
                var members = Namespace(declaration);
                if (members.TryReadAdded(name, out value))
                {
                    value = PyMetaclasses.Bind(value, instance, type);
                    return true;
                }
                if (members.TryRead(name, out _)) break;
            }
            value = null;
            return false;
        }

        internal static bool TryGetAttribute(Type type, string name, out object? value, bool instance = false)
        {
            for (Type? declaration = type; declaration != null; declaration = declaration.BaseType)
            {
                if (!declaration.IsDefined(typeof(PySourceAttribute))) continue;
                var ns = Namespace(declaration);
                if (!ns.TryRead(name, out var binding)) continue;
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
                value = PyMetaclasses.Bind(binding, null, type);
                return true;
            }
            value = null;
            return false;
        }

        internal static functools.cached_property CachedProperty(Type type, string name) =>
            (functools.cached_property)Namespace(type).Descriptors[name]!;

        static PyClosure Callable(Type type, string name, PySourceAttribute? source, MethodInfo? method,
            Func<object?[], IDictionary<string, object?>?, object?> invoke)
        {
            var callable = new PyClosure(name, invoke)
            {
                __module__ = source?.Module ?? Builtins.__module_of(type),
                __qualname__ = Builtins.PyTypeName(type) + "." + name,
                __doc__ = source?.DocString,
                _signature = method != null ? PySignatureAttribute.Of(method) : null,
                _code = source is { Line: > 0 } ? new sys_.code(name, source.Filename, source.Line) : null,
            };
            if (method?.IsDefined(typeof(PyAbstractMethodAttribute), false) == true)
                Builtins.setattr(callable, "__isabstractmethod__", true);
            return callable;
        }

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

        static PyClosure InstanceFunction(Type type, string name, MethodInfo[] methods, PySourceAttribute? source)
        {
            var method = methods.FirstOrDefault();
            var callable = Callable(type, name, source, method,
                (args, keywords) => Invoke(methods, name, Receiver(name, args), args[1..], keywords));
            if (method?.GetCustomAttribute<PyDataclassReprAttribute>() is { } generated &&
                type.GetMethod(generated.Body, Declared) is MethodInfo implementation)
            {
                callable.__qualname__ = Builtins.PyTypeName(type) + "." + name;
                var wrapped = Callable(type, name, source, implementation,
                    (args, keywords) => Invoke([implementation], name, Receiver(name, args), args[1..], keywords));
                wrapped.__qualname__ = "__create_fn__.<locals>." + name;
                callable.__wrapped__ = wrapped;
            }
            return callable;
        }

        internal static PyClosure ClassBodyFunction(Type type, string name, string implementation)
        {
            var methods = type.GetMethods(Declared).Where(m => m.Name == implementation).ToArray();
            var method = methods.FirstOrDefault();
            var source = method?.GetCustomAttribute<PySourceAttribute>();
            return method?.IsStatic == true
                ? Callable(type, name, source, method, (args, keywords) => Invoke(methods, name, null, args, keywords))
                : InstanceFunction(type, name, methods, source);
        }

        sealed class ClassNamespace : IDictionary
        {
            readonly Dictionary<string, Func<object?>> _bindings = new(StringComparer.Ordinal);
            readonly OrderedDictionary<string, object?> _added = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, object?> Descriptors = new(StringComparer.Ordinal);

            internal ClassNamespace(Type type)
            {
                var source = type.GetCustomAttribute<PySourceAttribute>();
                _bindings["__module__"] = () => Builtins.__module_of(type);
                _bindings["__qualname__"] = () => Builtins.PyTypeName(type);
                _bindings["__doc__"] = () => source?.DocString;
                if (type == typeof(Type)) AddDescriptor("__annotations__", new TypeAnnotations());
                if (type == typeof(PyFunction) || type == typeof(types.FunctionType))
                    _bindings["__call__"] = Bind(new PyClosure("__call__", (arguments, keywords) =>
                    {
                        object function = Receiver("__call__", arguments);
                        if (!inspect.isfunction(function))
                            throw new InvalidCastException("function.__call__ requires a function");
                        return Builtins.__invoke_kw(function, arguments[1..], keywords);
                    }));
                foreach (var field in type.GetFields(Declared).Where(f => f.IsStatic && !f.Name.StartsWith("<", StringComparison.Ordinal) &&
                    !f.Name.StartsWith("__typhon_branch_", StringComparison.Ordinal) && !f.IsDefined(typeof(CompilerGeneratedAttribute))))
                    _bindings[field.Name] = () => field.GetValue(null);
                foreach (var nested in type.GetNestedTypes(Declared))
                    if (nested.IsDefined(typeof(PySourceAttribute))) _bindings[nested.Name] = () => nested;
                foreach (var member in type.GetProperties(Declared))
                {
                    var spec = member.GetCustomAttribute<PyDescriptorAttribute>();
                    var declaration = member.GetCustomAttribute<PySourceAttribute>();
                    if (member.Name == "__annotations__" && member.GetGetMethod(true)?.IsStatic == true &&
                        member.GetCustomAttribute<PyClassSlotAttribute>() == null)
                        _bindings[member.Name] = () => member.GetValue(null);
                    else if (member.GetCustomAttribute<PyClassSlotAttribute>() is { } slot)
                    {
                        var storage = slot.Storage(member);
                        _bindings[member.Name] = () => storage.GetValue(null);
                    }
                    else if (spec?.Kind == PyDescriptorKind.NamedTupleField)
                    {
                        AddDescriptor(member.Name, new PyTupleGetter(spec.Index));
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

            internal bool SetAdded(string name, object? value)
            {
                if (_bindings.ContainsKey(name)) return false;
                lock (_added) _added[name] = value;
                return true;
            }

            internal bool RemoveAdded(string name)
            {
                lock (_added) return _added.Remove(name);
            }

            internal bool TryReadAdded(string name, out object? value)
            {
                lock (_added) return _added.TryGetValue(name, out value);
            }

            internal bool TryRead(string name, out object? value)
            {
                if (TryReadAdded(name, out value)) return true;
                if (_bindings.TryGetValue(name, out var binding))
                {
                    value = binding();
                    return !ReferenceEquals(value, Unassigned);
                }
                value = null;
                return false;
            }

            public object? this[object key]
            {
                get => key is string name && TryRead(name, out object? value) ? value : null;
                set => throw new NotSupportedException();
            }

            public ICollection Keys
            {
                get
                {
                    string[] added;
                    lock (_added) added = _added.Keys.ToArray();
                    return _bindings.Keys.Where(name => TryRead(name, out _)).Concat(added).ToArray();
                }
            }
            public ICollection Values => Snapshot().Values;
            public int Count => Keys.Count;
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public bool Contains(object key) => key is string name && TryRead(name, out _);
            public void Add(object key, object? value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public void Remove(object key) => throw new NotSupportedException();

            IDictionary Snapshot()
            {
                var result = new OrderedDictionary<string, object?>();
                foreach (string name in Keys)
                    if (TryRead(name, out var value)) result[name] = value;
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
