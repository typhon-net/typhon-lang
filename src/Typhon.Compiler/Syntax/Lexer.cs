// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    public enum TokenKind { Name, Number, String, Op, Newline, Indent, Dedent, EndOfFile }

    public sealed class Token
    {
        public TokenKind Kind;
        public string Text;      // raw text (for strings: the content between the quotes, undecoded)
        public string Prefix;    // string prefix (lower-cased), e.g. "f", "r", "rb"
        public int Line;         // 1-based
        public int Col;          // 1-based
        public int Pos;          // 0-based offset into the source
        public int EndPos;

        public bool Is(string op) => (Kind == TokenKind.Op || Kind == TokenKind.Name) && Text == op;
        public override string ToString() => Kind + " '" + Text + "' @" + Line + ":" + Col;
    }

    public sealed class SyntaxError : Exception
    {
        public int Line { get; }
        public int Col { get; }
        public SyntaxError(string message, int line, int col) : base(message) { Line = line; Col = col; }
    }

    /// <summary>Python-style tokenizer with INDENT/DEDENT and implicit line joining.</summary>
    public sealed class Lexer
    {
        static readonly string[] Ops =
        {
            "**=", "//=", ">>=", "<<=", "...",
            "->", "**", "//", "<<", ">>", "<=", ">=", "==", "!=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "@=", ":=",
            "+", "-", "*", "/", "%", "@", "&", "|", "^", "~", "<", ">", "(", ")", "[", "]", "{", "}", ",", ":", ".", ";", "=",
        };

        readonly string _src;
        readonly int _lineOffset;
        int _pos;
        int _line = 1;
        int _lineStart;
        int _parenDepth;
        readonly List<int> _indents = new List<int> { 0 };
        readonly List<Token> _tokens = new List<Token>();

        public Lexer(string source, int lineOffset = 0)
        {
            _src = source.Replace("\r\n", "\n").Replace('\r', '\n');
            _lineOffset = lineOffset;
        }

        public List<Token> Tokenize()
        {
            bool atLineStart = true;
            while (true)
            {
                if (atLineStart)
                {
                    atLineStart = false;
                    if (_parenDepth == 0 && !HandleIndentation()) break; // EOF reached
                }

                if (_pos >= _src.Length) break;
                char c = _src[_pos];

                if (c == '\n')
                {
                    if (_parenDepth == 0 && _tokens.Count > 0 && Last.Kind != TokenKind.Newline && Last.Kind != TokenKind.Indent && Last.Kind != TokenKind.Dedent)
                        Add(TokenKind.Newline, "\\n", _pos, _pos + 1);
                    _pos++; NewLine();
                    atLineStart = true;
                    continue;
                }
                if (c == ' ' || c == '\t' || c == '\f') { _pos++; continue; }
                if (c == '#') { while (_pos < _src.Length && _src[_pos] != '\n') _pos++; continue; }
                if (c == '\\' && _pos + 1 < _src.Length && _src[_pos + 1] == '\n') { _pos += 2; NewLine(); continue; }

                if (IsIdentStart(c))
                {
                    // string prefix?
                    int start = _pos;
                    int p = _pos;
                    while (p < _src.Length && p - start < 2 && "rRbBfFuUtT".IndexOf(_src[p]) >= 0) p++;
                    if (p < _src.Length && (_src[p] == '"' || _src[p] == '\'') && p > start)
                    {
                        string prefix = _src.Substring(start, p - start).ToLowerInvariant();
                        _pos = p;
                        LexString(prefix, start);
                        continue;
                    }
                    while (_pos < _src.Length && IsIdentPart(_src[_pos])) _pos++;
                    Add(TokenKind.Name, _src.Substring(start, _pos - start), start, _pos);
                    continue;
                }
                if (c == '"' || c == '\'') { LexString("", _pos); continue; }
                if (char.IsDigit(c) || (c == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]))) { LexNumber(); continue; }

                string op = MatchOp();
                if (op == null) throw Error("unexpected character '" + c + "'");
                if (op == "(" || op == "[" || op == "{") _parenDepth++;
                else if (op == ")" || op == "]" || op == "}") _parenDepth = Math.Max(0, _parenDepth - 1);
                Add(TokenKind.Op, op, _pos, _pos + op.Length);
                _pos += op.Length;
            }

            if (_tokens.Count > 0 && Last.Kind != TokenKind.Newline && Last.Kind != TokenKind.Dedent)
                Add(TokenKind.Newline, "\\n", _pos, _pos);
            while (_indents.Count > 1) { _indents.RemoveAt(_indents.Count - 1); Add(TokenKind.Dedent, "", _pos, _pos); }
            Add(TokenKind.EndOfFile, "", _pos, _pos);
            return _tokens;
        }

        Token Last => _tokens[_tokens.Count - 1];

        void NewLine() { _line++; _lineStart = _pos; }

        /// <summary>Measures indentation of the next logical line, skipping blank/comment lines. Returns false at EOF.</summary>
        bool HandleIndentation()
        {
            while (true)
            {
                int width = 0;
                int p = _pos;
                while (p < _src.Length && (_src[p] == ' ' || _src[p] == '\t' || _src[p] == '\f'))
                {
                    width = _src[p] == '\t' ? (width / 8 + 1) * 8 : width + 1;
                    p++;
                }
                if (p >= _src.Length) { _pos = p; return false; }
                if (_src[p] == '\n') { _pos = p + 1; NewLine(); continue; }
                if (_src[p] == '#') { while (p < _src.Length && _src[p] != '\n') p++; _pos = p; continue; }
                if (_src[p] == '\\' && p + 1 < _src.Length && _src[p + 1] == '\n') { _pos = p + 2; NewLine(); continue; }

                _pos = p;
                int current = _indents[_indents.Count - 1];
                if (width > current)
                {
                    _indents.Add(width);
                    Add(TokenKind.Indent, "", p, p);
                }
                else
                {
                    while (width < _indents[_indents.Count - 1])
                    {
                        _indents.RemoveAt(_indents.Count - 1);
                        Add(TokenKind.Dedent, "", p, p);
                    }
                    if (width != _indents[_indents.Count - 1]) throw Error("unindent does not match any outer indentation level");
                }
                return true;
            }
        }

        string MatchOp()
        {
            foreach (var op in Ops)
                if (string.CompareOrdinal(_src, _pos, op, 0, op.Length) == 0) return op;
            return null;
        }

        void LexNumber()
        {
            int start = _pos;
            if (_src[_pos] == '0' && _pos + 1 < _src.Length && "xXoObB".IndexOf(_src[_pos + 1]) >= 0)
            {
                _pos += 2;
                while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
            }
            else
            {
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                if (_pos < _src.Length && _src[_pos] == '.' && !(_pos + 1 < _src.Length && _src[_pos + 1] == '.'))
                {
                    _pos++;
                    while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                }
                if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
                {
                    int save = _pos;
                    _pos++;
                    if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                    if (_pos < _src.Length && char.IsDigit(_src[_pos])) { while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '_')) _pos++; }
                    else _pos = save;
                }
                if (_pos < _src.Length && (_src[_pos] == 'j' || _src[_pos] == 'J')) throw Error("complex numbers are not supported in Typhon");
            }
            Add(TokenKind.Number, _src.Substring(start, _pos - start), start, _pos);
        }

        void LexString(string prefix, int start)
        {
            char q = _src[_pos];
            bool triple = _pos + 2 < _src.Length && _src[_pos + 1] == q && _src[_pos + 2] == q;
            bool raw = prefix.IndexOf('r') >= 0;
            int startLine = _line, startCol = start - _lineStart + 1;
            _pos += triple ? 3 : 1;
            int contentStart = _pos;
            // an f-string's replacement fields may hold strings with the same quote (PEP 701, Python 3.12): f'{', '.join(xs)}'
            bool formatted = prefix.IndexOf('f') >= 0 || prefix.IndexOf('t') >= 0;
            int depth = 0;
            while (true)
            {
                if (_pos >= _src.Length) throw new SyntaxError("unterminated string literal", startLine + _lineOffset, startCol);
                char c = _src[_pos];
                if (formatted)
                {
                    if (depth == 0 && (c == '{' || c == '}'))
                    {
                        if (_pos + 1 < _src.Length && _src[_pos + 1] == c) { _pos += 2; continue; } // {{ / }}
                        if (c == '{') depth = 1;
                        _pos++; continue;
                    }
                    if (depth > 0)
                    {
                        if (c == '{') depth++;
                        else if (c == '}') depth--;
                        else if (c == '\'' || c == '"') { SkipNestedString(); continue; }
                        else if (c == '\n') { if (!triple) throw new SyntaxError("unterminated string literal", startLine + _lineOffset, startCol); _pos++; NewLine(); continue; }
                        _pos++; continue;
                    }
                }
                if (c == '\\')
                {
                    if (_pos + 1 < _src.Length && _src[_pos + 1] == '\n') { _pos += 2; NewLine(); continue; }
                    _pos += 2; continue;
                }
                if (c == '\n')
                {
                    if (!triple) throw new SyntaxError("unterminated string literal", startLine + _lineOffset, startCol);
                    _pos++; NewLine(); continue;
                }
                if (c == q)
                {
                    if (!triple) break;
                    if (_pos + 2 < _src.Length && _src[_pos + 1] == q && _src[_pos + 2] == q) break;
                }
                _pos++;
            }
            string content = _src.Substring(contentStart, _pos - contentStart);
            _pos += triple ? 3 : 1;
            var tok = new Token { Kind = TokenKind.String, Text = content, Prefix = prefix, Line = startLine + _lineOffset, Col = startCol, Pos = start, EndPos = _pos };
            _tokens.Add(tok);
            _ = raw;
        }

        /// <summary>
        /// Past a string inside an f-string's replacement field (its prefix letters were scanned as part of the expression):
        /// to its closing quote, through its own replacement fields when it is an f-string itself.
        /// </summary>
        void SkipNestedString()
        {
            int p = _pos - 1;
            while (p >= 0 && "rRbBfFuUtT".IndexOf(_src[p]) >= 0) p--;
            string prefix = _src.Substring(p + 1, _pos - p - 1).ToLowerInvariant();
            bool formatted = prefix.IndexOf('f') >= 0 || prefix.IndexOf('t') >= 0, raw = prefix.IndexOf('r') >= 0;
            char q = _src[_pos];
            bool triple = _pos + 2 < _src.Length && _src[_pos + 1] == q && _src[_pos + 2] == q;
            _pos += triple ? 3 : 1;
            int depth = 0;
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '\\' && !raw) { _pos += 2; continue; }
                if (c == '\n') { _pos++; NewLine(); continue; }
                if (formatted && depth == 0 && (c == '{' || c == '}'))
                {
                    if (_pos + 1 < _src.Length && _src[_pos + 1] == c) { _pos += 2; continue; }
                    if (c == '{') depth = 1;
                    _pos++; continue;
                }
                if (formatted && depth > 0)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    else if (c == '\'' || c == '"') { SkipNestedString(); continue; }
                    _pos++; continue;
                }
                if (c == q && (!triple || _pos + 2 < _src.Length && _src[_pos + 1] == q && _src[_pos + 2] == q)) { _pos += triple ? 3 : 1; return; }
                _pos++;
            }
        }

        void Add(TokenKind kind, string text, int start, int end)
        {
            _tokens.Add(new Token { Kind = kind, Text = text, Line = _line + _lineOffset, Col = start - _lineStart + 1, Pos = start, EndPos = end });
        }

        SyntaxError Error(string msg) => new SyntaxError(msg, _line + _lineOffset, _pos - _lineStart + 1);

        static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Decodes Python escape sequences (non-raw strings).</summary>
        public static string Decode(string content, bool raw)
        {
            if (raw) return content.Replace("\\\n", "");
            var sb = new StringBuilder(content.Length);
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c != '\\' || i + 1 >= content.Length) { sb.Append(c); continue; }
                char n = content[++i];
                switch (n)
                {
                    case '\n': break;
                    case '\\': sb.Append('\\'); break;
                    case '\'': sb.Append('\''); break;
                    case '"': sb.Append('"'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'v': sb.Append('\v'); break;
                    case 'x': sb.Append((char)Convert.ToInt32(content.Substring(i + 1, 2), 16)); i += 2; break;
                    case 'u': sb.Append((char)Convert.ToInt32(content.Substring(i + 1, 4), 16)); i += 4; break;
                    case 'U': sb.Append(char.ConvertFromUtf32(Convert.ToInt32(content.Substring(i + 1, 8), 16))); i += 8; break;
                    default:
                        if (n >= '0' && n <= '7')
                        {
                            int len = 1;
                            while (len < 3 && i + len < content.Length && content[i + len] >= '0' && content[i + len] <= '7') len++;
                            sb.Append((char)Convert.ToInt32(content.Substring(i, len), 8));
                            i += len - 1;
                        }
                        else { sb.Append('\\').Append(n); }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
