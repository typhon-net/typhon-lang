// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class urllib
    {
        internal static partial class parse
        {
            const string Hex = "0123456789ABCDEF";

            public static string quote(object? @string, [Optional] object? safe, string? encoding = null, string? errors = null)
            {
                if (@string is string text)
                {
                    if (text.Length == 0) return "";
                    return QuoteBytes(Encode(text, encoding ?? "utf-8", errors ?? "strict"), safe);
                }
                if (encoding != null) throw new InvalidCastException("quote() doesn't support 'encoding' for bytes");
                if (errors != null) throw new InvalidCastException("quote() doesn't support 'errors' for bytes");
                return quote_from_bytes(@string, safe);
            }

            public static string quote_from_bytes(object? bs, [Optional] object? safe) =>
                QuoteBytes(bs switch
                {
                    PyBytes bytes => bytes.Data,
                    PyByteArray array => array.ToArray(),
                    _ => throw new InvalidCastException("quote_from_bytes() expected bytes"),
                }, safe);

            static string QuoteBytes(byte[] bytes, object? safe)
            {
                if (bytes.Length == 0) return "";
                Span<bool> allowed = stackalloc bool[128];
                allowed.Clear();
                if (ReferenceEquals(safe, Missing.Value)) allowed['/'] = true;
                else if (safe is string chars)
                {
                    foreach (char c in chars)
                        if (c < 128) allowed[c] = true;
                }
                else
                {
                    foreach (object? item in Builtins.__iter_dyn(safe))
                    {
                        byte b = ByteOps.Byte(item);
                        if (b < 128) allowed[b] = true;
                    }
                }

                var result = new StringBuilder(bytes.Length);
                foreach (byte b in bytes)
                {
                    if (b < 128 && (allowed[b] || char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'))
                        result.Append((char)b);
                    else result.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 15]);
                }
                return result.ToString();
            }

            public static string unquote(object? @string, string? encoding = "utf-8", string? errors = "replace")
            {
                if (@string is PyBytes bytes)
                {
                    if (encoding == null || errors == null)
                        throw new InvalidCastException("decode() argument must be str");
                    return Decode(UnquoteBytes(bytes.Data), encoding, errors);
                }
                if (@string is not string text) throw new InvalidCastException("unquote() expected str or bytes");
                if (!text.Contains('%')) return text;

                encoding ??= "utf-8";
                errors ??= "replace";
                // Only ASCII spans are decoded. A non-ASCII character already present is left unchanged.
                var result = new StringBuilder(text.Length);
                int index = 0;
                while (index < text.Length)
                {
                    int start = index;
                    bool ascii = text[index] < 128;
                    do { index++; } while (index < text.Length && (text[index] < 128) == ascii);
                    if (ascii)
                        result.Append(Decode(UnquoteBytes(Encoding.ASCII.GetBytes(text[start..index])), encoding, errors));
                    else result.Append(text, start, index - start);
                }
                return result.ToString();
            }

            public static PyBytes unquote_to_bytes(object? @string) => new PyBytes(UnquoteBytes(@string switch
            {
                string text => Encode(text, "utf-8", "strict"),
                PyBytes bytes => bytes.Data,
                PyByteArray array => array.ToArray(),
                _ => throw new InvalidCastException("unquote_to_bytes() expected str or bytes"),
            }));

            static byte[] UnquoteBytes(byte[] data)
            {
                int first = Array.IndexOf(data, (byte)'%');
                if (first < 0) return data;
                var output = new byte[data.Length];
                data.AsSpan(0, first).CopyTo(output);
                int length = first;
                for (int index = first; index < data.Length; index++)
                {
                    int high, low;
                    if (data[index] == '%' && index + 2 < data.Length &&
                        (high = HexValue(data[index + 1])) >= 0 && (low = HexValue(data[index + 2])) >= 0)
                    {
                        output[length++] = (byte)((high << 4) | low);
                        index += 2;
                    }
                    else output[length++] = data[index];
                }
                return output[..length];
            }

            static int HexValue(byte b) => b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                _ => -1,
            };

            static string EncodingName(string name) => name.ToLowerInvariant().Replace('_', '-');

            static Encoding Codec(string name, string errors)
            {
                try
                {
                    if (name == "utf-8-sig") return ByteOps.EncodingOf("utf-8", errors);
                    var encoding = ByteOps.EncodingOf(name, errors is "surrogateescape" or "surrogatepass" ? "strict" : errors);
                    if (errors is not ("strict" or "surrogateescape" or "surrogatepass")) return encoding;
                    var strict = (Encoding)encoding.Clone();
                    strict.EncoderFallback = EncoderFallback.ExceptionFallback;
                    strict.DecoderFallback = DecoderFallback.ExceptionFallback;
                    return strict;
                }
                catch (ArgumentException e) { throw new LookupError($"unknown encoding: {name}", e); }
                catch (NotSupportedException e) { throw new LookupError($"unknown encoding: {name}", e); }
            }

            static byte[] Encode(string text, string name, string errors)
            {
                string normalized = EncodingName(name);
                Encoding codec = Codec(normalized, errors);
                // Encode one replacement per Unicode scalar, including characters represented by a UTF-16 pair.
                if (errors == "replace" && codec.CodePage is 20127 or 28591)
                {
                    int max = codec.CodePage == 20127 ? 127 : 255;
                    return text.EnumerateRunes().Select(rune => (byte)(rune.Value <= max ? rune.Value : '?')).ToArray();
                }
                byte[] prefix = normalized switch
                {
                    "utf-8-sig" => new byte[] { 0xef, 0xbb, 0xbf },
                    "utf-16" or "utf16" or "utf-32" or "utf32" => codec.GetPreamble(),
                    _ => Array.Empty<byte>(),
                };
                try
                {
                    byte[] bytes = (errors is "surrogateescape" or "surrogatepass") && text.Any(char.IsSurrogate)
                        ? EncodeSurrogates(text, name, errors, codec)
                        : codec.GetBytes(text);
                    return prefix.Length == 0 ? bytes : prefix.Concat(bytes).ToArray();
                }
                catch (EncoderFallbackException e)
                {
                    int end = e.Index + (e.IsUnknownSurrogate() ? 2 : 1);
                    string reason = normalized is "ascii" or "us-ascii" ? "ordinal not in range(128)" :
                        codec.CodePage == 28591 ? "ordinal not in range(256)" : "surrogates not allowed";
                    throw new UnicodeEncodeError(name, text, e.Index, end, reason);
                }
            }

            static byte[] EncodeSurrogates(string text, string name, string errors, Encoding codec)
            {
                using var output = new MemoryStream();
                int start = 0;
                for (int index = 0; index < text.Length; index++)
                {
                    char c = text[index];
                    if (!char.IsSurrogate(c)) continue;
                    if (char.IsSurrogatePair(text, index)) { index++; continue; }

                    if (index > start) output.Write(codec.GetBytes(text[start..index]));
                    if (errors == "surrogateescape" && c is >= '\udc80' and <= '\udcff')
                        output.WriteByte((byte)(c - 0xdc00));
                    else if (errors == "surrogatepass" && codec is UTF8Encoding)
                    {
                        output.WriteByte((byte)(0xe0 | (c >> 12)));
                        output.WriteByte((byte)(0x80 | ((c >> 6) & 63)));
                        output.WriteByte((byte)(0x80 | (c & 63)));
                    }
                    else throw new UnicodeEncodeError(name, text, index, index + 1, "surrogates not allowed");
                    start = index + 1;
                }
                if (start < text.Length) output.Write(codec.GetBytes(text[start..]));
                return output.ToArray();
            }

            static string Decode(byte[] data, string name, string errors)
            {
                string normalized = EncodingName(name);
                if (normalized == "utf-8-sig" && data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                    data = data[3..];
                else if (normalized is "utf-16" or "utf16")
                {
                    if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { data = data[2..]; normalized = "utf-16-le"; }
                    else if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { data = data[2..]; normalized = "utf-16-be"; }
                }
                else if (normalized is "utf-32" or "utf32")
                {
                    if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { data = data[4..]; normalized = "utf-32-le"; }
                    else if (data.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { data = data[4..]; normalized = "utf-32-be"; }
                }
                Encoding codec = Codec(normalized, errors);
                if (codec is UTF8Encoding && errors is "surrogateescape" or "surrogatepass" or "strict")
                    return ByteOps.DecodeUtf8(data, name, errors);
                if (codec.CodePage == Encoding.ASCII.CodePage && errors == "surrogateescape")
                    return string.Create(data.Length, data, (result, bytes) =>
                    {
                        for (int i = 0; i < bytes.Length; i++)
                            result[i] = bytes[i] < 128 ? (char)bytes[i] : (char)(0xdc00 + bytes[i]);
                    });
                try { return codec.GetString(data); }
                catch (DecoderFallbackException e)
                {
                    string reason = codec.CodePage == Encoding.ASCII.CodePage ? "ordinal not in range(128)" : "invalid byte sequence";
                    throw new UnicodeDecodeError(name, new PyBytes(data), e.Index, e.Index + (e.BytesUnknown?.Length ?? 1), reason);
                }
            }

        }
    }
}
