// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    /// <summary>
    /// Tokenizer for GDScript (Godot 4): indentation blocks like Python (INDENT/DEDENT, implicit line joining inside
    /// brackets, '\' continuations), GDScript's operators (&amp;&amp;, ||, !, $, @, ..) and string forms ("...", '...', """...""",
    /// r"raw", &amp;"StringName", ^"NodePath"). Produces the same <see cref="Token"/>s as the Python <see cref="Lexer"/>;
    /// a string token's <see cref="Token.Text"/> is already decoded and its <see cref="Token.Prefix"/> is "", "r", "&amp;" or "^".
    /// </summary>
    public sealed class GdScriptLexer
    {
        static readonly string[] Ops =
        {
            "**=", "<<=", ">>=",
            "**", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "->", ":=", "..",
            "+", "-", "*", "/", "%", "&", "|", "^", "~", "!", "<", ">", "(", ")", "[", "]", "{", "}", ",", ":", ".", ";", "=", "@", "$",
        };

        readonly string _src;
        int _pos;
        int _line = 1;
        int _lineStart;
        int _parenDepth;
        readonly List<int> _indents = new List<int> { 0 };
        readonly List<Token> _tokens = new List<Token>();

        public GdScriptLexer(string source)
        {
            _src = source.Replace("\r\n", "\n").Replace('\r', '\n');
            if (_src.Length > 0 && _src[0] == '﻿') _src = _src.Substring(1);
        }

        public List<Token> Tokenize()
        {
            bool atLineStart = true;
            while (true)
            {
                if (atLineStart)
                {
                    atLineStart = false;
                    if (_parenDepth == 0 && !HandleIndentation()) break;
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

                // r"raw", &"StringName", ^"NodePath"
                if ((c == 'r' || c == '&' || c == '^') && _pos + 1 < _src.Length && (_src[_pos + 1] == '"' || _src[_pos + 1] == '\''))
                {
                    int start = _pos;
                    _pos++;
                    LexString(c.ToString(), start);
                    continue;
                }
                if (IsIdentStart(c))
                {
                    int start = _pos;
                    while (_pos < _src.Length && IsIdentPart(_src[_pos])) _pos++;
                    Add(TokenKind.Name, _src.Substring(start, _pos - start), start, _pos);
                    continue;
                }
                if (c == '"' || c == '\'') { LexString("", _pos); continue; }
                if (char.IsDigit(c) || (c == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]) && !(_tokens.Count > 0 && Last.EndPos == _pos && (Last.Kind == TokenKind.Name || Last.Is(")") || Last.Is("]")))))
                { LexNumber(); continue; }

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

        bool HandleIndentation()
        {
            while (true)
            {
                int width = 0;
                int p = _pos;
                while (p < _src.Length && (_src[p] == ' ' || _src[p] == '\t' || _src[p] == '\f'))
                {
                    width = _src[p] == '\t' ? (width / 4 + 1) * 4 : width + 1;
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
            if (_src[_pos] == '0' && _pos + 1 < _src.Length && "xXbB".IndexOf(_src[_pos + 1]) >= 0)
            {
                _pos += 2;
                while (_pos < _src.Length && (Uri.IsHexDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
            }
            else
            {
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                if (_pos < _src.Length && _src[_pos] == '.' && !(_pos + 1 < _src.Length && (_src[_pos + 1] == '.' || IsIdentStart(_src[_pos + 1]) && _src[_pos + 1] != 'e' && _src[_pos + 1] != 'E')))
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
            }
            Add(TokenKind.Number, _src.Substring(start, _pos - start), start, _pos);
        }

        void LexString(string prefix, int start)
        {
            char q = _src[_pos];
            bool triple = _pos + 2 < _src.Length && _src[_pos + 1] == q && _src[_pos + 2] == q;
            bool raw = prefix == "r";
            int startLine = _line, startCol = start - _lineStart + 1;
            _pos += triple ? 3 : 1;
            int contentStart = _pos;
            while (true)
            {
                if (_pos >= _src.Length) throw new SyntaxError("unterminated string literal", startLine, startCol);
                char c = _src[_pos];
                if (c == '\\' && _pos + 1 < _src.Length)
                {
                    if (_src[_pos + 1] == '\n') { _pos += 2; NewLine(); continue; }
                    _pos += 2; continue;
                }
                if (c == '\n')
                {
                    if (!triple) throw new SyntaxError("unterminated string literal", startLine, startCol);
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
            _tokens.Add(new Token { Kind = TokenKind.String, Text = raw ? content : Decode(content), Prefix = prefix, Line = startLine, Col = startCol, Pos = start, EndPos = _pos });
        }

        /// <summary>GDScript's escape sequences: \n \t \r \a \b \f \v \" \' \\ \uXXXX \UXXXXXX, and a backslash-newline joins lines.</summary>
        static string Decode(string content)
        {
            var sb = new StringBuilder(content.Length);
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c != '\\' || i + 1 >= content.Length) { sb.Append(c); continue; }
                char n = content[++i];
                switch (n)
                {
                    case '\n': break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case 'u':
                    case 'U':
                    {
                        int len = n == 'u' ? 4 : 6;
                        int digits = 0;
                        while (digits < len && i + 1 + digits < content.Length && Uri.IsHexDigit(content[i + 1 + digits])) digits++;
                        int code = digits == 0 ? 0 : int.Parse(content.Substring(i + 1, digits), NumberStyles.HexNumber);
                        sb.Append(char.ConvertFromUtf32(code));
                        i += digits;
                        break;
                    }
                    default: sb.Append(n); break; // \" \' \\ and unknown escapes keep the character
                }
            }
            return sb.ToString();
        }

        void Add(TokenKind kind, string text, int start, int end)
        {
            _tokens.Add(new Token { Kind = kind, Text = text, Line = _line, Col = start - _lineStart + 1, Pos = start, EndPos = end });
        }

        SyntaxError Error(string msg) => new SyntaxError(msg, _line, _pos - _lineStart + 1);

        static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
