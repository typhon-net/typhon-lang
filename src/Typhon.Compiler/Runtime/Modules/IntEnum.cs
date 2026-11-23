// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Globalization;
    using Typhon.Stdlib;

    internal sealed class PyIntEnumType : TypeDelegator, IPyKeywordCallable, IEnumerable<PyIntEnumMember>, IPyLen
    {
        readonly string _name;
        internal readonly string Module;
        readonly OrderedDictionary<string, object?> _members = new(StringComparer.Ordinal);
        readonly Dictionary<BigInteger, PyIntEnumMember> _values = new();

        internal PyIntEnumType(string name, string module, IEnumerable<(string Name, BigInteger Value)> members) : base(typeof(PyIntEnumMember))
        {
            _name = name;
            Module = module;
            foreach (var (key, value) in members)
            {
                if (!_values.TryGetValue(value, out var member))
                    _values[value] = member = new PyIntEnumMember(this, key, value);
                _members.Add(key, member);
            }
        }

        internal IEnumerable<KeyValuePair<string, object?>> Members => _members;
        public override string Name => _name;
        public override string FullName => Module + "." + _name;
        public override Type? BaseType => typeof(Typhon.Stdlib.@enum.IntEnum);
        public override Type UnderlyingSystemType => this;
        public override ConstructorInfo[] GetConstructors(BindingFlags flags) => [];
        public override bool Equals(Type? other) => ReferenceEquals(this, other);
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        internal bool TryRead(string name, out object? value)
        {
            if (_members.TryGetValue(name, out value)) return true;
            if (name == "__module__") { value = Module; return true; }
            if (name == "__members__") { value = new Typhon.Stdlib.types.MappingProxyType(_members); return true; }
            return false;
        }

        public object? __call_kw__(object?[] args, IDictionary<string, object?>? kwargs)
        {
            object? input = (args.Length, kwargs?.Count ?? 0) switch
            {
                (1, 0) => args[0],
                (0, 1) when kwargs!.TryGetValue("value", out var value) => value,
                _ => throw new InvalidCastException($"{Name}() needs one value"),
            };
            if (input is PyIntEnumMember member && member.Owner == this) return member;
            try
            {
                if (_values.TryGetValue(Integer(input), out var existing)) return existing;
            }
            catch (InvalidCastException) { }
            throw new ArgumentException($"{Builtins.repr(input)} is not a valid {Name}");
        }

        internal static BigInteger Integer(object? input)
        {
            if (input is double real && double.IsFinite(real) && Math.Truncate(real) == real) return new BigInteger(real);
            object index = Builtins.__index_value(input);
            return index is BigInteger wide ? wide : (long)index;
        }

        public IEnumerator<PyIntEnumMember> GetEnumerator() => _values.Values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public long __len__() => _values.Count;
    }

    internal abstract class PyIntegerEnum : IPyRepr, IComparable
    {
        internal BigInteger Integer { get; private set; }
        internal string? _name_ = "";
        internal virtual string EnumName => GetType().Name;
        protected virtual string? EnumReprModule => null;
        public string? name => _name_;
        public dynamic value => Integer >= long.MinValue && Integer <= long.MaxValue ? (object)(long)Integer : Integer;

        protected PyIntegerEnum() { }

        protected PyIntegerEnum(string name, BigInteger integer)
        {
            _name_ = name;
            Integer = integer;
        }

        public dynamic __int__() => value;
        public dynamic __index__() => value;
        public override string ToString() => Integer.ToString(CultureInfo.InvariantCulture);
        public string __repr__()
        {
            if (EnumReprModule is string module) return $"{module}.{name}";
            string valueText = Integer.ToString(CultureInfo.InvariantCulture);
            return name == null ? $"<{EnumName}: {valueText}>" : $"<{EnumName}.{name}: {valueText}>";
        }
        public override bool Equals(object? other) => other is PyIntegerEnum member
            ? Integer == member.Integer : Builtins.eq((object)value, other);
        public override int GetHashCode() => Builtins.KeyHash((object)value);
        public int CompareTo(object? other) => Integer.CompareTo(PyIntEnumType.Integer(other));

        public static BigInteger operator +(PyIntegerEnum member, BigInteger offset) => member.Integer + offset;
        public static BigInteger operator +(BigInteger offset, PyIntegerEnum member) => offset + member.Integer;
        public static BigInteger operator -(PyIntegerEnum member, BigInteger offset) => member.Integer - offset;

        internal static PyIntegerEnum Allocate(Type type, object? input)
        {
            var member = (PyIntegerEnum)RuntimeHelpers.GetUninitializedObject(type);
            member.Integer = PyIntEnumType.Integer(input);
            return member;
        }

        internal static BigInteger NextAutoValue(IEnumerable<BigInteger> values) =>
            values.DefaultIfEmpty(BigInteger.Zero).Max() + BigInteger.One;

        internal static PyIntegerEnum Lookup(Type type, object? input)
        {
            if (input is PyIntegerEnum member && type.IsInstanceOfType(member)) return member;
            if (typeof(PyIntegerFlag).IsAssignableFrom(type)) return PyIntegerFlag.FromValue(type, PyIntEnumType.Integer(input));
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var entries = (IDictionary?)type.GetField("_value2member_map_", flags)?.GetValue(null);
            try
            {
                if (entries?[PyIntEnumType.Integer(input)] is PyIntegerEnum selected) return selected;
            }
            catch (InvalidCastException) { }
            throw new ArgumentException($"{Builtins.repr(input)} is not a valid {type.Name}");
        }
    }

    internal sealed class PyIntEnumMember(PyIntEnumType owner, string name, BigInteger integer) : PyIntegerEnum(name, integer)
    {
        internal PyIntEnumType Owner => owner;
        internal override string EnumName => owner.Name;
    }
}

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static class @enum
    {
        internal abstract class IntFlag : PyIntegerFlag { }

        internal static class IntEnum
        {
            public static Type _convert_(string name, string module, object filter, object? source = null,
                object? boundary = null, [PyKeywordOnly] bool as_global = false)
            {
                if (!sys_.modules.TryGetValue(module, out var target) || target == null)
                    throw new KeyNotFoundException(module);
                var globals = (IDictionary<string, object?>)(object)Builtins.getattr(target, "__dict__");
                var from = source == null ? globals : (IDictionary<string, object?>)(object)Builtins.getattr(source, "__dict__");
                var members = from.Where(pair => Builtins.truthy((object?)Builtins.__invoke(filter, [pair.Key])))
                    .Select(pair => (pair.Key, PyIntEnumType.Integer(pair.Value)))
                    .OrderBy(item => item.Item2).ThenBy(item => item.Key, StringComparer.Ordinal).ToList();
                var created = new PyIntEnumType(name, module, members);
                foreach (var member in created.Members) globals[member.Key] = member.Value;
                globals[name] = created;
                return created;
            }
        }
    }
}
