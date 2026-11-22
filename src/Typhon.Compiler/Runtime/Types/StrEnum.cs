// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    internal abstract class PyStrEnum : IPyRepr, IComparable
    {
        public string _value_ = "";
        public string _name_ = "";
        public string value => _value_;
        public string name => _name_;

        internal static PyStrEnum Allocate(Type cls, string value)
        {
            if (!typeof(PyStrEnum).IsAssignableFrom(cls))
                throw new InvalidCastException("str.__new__ requires a string subtype");
            var member = (PyStrEnum)RuntimeHelpers.GetUninitializedObject(cls);
            member._value_ = value;
            return member;
        }

        internal static PyStrEnum Lookup(Type cls, object? value)
        {
            if (value is PyStrEnum member && cls.IsInstanceOfType(member)) return member;
            if (value is PyStrEnum text) value = text.value;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var map = (IDictionary?)cls.GetField("_value2member_map_", flags)?.GetValue(null);
            if (value is string key && map?[key] is PyStrEnum result) return result;
            throw new ArgumentException($"{Builtins.repr(value)} is not a valid {cls.Name}");
        }

        public override string ToString() => value;
        public string __repr__() => $"<{GetType().Name}.{name}: {Builtins.repr(value)}>";
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(value);
        public override bool Equals(object? other) => other is PyStrEnum member ? value == member.value : other is string text && value == text;
        public int CompareTo(object? other) => other switch
        {
            null => 1,
            PyStrEnum member => string.CompareOrdinal(value, member.value),
            string text => string.CompareOrdinal(value, text),
            _ => throw new InvalidCastException("StrEnum values can only be compared with strings"),
        };
    }
}
