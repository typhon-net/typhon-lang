// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Typhon.Runtime;

    internal static class codecs
    {
        public static (PyBytes, long) escape_decode(object data, string errors = "strict")
        {
            byte[] source = data is string text ? Encoding.UTF8.GetBytes(text) : ByteOps.Buffer(data);
            using var output = new MemoryStream(source.Length);
            for (int offset = 0; offset < source.Length; offset++)
            {
                byte current = source[offset];
                if (current != (byte)'\\') { output.WriteByte(current); continue; }
                int escape = offset;
                if (++offset == source.Length) throw new ArgumentException("Trailing \\ in string");
                byte next = source[offset];
                if (next == (byte)'\n') continue;

                int decoded = next switch
                {
                    (byte)'a' => 7, (byte)'b' => 8, (byte)'t' => 9, (byte)'n' => 10,
                    (byte)'v' => 11, (byte)'f' => 12, (byte)'r' => 13,
                    (byte)'\\' or (byte)'\'' or (byte)'"' => next,
                    _ => -1,
                };
                if (decoded >= 0) { output.WriteByte((byte)decoded); continue; }
                if (IsOctal(next))
                {
                    int octal = next - '0';
                    for (int digits = 1; digits < 3 && offset + 1 < source.Length && IsOctal(source[offset + 1]); digits++)
                        octal = octal * 8 + source[++offset] - '0';
                    output.WriteByte(unchecked((byte)octal));
                    continue;
                }
                if (next == (byte)'x')
                {
                    int count = 0, hexadecimal = 0;
                    while (count < 2 && offset + 1 < source.Length)
                    {
                        int digit = HexDigit(source[offset + 1]);
                        if (digit < 0) break;
                        offset++;
                        count++;
                        hexadecimal = 16 * hexadecimal + digit;
                    }
                    if (count == 2) output.WriteByte((byte)hexadecimal);
                    else if (errors == "replace") output.WriteByte((byte)'?');
                    else if (errors != "ignore")
                        throw new ArgumentException(errors == "strict" ? $"invalid \\x escape at position {escape}" :
                            $"decoding error; unknown error handling code: {errors}");
                    continue;
                }

                output.WriteByte((byte)'\\');
                output.WriteByte(next);
            }
            return (new PyBytes(output.ToArray()), source.LongLength);
        }

        internal static bool IsRawUnicodeEscape(string encoding) =>
            encoding.StartsWith("raw", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(encodings.normalize_encoding(encoding), "raw_unicode_escape", StringComparison.OrdinalIgnoreCase);

        public static (string, long) raw_unicode_escape_decode(object data, string? errors = null, bool final = true)
        {
            byte[] source = ByteOps.Buffer(data);
            var output = new StringBuilder(source.Length);
            int offset = 0;
            while (offset < source.Length)
            {
                int start = offset;
                byte current = source[offset++];
                if (current != '\\') { output.Append((char)current); continue; }
                if (offset == source.Length && !final) return (output.ToString(), start);
                if (offset == source.Length) { output.Append('\\'); break; }

                byte kind = source[offset++];
                if (kind is not ((byte)'u' or (byte)'U'))
                {
                    output.Append('\\').Append((char)kind);
                    continue;
                }

                int digits = kind == 'u' ? 4 : 8;
                int end = offset + digits;
                uint codePoint = 0;
                while (offset < source.Length && offset < end)
                {
                    int digit = HexDigit(source[offset]);
                    if (digit < 0) break;
                    codePoint = 16 * codePoint + (uint)digit;
                    offset++;
                }
                if (offset < end && offset == source.Length && !final) return (output.ToString(), start);
                if (offset == end && codePoint <= 0x10ffff)
                {
                    if (codePoint <= 0xffff) output.Append((char)codePoint);
                    else output.Append(char.ConvertFromUtf32((int)codePoint));
                    continue;
                }

                string reason = offset == end ? "\\Uxxxxxxxx out of range" : kind == 'u' ?
                    "truncated \\uXXXX escape" : "truncated \\UXXXXXXXX escape";
                switch (errors)
                {
                    case "ignore": break;
                    case "replace": output.Append('\ufffd'); break;
                    case "backslashreplace":
                        for (int index = start; index < offset; index++)
                            output.Append("\\x").Append(source[index].ToString("x2", CultureInfo.InvariantCulture));
                        break;
                    case null or "strict" or "surrogateescape" or "surrogatepass":
                        throw new UnicodeDecodeError("rawunicodeescape", new PyBytes(source), start, offset, reason);
                    case "xmlcharrefreplace" or "namereplace":
                        throw new InvalidCastException("don't know how to handle UnicodeDecodeError in error callback");
                    default: throw new LookupError($"unknown error handler name '{errors}'");
                }
            }
            return (output.ToString(), offset);
        }

        public static (PyBytes, long) raw_unicode_escape_encode(string text, string? errors = null)
        {
            using var output = new MemoryStream(text.Length);
            long length = 0;
            for (int offset = 0; offset < text.Length; offset++, length++)
            {
                char current = text[offset];
                if (current <= '\xff') output.WriteByte((byte)current);
                else if (char.IsHighSurrogate(current) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]))
                {
                    int scalar = char.ConvertToUtf32(current, text[++offset]);
                    output.Write(Encoding.ASCII.GetBytes("\\U" + scalar.ToString("x8", CultureInfo.InvariantCulture)));
                }
                else output.Write(Encoding.ASCII.GetBytes("\\u" + ((int)current).ToString("x4", CultureInfo.InvariantCulture)));
            }
            return (new PyBytes(output.ToArray()), length);
        }

        static bool IsOctal(byte value) => value >= '0' && value <= '7';
        static int HexDigit(byte value) => value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            _ => -1,
        };
    }
}
