// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace Typhon.Shared
{
    // Order operators by length so a prefix cannot hide a longer token.
    internal static class PythonLexical
    {
        static readonly string[] Operators =
        {
            "**=", "//=", ">>=", "<<=", "...",
            "->", "**", "//", "<<", ">>", "<=", ">=", "==", "!=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "@=", ":=",
            "+", "-", "*", "/", "%", "@", "&", "|", "^", "~", "<", ">", "(", ")", "[", "]", "{", "}", ",", ":", ".", ";", "=",
        };

        internal static string MatchOperator(string text, int position, bool legacyNotEqual = false)
        {
            if (legacyNotEqual && StartsWithAt(text, position, "<>")) return "<>";
            foreach (string op in Operators)
                if (StartsWithAt(text, position, op)) return op;
            return null!;
        }

        internal static bool StartsWithAt(string text, int position, string value) =>
            position <= text.Length - value.Length && string.CompareOrdinal(text, position, value, 0, value.Length) == 0;
    }
}
