// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class codecs
    {
        const string AcePrefix = "xn--";
        static readonly Lazy<NameprepData> Nameprep = new(() => new NameprepData());
        static readonly IdnMapping PlatformIdn = new() { AllowUnassigned = true, UseStd3AsciiRules = false };

        internal static bool IsIdna(string encoding) => encoding.Contains("idna", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(encodings.normalize_encoding(encoding), "idna", StringComparison.OrdinalIgnoreCase);

        static void RequireIdnaStrict(string errors)
        {
            if (errors != "strict") throw new UnicodeError($"Unsupported error handling: {errors}");
        }

        internal static byte[] EncodeIdna(string text, string errors)
        {
            RequireIdnaStrict(errors);
            string[] labels = text.Split(['.', '\u3002', '\uff0e', '\uff61']);
            if (text.All(char.IsAscii))
            {
                int missing = Array.FindIndex(labels, 0, labels.Length - 1, label => label.Length == 0);
                if (missing >= 0)
                {
                    int start = labels.Take(missing).Sum(label => label.Length) + missing;
                    throw new UnicodeEncodeError("idna", text, start, start + 1, "label empty");
                }
            }
            long offset = 0;
            for (int i = 0; i < labels.Length; i++)
            {
                if (i == labels.Length - 1 && labels[i].Length == 0) break;
                string label = labels[i];
                try { labels[i] = IdnaAsciiLabel(label); }
                catch (UnicodeEncodeError error)
                {
                    throw new UnicodeEncodeError("idna", text, offset + error.start, offset + error.end, error.reason);
                }
                offset += Builtins.len(label) + 1;
            }
            return Encoding.ASCII.GetBytes(string.Join('.', labels));
        }

        static string IdnaAsciiLabel(string input)
        {
            string label = input.All(char.IsAscii) ? input : PrepareIdnaLabel(input);
            if (label.Length == 0) throw new UnicodeEncodeError("idna", label, 0, 1, "label empty");
            if (label.All(char.IsAscii))
            {
                if (label.Length >= 64) throw new UnicodeEncodeError("idna", label, 0, label.Length, "label too long");
                return label;
            }
            if (label.StartsWith(AcePrefix, StringComparison.OrdinalIgnoreCase))
                throw new UnicodeEncodeError("idna", label, 0, AcePrefix.Length, "Label starts with ACE prefix");
            // Every scalar costs at least one ASCII byte with Bootstring. The ACE prefix costs another four.
            long length = Builtins.len(label);
            if (length >= 60) throw new UnicodeEncodeError("idna", label, 0, length, "label too long");
            string? ascii = null;
            try
            {
                string candidate = PlatformIdn.GetAscii(label);
                if (candidate.StartsWith(AcePrefix, StringComparison.Ordinal) && !candidate.Contains('.') &&
                    PlatformIdn.GetUnicode(candidate) == label) ascii = candidate;
            }
            catch (Exception error) when (error is ArgumentException or PlatformNotSupportedException) { }
            // Platform IDNA may apply newer mappings or reject otherwise valid IDNA 2003 labels.
            ascii ??= AcePrefix + PunycodeEncode(IdnaCodepoints(label).ToArray());
            if (ascii.Length >= 64) throw new UnicodeEncodeError("idna", label, 0, length, "label too long");
            return ascii;
        }

        static string PrepareIdnaLabel(string input)
        {
            NameprepData data = Nameprep.Value;
            var mapped = new StringBuilder(input.Length);
            foreach (int point in IdnaCodepoints(input))
                mapped.Append(data.Mappings.TryGetValue(point, out string? mapping) ? mapping : IdnaCharacter(point));
            var database = unicodedata.ucd_3_2_0;
            string label = database.normalize("NFKC", mapped.ToString());
            var points = IdnaCodepoints(label).ToArray();
            for (int i = 0; i < points.Length; i++)
                if (data.Prohibited(points[i]))
                    throw new UnicodeEncodeError("idna", label, i, i + 1, $"Invalid character {Builtins.repr(IdnaCharacter(points[i]))}");

            string[] direction = points.Select(point => database.bidirectional(IdnaCharacter(point))).ToArray();
            if (direction.Any(IsRightToLeft))
            {
                int left = Array.IndexOf(direction, "L");
                if (left >= 0) throw new UnicodeEncodeError("idna", label, left, left + 1, "Violation of BIDI requirement 2");
                int edge = !IsRightToLeft(direction[0]) ? 0 : !IsRightToLeft(direction[^1]) ? direction.Length - 1 : -1;
                if (edge >= 0) throw new UnicodeEncodeError("idna", label, edge, edge + 1, "Violation of BIDI requirement 3");
            }
            return label;
        }

        static bool IsRightToLeft(string direction) => direction is "R" or "AL";

        internal static string DecodeIdna(byte[] bytes, string errors)
        {
            RequireIdnaStrict(errors);
            string ascii = Encoding.Latin1.GetString(bytes);
            if (ascii.All(char.IsAscii) && !ascii.Contains(AcePrefix, StringComparison.OrdinalIgnoreCase)) return ascii;
            string[] labels = ascii.Split('.');
            int offset = 0;
            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i];
                try { labels[i] = IdnaUnicodeLabel(label); }
                catch (UnicodeEncodeError error)
                {
                    throw new UnicodeDecodeError("idna", new PyBytes(bytes), offset + error.start, offset + error.end, error.reason);
                }
                catch (UnicodeDecodeError error)
                {
                    throw new UnicodeDecodeError("idna", new PyBytes(bytes), offset + error.start, offset + error.end, error.reason);
                }
                offset += label.Length + 1;
            }
            return string.Join('.', labels);
        }

        static string IdnaUnicodeLabel(string label)
        {
            UnicodeDecodeError Invalid(int start, int end, string reason) =>
                new("idna", new PyBytes(Encoding.Latin1.GetBytes(label)), start, end, reason);
            if (label.Length > 1024) throw Invalid(0, label.Length, "label way too long");
            if (!label.StartsWith(AcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                int invalid = label.AsSpan().IndexOfAnyInRange((char)128, char.MaxValue);
                if (invalid >= 0) throw Invalid(invalid, invalid + 1, "ordinal not in range(128)");
                return label;
            }
            string? decoded = null;
            // Punycode basic characters retain their original case in Python's decoder.
            if (label.All(character => character is not (>= 'A' and <= 'Z')))
                try
                {
                    string candidate = PlatformIdn.GetUnicode(label);
                    if (candidate != label && string.Equals(IdnaAsciiLabel(candidate), label, StringComparison.Ordinal)) decoded = candidate;
                }
                catch (Exception error) when (error is ArgumentException or PlatformNotSupportedException) { }
            decoded ??= PunycodeDecode(label, Invalid);
            string encoded = IdnaAsciiLabel(decoded);
            if (!string.Equals(encoded, label, StringComparison.OrdinalIgnoreCase))
                throw Invalid(0, label.Length, $"IDNA does not round-trip, '{new PyBytes(Encoding.Latin1.GetBytes(label)).__repr__()}' != '{new PyBytes(Encoding.ASCII.GetBytes(encoded)).__repr__()}'");
            return decoded;
        }

        static IEnumerable<int> IdnaCodepoints(string text)
        {
            for (int i = 0; i < text.Length; i++)
                yield return char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i++) : text[i];
        }

        static string IdnaCharacter(int point) => point <= 0xffff ? ((char)point).ToString() : char.ConvertFromUtf32(point);

        // RFC 3492's Bootstring parameters. The fallback covers prepped labels the platform alters or rejects.
        static string PunycodeEncode(int[] input)
        {
            var output = new StringBuilder();
            foreach (int point in input)
                if (point < 128) output.Append((char)point);
            int basic = output.Length, handled = basic, next = 128, bias = 72;
            long delta = 0;
            if (basic != 0) output.Append('-');
            while (handled < input.Length)
            {
                int smallest = input.Where(point => point >= next).Min();
                delta += (long)(smallest - next) * (handled + 1);
                next = smallest;
                foreach (int point in input)
                {
                    if (point < next) delta++;
                    if (point != next) continue;
                    long rest = delta;
                    for (int k = 36; ; k += 36)
                    {
                        int limit = Math.Clamp(k - bias, 1, 26);
                        if (rest < limit) break;
                        output.Append(PunycodeDigit(limit + (int)((rest - limit) % (36 - limit))));
                        rest = (rest - limit) / (36 - limit);
                    }
                    output.Append(PunycodeDigit((int)rest));
                    bias = PunycodeBias(delta, handled + 1, handled == basic);
                    delta = 0;
                    handled++;
                }
                delta++;
                next++;
            }
            return output.ToString();
        }

        static string PunycodeDecode(string label, Func<int, int, string, UnicodeDecodeError> invalid)
        {
            int split = label.LastIndexOf('-');
            var points = new List<int>();
            if (split >= AcePrefix.Length)
                for (int i = AcePrefix.Length; i < split; i++)
                {
                    if (!char.IsAscii(label[i])) throw invalid(i, i + 1, "ordinal not in range(128)");
                    points.Add(label[i]);
                }
            int basic = points.Count, bias = 72, next = 128;
            long position = 0;
            int cursor = Math.Max(AcePrefix.Length, split + 1);
            try
            {
                while (cursor < label.Length)
                {
                    long start = position, weight = 1;
                    for (int k = 36; ; k += 36)
                    {
                        if (cursor >= label.Length) throw invalid(cursor, cursor + 1, "incomplete punicode string");
                        char character = char.ToLowerInvariant(label[cursor++]);
                        int digit = character is >= 'a' and <= 'z' ? character - 'a' :
                            character is >= '0' and <= '9' ? character - '0' + 26 : -1;
                        if (digit < 0) throw invalid(cursor - 1, cursor, "Invalid extended code point");
                        position = checked(position + digit * weight);
                        int limit = Math.Clamp(k - bias, 1, 26);
                        if (digit < limit) break;
                        weight = checked(weight * (36 - limit));
                    }
                    int count = points.Count + 1;
                    bias = PunycodeBias(position - start, count, points.Count == basic);
                    next = checked(next + (int)(position / count));
                    position %= count;
                    if (next > 0x10ffff) throw invalid(AcePrefix.Length, label.Length, "Invalid character");
                    points.Insert((int)position++, next);
                }
            }
            catch (OverflowException) { throw invalid(AcePrefix.Length, label.Length, "Invalid character"); }
            return string.Concat(points.Select(IdnaCharacter));
        }

        static char PunycodeDigit(int digit) => (char)(digit < 26 ? 'a' + digit : '0' + digit - 26);
        static int PunycodeBias(long delta, int count, bool initial)
        {
            delta /= initial ? 700 : 2;
            delta += delta / count;
            int advanced = 0;
            while (delta > 455) { delta /= 35; advanced += 36; }
            return advanced + (int)(36 * delta / (delta + 38));
        }

        sealed class NameprepData
        {
            internal readonly Dictionary<int, string> Mappings = new();
            readonly int[] _starts, _ends;

            internal NameprepData()
            {
                using var packed = new MemoryStream(Convert.FromBase64String(IdnaNameprepTables));
                using var gzip = new GZipStream(packed, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip);
                int maps = reader.ReadInt32(), ranges = reader.ReadInt32();
                for (int i = 0; i < maps; i++)
                {
                    int point = reader.ReadInt32();
                    Mappings.Add(point, Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16())));
                }
                _starts = new int[ranges];
                _ends = new int[ranges];
                for (int i = 0; i < ranges; i++)
                {
                    _starts[i] = reader.ReadInt32();
                    _ends[i] = reader.ReadInt32();
                }
            }

            internal bool Prohibited(int point)
            {
                int range = Array.BinarySearch(_starts, point);
                return range >= 0 || ~range > 0 && point <= _ends[~range - 1];
            }
        }
    }
}
