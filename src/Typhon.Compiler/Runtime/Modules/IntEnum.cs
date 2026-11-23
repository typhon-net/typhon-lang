// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Globalization;
    using Typhon.Stdlib;

    internal sealed class PyIntEnumType : TypeDelegator, IPyKeywordCallable, IEnumerable<PyIntegerEnum>, IPyLen
    {
        readonly string _name;
        internal readonly string Module;
        readonly OrderedDictionary<string, object?> _members = new(StringComparer.Ordinal);
        readonly Dictionary<BigInteger, PyIntegerEnum> _values = new();
        readonly ConcurrentDictionary<BigInteger, PyIntegerFlag> _combinations = new();
        readonly bool _isFlag;

        internal PyIntEnumType(string name, string module, IEnumerable<(string Name, BigInteger Value)> members,
            bool isFlag = false) : base(isFlag ? typeof(PyIntFlagMember) : typeof(PyIntEnumMember))
        {
            _isFlag = isFlag;
            _name = name;
            Module = module;
            foreach (var (key, value) in members)
            {
                if (!_values.TryGetValue(value, out var member))
                    _values[value] = member = isFlag ? new PyIntFlagMember(this, key, value) : new PyIntEnumMember(this, key, value);
                _members.Add(key, member);
            }
        }

        internal IEnumerable<KeyValuePair<string, object?>> Members => _members;
        public override string Name => _name;
        public override string FullName => Module + "." + _name;
        public override Type? BaseType => _isFlag ? typeof(@enum.IntFlag) : typeof(@enum.IntEnum);
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
            if (input is PyIntegerEnum member && member.DynamicEnumType == this) return member;
            try
            {
                BigInteger value = Integer(input);
                if (_isFlag) return FlagValue(value);
                if (_values.TryGetValue(value, out var existing)) return existing;
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

        internal IEnumerable<PyIntegerFlag> FlagMembers => _values.Values.Cast<PyIntegerFlag>();

        internal PyIntegerFlag FlagValue(BigInteger value)
        {
            if (_values.TryGetValue(value, out var exact)) return (PyIntegerFlag)exact;
            value = PyIntegerFlag.NormalizeBits(value, FlagMembers);
            if (_values.TryGetValue(value, out var normalized)) return (PyIntegerFlag)normalized;
            return _combinations.GetOrAdd(value, bits =>
                new PyIntFlagMember(this, PyIntegerFlag.CombinationName(FlagMembers, bits), bits));
        }

        IEnumerable<PyIntegerEnum> IterableMembers => _isFlag
            ? _values.Values.Where(member => PyIntegerFlag.IsSingleBit(member.Integer))
            : _values.Values;
        public IEnumerator<PyIntegerEnum> GetEnumerator() => IterableMembers.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public long __len__() => IterableMembers.LongCount();
    }

    internal abstract class PyIntegerEnum : PyInteger
    {
        internal string? _name_ = "";
        internal virtual PyIntEnumType? DynamicEnumType => null;
        internal virtual string EnumName => DynamicEnumType?.Name ?? GetType().Name;
        protected virtual string? EnumReprModule => null;
        public string? name => _name_;
        public dynamic value => PrimitiveValue;

        protected PyIntegerEnum() { }

        protected PyIntegerEnum(string? name, BigInteger integer) : base(integer) => _name_ = name;

        public override string __repr__()
        {
            if (EnumReprModule is string module) return $"{module}.{name}";
            string valueText = Integer.ToString(CultureInfo.InvariantCulture);
            return name == null ? $"<{EnumName}: {valueText}>" : $"<{EnumName}.{name}: {valueText}>";
        }
        internal static PyIntegerEnum Allocate(Type type, object? input) =>
            (PyIntegerEnum)AllocateSubclass(type, PyIntEnumType.Integer(input));

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
        internal override PyIntEnumType DynamicEnumType => owner;
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
        internal abstract class Enum : PyPlainEnum
        {
            public static Type __typhon_factory__(string value, object names, [PyKeywordOnly] string? module = null,
                [PyKeywordOnly] string? qualname = null, [PyKeywordOnly] long start = 1)
            {
                if (names is string text)
                    names = text.Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return new PyPlainEnumType(value, module ?? "__main__", qualname ?? value, Members());

                IEnumerable<(string, object?)> Members()
                {
                    if (names is System.Collections.IDictionary mapping)
                    {
                        foreach (var pair in Builtins.ReadDictItems(mapping)) yield return ((string)pair.Key, pair.Value);
                        yield break;
                    }
                    foreach (object? name in Builtins.__iter_dyn(names))
                    {
                        if (name is string key)
                        {
                            yield return (key, start);
                            start = checked(start + 1);
                            continue;
                        }
                        var pair = Builtins.__unpack_items(Builtins.__iter_dyn(name), 2);
                        yield return ((string)pair[0], pair[1]);
                    }
                }
            }
        }
        internal abstract class IntFlag : PyIntegerFlag
        {
            protected IntFlag() { }
            protected IntFlag(string? name, System.Numerics.BigInteger value) : base(name, value) { }

            public static Type _convert_(string name, string module, object filter, object? source = null,
                [PyKeywordOnly] object? boundary = null, [PyKeywordOnly] bool as_global = false) =>
                ConvertIntegerConstants(name, module, filter, source, isFlag: true);
        }

        internal static class IntEnum
        {
            public static Type _convert_(string name, string module, object filter, object? source = null,
                [PyKeywordOnly] object? boundary = null, [PyKeywordOnly] bool as_global = false) =>
                ConvertIntegerConstants(name, module, filter, source, isFlag: false);
        }

        static Type ConvertIntegerConstants(string name, string module, object filter, object? source, bool isFlag)
        {
            if (!sys_.modules.TryGetValue(module, out var target) || target == null)
                throw new KeyNotFoundException(module);
            var globals = (IDictionary<string, object?>)Builtins.getattr(target, "__dict__")!;
            var from = source == null ? globals : (IDictionary<string, object?>)Builtins.getattr(source, "__dict__")!;
            var members = from.Where(pair => Builtins.truthy((object?)Builtins.__invoke(filter, [pair.Key])))
                .Select(pair => (pair.Key, PyIntEnumType.Integer(pair.Value)))
                .OrderBy(item => item.Item2).ThenBy(item => item.Key, StringComparer.Ordinal).ToList();
            var created = new PyIntEnumType(name, module, members, isFlag);
            foreach (var member in created.Members) globals[member.Key] = member.Value;
            globals[name] = created;
            return created;
        }
    }
}
