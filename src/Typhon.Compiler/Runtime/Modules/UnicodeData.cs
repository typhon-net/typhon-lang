// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;

    internal static partial class unicodedata
    {
        public const string unidata_version = "17.0.0";

        static readonly Lazy<UCD> Current = new(() => new UCD(unidata_version, CurrentData));
        public static readonly UCD ucd_3_2_0 = new UCD("3.2.0", LegacyData);

        public static string category(string chr) => Current.Value.category(chr);
        public static string bidirectional(string chr) => Current.Value.bidirectional(chr);
        public static long combining(string chr) => Current.Value.combining(chr);
        public static string east_asian_width(string chr) => Current.Value.east_asian_width(chr);
        public static long mirrored(string chr) => Current.Value.mirrored(chr);
        public static string decomposition(string chr) => Current.Value.decomposition(chr);
        public static long @decimal(string chr) => Current.Value.@decimal(chr);
        public static dynamic? @decimal(string chr, object? @default) => Current.Value.@decimal(chr, @default);
        public static long digit(string chr) => Current.Value.digit(chr);
        public static dynamic? digit(string chr, object? @default) => Current.Value.digit(chr, @default);
        public static double numeric(string chr) => Current.Value.numeric(chr);
        public static dynamic? numeric(string chr, object? @default) => Current.Value.numeric(chr, @default);
        public static string normalize(string? form, string? unistr) => Current.Value.normalize(form, unistr);
        public static bool is_normalized(string? form, string? unistr) => Current.Value.is_normalized(form, unistr);

        internal sealed class UCD
        {
            public string unidata_version { get; }
            readonly Lazy<Database> _database;
            Database Data => _database.Value;

            internal UCD(string version, string encoded)
            {
                unidata_version = version;
                _database = new Lazy<Database>(() => new Database(encoded));
            }

            public string category(string chr) => Properties(chr).Category;
            public string bidirectional(string chr) => Properties(chr).Bidirectional;
            public long combining(string chr) => Properties(chr).Combining;
            public string east_asian_width(string chr) => Properties(chr).Width;
            public long mirrored(string chr) => Properties(chr).Mirrored;
            public string decomposition(string chr) => Data.Decompositions.GetValueOrDefault(Codepoint(chr), "");

            public long @decimal(string chr) => Numbers(chr) is Numbers { Decimal: >= 0 } value
                ? value.Decimal : throw new ArgumentException("not a decimal");
            public dynamic? @decimal(string chr, object? @default) => Numbers(chr) is Numbers { Decimal: >= 0 } value
                ? (long)value.Decimal : @default;
            public long digit(string chr) => Numbers(chr) is Numbers { Digit: >= 0 } value
                ? value.Digit : throw new ArgumentException("not a digit");
            public dynamic? digit(string chr, object? @default) => Numbers(chr) is Numbers { Digit: >= 0 } value
                ? (long)value.Digit : @default;
            public double numeric(string chr) => Numbers(chr)?.Value ?? throw new ArgumentException("not a numeric character");
            public dynamic? numeric(string chr, object? @default) => Numbers(chr) is Numbers value ? value.Value : @default;

            CharacterProperties Properties(string chr) => Data.Properties(Codepoint(chr));
            Numbers? Numbers(string chr) => Data.Numbers.TryGetValue(Codepoint(chr), out var number) ? number : null;

            public string normalize(string? form, string? unistr) => Normalize(form, unistr, "normalize");
            public bool is_normalized(string? form, string? unistr) =>
                string.Equals(unistr, Normalize(form, unistr, "is_normalized"), StringComparison.Ordinal);

            string Normalize(string? form, string? text, string operation)
            {
                if (form == null) throw new InvalidCastException($"{operation}() argument 1 must be str, not None");
                if (text == null) throw new InvalidCastException($"{operation}() argument 2 must be str, not None");
                if (text.Length == 0) return text;
                if (form is not ("NFC" or "NFD" or "NFKC" or "NFKD")) throw new ArgumentException("invalid normalization form");
                if (text.All(ch => ch <= '\x7f')) return text;
                var data = Data;
                var codepoints = new List<int>(text.Length);
                bool compatibility = form is "NFKC" or "NFKD";
                for (int i = 0; i < text.Length; i++)
                {
                    int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i++) : text[i];
                    if (compatibility && data.Compatible.TryGetValue(cp, out var variant)) codepoints.AddRange(variant);
                    else if (data.Canonical.TryGetValue(cp, out var decomposed)) codepoints.AddRange(decomposed);
                    else if (cp is >= 0xAC00 and < 0xD7A4) DecomposeHangul(cp, codepoints);
                    else codepoints.Add(cp);
                }
                // CPython's 3.2 view restricts decomposition, but shares the current canonical
                // ordering and composition tables with the main database.
                var canonical = Current.Value.Data;
                CanonicalOrder(codepoints, canonical);
                if (form is "NFC" or "NFKC") Compose(codepoints, canonical);
                var result = new StringBuilder(codepoints.Count);
                foreach (int point in codepoints)
                {
                    if (point <= 0xFFFF) result.Append((char)point);
                    else result.Append(char.ConvertFromUtf32(point));
                }
                return result.ToString();
            }

            static void DecomposeHangul(int cp, List<int> output)
            {
                int offset = cp - 0xAC00;
                output.Add(0x1100 + offset / 588);
                output.Add(0x1161 + offset % 588 / 28);
                if (offset % 28 != 0) output.Add(0x11A7 + offset % 28);
            }

            static void CanonicalOrder(List<int> points, Database data)
            {
                int start = 0;
                byte previous = 0;
                bool unordered = false;
                for (int i = 0; i <= points.Count; i++)
                {
                    byte current = i == points.Count ? (byte)0 : data.Properties(points[i]).Combining;
                    if (current == 0)
                    {
                        if (unordered)
                        {
                            var ordered = points.GetRange(start, i - start).OrderBy(point => data.Properties(point).Combining).ToArray();
                            for (int j = 0; j < ordered.Length; j++) points[start + j] = ordered[j];
                        }
                        start = i + 1;
                        unordered = false;
                    }
                    else if (current < previous) unordered = true;
                    previous = current;
                }
            }

            static void Compose(List<int> points, Database data)
            {
                int starter = -1;
                int write = 0;
                byte previous = 0;
                for (int read = 0; read < points.Count; read++)
                {
                    int current = points[read];
                    byte combining = data.Properties(current).Combining;
                    if (starter >= 0 && (previous == 0 || previous < combining))
                    {
                        int first = points[starter];
                        int composed = ComposeHangul(first, current);
                        if (composed != -1 || data.Compositions.TryGetValue((first, current), out composed))
                        {
                            points[starter] = composed;
                            continue;
                        }
                    }
                    if (combining == 0) starter = write;
                    previous = combining;
                    points[write++] = current;
                }
                points.RemoveRange(write, points.Count - write);
            }

            static int ComposeHangul(int first, int second)
            {
                if (first is >= 0x1100 and < 0x1113 && second is >= 0x1161 and < 0x1176)
                    return 0xAC00 + ((first - 0x1100) * 21 + second - 0x1161) * 28;
                if (first is >= 0xAC00 and < 0xD7A4 && (first - 0xAC00) % 28 == 0 && second is > 0x11A7 and < 0x11C3)
                    return first + second - 0x11A7;
                return -1;
            }

            internal static int Codepoint(string? text) => text switch
            {
                null => throw new InvalidCastException("argument must be a unicode character, not None"),
                { Length: 1 } => text[0],
                { Length: 2 } when char.IsSurrogatePair(text, 0) => char.ConvertToUtf32(text, 0),
                _ => throw new InvalidCastException("argument must be a unicode character, not str"),
            };
        }

        readonly record struct CharacterProperties(string Category, string Bidirectional, byte Combining, string Width, byte Mirrored);
        readonly record struct Numbers(sbyte Decimal, sbyte Digit, double Value);

        sealed class Database
        {
            readonly CharacterProperties[] _properties;
            readonly int[] _rangeStarts;
            readonly int[] _rangeValues;
            public readonly Dictionary<int, string> Decompositions;
            public readonly Dictionary<int, Numbers> Numbers;
            public readonly Dictionary<int, int[]> Canonical;
            public readonly Dictionary<int, int[]> Compatible;
            public readonly Dictionary<(int, int), int> Compositions;

            internal Database(string data)
            {
                using var memory = new MemoryStream(Convert.FromBase64String(data), writable: false);
                using var gzip = new GZipStream(memory, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip);
                string ReadString() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                int[] ReadCodepoints()
                {
                    var points = new int[reader.ReadInt32()];
                    for (int i = 0; i < points.Length; i++) points[i] = reader.ReadInt32();
                    return points;
                }
                _properties = new CharacterProperties[reader.ReadInt32()];
                for (int i = 0; i < _properties.Length; i++)
                    _properties[i] = new CharacterProperties(ReadString(), ReadString(), reader.ReadByte(), ReadString(), reader.ReadByte());
                _rangeStarts = new int[reader.ReadInt32()];
                _rangeValues = new int[_rangeStarts.Length];
                for (int i = 0; i < _rangeStarts.Length; i++)
                {
                    _rangeStarts[i] = reader.ReadInt32();
                    _rangeValues[i] = reader.ReadInt32();
                }
                Decompositions = ReadMap(reader, reader.ReadInt32, ReadString);
                Numbers = ReadMap(reader, reader.ReadInt32, () => new Numbers(reader.ReadSByte(), reader.ReadSByte(), reader.ReadDouble()));
                Canonical = ReadMap(reader, reader.ReadInt32, ReadCodepoints);
                Compatible = ReadMap(reader, reader.ReadInt32, ReadCodepoints);
                Compositions = ReadMap(reader, () => (reader.ReadInt32(), reader.ReadInt32()), reader.ReadInt32);
            }

            static Dictionary<TKey, TValue> ReadMap<TKey, TValue>(BinaryReader reader, Func<TKey> key, Func<TValue> value) where TKey : notnull
            {
                int count = reader.ReadInt32();
                var result = new Dictionary<TKey, TValue>(count);
                for (int i = 0; i < count; i++) result.Add(key(), value());
                return result;
            }

            internal CharacterProperties Properties(int point)
            {
                int range = Array.BinarySearch(_rangeStarts, point);
                return _properties[_rangeValues[range >= 0 ? range : ~range - 1]];
            }
        }
    }
}
