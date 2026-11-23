// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    /// <summary>Recursive-descent parser for the Typhon subset of Python 3.12 syntax.</summary>
    public sealed partial class Parser
    {
        readonly List<Token> _t;
        readonly bool _openGrouping;
        bool _barryAsFlufl;
        int _i;

        public Parser(List<Token> tokens, bool openGrouping = false, bool barryAsFlufl = false)
        { _t = tokens; _openGrouping = openGrouping; _barryAsFlufl = barryAsFlufl; }

        public static ModuleAst ParseModule(string source, string path, bool barryAsFlufl = false, bool expressionOnly = false, bool typeComments = false)
        {
            var lexer = new Lexer(source, typeComments: typeComments);
            var p = new Parser(lexer.Tokenize(), lexer.HasOpenGrouping, barryAsFlufl);
            var m = new ModuleAst { Path = path, TypeComments = lexer.TypeComments };
            if (expressionOnly)
            {
                if (p.Cur.Kind == TokenKind.EndOfFile) return m;
                var start = p.Cur;
                var expression = p.ParseTestListOrStar();
                p.ExpectNewline();
                if (p.Cur.Kind != TokenKind.EndOfFile) throw p.Err("invalid syntax");
                m.Body.Add(p.At(new ExprStmt { Value = expression }, start));
                return m;
            }
            while (p.Cur.Kind != TokenKind.EndOfFile)
            {
                if (p.Cur.Kind == TokenKind.Newline) { p._i++; continue; }
                var statements = p.ParseStatement();
                foreach (var statement in statements)
                    if (statement is ImportFromStmt import && import.Level == 0 && import.Module == "__future__" &&
                        import.Names.Exists(name => name.Name == "barry_as_FLUFL")) p._barryAsFlufl = true;
                m.Body.AddRange(statements);
            }
            return m;
        }

        /// <summary>Parses a standalone expression (used for f-string holes).</summary>
        public static Expr ParseExpression(string source, int line, int col)
        {
            var tokens = new Lexer("(" + source + ")", line - 1).Tokenize();
            var p = new Parser(tokens);
            var e = p.ParseTestListOrStar();
            return e;
        }


        Token Cur => _t[_i];
        Token Peek(int n = 1) => _t[System.Math.Min(_i + n, _t.Count - 1)];

        bool IsOp(string op) => Cur.Kind == TokenKind.Op && Cur.Text == op;
        bool IsKw(string kw) => Cur.Kind == TokenKind.Name && Cur.Text == kw;

        bool AcceptOp(string op) { if (IsOp(op)) { _i++; return true; } return false; }
        bool AcceptKw(string kw) { if (IsKw(kw)) { _i++; return true; } return false; }

        Token ExpectOp(string op)
        {
            if (!IsOp(op)) throw Err("expected '" + op + "' but found " + Describe(Cur));
            return _t[_i++];
        }

        void ExpectKw(string kw)
        {
            if (!IsKw(kw)) throw Err("expected '" + kw + "' but found " + Describe(Cur));
            _i++;
        }

        string ExpectName()
        {
            if (Cur.Kind != TokenKind.Name || Keywords.Contains(Cur.Text)) throw Err("expected a name but found " + Describe(Cur));
            return _t[_i++].Text;
        }

        void ExpectNewline()
        {
            if (Cur.Kind == TokenKind.Newline) { _i++; return; }
            if (Cur.Kind == TokenKind.EndOfFile || Cur.Kind == TokenKind.Dedent) return;
            throw Err("expected end of line but found " + Describe(Cur));
        }

        static string Describe(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.Newline: return "end of line";
                case TokenKind.Indent: return "an indented block";
                case TokenKind.Dedent: return "end of block";
                case TokenKind.EndOfFile: return "end of file";
                default: return "'" + t.Text + "'";
            }
        }

        bool AtEndOfInput => Cur.Kind == TokenKind.EndOfFile || Cur.Pos == _t[_t.Count - 1].Pos && Cur.Pos == Cur.EndPos;

        SyntaxError Err(string msg, bool needsMoreInput = false, bool isIndentationError = false) =>
            new SyntaxError(Cur.Kind == TokenKind.Indent ? "unexpected indent" : msg, Cur.Line, Cur.Col,
                AtEndOfInput, needsMoreInput || AtEndOfInput && _openGrouping, isIndentationError || Cur.Kind == TokenKind.Indent);

        T At<T>(T node, Token tok) where T : Node
        {
            node.Line = tok.Line;
            node.Col = tok.Col;
            node.StartOffset = tok.Pos;
            node.EndOffset = tok.EndPos;
            return node;
        }

        T Spanned<T>(T node) where T : Node
        {
            int last = _i - 1;
            while (last >= 0 && (_t[last].Kind == TokenKind.Newline || _t[last].Kind == TokenKind.Dedent)) last--;
            if (last >= 0) node.EndOffset = System.Math.Max(node.EndOffset, _t[last].EndPos);
            return node;
        }

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
            "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
            "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
        };
    }
}
