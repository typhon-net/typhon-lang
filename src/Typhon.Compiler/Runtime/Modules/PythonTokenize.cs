// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using Typhon.Runtime;
    using Typhon.Shared;

    internal static partial class tokenize
    {
        public const long ENDMARKER = 0, NAME = 1, NUMBER = 2, STRING = 3, NEWLINE = 4, INDENT = 5, DEDENT = 6,
            LPAR = 7, RPAR = 8, LSQB = 9, RSQB = 10, COLON = 11, COMMA = 12, SEMI = 13, PLUS = 14, MINUS = 15,
            STAR = 16, SLASH = 17, VBAR = 18, AMPER = 19, LESS = 20, GREATER = 21, EQUAL = 22, DOT = 23,
            PERCENT = 24, LBRACE = 25, RBRACE = 26, EQEQUAL = 27, NOTEQUAL = 28, LESSEQUAL = 29, GREATEREQUAL = 30,
            TILDE = 31, CIRCUMFLEX = 32, LEFTSHIFT = 33, RIGHTSHIFT = 34, DOUBLESTAR = 35, PLUSEQUAL = 36,
            MINEQUAL = 37, STAREQUAL = 38, SLASHEQUAL = 39, PERCENTEQUAL = 40, AMPEREQUAL = 41, VBAREQUAL = 42,
            CIRCUMFLEXEQUAL = 43, LEFTSHIFTEQUAL = 44, RIGHTSHIFTEQUAL = 45, DOUBLESTAREQUAL = 46,
            DOUBLESLASH = 47, DOUBLESLASHEQUAL = 48, AT = 49, ATEQUAL = 50, RARROW = 51, ELLIPSIS = 52,
            COLONEQUAL = 53, EXCLAMATION = 54, OP = 55, TYPE_IGNORE = 56, TYPE_COMMENT = 57, SOFT_KEYWORD = 58,
            FSTRING_START = 59, FSTRING_MIDDLE = 60, FSTRING_END = 61, TSTRING_START = 62, TSTRING_MIDDLE = 63,
            TSTRING_END = 64, COMMENT = 65, NL = 66, ERRORTOKEN = 67, ENCODING = 68, N_TOKENS = 69, NT_OFFSET = 256;

        static readonly string[] TokenNames = (
            "ENDMARKER NAME NUMBER STRING NEWLINE INDENT DEDENT LPAR RPAR LSQB RSQB COLON COMMA SEMI PLUS MINUS STAR SLASH " +
            "VBAR AMPER LESS GREATER EQUAL DOT PERCENT LBRACE RBRACE EQEQUAL NOTEQUAL LESSEQUAL GREATEREQUAL TILDE " +
            "CIRCUMFLEX LEFTSHIFT RIGHTSHIFT DOUBLESTAR PLUSEQUAL MINEQUAL STAREQUAL SLASHEQUAL PERCENTEQUAL AMPEREQUAL " +
            "VBAREQUAL CIRCUMFLEXEQUAL LEFTSHIFTEQUAL RIGHTSHIFTEQUAL DOUBLESTAREQUAL DOUBLESLASH DOUBLESLASHEQUAL AT " +
            "ATEQUAL RARROW ELLIPSIS COLONEQUAL EXCLAMATION OP TYPE_IGNORE TYPE_COMMENT SOFT_KEYWORD FSTRING_START " +
            "FSTRING_MIDDLE FSTRING_END TSTRING_START TSTRING_MIDDLE TSTRING_END COMMENT NL ERRORTOKEN ENCODING N_TOKENS").Split(' ');

        public static readonly OrderedDictionary<long, string> tok_name = MakeTokenNames();
        public static readonly OrderedDictionary<string, long> EXACT_TOKEN_TYPES = MakeOperatorTypes();

        static OrderedDictionary<long, string> MakeTokenNames()
        {
            var result = new OrderedDictionary<long, string>();
            for (int i = 0; i < TokenNames.Length; i++) result[i] = TokenNames[i];
            result[NT_OFFSET] = "NT_OFFSET";
            return result;
        }

        static OrderedDictionary<string, long> MakeOperatorTypes()
        {
            string[] ascending = "( ) [ ] : , ; + - * / | & < > = . % { } == != <= >= ~ ^ << >> ** += -= *= /= %= &= |= ^= <<= >>= **= // //= @ @= -> ... := !".Split(' ');
            var result = new OrderedDictionary<string, long>();
            foreach (var pair in ascending.Select((text, index) => (text, index)).OrderBy(x => x.text, StringComparer.Ordinal))
                result[pair.text] = pair.index + LPAR;
            return result;
        }

        public static bool ISTERMINAL(long type) => type < NT_OFFSET;
        public static bool ISNONTERMINAL(long type) => type >= NT_OFFSET;
        public static bool ISEOF(long type) => type == ENDMARKER;

        public sealed class TokenError : PyExceptionBase
        {
            public TokenError(string message, (long, long) location) : base(message, location) { }
        }

        /// <summary>The same five members as CPython's TokenInfo named tuple.</summary>
        internal sealed class TokenInfo(long type, string @string, (long, long) start, (long, long) end, string line) :
            IReadOnlyList<object>, ITuple, IPyTuple, IPyRepr
        {
            public readonly long type = type;
            public readonly string @string = @string;
            public readonly PyTuple<long> start = new([start.Item1, start.Item2]);
            public readonly PyTuple<long> end = new([end.Item1, end.Item2]);
            public readonly string line = line;
            public long exact_type => type == OP && EXACT_TOKEN_TYPES.TryGetValue(@string, out long exact) ? exact : type;
            public int Count => 5;
            int ITuple.Length => Count;
            public dynamic this[long index] => this[Builtins.__pos(index, Count)];
            public object this[int index] => index switch
            {
                0 => type, 1 => @string, 2 => start, 3 => end, 4 => line,
                _ => throw new IndexOutOfRangeException("tuple index out of range"),
            };

            public void Deconstruct(out long type, out string @string, out PyTuple<long> start, out PyTuple<long> end, out string line) =>
                (type, @string, start, end, line) = (this.type, this.@string, this.start, this.end, this.line);
            public IEnumerator<object> GetEnumerator()
            {
                for (int i = 0; i < Count; i++) yield return this[i];
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public string __repr__() => $"TokenInfo(type={type} ({tok_name.get(type, "UNKNOWN")}), string={Builtins.repr(@string)}, " +
                $"start={Builtins.repr(start)}, end={Builtins.repr(end)}, line={Builtins.repr(line)})";
            public override string ToString() => __repr__();
        }

        public static PyIterator<TokenInfo> generate_tokens(object readline) => new(new SourceTokenizer(readline).Scan().GetEnumerator());

        readonly record struct Cursor(int Row, int Column);

        /// <summary>Physical source tokens with trivia and original positions, for source tools.</summary>
        sealed class SourceTokenizer(object readline)
        {
            static readonly Regex Number = new(
                @"\G(?:0[xX]_?[0-9a-fA-F](?:_?[0-9a-fA-F])*|0[oO]_?[0-7](?:_?[0-7])*|0[bB]_?[01](?:_?[01])*|(?:[0-9](?:_?[0-9])*(?:\.(?:[0-9](?:_?[0-9])*)?)?|\.[0-9](?:_?[0-9])*)(?:[eE][+-]?[0-9](?:_?[0-9])*)?[jJ]?)",
                RegexOptions.CultureInvariant);
            static readonly HashSet<string> StringPrefixes = new(StringComparer.OrdinalIgnoreCase)
                { "r", "u", "b", "f", "t", "br", "rb", "fr", "rf", "tr", "rt" };

            readonly List<string> _lines = new();
            readonly List<int[]?> _columns = new();
            readonly Stack<(int Width, int Alternate)> _indents = new(new[] { (0, 0) });
            int _row, _column, _grouping;
            bool _eof, _logicalCode, _continued;

            Cursor Position => new(_row, _column);
            string Current => _lines[_row];
            int ContentLength => Current.Length - LineEnding.Length;
            string LineEnding => Current.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" :
                Current.EndsWith('\n') ? "\n" : Current.EndsWith('\r') ? "\r" : "";
            char Peek(int ahead = 0) => _column + ahead < ContentLength ? Current[_column + ahead] : '\0';
            bool HasLine()
            {
                if (_row < _lines.Count) return true;
                if (_eof) return false;
                object? line;
                try { line = Builtins.__invoke(readline, []); }
                catch (StopIteration) { line = ""; }
                if (line is not string text) throw new InvalidCastException("readline() must return a string");
                if (text.Length == 0) { _eof = true; return false; }
                _lines.Add(text);
                _columns.Add(CodePointColumns(text));
                return true;
            }

            static int[]? CodePointColumns(string text)
            {
                if (!text.Any(char.IsHighSurrogate)) return null;
                var offsets = new int[text.Length + 1];
                for (int i = 0; i < text.Length; i++)
                    offsets[i + 1] = offsets[i] + (i > 0 && char.IsSurrogatePair(text, i - 1) ? 0 : 1);
                return offsets;
            }

            int ColumnAt(Cursor cursor)
            {
                if (cursor.Row >= _columns.Count || _columns[cursor.Row] is not int[] offsets) return cursor.Column;
                int withinLine = Math.Min(cursor.Column, offsets.Length - 1);
                return offsets[withinLine] + cursor.Column - withinLine;
            }

            (long, long) Location(Cursor cursor) => ((long)cursor.Row + 1, ColumnAt(cursor));

            string Slice(Cursor start, Cursor end)
            {
                if (start.Row == end.Row) return _lines[start.Row][start.Column..end.Column];
                return _lines[start.Row][start.Column..] + string.Concat(_lines.Skip(start.Row + 1).Take(end.Row - start.Row - 1)) + _lines[end.Row][..end.Column];
            }

            TokenInfo Make(long type, Cursor start, Cursor end, string? content = null) =>
                new(type, content ?? Slice(start, end), Location(start), Location(end),
                    start.Row >= _lines.Count ? "" : start.Row == end.Row ? _lines[start.Row] :
                        string.Concat(_lines.Skip(start.Row).Take(end.Row - start.Row + 1)));

            TokenError Error(string message, Cursor position, bool oneBased = true)
            {
                var (row, column) = Location(position);
                return new TokenError(message, (row, column + (oneBased ? 1 : 0)));
            }

            IEnumerable<TokenInfo> Indentation()
            {
                int width = 0, alternate = 0;
                while (Peek() is ' ' or '\t' or '\f')
                {
                    char character = Current[_column++];
                    if (character == '\f') { width = alternate = 0; continue; }
                    width = character == '\t' ? (width / 8 + 1) * 8 : width + 1;
                    alternate++;
                }
                if (_column >= ContentLength || Peek() == '#') yield break;
                if (Peek() == '\\' && _column + 1 == ContentLength) yield break;

                var previous = _indents.Peek();
                var details = ("<string>", (long)_row + 1, (long)ColumnAt(new Cursor(_row, ContentLength)) + 1,
                    Current[..ContentLength], (object?)null, (object?)null);
                const string mixed = "inconsistent use of tabs and spaces in indentation";
                if (width > previous.Width)
                {
                    if (alternate <= previous.Alternate) throw new TabError(mixed, details);
                    if (_indents.Count == 100) throw new IndentationError("too many levels of indentation", details);
                    _indents.Push((width, alternate));
                    yield return Make(INDENT, new Cursor(_row, 0), Position);
                    yield break;
                }
                int dedents = 0;
                while (width < _indents.Peek().Width) { _indents.Pop(); dedents++; }
                if (width != _indents.Peek().Width) throw new IndentationError("unindent does not match any outer indentation level", details);
                if (alternate != _indents.Peek().Alternate) throw new TabError(mixed, details);
                while (dedents-- > 0) yield return Make(DEDENT, Position, Position, "");
            }

            public IEnumerable<TokenInfo> Scan()
            {
                while (HasLine())
                {
                    if (_grouping == 0 && !_continued)
                        foreach (var indentation in Indentation()) yield return indentation;
                    _continued = false;
                    while (_column < ContentLength)
                    {
                        char character = Peek();
                        if (character is ' ' or '\t' or '\f') { _column++; continue; }
                        if (character == '#') { yield return ReadComment(); break; }
                        if (character == '\\' && _column + 1 == ContentLength && LineEnding.Length > 0)
                        {
                            _continued = true;
                            break;
                        }
                        if (character == '\0') throw Error("source code cannot contain null bytes", Position);
                        _logicalCode = true;
                        if (character is '(' or '[' or '{') _grouping++;
                        else if (character is ')' or ']' or '}') _grouping = Math.Max(_grouping - 1, 0);
                        foreach (var token in Atom()) yield return token;
                    }
                    if (!_continued)
                    {
                        long kind = _grouping == 0 && _logicalCode ? NEWLINE : NL;
                        yield return Newline(kind);
                        if (kind == NEWLINE) _logicalCode = false;
                    }
                    AdvanceLine();
                }
                if (_continued || _grouping != 0)
                    throw Error("unexpected EOF in multi-line statement", new Cursor(Math.Max(0, _row - 1),
                        _continued ? _lines[^1].TrimEnd('\r', '\n').Length + 1 : 0), oneBased: false);
                while (_indents.Count > 1) { _indents.Pop(); yield return Make(DEDENT, Position, Position, ""); }
                yield return Make(ENDMARKER, Position, Position, "");
            }

            void AdvanceLine() { _row++; _column = 0; }
            TokenInfo Newline(long kind) => Make(kind, new Cursor(_row, ContentLength),
                new Cursor(_row, ContentLength + Math.Max(LineEnding.Length, 1)), LineEnding);

            TokenInfo ReadComment()
            {
                Cursor start = Position;
                _column = ContentLength;
                return Make(COMMENT, start, Position);
            }

            static bool IdentifierStart(string text, int column)
            {
                if (text[column] == '_') return true;
                return CharUnicodeInfo.GetUnicodeCategory(text, column) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                    UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
            }

            static bool IdentifierPart(string text, int column) => IdentifierStart(text, column) ||
                CharUnicodeInfo.GetUnicodeCategory(text, column) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;

            void AdvanceCharacter() => _column += char.IsSurrogatePair(Current, _column) ? 2 : 1;

            IEnumerable<TokenInfo> Atom()
            {
                Cursor start = Position;
                char character = Peek();
                if (character is '\'' or '"')
                {
                    yield return ReadString(start);
                    yield break;
                }
                if (char.IsAsciiDigit(character) || character == '.' && char.IsAsciiDigit(Peek(1)))
                {
                    var match = Number.Match(Current, _column);
                    if (character == '0' && Peek(1) is 'x' or 'X' or 'b' or 'B' or 'o' or 'O' && match.Length < 3)
                    {
                        string kind = char.ToLowerInvariant(Peek(1)) switch { 'x' => "hexadecimal", 'b' => "binary", _ => "octal" };
                        throw Error($"invalid {kind} literal", new Cursor(_row, _column + 1));
                    }
                    _column += match.Length;
                    yield return Make(NUMBER, start, Position);
                    yield break;
                }
                if (IdentifierStart(Current, _column))
                {
                    do AdvanceCharacter(); while (_column < ContentLength && IdentifierPart(Current, _column));
                    if (Peek() is '\'' or '"' && StringPrefixes.Contains(Current[start.Column.._column]))
                    {
                        string prefix = Current[start.Column.._column].ToLowerInvariant();
                        if (prefix.Contains('f') || prefix.Contains('t'))
                            foreach (var token in Formatted(start, prefix.Contains('f') ? FSTRING_START : TSTRING_START)) yield return token;
                        else yield return ReadString(start);
                    }
                    else yield return Make(NAME, start, Position);
                    yield break;
                }
                if (PythonLexical.MatchOperator(Current, _column) is string op) _column += op.Length;
                else AdvanceCharacter();
                yield return Make(OP, start, Position);
            }

            int Quotes(char quote) => Peek(1) == quote && Peek(2) == quote ? 3 : 1;
            bool AtQuote(char quote, int quotes) => Peek() == quote && (quotes == 1 || Peek(1) == quote && Peek(2) == quote);

            TokenInfo ReadString(Cursor start)
            {
                char quote = Peek();
                int quotes = Quotes(quote);
                _column += quotes;
                while (HasLine())
                {
                    if (AtQuote(quote, quotes))
                    {
                        _column += quotes;
                        return Make(STRING, start, Position);
                    }
                    if (_column == ContentLength)
                    {
                        if (quotes == 1) throw Error($"unterminated string literal (detected at line {_row + 1})", start);
                        AdvanceLine();
                        continue;
                    }
                    if (Peek() == '\\')
                    {
                        _column++;
                        if (_column == ContentLength && LineEnding.Length > 0) { AdvanceLine(); continue; }
                        if (_column < ContentLength) { AdvanceCharacter(); continue; }
                    }
                    else AdvanceCharacter();
                }
                throw Error(quotes == 3 ? "EOF in multi-line string" : $"unterminated string literal (detected at line {_row})", start);
            }

            IEnumerable<TokenInfo> Formatted(Cursor start, long startType)
            {
                char quote = Peek();
                int quotes = Quotes(quote);
                _column += quotes;
                yield return Make(startType, start, Position);
                foreach (var middle in FormattedText(quote, quotes, startType + 1, formatSpec: false)) yield return middle;
                Cursor suffix = Position;
                _column += quotes;
                yield return Make(startType + 2, suffix, Position);
            }

            IEnumerable<TokenInfo> FormattedText(char quote, int quotes, long middleType, bool formatSpec)
            {
                Cursor start = Position;
                while (HasLine())
                {
                    char character = Peek();
                    if (formatSpec && character == '}' || !formatSpec && AtQuote(quote, quotes))
                    {
                        if (start != Position || formatSpec) yield return Make(middleType, start, Position);
                        yield break;
                    }
                    if (!formatSpec && character is '{' or '}' && Peek(1) == character)
                    {
                        _column++;
                        yield return Make(middleType, start, Position);
                        _column++;
                        start = Position;
                        continue;
                    }
                    if (character == '{')
                    {
                        if (start != Position) yield return Make(middleType, start, Position);
                        Cursor brace = Position;
                        _column++;
                        yield return Make(OP, brace, Position);
                        foreach (var token in Field(quote, quotes, middleType)) yield return token;
                        start = Position;
                        continue;
                    }
                    if (character == '}' && !formatSpec) throw Error("f-string: single '}' is not allowed", Position);
                    if (_column >= ContentLength)
                    {
                        if (quotes == 1) throw Error("unterminated string literal", start);
                        AdvanceLine();
                        continue;
                    }
                    if (character == '\\' && Peek(1) is not ('{' or '}'))
                    {
                        _column++;
                        if (_column >= ContentLength) { AdvanceLine(); continue; }
                    }
                    AdvanceCharacter();
                }
                throw Error("EOF in multi-line string", start);
            }

            IEnumerable<TokenInfo> Field(char quote, int quotes, long middleType)
            {
                int groups = 0;
                while (HasLine())
                {
                    if (_column == ContentLength) { yield return Newline(NL); AdvanceLine(); continue; }
                    char character = Peek();
                    if (character is ' ' or '\t' or '\f') { _column++; continue; }
                    if (character == '#') { yield return ReadComment(); continue; }
                    if (groups == 0 && character is ':' or '}')
                    {
                        Cursor separator = Position;
                        _column++;
                        yield return Make(OP, separator, Position);
                        if (character == '}') yield break;
                        foreach (var token in FormattedText(quote, quotes, middleType, formatSpec: true)) yield return token;
                        Cursor closing = Position;
                        _column++;
                        yield return Make(OP, closing, Position);
                        yield break;
                    }
                    if (character is '(' or '[' or '{') groups++;
                    else if (character is ')' or ']' or '}') groups = Math.Max(groups - 1, 0);
                    foreach (var token in Atom()) yield return token;
                }
                throw Error("f-string: expecting '}'", new Cursor(Math.Max(0, _row - 1), 0));
            }
        }
    }
}
