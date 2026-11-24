// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class codecs
    {
        static readonly ConcurrentDictionary<string, object> ErrorHandlers = MakeHandlers();

        static ConcurrentDictionary<string, object> MakeHandlers()
        {
            var handlers = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in new[] { "strict", "ignore", "replace", "xmlcharrefreplace", "backslashreplace",
                                           "namereplace", "surrogateescape", "surrogatepass" })
            {
                handlers[name] = new PyClosure(name, (args, keywords) =>
                {
                    if (args.Length != 1 || keywords?.Count > 0)
                        throw PyClosure.ArgCountError(name, 1, 1, args.Length);
                    return HandleError(name, args[0]);
                });
            }
            return handlers;
        }

        public static dynamic lookup_error([PyPositionalOnly] string name) =>
            ErrorHandlers.TryGetValue(name, out object? handler) ? handler : throw new LookupError("unknown error handler name '" + name + "'");

        public static void register_error([PyPositionalOnly] string name, [PyPositionalOnly] object error_handler)
        {
            if (!Builtins.callable(error_handler)) throw new InvalidCastException("handler must be callable");
            ErrorHandlers[name] = error_handler;
        }

        static (object, long) HandleError(string handler, object? value)
        {
            if (handler == "strict" && value is Exception error) throw error;
            if (value is UnicodeDecodeError decode &&
                handler is "ignore" or "replace" or "backslashreplace" or "surrogateescape" or "surrogatepass")
            {
                byte[] bytes = ByteOps.Buffer(decode.@object);
                int start = (int)Math.Clamp(decode.start, 0, bytes.LongLength);
                int end = (int)Math.Clamp(decode.end, 0, bytes.LongLength);
                var span = bytes.AsSpan(start, Math.Max(0, end - start));
                return handler switch
                {
                    "ignore" => ("", end),
                    "replace" => ("\ufffd", end),
                    "backslashreplace" => (string.Concat(span.ToArray().Select(b => $"\\x{b:x2}")), end),
                    "surrogateescape" => DecodeSurrogateEscape(span, start, decode),
                    _ => DecodeSurrogatePass(bytes.AsSpan(start), start, decode),
                };
            }
            if (value is UnicodeEncodeError or UnicodeTranslateError)
            {
                bool encoding = value is UnicodeEncodeError;
                if (handler is "ignore" or "replace" or "backslashreplace" ||
                    encoding && handler is "xmlcharrefreplace" or "namereplace" or "surrogateescape" or "surrogatepass")
                {
                    var (input, first, last) = value switch
                    {
                        UnicodeEncodeError e => (e.@object, e.start, e.end),
                        UnicodeTranslateError e => (e.@object, e.start, e.end),
                        _ => throw new InvalidOperationException(),
                    };
                    string text = input switch
                    {
                        string source => source,
                        PyString source => source.Text,
                        PyStrEnum member => member.value,
                        _ => throw new InvalidCastException("exception object must be a string"),
                    };
                    var points = TextPoints(text).ToArray();
                    int start = (int)Math.Clamp(first, 0, points.LongLength);
                    int end = (int)Math.Clamp(last, 0, points.LongLength);
                    int[] selected = points[start..Math.Max(start, end)];
                    return handler switch
                    {
                        "ignore" => ("", end),
                        "replace" => (new string(encoding ? '?' : '\ufffd', selected.Length), end),
                        "backslashreplace" => (string.Concat(selected.Select(Escape)), end),
                        "xmlcharrefreplace" => (string.Concat(selected.Select(point => FormattableString.Invariant($"&#{point};"))), end),
                        "namereplace" => (string.Concat(selected.Select(point => unicodedata.NameOf(point) is string name ? "\\N{" + name + "}" : Escape(point))), end),
                        _ => (new PyBytes(EncodeSurrogates(selected, handler, (UnicodeEncodeError)value)), end),
                    };
                }
            }
            string type = value == null ? "NoneType" : Builtins.PyTypeName(value.GetType());
            throw new InvalidCastException($"don't know how to handle {type} in error callback");
        }

        static IEnumerable<int> TextPoints(string text)
        {
            for (int index = 0; index < text.Length; index++)
                yield return char.IsSurrogatePair(text, index) ? char.ConvertToUtf32(text, index++) : text[index];
        }

        static string Escape(int value) => value switch
        {
            < 0x100 => $"\\x{value:x2}",
            < 0x10000 => $"\\u{value:x4}",
            _ => $"\\U{value:x8}",
        };

        static (object, long) DecodeSurrogateEscape(ReadOnlySpan<byte> bytes, int start, UnicodeDecodeError error)
        {
            Span<char> result = stackalloc char[4];
            int length = 0;
            while (length < result.Length && length < bytes.Length && bytes[length] >= 0x80)
            {
                result[length] = (char)(0xdc00 + bytes[length]);
                length++;
            }
            if (length == 0) throw error;
            return (new string(result[..length]), start + length);
        }

        static (int Width, bool LittleEndian) SurrogateFormat(string encoding)
        {
            string format = encoding.ToLowerInvariant().Replace("-", "").Replace("_", "");
            return format switch
            {
                "utf8" => (1, true),
                "utf16" => (2, BitConverter.IsLittleEndian),
                "utf16le" => (2, true),
                "utf16be" => (2, false),
                "utf32" => (4, BitConverter.IsLittleEndian),
                "utf32le" => (4, true),
                "utf32be" => (4, false),
                _ => (0, false),
            };
        }

        static byte[] EncodeSurrogates(int[] selected, string handler, UnicodeEncodeError error)
        {
            if (handler == "surrogateescape")
            {
                if (selected.Any(value => value is < 0xdc80 or > 0xdcff)) throw error;
                return selected.Select(value => (byte)(value - 0xdc00)).ToArray();
            }

            var (width, little) = SurrogateFormat(error.encoding);
            if (width == 0 || selected.Any(value => value is < 0xd800 or > 0xdfff)) throw error;
            using var output = new MemoryStream();
            Span<byte> buffer = stackalloc byte[4];
            foreach (int value in selected)
            {
                if (width == 1)
                {
                    output.WriteByte((byte)(0xe0 | value >> 12));
                    output.WriteByte((byte)(0x80 | value >> 6 & 0x3f));
                    output.WriteByte((byte)(0x80 | value & 0x3f));
                }
                else
                {
                    if (width == 2)
                    {
                        if (little) BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
                        else BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
                    }
                    else if (little) BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)value);
                    else BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
                    output.Write(buffer[..width]);
                }
            }
            return output.ToArray();
        }

        static (object, long) DecodeSurrogatePass(ReadOnlySpan<byte> bytes, int start, UnicodeDecodeError error)
        {
            var (width, little) = SurrogateFormat(error.encoding);
            if (width == 0 || bytes.Length < (width == 1 ? 3 : width)) throw error;
            uint value;
            if (width == 1)
            {
                if (bytes[0] != 0xed || (bytes[1] & 0xc0) != 0x80 || (bytes[2] & 0xc0) != 0x80) throw error;
                value = (uint)((bytes[0] & 0xf) << 12 | (bytes[1] & 0x3f) << 6 | bytes[2] & 0x3f);
                width = 3;
            }
            else if (width == 2)
                value = little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
            else
                value = little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (value is < 0xd800 or > 0xdfff) throw error;
            return (((char)value).ToString(), start + width);
        }
    }
}
