// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    // Expressions: precedence climbing from lambdas and conditionals down to atoms, f-strings and comprehensions.
    public sealed partial class Parser
    {
        /// <summary>namedexpr_test: an assignment expression `name := value`, or a test.</summary>
        Expr ParseNamedExprTest()
        {
            var tok = Cur;
            if (Cur.Kind == TokenKind.Name && !Keywords.Contains(Cur.Text) && Peek().Kind == TokenKind.Op && Peek().Text == ":=")
            {
                var target = At(new NameExpr { Id = ExpectName() }, tok);
                _i++; // ':='
                return Spanned(At(new NamedExpr { Target = target, Value = ParseTest() }, tok));
            }
            var e = ParseTest();
            if (IsOp(":=")) throw Err("the target of ':=' must be a name");
            return e;
        }

        /// <summary>star_named_expression: `*x` or a named expression (list/set displays, parenthesized tuples).</summary>
        Expr ParseNamedExprOrStar()
        {
            var tok = Cur;
            if (AcceptOp("*")) return Spanned(At(new StarredExpr { Value = ParseOrExpr() }, tok));
            return ParseNamedExprTest();
        }

        /// <summary>testlist_star_expr: comma separated tests, producing a tuple when there is a comma.</summary>
        Expr ParseTestListOrStar(bool allowYield = false)
        {
            var tok = Cur;
            if (allowYield && IsKw("yield"))
            {
                _i++;
                var y = At(new YieldExpr(), tok);
                if (AcceptKw("from")) { y.From = true; y.Value = ParseTest(); }
                else if (!AtStatementEnd() && !IsOp(")")) y.Value = ParseTestListOrStar();
                return Spanned(y);
            }
            var first = ParseTestOrStar();
            if (!IsOp(",")) return first;
            var tuple = At(new TupleExpr(), tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (!StartsExpression()) break;
                tuple.Elts.Add(ParseTestOrStar());
            }
            return Spanned(tuple);
        }

        Expr ParseTestOrStar()
        {
            var tok = Cur;
            if (AcceptOp("*")) return Spanned(At(new StarredExpr { Value = ParseOrExpr() }, tok));
            return ParseTest();
        }

        bool StartsExpression()
        {
            var c = Cur;
            if (c.Kind == TokenKind.Name) return !(c.Text == "in" || c.Text == "for" || c.Text == "if" || c.Text == "else" || c.Text == "and" || c.Text == "or" || c.Text == "as" || c.Text == "from" || c.Text == "import");
            if (c.Kind == TokenKind.Number || c.Kind == TokenKind.String) return true;
            if (c.Kind == TokenKind.Op) return c.Text == "(" || c.Text == "[" || c.Text == "{" || c.Text == "-" || c.Text == "+" || c.Text == "~" || c.Text == "*" || c.Text == "...";
            return false;
        }

        Expr ParseTest()
        {
            var tok = Cur;
            if (IsKw("lambda")) return ParseLambda();
            var e = ParseOrTest();
            if (IsKw("if") )
            {
                _i++;
                var test = ParseOrTest();
                ExpectKw("else");
                var orElse = ParseTest();
                return Spanned(At(new IfExpr { Test = test, Body = e, OrElse = orElse }, tok));
            }
            return e;
        }

        Expr ParseLambda()
        {
            var tok = Cur;
            ExpectKw("lambda");
            var l = At(new LambdaExpr(), tok);
            while (!IsOp(":"))
            {
                if (l.KwArgs != null) throw Err("**kwargs must be the last lambda parameter");
                if (AcceptOp("**")) l.KwArgs = ExpectName();
                else if (l.VarArgs != null) throw Err("keyword-only lambda parameters are not supported in Typhon");
                else if (AcceptOp("*")) l.VarArgs = ExpectName();
                else
                {
                    l.Params.Add(ExpectName());
                    if (AcceptOp("=")) l.Defaults.Add(ParseTest());
                    else if (l.Defaults.Count != 0) throw Err("non-default argument follows default argument");
                }
                if (IsOp("=")) throw Err("variadic lambda parameters cannot have default values");
                if (!AcceptOp(",")) break;
            }
            ExpectOp(":");
            l.Body = ParseTest();
            return Spanned(l);
        }

        Expr ParseOrTest()
        {
            var tok = Cur;
            var e = ParseAndTest();
            if (!IsKw("or")) return e;
            var b = At(new BoolOpExpr { Op = "or" }, tok);
            b.Values.Add(e);
            while (AcceptKw("or")) b.Values.Add(ParseAndTest());
            return Spanned(b);
        }

        Expr ParseAndTest()
        {
            var tok = Cur;
            var e = ParseNotTest();
            if (!IsKw("and")) return e;
            var b = At(new BoolOpExpr { Op = "and" }, tok);
            b.Values.Add(e);
            while (AcceptKw("and")) b.Values.Add(ParseNotTest());
            return Spanned(b);
        }

        Expr ParseNotTest()
        {
            var tok = Cur;
            if (AcceptKw("not")) return Spanned(At(new UnaryExpr { Op = "not", Operand = ParseNotTest() }, tok));
            return ParseComparison();
        }

        Expr ParseComparison()
        {
            var tok = Cur;
            var left = ParseOrExpr();
            CompareExpr cmp = null;
            while (true)
            {
                string op = null;
                if (Cur.Kind == TokenKind.Op && (Cur.Text == "<" || Cur.Text == ">" || Cur.Text == "==" || Cur.Text == ">=" || Cur.Text == "<=" || Cur.Text == "!=" || Cur.Text == "<>"))
                {
                    if (Cur.Text == "!=" && _barryAsFlufl) throw Err("with Barry as BDFL, use '<>' instead of '!='");
                    if (Cur.Text == "<>" && !_barryAsFlufl) throw Err("invalid syntax. Maybe you meant '!=' instead of '<>'?");
                    op = Cur.Text == "<>" ? "!=" : Cur.Text;
                    _i++;
                }
                else if (IsKw("in")) { op = "in"; _i++; }
                else if (IsKw("not") && Peek().Is("in")) { op = "not in"; _i += 2; }
                else if (IsKw("is"))
                {
                    _i++;
                    op = AcceptKw("not") ? "is not" : "is";
                }
                if (op == null) break;
                if (cmp == null) cmp = At(new CompareExpr { Left = left }, tok);
                cmp.Ops.Add(op);
                cmp.Comparators.Add(ParseOrExpr());
            }
            return cmp == null ? left : Spanned(cmp);
        }

        Expr ParseOrExpr() => ParseBinary(0);

        static readonly string[][] BinaryLevels =
        {
            new[] { "|" },
            new[] { "^" },
            new[] { "&" },
            new[] { "<<", ">>" },
            new[] { "+", "-" },
            new[] { "*", "/", "//", "%", "@" },
        };

        Expr ParseBinary(int level)
        {
            if (level >= BinaryLevels.Length) return ParseFactor();
            var tok = Cur;
            var left = ParseBinary(level + 1);
            while (Cur.Kind == TokenKind.Op && System.Array.IndexOf(BinaryLevels[level], Cur.Text) >= 0)
            {
                var opTok = Cur;
                _i++;
                var right = ParseBinary(level + 1);
                left = Spanned(At(new BinOpExpr { Op = opTok.Text, Left = left, Right = right }, tok));
            }
            return left;
        }

        Expr ParseFactor()
        {
            var tok = Cur;
            if (Cur.Kind == TokenKind.Op && (Cur.Text == "-" || Cur.Text == "+" || Cur.Text == "~"))
            {
                _i++;
                return Spanned(At(new UnaryExpr { Op = tok.Text, Operand = ParseFactor() }, tok));
            }
            return ParsePower();
        }

        Expr ParsePower()
        {
            var tok = Cur;
            Expr e;
            if (AcceptKw("await")) e = Spanned(At(new AwaitExpr { Value = ParsePrimary() }, tok));
            else e = ParsePrimary();
            if (AcceptOp("**")) return Spanned(At(new BinOpExpr { Op = "**", Left = e, Right = ParseFactor() }, tok));
            return e;
        }

        Expr ParsePrimary()
        {
            int start = Cur.Pos;
            var e = ParseAtom();
            while (true)
            {
                var tok = Cur;
                if (AcceptOp("."))
                {
                    e = At(new AttributeExpr { Target = e, Name = ExpectNameOrKeyword() }, tok);
                }
                else if (IsOp("("))
                {
                    _i++;
                    var call = At(new CallExpr { Func = e }, tok);
                    call.Line = e.Line; call.Col = e.Col;
                    ParseCallArgs(call);
                    e = call;
                }
                else if (IsOp("["))
                {
                    _i++;
                    var sub = At(new SubscriptExpr { Target = e }, tok);
                    sub.Line = e.Line; sub.Col = e.Col;
                    sub.Index = ParseSubscriptList();
                    ExpectOp("]");
                    e = sub;
                }
                else return e;
                e.StartOffset = start;
                Spanned(e);
            }
        }

        string ExpectNameOrKeyword()
        {
            if (Cur.Kind != TokenKind.Name) throw Err("expected an attribute name but found " + Describe(Cur));
            return _t[_i++].Text;
        }

        void ParseCallArgs(CallExpr call)
        {
            while (!IsOp(")"))
            {
                var arg = new Arg();
                if (AcceptOp("**")) { arg.DoubleStar = true; arg.Value = ParseTest(); }
                else if (AcceptOp("*")) { arg.Star = true; arg.Value = ParseTest(); }
                else if (Cur.Kind == TokenKind.Name && Peek().Is("=") && Peek().Kind == TokenKind.Op)
                {
                    arg.Name = Cur.Text; _i += 2; arg.Value = ParseTest();
                }
                else
                {
                    var tok = Cur;
                    var v = ParseNamedExprTest();
                    if (IsKw("for") || IsKw("async"))
                        v = ParseComprehensionTail(ComprehensionKind.Generator, v, null, tok);
                    arg.Value = v;
                }
                call.Args.Add(arg);
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
        }

        Expr ParseSubscriptList()
        {
            var tok = Cur;
            var first = ParseSubscript();
            if (!IsOp(",")) return first;
            var tuple = At(new TupleExpr(), tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp("]")) break;
                tuple.Elts.Add(ParseSubscript());
            }
            return Spanned(tuple);
        }

        Expr ParseSubscript()
        {
            var tok = Cur;
            Expr lower = null;
            if (!IsOp(":")) { lower = ParseTest(); if (!IsOp(":")) return lower; }
            ExpectOp(":");
            var s = At(new SliceExpr { Lower = lower }, tok);
            if (!IsOp(":") && !IsOp("]") && !IsOp(",")) s.Upper = ParseTest();
            if (AcceptOp(":"))
            {
                if (!IsOp("]") && !IsOp(",")) s.Step = ParseTest();
            }
            return Spanned(s);
        }

        Expr ParseAtom()
        {
            var tok = Cur;
            switch (tok.Kind)
            {
                case TokenKind.Number:
                    _i++;
                    bool isFloat = !tok.Text.StartsWith("0x") && !tok.Text.StartsWith("0X") &&
                                   (tok.Text.Contains(".") || tok.Text.Contains("e") || tok.Text.Contains("E"));
                    return At(new ConstExpr { Kind = isFloat ? ConstKind.Float : ConstKind.Int, Raw = tok.Text }, tok);
                case TokenKind.String:
                    return Spanned(ParseStrings());
                case TokenKind.Name:
                    if (tok.Text == "None") { _i++; return At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, tok); }
                    if (tok.Text == "True" || tok.Text == "False") { _i++; return At(new ConstExpr { Kind = ConstKind.Bool, Raw = tok.Text }, tok); }
                    if (Keywords.Contains(tok.Text) && tok.Text != "await") throw Err("unexpected keyword '" + tok.Text + "'");
                    _i++;
                    return At(new NameExpr { Id = tok.Text }, tok);
                case TokenKind.Op:
                    if (tok.Text == "...") { _i++; return At(new ConstExpr { Kind = ConstKind.Ellipsis, Raw = "..." }, tok); }
                    if (tok.Text == "(") return ParseParenAtom();
                    if (tok.Text == "[") return ParseListAtom();
                    if (tok.Text == "{") return ParseBraceAtom();
                    break;
            }
            throw Err("unexpected " + Describe(tok));
        }

        Expr ParseParenAtom()
        {
            var tok = ExpectOp("(");
            if (AcceptOp(")")) return Spanned(At(new TupleExpr { Parenthesized = true }, tok));
            if (IsKw("yield"))
            {
                var y = ParseTestListOrStar(allowYield: true);
                ExpectOp(")");
                return y;
            }
            var first = ParseNamedExprOrStar();
            if (IsKw("for") || IsKw("async"))
            {
                var g = ParseComprehensionTail(ComprehensionKind.Generator, first, null, tok);
                ExpectOp(")");
                return Spanned(g);
            }
            if (AcceptOp(")")) return first; // grouping
            var tuple = At(new TupleExpr { Parenthesized = true }, tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp(")")) break;
                tuple.Elts.Add(ParseNamedExprOrStar());
            }
            ExpectOp(")");
            return Spanned(tuple);
        }

        Expr ParseListAtom()
        {
            var tok = ExpectOp("[");
            var list = At(new ListExpr(), tok);
            if (AcceptOp("]")) return Spanned(list);
            var first = ParseNamedExprOrStar();
            if (IsKw("for") || IsKw("async"))
            {
                var c = ParseComprehensionTail(ComprehensionKind.List, first, null, tok);
                ExpectOp("]");
                return Spanned(c);
            }
            list.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp("]")) break;
                list.Elts.Add(ParseNamedExprOrStar());
            }
            ExpectOp("]");
            return Spanned(list);
        }

        Expr ParseBraceAtom()
        {
            var tok = ExpectOp("{");
            if (AcceptOp("}")) return Spanned(At(new DictExpr(), tok));
            if (IsOp("**")) return ParseDictDisplay(tok, null, null);
            var first = ParseNamedExprOrStar();
            if (!(first is StarredExpr) && AcceptOp(":"))
            {
                var value = ParseTest();
                if (IsKw("for") || IsKw("async"))
                {
                    var c = ParseComprehensionTail(ComprehensionKind.Dict, first, value, tok);
                    ExpectOp("}");
                    return Spanned(c);
                }
                return ParseDictDisplay(tok, first, value);
            }
            if (IsKw("for") || IsKw("async"))
            {
                var c = ParseComprehensionTail(ComprehensionKind.Set, first, null, tok);
                ExpectOp("}");
                return Spanned(c);
            }
            var set = At(new SetExpr(), tok);
            set.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp("}")) break;
                set.Elts.Add(ParseNamedExprOrStar());
            }
            ExpectOp("}");
            return Spanned(set);
        }

        /// <summary>The rest of a dict display after its first item; `**mapping` items are stored with a null key.</summary>
        Expr ParseDictDisplay(Token tok, Expr firstKey, Expr firstValue)
        {
            var dict = At(new DictExpr(), tok);
            if (firstKey != null) { dict.Keys.Add(firstKey); dict.Values.Add(firstValue); }
            else { ExpectOp("**"); dict.Keys.Add(null); dict.Values.Add(ParseOrExpr()); }
            while (AcceptOp(","))
            {
                if (IsOp("}")) break;
                if (AcceptOp("**")) { dict.Keys.Add(null); dict.Values.Add(ParseOrExpr()); continue; }
                dict.Keys.Add(ParseTest());
                ExpectOp(":");
                dict.Values.Add(ParseTest());
            }
            ExpectOp("}");
            return Spanned(dict);
        }
    }
}
