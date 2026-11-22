// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    // The match statement (PEP 634): patterns.
    public sealed partial class Parser
    {
        /// <summary>'match' is a soft keyword: a statement when the logical line ends with ':' and an indented 'case' follows.</summary>
        bool StartsMatchStatement()
        {
            if (Peek().Kind == TokenKind.Newline || (Peek().Kind == TokenKind.Op && (Peek().Text == "=" || Peek().Text == "." || Peek().Text.EndsWith("=") && Peek().Text != "==")))
                return false;
            int j = _i + 1;
            while (j < _t.Count && _t[j].Kind != TokenKind.Newline && _t[j].Kind != TokenKind.EndOfFile) j++;
            if (j <= _i + 2 || j + 1 >= _t.Count || !_t[j - 1].Is(":")) return false;
            if (_t[j + 1].Kind == TokenKind.EndOfFile) return true;
            return j + 2 < _t.Count && _t[j + 1].Kind == TokenKind.Indent && _t[j + 2].Kind == TokenKind.Name && _t[j + 2].Text == "case";
        }

        MatchStmt ParseMatch()
        {
            var tok = Cur;
            _i++; // 'match'
            var m = At(new MatchStmt { Subject = ParseTestListOrStar() }, tok);
            ExpectOp(":");
            if (Cur.Kind != TokenKind.Newline) throw Err("expected a new line after 'match ...:'");
            _i++;
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block of 'case' clauses", needsMoreInput: AtEndOfInput, isIndentationError: true);
            _i++;
            while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
            {
                if (Cur.Kind == TokenKind.Newline) { _i++; continue; }
                if (!IsKw("case")) throw Err("expected 'case' but found " + Describe(Cur));
                var ctok = Cur;
                _i++;
                var c = At(new MatchCase { Pattern = ParseOpenSequencePattern() }, ctok);
                if (AcceptKw("if")) c.Guard = ParseNamedExprTest();
                c.Body = ParseBlock();
                m.Cases.Add(c);
            }
            if (Cur.Kind == TokenKind.Dedent) _i++;
            if (m.Cases.Count == 0) throw Err("a 'match' statement needs at least one 'case'");
            return m;
        }

        /// <summary>A '*name' item of a sequence pattern while it is being parsed.</summary>
        sealed class StarPattern : Pattern { public string Name; }

        /// <summary>case a, b, *rest: (a sequence pattern without brackets) or a single pattern.</summary>
        Pattern ParseOpenSequencePattern()
        {
            var tok = Cur;
            var first = ParseMaybeStarPattern();
            if (!IsOp(","))
            {
                if (first is StarPattern) throw Err("a star pattern must be inside a sequence pattern");
                return first;
            }
            var seq = At(new SequencePattern(), tok);
            AddSequenceItem(seq, first);
            while (AcceptOp(","))
            {
                if (IsOp(":") || IsKw("if")) break;
                AddSequenceItem(seq, ParseMaybeStarPattern());
            }
            return seq;
        }

        void AddSequenceItem(SequencePattern seq, Pattern p)
        {
            if (p is StarPattern star)
            {
                if (seq.StarIndex >= 0) throw new SyntaxError("multiple starred names in a sequence pattern", p.Line, p.Col);
                seq.StarIndex = seq.Items.Count;
                seq.StarName = star.Name;
            }
            else seq.Items.Add(p);
        }

        Pattern ParseMaybeStarPattern()
        {
            var tok = Cur;
            if (AcceptOp("*"))
            {
                string name = ExpectName();
                return At(new StarPattern { Name = name == "_" ? null : name }, tok);
            }
            return ParsePattern();
        }

        Pattern ParsePattern()
        {
            var tok = Cur;
            var p = ParseOrPattern();
            if (AcceptKw("as")) return At(new AsPattern { Inner = p, Name = ExpectName() }, tok);
            return p;
        }

        Pattern ParseOrPattern()
        {
            var tok = Cur;
            var first = ParseClosedPattern();
            if (!IsOp("|")) return first;
            var or = At(new OrPattern(), tok);
            or.Alternatives.Add(first);
            while (AcceptOp("|")) or.Alternatives.Add(ParseClosedPattern());
            return or;
        }

        Pattern ParseClosedPattern()
        {
            var tok = Cur;
            if (Cur.Kind == TokenKind.Number || Cur.Kind == TokenKind.String || IsKw("None") || IsKw("True") || IsKw("False"))
                return At(new ValuePattern { Value = ParseAtom() }, tok);
            if (IsOp("-") && Peek().Kind == TokenKind.Number)
            {
                _i++;
                return At(new ValuePattern { Value = At(new UnaryExpr { Op = "-", Operand = ParseAtom() }, tok) }, tok);
            }
            if (AcceptOp("("))
            {
                if (AcceptOp(")")) return At(new SequencePattern(), tok);
                var first = ParseMaybeStarPattern();
                if (AcceptOp(")"))
                {
                    if (first is StarPattern) { var one = At(new SequencePattern(), tok); AddSequenceItem(one, first); return one; }
                    return first; // group
                }
                var seq = At(new SequencePattern(), tok);
                AddSequenceItem(seq, first);
                while (AcceptOp(","))
                {
                    if (IsOp(")")) break;
                    AddSequenceItem(seq, ParseMaybeStarPattern());
                }
                ExpectOp(")");
                return seq;
            }
            if (AcceptOp("["))
            {
                var seq = At(new SequencePattern(), tok);
                while (!IsOp("]"))
                {
                    AddSequenceItem(seq, ParseMaybeStarPattern());
                    if (!AcceptOp(",")) break;
                }
                ExpectOp("]");
                return seq;
            }
            if (AcceptOp("{"))
            {
                var map = At(new MappingPattern(), tok);
                while (!IsOp("}"))
                {
                    if (AcceptOp("**")) { map.Rest = ExpectName(); AcceptOp(","); break; }
                    var keyTok = Cur;
                    Expr key = Cur.Kind == TokenKind.Name && !IsKw("None") && !IsKw("True") && !IsKw("False") ? ParseDottedValue() : ((ValuePattern)ParseClosedPattern()).Value;
                    ExpectOp(":");
                    map.Keys.Add(key);
                    map.Values.Add(ParsePattern());
                    if (!AcceptOp(",")) break;
                }
                ExpectOp("}");
                return map;
            }
            if (Cur.Kind == TokenKind.Name && !Keywords.Contains(Cur.Text))
            {
                var value = ParseDottedValue();
                if (AcceptOp("("))
                {
                    var cls = At(new ClassPattern { Cls = value }, tok);
                    while (!IsOp(")"))
                    {
                        if (Cur.Kind == TokenKind.Name && Peek().Kind == TokenKind.Op && Peek().Text == "=")
                        {
                            cls.KeywordNames.Add(Cur.Text);
                            _i += 2;
                            cls.KeywordPatterns.Add(ParsePattern());
                        }
                        else
                        {
                            if (cls.KeywordNames.Count > 0) throw Err("positional patterns follow keyword patterns");
                            cls.Positional.Add(ParsePattern());
                        }
                        if (!AcceptOp(",")) break;
                    }
                    ExpectOp(")");
                    return cls;
                }
                if (value is NameExpr n) return At(new CapturePattern { Name = n.Id == "_" ? null : n.Id }, tok);
                return At(new ValuePattern { Value = value }, tok); // a dotted name is a value (Color.RED)
            }
            throw Err("unsupported pattern starting with " + Describe(Cur));
        }

        Expr ParseDottedValue()
        {
            var tok = Cur;
            Expr e = At(new NameExpr { Id = ExpectName() }, tok);
            while (IsOp(".") && Peek().Kind == TokenKind.Name) { _i++; e = At(new AttributeExpr { Target = e, Name = ExpectNameOrKeyword() }, tok); }
            return e;
        }

        ComprehensionExpr ParseComprehensionTail(ComprehensionKind kind, Expr elt, Expr valueElt, Token tok)
        {
            var c = At(new ComprehensionExpr { Kind = kind, Elt = elt, ValueElt = valueElt }, tok);
            while (IsKw("for") || IsKw("async"))
            {
                var g = new CompFor();
                if (AcceptKw("async")) g.IsAsync = true;
                ExpectKw("for");
                g.Target = ParseTargetList();
                ExpectKw("in");
                g.Iter = ParseOrTest();
                while (IsKw("if"))
                {
                    _i++;
                    g.Ifs.Add(ParseOrTest());
                }
                c.Generators.Add(g);
            }
            return c;
        }

        Expr ParseStrings()
        {
            var tok = Cur;
            bool anyF = false, anyBytes = false, anyT = false;
            var pieces = new List<Token>();
            while (Cur.Kind == TokenKind.String)
            {
                if (Cur.Prefix.IndexOf('f') >= 0) anyF = true;
                if (Cur.Prefix.IndexOf('t') >= 0) anyT = true;
                if (Cur.Prefix.IndexOf('b') >= 0) anyBytes = true;
                pieces.Add(Cur);
                _i++;
            }
            if (anyBytes)
            {
                // b"..." (adjacent bytes literals concatenate; mixing them with str literals is an error, as in Python)
                if (pieces.Exists(x => x.Prefix.IndexOf('b') < 0)) throw new SyntaxError("cannot mix bytes and nonbytes literals", tok.Line, tok.Col);
                var bytesText = new StringBuilder();
                foreach (var p in pieces) bytesText.Append(Lexer.Decode(p.Text, p.Prefix.IndexOf('r') >= 0));
                foreach (char ch in bytesText.ToString())
                    if (ch > 0xff) throw new SyntaxError("bytes can only contain ASCII literal characters", tok.Line, tok.Col);
                return At(new ConstExpr { Kind = ConstKind.Bytes, Str = bytesText.ToString() }, tok);
            }
            // t"...": a template (PEP 750); adjacent t-strings make one template (a str literal may not join them)
            if (anyT)
            {
                if (pieces.Exists(x => x.Prefix.IndexOf('t') < 0)) throw new SyntaxError("cannot mix t-string literals with string or bytes literals", tok.Line, tok.Col);
                var t = At(new FStringExpr { IsTemplate = true }, tok);
                foreach (var p in pieces)
                {
                    bool raw = p.Prefix.IndexOf('r') >= 0;
                    ParseFStringBody(p, raw, t.Parts);
                }
                return t;
            }
            if (!anyF)
            {
                var sb = new StringBuilder();
                foreach (var p in pieces) sb.Append(Lexer.Decode(p.Text, p.Prefix.IndexOf('r') >= 0));
                return At(new ConstExpr { Kind = ConstKind.Str, Str = sb.ToString() }, tok);
            }
            var f = At(new FStringExpr(), tok);
            foreach (var p in pieces)
            {
                bool raw = p.Prefix.IndexOf('r') >= 0;
                if (p.Prefix.IndexOf('f') < 0) { f.Parts.Add(new FStringPart { Literal = Lexer.Decode(p.Text, raw) }); continue; }
                ParseFStringBody(p, raw, f.Parts);
            }
            return f;
        }

        static void ParseFStringBody(Token tok, bool raw, List<FStringPart> parts)
        {
            string s = tok.Text;
            var lit = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '{' && i + 1 < s.Length && s[i + 1] == '{') { lit.Append('{'); i += 2; continue; }
                if (c == '}' && i + 1 < s.Length && s[i + 1] == '}') { lit.Append('}'); i += 2; continue; }
                if (c != '{') { lit.Append(c); i++; continue; }

                if (lit.Length > 0) { parts.Add(new FStringPart { Literal = Lexer.Decode(lit.ToString(), raw) }); lit.Clear(); }
                // find the end of the hole, tracking nesting and quotes
                int depth = 0; int j = i + 1; char quote = '\0';
                int exprEnd = -1, convPos = -1, specPos = -1;
                for (; j < s.Length; j++)
                {
                    char d = s[j];
                    if (quote != '\0') { if (d == quote) quote = '\0'; continue; }
                    if (d == '\'' || d == '"') { quote = d; continue; }
                    if (d == '(' || d == '[' || d == '{') { depth++; continue; }
                    if ((d == ')' || d == ']' || d == '}') && depth > 0) { depth--; continue; }
                    if (depth == 0)
                    {
                        if (d == '}') break;
                        if (d == '!' && specPos < 0 && convPos < 0 && j + 1 < s.Length && s[j + 1] != '=') { convPos = j; if (exprEnd < 0) exprEnd = j; }
                        else if (d == ':' && specPos < 0) { specPos = j; if (exprEnd < 0) exprEnd = j; }
                    }
                }
                if (j >= s.Length) throw new SyntaxError("f-string: expecting '}'", tok.Line, tok.Col);
                if (exprEnd < 0) exprEnd = j;
                string exprText = s.Substring(i + 1, exprEnd - i - 1);
                var part = new FStringPart();
                string trimmed = exprText.TrimEnd();
                bool selfDocumenting = trimmed.EndsWith("=") && !(trimmed.Length >= 2 && "=!<>".IndexOf(trimmed[trimmed.Length - 2]) >= 0);
                if (selfDocumenting)
                {
                    // f"{expr = }" prints the expression text as written, then the value: repr() unless a conversion or spec is given
                    parts.Add(new FStringPart { Literal = exprText });
                    exprText = trimmed.Substring(0, trimmed.Length - 1);
                }
                part.Expr = ParseExpression(exprText, tok.Line, tok.Col);
                part.ExprText = exprText.Trim();
                if (convPos >= 0) part.Conversion = s[convPos + 1];
                if (specPos >= 0) part.FormatSpec = s.Substring(specPos + 1, j - specPos - 1);
                // f"{x:>{width}}": the spec's own replacement fields, evaluated when the string is built
                if (part.FormatSpec != null && part.FormatSpec.Contains("{"))
                {
                    part.SpecParts = new List<FStringPart>();
                    ParseFStringBody(new Token { Kind = TokenKind.String, Text = part.FormatSpec, Prefix = "f", Line = tok.Line, Col = tok.Col }, raw, part.SpecParts);
                }
                if (selfDocumenting && convPos < 0 && specPos < 0) part.Conversion = 'r';
                parts.Add(part);
                i = j + 1;
            }
            if (lit.Length > 0) parts.Add(new FStringPart { Literal = Lexer.Decode(lit.ToString(), raw) });
        }
    }
}
