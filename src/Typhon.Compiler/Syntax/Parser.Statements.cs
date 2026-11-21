// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    // Statements: simple and compound statements, definitions and imports.
    public sealed partial class Parser
    {
        List<Stmt> ParseStatement()
        {
            var tok = Cur;
            if (IsOp("@")) return new List<Stmt> { ParseDecorated() };
            if (IsKw("def")) return new List<Stmt> { ParseFuncDef(new List<Expr>(), false) };
            if (IsKw("class")) return new List<Stmt> { ParseClassDef(new List<Expr>()) };
            if (IsKw("if")) return new List<Stmt> { ParseIf() };
            if (IsKw("while")) return new List<Stmt> { ParseWhile() };
            if (IsKw("for")) return new List<Stmt> { ParseFor(false) };
            if (IsKw("try")) return new List<Stmt> { ParseTry() };
            if (IsKw("with")) return new List<Stmt> { ParseWith(false) };
            if (IsKw("async"))
            {
                _i++;
                if (IsKw("def")) return new List<Stmt> { ParseFuncDef(new List<Expr>(), true) };
                if (IsKw("for")) return new List<Stmt> { ParseFor(true) };
                if (IsKw("with")) return new List<Stmt> { ParseWith(true) };
                throw Err("expected 'def', 'for' or 'with' after 'async'");
            }
            if (IsKw("match") && StartsMatchStatement()) return new List<Stmt> { ParseMatch() };
            return ParseSimpleStatements();
        }

        List<Stmt> ParseSimpleStatements()
        {
            var list = new List<Stmt> { ParseSmallStatement() };
            while (AcceptOp(";"))
            {
                if (Cur.Kind == TokenKind.Newline) break;
                list.Add(ParseSmallStatement());
            }
            ExpectNewline();
            return list;
        }

        Stmt ParseSmallStatement()
        {
            var tok = Cur;
            if (AcceptKw("pass")) return At(new PassStmt(), tok);
            if (AcceptKw("break")) return At(new BreakStmt(), tok);
            if (AcceptKw("continue")) return At(new ContinueStmt(), tok);
            if (AcceptKw("return"))
            {
                var r = At(new ReturnStmt(), tok);
                if (!AtStatementEnd()) r.Value = ParseTestListOrStar();
                return r;
            }
            if (AcceptKw("raise"))
            {
                var r = At(new RaiseStmt(), tok);
                if (!AtStatementEnd())
                {
                    r.Exc = ParseTest();
                    if (AcceptKw("from")) r.Cause = ParseTest();
                }
                return r;
            }
            if (IsKw("global") || IsKw("nonlocal"))
            {
                var g = At(new GlobalStmt { Nonlocal = Cur.Text == "nonlocal" }, tok);
                _i++;
                g.Names.Add(ExpectName());
                while (AcceptOp(",")) g.Names.Add(ExpectName());
                return g;
            }
            if (AcceptKw("assert"))
            {
                var a = At(new AssertStmt { Test = ParseTest() }, tok);
                if (AcceptOp(",")) a.Msg = ParseTest();
                return a;
            }
            if (AcceptKw("del"))
            {
                var d = At(new DelStmt(), tok);
                d.Targets.Add(ParseOrExpr());
                while (AcceptOp(",")) d.Targets.Add(ParseOrExpr());
                return d;
            }
            if (AcceptKw("import"))
            {
                var s = At(new ImportStmt(), tok);
                do
                {
                    var alias = new ImportAlias { Name = ParseDottedName() };
                    if (AcceptKw("as")) alias.AsName = ExpectName();
                    s.Names.Add(alias);
                } while (AcceptOp(","));
                return s;
            }
            if (AcceptKw("from"))
            {
                var s = At(new ImportFromStmt(), tok);
                while (IsOp(".") || IsOp("..."))
                {
                    s.Level += Cur.Text.Length;
                    _i++;
                }
                s.Module = IsKw("import") ? "" : ParseDottedName();
                ExpectKw("import");
                if (AcceptOp("*")) { s.Names.Add(new ImportAlias { Name = "*" }); return s; }
                bool paren = AcceptOp("(");
                do
                {
                    if (paren && IsOp(")")) break;
                    var alias = new ImportAlias { Name = ExpectName() };
                    if (AcceptKw("as")) alias.AsName = ExpectName();
                    s.Names.Add(alias);
                } while (AcceptOp(","));
                if (paren) ExpectOp(")");
                return s;
            }

            // expression statement / assignment
            var first = ParseTestListOrStar(allowYield: true);
            if (IsOp(":"))
            {
                _i++;
                var ann = At(new AnnAssignStmt { Target = first, Annotation = ParseTest() }, tok);
                if (AcceptOp("=")) ann.Value = ParseTestListOrStar(allowYield: true);
                return ann;
            }
            if (Cur.Kind == TokenKind.Op && Cur.Text.Length >= 2 && Cur.Text.EndsWith("=") && Cur.Text != "==" && Cur.Text != "<=" && Cur.Text != ">=" && Cur.Text != "!=")
            {
                string op = Cur.Text.Substring(0, Cur.Text.Length - 1);
                _i++;
                return At(new AugAssignStmt { Target = first, Op = op, Value = ParseTestListOrStar(allowYield: true) }, tok);
            }
            if (IsOp("="))
            {
                var assign = At(new AssignStmt(), tok);
                assign.Targets.Add(first);
                Expr value = null;
                while (AcceptOp("="))
                {
                    value = ParseTestListOrStar(allowYield: true);
                    if (IsOp("=")) assign.Targets.Add(value);
                }
                assign.Value = value;
                return assign;
            }
            return At(new ExprStmt { Value = first }, tok);
        }

        bool AtStatementEnd() => Cur.Kind == TokenKind.Newline || Cur.Kind == TokenKind.EndOfFile || IsOp(";");

        string ParseDottedName()
        {
            var sb = new StringBuilder(ExpectName());
            while (IsOp(".") && Peek().Kind == TokenKind.Name) { _i++; sb.Append('.').Append(ExpectName()); }
            return sb.ToString();
        }

        List<Stmt> ParseBlock()
        {
            ExpectOp(":");
            var body = new List<Stmt>();
            if (Cur.Kind != TokenKind.Newline)
            {
                body.AddRange(ParseSimpleStatements());
                return body;
            }
            _i++;
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block");
            _i++;
            while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
            {
                if (Cur.Kind == TokenKind.Newline) { _i++; continue; }
                body.AddRange(ParseStatement());
            }
            if (Cur.Kind == TokenKind.Dedent) _i++;
            return body;
        }

        Stmt ParseDecorated()
        {
            var decorators = new List<Expr>();
            while (AcceptOp("@"))
            {
                decorators.Add(ParseTest());
                ExpectNewline();
            }
            if (IsKw("def")) return ParseFuncDef(decorators, false);
            if (IsKw("async")) { _i++; return ParseFuncDef(decorators, true); }
            if (IsKw("class")) return ParseClassDef(decorators);
            throw Err("expected 'def' or 'class' after decorator");
        }

        List<TypeParam> ParseTypeParams()
        {
            var list = new List<TypeParam>();
            if (!AcceptOp("[")) return list;
            do
            {
                if (IsOp("]")) break;
                var tp = new TypeParam { Name = ExpectName() };
                if (AcceptOp(":")) tp.Bound = ParseTest();
                list.Add(tp);
            } while (AcceptOp(","));
            ExpectOp("]");
            return list;
        }

        FunctionDef ParseFuncDef(List<Expr> decorators, bool isAsync)
        {
            var tok = Cur;
            ExpectKw("def");
            var f = At(new FunctionDef { Name = ExpectName(), Decorators = decorators, IsAsync = isAsync }, tok);
            f.TypeParams = ParseTypeParams();
            ExpectOp("(");
            while (!IsOp(")"))
            {
                var ptok = Cur;
                var p = At(new Param(), ptok);
                if (AcceptOp("**")) p.Kind = ParamKind.KwArgs;
                else if (AcceptOp("*"))
                {
                    p.Kind = ParamKind.VarArgs;
                    if (IsOp(",")) { _i++; continue; } // bare '*' (keyword-only marker) — ignored
                }
                else if (AcceptOp("/")) { if (!IsOp(")")) ExpectOp(","); continue; }
                p.Name = ExpectName();
                if (AcceptOp(":")) p.Annotation = ParseTest();
                if (AcceptOp("=")) p.Default = ParseTest();
                f.Params.Add(p);
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
            if (AcceptOp("->")) f.Returns = ParseTest();
            f.Body = ParseBlock();
            return f;
        }

        ClassDef ParseClassDef(List<Expr> decorators)
        {
            var tok = Cur;
            ExpectKw("class");
            var c = At(new ClassDef { Name = ExpectName(), Decorators = decorators }, tok);
            c.TypeParams = ParseTypeParams();
            if (AcceptOp("("))
            {
                while (!IsOp(")"))
                {
                    if (Cur.Kind == TokenKind.Name && Peek().Is("=")) { _i += 2; ParseTest(); } // metaclass=..., ignored
                    else c.Bases.Add(ParseTest());
                    if (!AcceptOp(",")) break;
                }
                ExpectOp(")");
            }
            c.Body = ParseBlock();
            return c;
        }

        IfStmt ParseIf()
        {
            var tok = Cur;
            _i++; // 'if' or 'elif'
            var s = At(new IfStmt { Test = ParseNamedExprTest() }, tok);
            s.Body = ParseBlock();
            if (IsKw("elif")) s.OrElse.Add(ParseIf());
            else if (AcceptKw("else")) s.OrElse = ParseBlock();
            return s;
        }

        WhileStmt ParseWhile()
        {
            var tok = Cur;
            ExpectKw("while");
            var s = At(new WhileStmt { Test = ParseNamedExprTest() }, tok);
            s.Body = ParseBlock();
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            return s;
        }

        ForStmt ParseFor(bool isAsync)
        {
            var tok = Cur;
            ExpectKw("for");
            var s = At(new ForStmt { IsAsync = isAsync }, tok);
            s.Target = ParseTargetList();
            ExpectKw("in");
            s.Iter = ParseTestListOrStar();
            s.Body = ParseBlock();
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            return s;
        }

        Expr ParseTargetList()
        {
            var tok = Cur;
            var first = ParseOrExpr();
            if (!IsOp(",")) return first;
            var tuple = At(new TupleExpr(), tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsKw("in") || IsOp("=")) break;
                tuple.Elts.Add(ParseOrExpr());
            }
            return tuple;
        }

        TryStmt ParseTry()
        {
            var tok = Cur;
            ExpectKw("try");
            var s = At(new TryStmt(), tok);
            s.Body = ParseBlock();
            while (IsKw("except"))
            {
                var htok = Cur;
                _i++;
                var h = At(new ExceptHandler(), htok);
                if (!IsOp(":"))
                {
                    var typeTok = Cur;
                    h.Type = ParseTest();
                    if (IsOp(","))
                    {
                        // Python 3.14 (PEP 758): `except A, B:` catches A or B, like `except (A, B):`
                        var types = At(new TupleExpr(), typeTok);
                        types.Elts.Add(h.Type);
                        while (AcceptOp(","))
                        {
                            if (IsOp(":") || IsKw("as")) break;
                            types.Elts.Add(ParseTest());
                        }
                        h.Type = types;
                    }
                    if (AcceptKw("as")) h.Name = ExpectName();
                }
                h.Body = ParseBlock();
                s.Handlers.Add(h);
            }
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            if (AcceptKw("finally")) s.FinalBody = ParseBlock();
            return s;
        }

        WithStmt ParseWith(bool isAsync)
        {
            var tok = Cur;
            ExpectKw("with");
            var s = At(new WithStmt { IsAsync = isAsync }, tok);
            do
            {
                var item = new WithItem { Context = ParseTest() };
                if (AcceptKw("as")) item.Var = ParseOrExpr();
                s.Items.Add(item);
            } while (AcceptOp(","));
            s.Body = ParseBlock();
            return s;
        }
    }
}
