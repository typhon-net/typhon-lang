// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    /// <summary>A Python class created inside a function, with a namespace for each execution of the class statement.</summary>
    internal sealed class PyLocalDataClass : TypeDelegator, IPyKeywordCallable
    {
        readonly string _name;
        internal readonly string PythonModule;
        internal readonly string QualName;
        internal readonly OrderedDictionary<string, object?> Members;

        public PyLocalDataClass(string name, string module, string qualName, Type parent,
            OrderedDictionary<string, object?>? members = null) : base(parent)
        {
            _name = name;
            PythonModule = module;
            QualName = qualName;
            Members = members ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        }

        internal bool TryLookup(string name, out object? value) =>
            Members.TryGetValue(name, out value) ||
            BaseType is PyLocalDataClass parent && parent.TryLookup(name, out value);

        public object? __call__(params object?[] args) => __call_kw__(args, null);

        public object? __call_kw__(object?[] args, IDictionary<string, object?>? kwargs)
        {
            if (PyMetaclasses.TryBindMember(this, "__call__", out var call))
                return Builtins.__invoke_kw(call, args, kwargs);
            if (BaseType != typeof(object) && BaseType is not PyLocalDataClass)
                throw new NotSupportedException("local class instance layout requires a local class or object as its base");
            var instance = new PyLocalClassInstance(this);
            if (TryLookup("__init__", out var initializer))
            {
                object? method = PyMetaclasses.Bind(initializer, instance, this);
                if (Builtins.__invoke_kw(method, args, kwargs) is object)
                    throw new InvalidCastException("__init__() must return None");
            }
            else if (args.Length > 0 || kwargs?.Count > 0)
                throw new InvalidCastException(Name + "() takes no arguments");
            return instance;
        }

        public override string Name => _name;
        public override string FullName => PythonModule + "." + QualName;
        public override Type? BaseType => typeImpl;
        public override Type UnderlyingSystemType => this;
        public override ConstructorInfo[] GetConstructors(BindingFlags flags) => [];
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override bool Equals(Type? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    internal sealed class PyLocalClassInstance(PyLocalDataClass type) : DynamicObject
    {
        internal PyLocalDataClass PythonType => type;
        readonly OrderedDictionary<string, object?> _attributes = new();

        internal bool TryRead(string name, out object? value)
        {
            if (name == "__class__") { value = type; return true; }
            if (name == "__dict__") { value = _attributes; return true; }
            bool foundClassValue = type.TryLookup(name, out object? classValue);
            if (classValue is Typhon.Stdlib.builtins.property descriptor)
            {
                value = descriptor.__get__(this, type);
                return true;
            }
            if (_attributes.TryGetValue(name, out value)) return true;
            if (!foundClassValue) return false;
            value = PyMetaclasses.Bind(classValue, this, type);
            return true;
        }

        internal void Set(string name, object? value)
        {
            if (type.TryLookup(name, out object? member) && member is Typhon.Stdlib.builtins.property descriptor)
                descriptor.__set__(this, value);
            else
                _attributes[name] = value;
        }

        internal bool Remove(string name)
        {
            if (type.TryLookup(name, out object? member) && member is Typhon.Stdlib.builtins.property descriptor)
            {
                descriptor.__delete__(this);
                return true;
            }
            return _attributes.Remove(name);
        }

        public override bool TryGetMember(GetMemberBinder binder, out object? result) => TryRead(binder.Name, out result);
        public override bool TrySetMember(SetMemberBinder binder, object? value) { Set(binder.Name, value); return true; }
        public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
        {
            result = null;
            if (!TryRead(binder.Name, out object? method)) return false;
            args ??= [];
            var names = binder.CallInfo.ArgumentNames;
            int positional = args.Length - names.Count;
            var kwargs = new Dictionary<string, object?>();
            for (int index = 0; index < names.Count; index++) kwargs[names[index]] = args[positional + index];
            result = Builtins.__invoke_kw(method, args[..positional], kwargs);
            return true;
        }
    }

}
