// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;

    // Keep only surrogate-pair positions to translate Python character offsets to UTF-16 offsets.
    internal readonly struct StringOffsets
    {
        readonly List<int>? _pairs;
        public int Length { get; }

        public StringOffsets(string text)
        {
            List<int>? pairs = null;
            for (int i = 0; i < text.Length;)
            {
                int next = text.AsSpan(i).IndexOfAnyInRange('\uD800', '\uDBFF');
                if (next < 0) break;
                i += next;
                if (char.IsSurrogatePair(text, i)) { (pairs ??= new List<int>()).Add(i); i++; }
                i++;
            }
            _pairs = pairs;
            Length = text.Length - (pairs?.Count ?? 0);
        }

        public int ToCodeUnit(int index) => index + CountPairsBefore(index, codePoints: true);
        public int ToCodePoint(int index) => index - CountPairsBefore(index, codePoints: false);
        public bool IsBoundary(int index) => _pairs == null || _pairs.BinarySearch(index - 1) < 0;

        public static int At(string text, int offset) =>
            char.IsSurrogatePair(text, offset) ? char.ConvertToUtf32(text, offset) : text[offset];

        public static int Compare(string? left, string? right)
        {
            if (left == null || right == null) return string.CompareOrdinal(left, right);
            int offset = left.AsSpan().CommonPrefixLength(right.AsSpan());
            // A shared high surrogate may start a pair on either side of this boundary.
            if (offset > 0 && (char.IsSurrogatePair(left, offset - 1) || char.IsSurrogatePair(right, offset - 1)))
                offset--;
            if (offset == left.Length || offset == right.Length)
                return left.Length.CompareTo(right.Length);
            return At(left, offset).CompareTo(At(right, offset));
        }

        int CountPairsBefore(int index, bool codePoints)
        {
            if (_pairs == null) return 0;
            int start = 0, end = _pairs.Count;
            while (start < end)
            {
                int middle = start + (end - start) / 2;
                int pair = _pairs[middle] - (codePoints ? middle : 0);
                if (pair < index) start = middle + 1;
                else end = middle;
            }
            return start;
        }
    }
}
