// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// Flag composition follows CPython's Lib/enum.py. See CPython-LICENSE.txt.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    internal abstract class PyIntegerFlag : PyIntegerEnum, IEnumerable<PyIntegerFlag>, IPyLen
    {
        const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly ConditionalWeakTable<Type, ConcurrentDictionary<BigInteger, PyIntegerFlag>> Combinations = new();

        internal new static BigInteger NextAutoValue(IEnumerable<BigInteger> values)
        {
            BigInteger largest = values.DefaultIfEmpty().Max();
            return BigInteger.One << checked((int)BigInteger.Max(largest, BigInteger.Zero).GetBitLength());
        }

        static IDictionary Members(Type type) => type.GetField("_value2member_map_", StaticMembers)?.GetValue(null) as IDictionary
            ?? throw new InvalidCastException($"<{type.Name}> has no members");

        static IEnumerable<PyIntegerFlag> NamedMembers(IDictionary members) => members.Values.Cast<PyIntegerFlag>();

        static bool IsSingleBit(BigInteger bits) => bits.Sign > 0 && (bits & (bits - 1)).IsZero;

        internal static PyIntegerFlag FromValue(Type type, BigInteger value)
        {
            IDictionary members = Members(type);
            if (members[value] is PyIntegerFlag named) return named;

            // Integer expressions in the class body can use the flags it has bound so far.
            if (type.GetField("__enum_complete", StaticMembers)?.GetValue(null) is not true)
                return (PyIntegerFlag)Allocate(type, value);

            if (value.Sign < 0)
            {
                BigInteger mask = NamedMembers(members).Aggregate(BigInteger.Zero, (bits, member) => bits | member.Integer);
                int width = checked((int)Math.Max(mask.GetBitLength(), BigInteger.Abs(value).GetBitLength()));
                value += BigInteger.One << width;
                if (members[value] is PyIntegerFlag normalized) return normalized;
            }
            return Combinations.GetOrCreateValue(type).GetOrAdd(value, bits => CombineMembers(type, members, bits));
        }

        static PyIntegerFlag CombineMembers(Type type, IDictionary members, BigInteger bits)
        {
            var flag = (PyIntegerFlag)Allocate(type, bits);
            var names = new List<string>();
            BigInteger covered = BigInteger.Zero;
            var descriptors = NamedMembers(members).Where(member => member.Integer.Sign > 0 &&
                (member.Integer & bits) == member.Integer).OrderBy(member => !IsSingleBit(member.Integer));
            foreach (var member in descriptors)
            {
                if ((member.Integer & covered) == member.Integer) continue;
                names.Add(member.name!);
                covered |= member.Integer;
            }
            BigInteger unknown = bits ^ covered;
            if (names.Count > 0 && !unknown.IsZero) names.Add(unknown.ToString(CultureInfo.InvariantCulture));
            flag._name_ = names.Count == 0 ? null : string.Join("|", names);
            return flag;
        }

        public bool __contains__(object? other)
        {
            if (other is not PyIntegerFlag flag || flag.GetType() != GetType())
                throw new InvalidCastException($"unsupported operand for 'in': '{Builtins.PyTypeName(other?.GetType() ?? typeof(object))}'");
            return (Integer & flag.Integer) == flag.Integer;
        }

        public long __len__() => checked((long)BigInteger.PopCount(Integer));
        public IEnumerator<PyIntegerFlag> GetEnumerator() => NamedMembers(Members(GetType()))
            .Where(member => IsSingleBit(member.Integer) && (member.Integer & Integer) == member.Integer).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static PyIntegerFlag operator |(PyIntegerFlag left, PyIntegerFlag right) => left | right.Integer;
        public static PyIntegerFlag operator |(PyIntegerFlag left, BigInteger right) => FromValue(left.GetType(), left.Integer | right);
        public static PyIntegerFlag operator |(BigInteger left, PyIntegerFlag right) => right | left;
        public static PyIntegerFlag operator &(PyIntegerFlag left, PyIntegerFlag right) => left & right.Integer;
        public static PyIntegerFlag operator &(PyIntegerFlag left, BigInteger right) => FromValue(left.GetType(), left.Integer & right);
        public static PyIntegerFlag operator &(BigInteger left, PyIntegerFlag right) => right & left;
        public static PyIntegerFlag operator ^(PyIntegerFlag left, PyIntegerFlag right) => left ^ right.Integer;
        public static PyIntegerFlag operator ^(PyIntegerFlag left, BigInteger right) => FromValue(left.GetType(), left.Integer ^ right);
        public static PyIntegerFlag operator ^(BigInteger left, PyIntegerFlag right) => right ^ left;
        public static PyIntegerFlag operator ~(PyIntegerFlag value) => FromValue(value.GetType(), ~value.Integer);
    }
}
