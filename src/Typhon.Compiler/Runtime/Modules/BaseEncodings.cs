// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Retain Python's module name.

namespace Typhon.Stdlib
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static partial class binascii
    {
        public static readonly PyBytes BASE32_ALPHABET = PyBytes.Latin1("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567");
        public static readonly PyBytes BASE32HEX_ALPHABET = PyBytes.Latin1("0123456789ABCDEFGHIJKLMNOPQRSTUV");
        public static readonly PyBytes BASE85_ALPHABET = PyBytes.Latin1("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~");
        public static readonly PyBytes Z85_ALPHABET = PyBytes.Latin1("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-:+=^!/*?&<>()[]{}@%$#");

        static readonly byte[] Ascii85Alphabet = Enumerable.Range('!', 85).Select(value => (byte)value).ToArray();
        static readonly sbyte[] Ascii85Table = BuildTable(Ascii85Alphabet);

        static byte[] SelectAlphabet(object? alphabet, PyBytes fallback)
        {
            byte[] bytes = ReferenceEquals(alphabet, Missing.Value) ? fallback.Data : Data(alphabet);
            if (bytes.Length != fallback.Count) throw new ArgumentException($"alphabet must have length {fallback.Count}");
            return bytes;
        }

        static byte[] IgnoredBytes(object? ignored) => ReferenceEquals(ignored, Missing.Value) ? Array.Empty<byte>() : Data(ignored);

        static PyBytes Wrapped(byte[] data, long wrapcol, int group, bool adobe = false)
        {
            if (wrapcol < 0) throw new ArgumentException("Cannot convert negative int");
            if (wrapcol == 0 || wrapcol >= data.Length) return new PyBytes(data);
            int width = (int)Math.Max(adobe ? 2 : group, wrapcol / group * group);
            using var output = new MemoryStream();
            for (int i = 0; i < data.Length;)
            {
                if (i != 0) output.WriteByte((byte)'\n');
                int length = Math.Min(width, data.Length - i);
                // Adobe's closing marker must remain on one line.
                if (adobe && i + length == data.Length - 1) length--;
                output.Write(data, i, length);
                i += length;
            }
            return new PyBytes(output.ToArray());
        }

        public static PyBytes b2a_base32(object data, [Optional, DefaultParameterValue(true)] bool padded,
            [Optional, DefaultParameterValue(0L)] long wrapcol, [Optional] object? alphabet)
        {
            var symbols = SelectAlphabet(alphabet, BASE32_ALPHABET);
            using var output = new MemoryStream();
            uint rest = 0;
            int bits = 0;
            foreach (byte source in Data(data))
            {
                rest = (rest << 8) | source;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    output.WriteByte(symbols[(rest >> bits) & 31]);
                }
                rest &= (1u << bits) - 1;
            }
            if (bits != 0) output.WriteByte(symbols[rest << (5 - bits)]);
            if (padded)
                while ((output.Length & 7) != 0) output.WriteByte((byte)'=');
            return Wrapped(output.ToArray(), wrapcol, 8);
        }

        public static PyBytes a2b_base32(object data, [Optional, DefaultParameterValue(true)] bool padded,
            [Optional] object? alphabet, [Optional] object? ignorechars, bool canonical = false)
        {
            var table = BuildTable(SelectAlphabet(alphabet, BASE32_ALPHABET));
            var ignored = IgnoredBytes(ignorechars);
            using var output = new MemoryStream();
            int count = 0, pads = 0, bits = 0;
            uint rest = 0;
            foreach (byte source in DecodeData(data))
            {
                if (source == '=')
                {
                    if (!padded) throw new Error("Padding not allowed");
                    if (count == 0) throw new Error("Leading padding not allowed");
                    pads++;
                    continue;
                }
                int digit = table[source];
                if (digit < 0)
                {
                    if (ignored.Contains(source)) continue;
                    throw new Error("Only base32 data is allowed");
                }
                if (pads > 0) throw new Error((count + pads) % 8 == 0
                    ? "Excess data after padding" : "Discontinuous padding not allowed");
                rest = (rest << 5) | (uint)digit;
                bits += 5;
                count++;
                if (bits >= 8)
                {
                    bits -= 8;
                    output.WriteByte((byte)(rest >> bits));
                    rest &= (1u << bits) - 1;
                }
            }
            int remaining = count % 8;
            if (remaining is 1 or 3 or 6) throw new Error("Incorrect padding");
            if (padded)
            {
                int required = (8 - remaining) % 8;
                if (pads > required) throw new Error("Excess padding not allowed");
                if (pads < required) throw new Error("Incorrect padding");
            }
            if (canonical && rest != 0) throw new Error("Non-zero padding bits");
            return new PyBytes(output.ToArray());
        }

        public static PyBytes b2a_base85(object data, [Optional, DefaultParameterValue(false)] bool pad,
            [Optional, DefaultParameterValue(0L)] long wrapcol, [Optional] object? alphabet) =>
            Wrapped(Encode85(Data(data), SelectAlphabet(alphabet, BASE85_ALPHABET), pad, false, false, false), wrapcol, 5);

        public static PyBytes a2b_base85(object data, [Optional] object? alphabet,
            [Optional] object? ignorechars, bool canonical = false) =>
            Decode85(DecodeData(data), BuildTable(SelectAlphabet(alphabet, BASE85_ALPHABET)), IgnoredBytes(ignorechars),
                ascii: false, foldspaces: false, canonical);

        public static PyBytes b2a_ascii85(object data, bool foldspaces = false, long wrapcol = 0,
            bool pad = false, bool adobe = false) =>
            Wrapped(Encode85(Data(data), Ascii85Alphabet, pad, true, foldspaces, adobe), wrapcol, 1, adobe);

        public static PyBytes a2b_ascii85(object data, [Optional, DefaultParameterValue(false)] bool foldspaces,
            [Optional, DefaultParameterValue(false)] bool adobe, [Optional] object? ignorechars,
            bool canonical = false)
        {
            var bytes = DecodeData(data);
            if (adobe)
            {
                if (!bytes.AsSpan().EndsWith("~>"u8)) throw new Error("Ascii85 encoded byte sequences must end with b'~>'");
                int start = bytes.AsSpan().StartsWith("<~"u8) ? 2 : 0;
                bytes = bytes[start..^2];
            }
            return Decode85(bytes, Ascii85Table, IgnoredBytes(ignorechars), ascii: true, foldspaces, canonical);
        }

        static byte[] Encode85(byte[] data, byte[] alphabet, bool pad, bool ascii, bool foldspaces, bool adobe)
        {
            using var output = new MemoryStream();
            if (adobe) output.Write("<~"u8);
            Span<byte> digits = stackalloc byte[5];
            for (int offset = 0; offset < data.Length; offset += 4)
            {
                int length = Math.Min(4, data.Length - offset);
                uint value = 0;
                for (int i = 0; i < length; i++) value |= (uint)data[offset + i] << (24 - i * 8);
                if (ascii && (length == 4 || pad) && (value == 0 || foldspaces && value == 0x20202020))
                {
                    output.WriteByte(value == 0 ? (byte)'z' : (byte)'y');
                    continue;
                }
                for (int i = 4; i >= 0; i--)
                {
                    digits[i] = alphabet[value % 85];
                    value /= 85;
                }
                output.Write(digits[..(pad ? 5 : length + 1)]);
            }
            if (adobe) output.Write("~>"u8);
            return output.ToArray();
        }

        static PyBytes Decode85(byte[] data, sbyte[] table, byte[] ignored, bool ascii, bool foldspaces, bool canonical)
        {
            using var output = new MemoryStream();
            int width = 0, start = 0;
            ulong value = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte symbol = data[i];
                if (ascii && (symbol == 'z' || foldspaces && symbol == 'y'))
                {
                    if (width != 0) throw new Error($"'{(char)symbol}' inside Ascii85 5-tuple");
                    byte repeated = symbol == 'z' ? (byte)0 : (byte)' ';
                    for (int j = 0; j < 4; j++) output.WriteByte(repeated);
                    continue;
                }
                int digit = table[symbol];
                if (digit < 0)
                {
                    if (ignored.Contains(symbol)) continue;
                    throw new Error(ascii ? $"Non-Ascii85 digit found: {(char)symbol}" : $"bad Base85 character at position {i}");
                }
                if (width == 0) start = i;
                value = value * 85 + (uint)digit;
                if (++width == 5)
                {
                    EmitGroup(value, 4);
                    value = 0;
                    width = 0;
                }
            }
            if (width != 0)
            {
                ulong original = value;
                for (int i = width; i < 5; i++) value = value * 85 + 84;
                EmitGroup(value, width - 1, original);
            }
            return new PyBytes(output.ToArray());

            void EmitGroup(ulong group, int length, ulong original = 0)
            {
                if (group > uint.MaxValue) throw new Error(ascii ? "Ascii85 overflow" : $"Base85 overflow in hunk starting at byte {start}");
                if (length == 0) throw new Error(ascii ? "Incomplete Ascii85 group" : "Incomplete Base85 group");
                if (canonical && ascii && length == 4)
                {
                    if (group == 0) throw new Error("Non-canonical encoding, use 'z' for all-zero groups");
                    if (foldspaces && group == 0x20202020) throw new Error("Non-canonical encoding, use 'y' for all-space groups");
                }
                if (canonical && length != 4)
                {
                    ulong truncated = group & (uint.MaxValue << ((4 - length) * 8));
                    for (int i = length + 1; i < 5; i++) truncated /= 85;
                    if (truncated != original) throw new Error("Non-zero padding bits");
                }
                for (int offset = 0; offset < length; offset++) output.WriteByte((byte)(group >> ((3 - offset) * 8)));
            }
        }
    }
}
