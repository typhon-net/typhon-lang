// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Numerics;

    internal sealed class PySlice(object? start, object? stop, object? step = null) : IPyRepr
    {
        public object? start { get; } = start;
        public object? stop { get; } = stop;
        public object? step { get; } = step;

        public PySlice(object? stop) : this(null, stop) { }

        static BigInteger Index(object? value, bool allowProtocol = true)
        {
            if (value is BigInteger big) return big;
            if (value is bool boolean) return boolean ? BigInteger.One : BigInteger.Zero;
            if (value is long or int or short or byte or ulong or uint or ushort or sbyte)
                return value is ulong wide ? new BigInteger(wide) : new BigInteger(Convert.ToInt64(value));
            if (value != null && allowProtocol && Builtins.getattr(value, "__index__", null) is object method)
                return Index(Builtins.__invoke(method, []), allowProtocol: false);
            throw new InvalidCastException("slice indices must be integers or None or have an __index__ method");
        }

        BigInteger Stride()
        {
            BigInteger stride = step == null ? BigInteger.One : Index(step);
            return stride.IsZero ? throw new ArgumentException("slice step cannot be zero") : stride;
        }

        public (dynamic, dynamic, dynamic) indices(object? length)
        {
            BigInteger count = Index(length);
            if (count.Sign < 0) throw new ArgumentException("length should not be negative");
            var (first, last, stride) = BigIndices(count);
            return (PythonInteger(first), PythonInteger(last), PythonInteger(stride));
        }

        internal (BigInteger, BigInteger, BigInteger) BigIndices(BigInteger count)
        {
            BigInteger stride = Stride();
            BigInteger lower = stride.Sign < 0 ? -1 : 0;
            BigInteger upper = stride.Sign < 0 ? count - 1 : count;
            BigInteger Bound(object value)
            {
                var index = Index(value);
                return BigInteger.Min(BigInteger.Max(index.Sign < 0 ? index + count : index, lower), upper);
            }
            return (start == null ? stride.Sign < 0 ? upper : lower : Bound(start),
                    stop == null ? stride.Sign < 0 ? lower : upper : Bound(stop), stride);
        }

        static object PythonInteger(BigInteger value) => value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;

        internal (long?, long?, long) Bounds()
        {
            long? Bound(object? value) => value == null ? null : (long)BigInteger.Min(BigInteger.Max(Index(value), long.MinValue), long.MaxValue);
            long stride = (long)BigInteger.Min(BigInteger.Max(Stride(), -((long)int.MaxValue + 1)), (long)int.MaxValue + 1);
            return (Bound(start), Bound(stop), stride);
        }

        public override bool Equals(object? value) => value is PySlice other &&
            Builtins.eq(start, other.start) && Builtins.eq(stop, other.stop) && Builtins.eq(step, other.step);
        public override int GetHashCode() => HashCode.Combine(Builtins.KeyHash(start), Builtins.KeyHash(stop), Builtins.KeyHash(step));
        public string __repr__() => $"slice({Builtins.repr(start)}, {Builtins.repr(stop)}, {Builtins.repr(step)})";
        public override string ToString() => __repr__();
    }
}
