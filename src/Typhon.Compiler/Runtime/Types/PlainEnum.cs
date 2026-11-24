// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    internal interface IPyPlainEnum
    {
        object? value { get; }
    }

    internal abstract class PyPlainEnum : IPyPlainEnum, IPyRepr
    {
        public object? _value_;
        public string _name_ = "";
        public object? value => _value_;
        public string name => _name_;

        internal static PyPlainEnum Allocate(Type type, object? value)
        {
            var member = (PyPlainEnum)RuntimeHelpers.GetUninitializedObject(type);
            member._value_ = value;
            return member;
        }

        internal static IEnumerable<IPyPlainEnum> Members(Type type) =>
            (IEnumerable<IPyPlainEnum>?)type.GetField("__unique_members", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) ?? [];

        internal static IPyPlainEnum Lookup(Type type, object? value)
        {
            if (value is IPyPlainEnum member && type.IsInstanceOfType(member)) return member;
            foreach (var candidate in Members(type))
                if (Builtins.eq(candidate.value, value)) return candidate;
            throw new ArgumentException($"{Builtins.repr(value)} is not a valid {type.Name}");
        }

        internal virtual string EnumName => GetType().Name;
        public override string ToString() => $"{EnumName}.{name}";
        public string __repr__() => Representation(ToString(), value);
        internal static string Representation(string qualifiedName, object? value) => $"<{qualifiedName}: {Builtins.repr(value)}>";
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(name);
    }

    internal sealed class PyPlainEnumType : TypeDelegator, IPyKeywordCallable, IEnumerable<PyPlainEnum>, IPyLen
    {
        readonly string _name;
        readonly string _module;
        readonly string _qualification;
        readonly OrderedDictionary<string, object?> _members = new(StringComparer.Ordinal);
        readonly List<PyPlainEnum> _unique = new();

        internal PyPlainEnumType(string name, string module, string qualification, IEnumerable<(string, object?)> members)
            : base(typeof(PyPlainEnumMember))
        {
            _name = name;
            _module = module;
            _qualification = qualification;
            foreach (var (key, value) in members)
            {
                if (_members.ContainsKey(key)) throw new InvalidCastException($"'{key}' already defined");
                var member = _unique.FirstOrDefault(item => Builtins.eq(item.value, value));
                if (member == null)
                {
                    member = new PyPlainEnumMember(this) { _name_ = key, _value_ = value };
                    _unique.Add(member);
                }
                _members.Add(key, member);
            }
        }

        internal IEnumerable<KeyValuePair<string, object?>> Members => _members;
        internal string PythonModule => _module;
        internal string PythonQualification => _qualification;
        public override string Name => _name;
        public override string FullName => _module + "." + _qualification;
        public override Type? BaseType => typeof(Typhon.Stdlib.@enum.Enum);
        public override Type UnderlyingSystemType => this;
        public override bool Equals(Type? other) => ReferenceEquals(this, other);
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        internal object? Member(object? key) => key is string name && _members.TryGetValue(name, out var member)
            ? member : throw new KeyNotFoundException(Builtins.repr(key));

        internal bool TryRead(string name, out object? value)
        {
            if (_members.TryGetValue(name, out value)) return true;
            if (name == "__module__") { value = _module; return true; }
            if (name == "__qualname__") { value = _qualification; return true; }
            if (name == "__members__") { value = new Typhon.Stdlib.types.MappingProxyType(_members); return true; }
            return false;
        }

        public object? __call_kw__(object?[] args, IDictionary<string, object?>? kwargs)
        {
            object? input = (args.Length, kwargs?.Count ?? 0) switch
            {
                (1, 0) => args[0],
                (0, 1) when kwargs!.TryGetValue("value", out var value) => value,
                _ => throw new InvalidCastException($"{Name}() expected one value"),
            };
            if (input is PyPlainEnumMember existing && existing.Owner == this) return existing;
            foreach (var member in _unique)
                if (Builtins.eq(member.value, input)) return member;
            throw new ArgumentException($"{Builtins.repr(input)} is not a valid {Name}");
        }

        public IEnumerator<PyPlainEnum> GetEnumerator() => _unique.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public long __len__() => _unique.Count;
    }

    internal sealed class PyPlainEnumMember(PyPlainEnumType owner) : Typhon.Stdlib.@enum.Enum
    {
        internal PyPlainEnumType Owner => owner;
        internal override string EnumName => owner.Name;
    }
}
