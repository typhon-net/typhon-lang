// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Typhon.Runtime;

    internal static class encodings
    {
        internal static class aliases_
        {
            public static readonly OrderedDictionary<string, string> aliases = EncodingAliasData.Load();
        }

        public static dynamic aliases { get; } = PyModule.Of(typeof(aliases_), "encodings.aliases");

        public static string normalize_encoding(object? encoding)
        {
            string text;
            if (encoding is string name) text = name;
            else if (encoding is PyBytes bytes)
            {
                int invalid = bytes.Data.AsSpan().IndexOfAnyInRange((byte)128, byte.MaxValue);
                if (invalid >= 0) throw new UnicodeDecodeError("ascii", bytes, invalid, invalid + 1, "ordinal not in range(128)");
                text = Encoding.ASCII.GetString(bytes.Data);
            }
            else
            {
                object? isAscii = Builtins.getattr(encoding, "isascii");
                if (!Builtins.truthy((object?)Builtins.__invoke(isAscii, []))) WarnEncoding();
                throw new InvalidCastException($"_normalize_encoding() argument must be str, not {Builtins.PyTypeName(Builtins.__type_of(encoding))}");
            }
            if (text.AsSpan().ContainsAnyInRange('\x80', char.MaxValue)) WarnEncoding();

            var normalized = new StringBuilder(text.Length);
            bool separator = false;
            foreach (char character in text)
            {
                if (char.IsAsciiLetterOrDigit(character) || character == '.')
                {
                    if (separator && normalized.Length > 0) normalized.Append('_');
                    normalized.Append(character);
                    separator = false;
                }
                else separator = true;
            }
            return normalized.ToString();
        }

        static void WarnEncoding() =>
            warnings.warn("Support for non-ascii encoding names will be removed in 3.17", typeof(DeprecationWarning), stacklevel: 2);
    }
}
