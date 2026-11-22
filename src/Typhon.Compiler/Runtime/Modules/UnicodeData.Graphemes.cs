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
    using Typhon.Runtime;

    internal static partial class unicodedata
    {
        enum GraphemeBreak : byte
        {
            Other, CR, LF, Control, Extend, ZWJ, Regional_Indicator, Prepend, SpacingMark, L, V, T, LV, LVT,
        }

        enum IndicBreak : byte { None, Consonant, Extend, Linker }

        readonly record struct GraphemeProperties(byte Bits)
        {
            public GraphemeBreak Break => (GraphemeBreak)(Bits & 15);
            public IndicBreak Indic => (IndicBreak)((Bits >> 4) & 3);
            public bool Pictographic => (Bits & 64) != 0;
        }

        static readonly Lazy<(int[] Starts, byte[] Properties)> GraphemeRanges = new(() =>
        {
            using var buffer = new MemoryStream(Convert.FromBase64String(GraphemeData), writable: false);
            using var stream = new GZipStream(buffer, CompressionMode.Decompress);
            using var reader = new BinaryReader(stream);
            var starts = new int[reader.ReadInt32()];
            var properties = new byte[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                starts[i] = reader.ReadInt32();
                properties[i] = reader.ReadByte();
            }
            return (starts, properties);
        });

        static GraphemeProperties GraphemeOf(int codepoint)
        {
            var (starts, properties) = GraphemeRanges.Value;
            int index = Array.BinarySearch(starts, codepoint);
            return new GraphemeProperties(properties[index >= 0 ? index : ~index - 1]);
        }

        public static string grapheme_cluster_break(string? character) => GraphemeOf(UCD.Codepoint(character)).Break.ToString();
        public static string indic_conjunct_break(string? character) => GraphemeOf(UCD.Codepoint(character)).Indic.ToString();
        public static bool extended_pictographic(string? character) => GraphemeOf(UCD.Codepoint(character)).Pictographic;

        internal sealed class Segment(string text, int firstOffset, int endOffset, long firstIndex, long endIndex) : IPyRepr
        {
            public long start => firstIndex;
            public long end => endIndex;
            public string __repr__() => $"<Segment {start}:{end}>";
            public override string ToString() => text[firstOffset..endOffset];
        }

        public static PyIterator<Segment> iter_graphemes(string? unistr)
        {
            if (unistr == null) throw new InvalidCastException("iter_graphemes() argument must be str, not None");
            return new PyIterator<Segment>(Graphemes(unistr).GetEnumerator());
        }

        static IEnumerable<Segment> Graphemes(string text)
        {
            var previous = GraphemeBreak.Other;
            int firstOffset = 0, firstIndex = 0, index = 0, run = 0;
            bool emojiBeforeJoiner = false, emojiWithExtenders = false;
            bool indicConsonant = false, indicLinker = false;
            for (int offset = 0; offset < text.Length; index++)
            {
                bool pair = char.IsSurrogatePair(text, offset);
                var current = GraphemeOf(pair ? char.ConvertToUtf32(text, offset) : text[offset]);
                if (index != 0 && HasBoundary(previous, current, run, emojiBeforeJoiner, indicLinker))
                {
                    yield return new Segment(text, firstOffset, offset, firstIndex, index);
                    firstOffset = offset;
                    firstIndex = index;
                }

                run = current.Break == GraphemeBreak.Regional_Indicator ? run + 1 : 0;
                emojiBeforeJoiner = current.Break == GraphemeBreak.ZWJ && emojiWithExtenders;
                emojiWithExtenders = current.Pictographic || current.Break == GraphemeBreak.Extend && emojiWithExtenders;
                if (current.Indic == IndicBreak.Linker)
                    indicLinker = indicConsonant;
                else if (current.Indic != IndicBreak.Extend)
                {
                    indicConsonant = current.Indic == IndicBreak.Consonant;
                    indicLinker = false;
                }
                previous = current.Break;
                offset += pair ? 2 : 1;
            }
            if (index != 0) yield return new Segment(text, firstOffset, text.Length, firstIndex, index);
        }

        // Extended grapheme cluster boundaries, Unicode Standard Annex #29.
        static bool HasBoundary(GraphemeBreak previous, GraphemeProperties next, int regionalRun, bool emojiBeforeJoiner, bool indicLinker)
        {
            GraphemeBreak current = next.Break;
            if (previous == GraphemeBreak.CR && current == GraphemeBreak.LF) return false;
            if (IsControl(previous) || IsControl(current)) return true;
            if (previous == GraphemeBreak.L && current is GraphemeBreak.L or GraphemeBreak.V or GraphemeBreak.LV or GraphemeBreak.LVT) return false;
            if (previous is GraphemeBreak.LV or GraphemeBreak.V && current is GraphemeBreak.V or GraphemeBreak.T) return false;
            if (previous is GraphemeBreak.LVT or GraphemeBreak.T && current == GraphemeBreak.T) return false;
            if (current is GraphemeBreak.Extend or GraphemeBreak.ZWJ or GraphemeBreak.SpacingMark || previous == GraphemeBreak.Prepend) return false;
            if (next.Indic == IndicBreak.Consonant && indicLinker) return false;
            if (next.Pictographic && emojiBeforeJoiner) return false;
            return current != GraphemeBreak.Regional_Indicator || (regionalRun & 1) == 0;
        }

        static bool IsControl(GraphemeBreak value) => value is GraphemeBreak.CR or GraphemeBreak.LF or GraphemeBreak.Control;
    }
}
