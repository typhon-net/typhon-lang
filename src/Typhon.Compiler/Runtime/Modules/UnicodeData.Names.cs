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

    internal static partial class unicodedata
    {
        static readonly Lazy<CharacterNames> Names = new(() => new CharacterNames(CurrentNames));
        static readonly Lazy<CharacterNames> LegacyCharacterNames = new(() => new CharacterNames(LegacyNames));

        public static string name(string chr) => Current.Value.name(chr);
        public static dynamic? name(string chr, object? @default) => Current.Value.name(chr, @default);
        internal static string? NameOf(int codepoint) => Names.Value.NameOf(codepoint);

        sealed class CharacterNames
        {
            readonly Dictionary<int, string> _names = new();
            readonly List<(int Start, int End, string Prefix)> _ranges = new();

            internal CharacterNames(string encoded)
            {
                using var source = new MemoryStream(Convert.FromBase64String(encoded));
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                while (reader.ReadLine() is string record)
                {
                    int split = record.IndexOf('\t');
                    string name = record[(split + 1)..];
                    var range = record.AsSpan(0, split);
                    int separator = range.IndexOf('-');
                    if (separator < 0)
                        _names.Add(int.Parse(range, NumberStyles.HexNumber, CultureInfo.InvariantCulture), name);
                    else
                        _ranges.Add((int.Parse(range[..separator], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            int.Parse(range[(separator + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), name));
                }
            }

            internal string? NameOf(int codepoint)
            {
                if (_names.TryGetValue(codepoint, out string? name)) return name;
                foreach (var (start, end, prefix) in _ranges)
                    if (codepoint >= start && codepoint <= end)
                        return prefix + codepoint.ToString("X4", CultureInfo.InvariantCulture);
                return null;
            }
        }
    }
}
