// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Numerics;

    internal static partial class Builtins
    {
        /// <summary>Parse a Python hexadecimal float and round once to the nearest binary64 value.</summary>
        public static double __float_fromhex(object? value)
        {
            const string invalid = "invalid hexadecimal floating-point string";
            if (value is not string input) throw new InvalidCastException("fromhex() argument must be str");
            string text = input.Trim(' ', '\t', '\r', '\n', '\v', '\f');
            bool negative = text.StartsWith('-');
            if (negative || text.StartsWith('+')) text = text[1..];
            if (text.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("infinity", StringComparison.OrdinalIgnoreCase))
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            if (text.Equals("nan", StringComparison.OrdinalIgnoreCase))
                return Math.CopySign(double.NaN, negative ? -1.0 : 1.0);

            int exponentStart = text.IndexOfAny(new[] { 'p', 'P' });
            long exponent = 0;
            if (exponentStart >= 0)
            {
                string exponentText = text[(exponentStart + 1)..];
                bool exponentNegative = exponentText.StartsWith('-');
                int start = exponentNegative || exponentText.StartsWith('+') ? 1 : 0;
                if (start == exponentText.Length || exponentText.Skip(start).Any(c => !char.IsAsciiDigit(c)))
                    throw new ArgumentException(invalid);
                long limit = 8L * text.Length + 2048;
                if (long.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
                    exponent = Math.Clamp(parsed, -limit, limit);
                else exponent = exponentNegative ? -limit : limit;
                text = text[..exponentStart];
            }
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            int point = text.IndexOf('.');
            if (point >= 0)
            {
                exponent -= 4L * (text.Length - point - 1);
                text = text.Remove(point, 1);
            }
            // A leading zero keeps BigInteger's hexadecimal parser from treating the first digit as a sign bit.
            if (text.Length == 0 || text.Any(c => !char.IsAsciiHexDigit(c)) ||
                    !BigInteger.TryParse("0" + text, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var significand))
                throw new ArgumentException(invalid);

            double sign = negative ? -1.0 : 1.0;
            if (significand.IsZero) return Math.CopySign(0.0, sign);
            long highest = exponent + significand.GetBitLength() - 1;
            if (highest < -1075) return Math.CopySign(0.0, sign);
            if (highest > 1023) throw new OverflowException("hexadecimal value too large to represent as a float");

            long quantum = Math.Max(-1074, highest - 52);
            if (exponent < quantum)
            {
                int shift = checked((int)(quantum - exponent));
                var retained = significand >> shift;
                var halfway = BigInteger.One << (shift - 1);
                var remainder = significand - (retained << shift);
                if (remainder > halfway || remainder == halfway && !retained.IsEven)
                    retained += BigInteger.One;
                significand = retained;
                exponent = quantum;
            }
            double result = Math.ScaleB((double)significand, (int)exponent);
            if (double.IsInfinity(result)) throw new OverflowException("hexadecimal value too large to represent as a float");
            return Math.CopySign(result, sign);
        }
    }
}
