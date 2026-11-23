// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Globalization;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    /// <summary>The immutable integer payload of a compiled int subclass.</summary>
    internal class PyInteger : IPyBool, IPyRepr, IComparable
    {
        protected PyInteger() { }
        protected PyInteger(BigInteger value) => Integer = value;

        internal BigInteger Integer { get; private set; }
        internal object PrimitiveValue => Integer >= long.MinValue && Integer <= long.MaxValue ? (long)Integer : (object)Integer;

        internal static PyInteger AllocateSubclass(Type type, BigInteger value)
        {
            if (!typeof(PyInteger).IsAssignableFrom(type))
                throw new InvalidCastException("int.__new__ requires an integer subtype");
            var instance = (PyInteger)RuntimeHelpers.GetUninitializedObject(type);
            instance.Integer = value;
            return instance;
        }

        public static PyInteger __new__(Type cls, params object?[] arguments) =>
            AllocateSubclass(cls, Builtins.ConstructBigInt(arguments, null));

        public virtual bool __bool__() => !Integer.IsZero;
        public dynamic __int__() => PrimitiveValue;
        public dynamic __index__() => PrimitiveValue;
        public virtual string __repr__() => Integer.ToString(CultureInfo.InvariantCulture);
        public override string ToString() => Integer.ToString(CultureInfo.InvariantCulture);
        public override bool Equals(object? other) =>
            other is PyInteger integer ? Integer == integer.Integer : Builtins.eq(PrimitiveValue, other);
        public override int GetHashCode() => Builtins.KeyHash(PrimitiveValue);
        public int CompareTo(object? other) => Integer.CompareTo(Builtins.ConstructBigInt([other], null));

        public static BigInteger operator +(PyInteger value, BigInteger offset) => value.Integer + offset;
        public static BigInteger operator +(BigInteger offset, PyInteger value) => offset + value.Integer;
        public static BigInteger operator -(PyInteger value, BigInteger offset) => value.Integer - offset;
    }
}
