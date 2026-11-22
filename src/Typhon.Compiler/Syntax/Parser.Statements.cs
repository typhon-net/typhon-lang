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
            if (IsOp("@")) return new List<Stmt> { Spanned(ParseDecorated()) };
            if (IsKw("def")) return new List<Stmt> { Spanned(ParseFuncDef(new List<Expr>(), false)) };
            if (IsKw("class")) return new List<Stmt> { Spanned(ParseClassDef(new List<Expr>())) };
            if (IsKw("if")) return new List<Stmt> { Spanned(ParseIf()) };
            if (IsKw("while")) return new List<Stmt> { Spanned(ParseWhile()) };
            if (IsKw("for")) return new List<Stmt> { Spanned(ParseFor(false)) };
            if (IsKw("try")) return new List<Stmt> { Spanned(ParseTry()) };
            if (IsKw("with")) return new List<Stmt> { Spanned(ParseWith(false)) };
            if (IsKw("async"))
            {
                _i++;
                if (IsKw("def")) return new List<Stmt> { Spanned(ParseFuncDef(new List<Expr>(), true)) };
                if (IsKw("for")) return new List<Stmt> { Spanned(ParseFor(true)) };
                if (IsKw("with")) return new List<Stmt> { Spanned(ParseWith(true)) };
                throw Err("expected 'def', 'for' or 'with' after 'async'");
            }
            if (IsKw("match") && StartsMatchStatement()) return new List<Stmt> { Spanned(ParseMatch()) };
            // type Alias[T] = expression (PEP 695, Python 3.12): a soft keyword; the alias is an ordinary assignment of the type
            if (IsKw("type") && Peek().Kind == TokenKind.Name && Peek(2).Kind == TokenKind.Op && (Peek(2).Text == "=" || Peek(2).Text == "["))
            {
                var ttok = Cur;
                _i++;
                var target = At(new NameExpr { Id = ExpectName() }, ttok);
                if (AcceptOp("["))
                {
                    // (the alias's own type parameters: Typhon's generic aliases bind them by use)
                    int depth = 1;
                    while (depth > 0 && Cur.Kind != TokenKind.EndOfFile) { if (IsOp("[")) depth++; else if (IsOp("]")) depth--; _i++; }
                }
                ExpectOp("=");
                var a = At(new AssignStmt(), ttok);
                a.Targets.Add(target);
                a.Value = ParseTest();
                ExpectNewline();
                return new List<Stmt> { Spanned(a) };
            }
            return ParseSimpleStatements();
        }

        List<Stmt> ParseSimpleStatements()
        {
            var list = new List<Stmt> { Spanned(ParseSmallStatement()) };
            while (AcceptOp(";"))
            {
                if (Cur.Kind == TokenKind.Newline) break;
                list.Add(Spanned(ParseSmallStatement()));
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
            // lazy import x / lazy from x import y (PEP 810, Python 3.15): a soft keyword; a compiled program binds its imports
            // when it starts, so the import is an ordinary one
            bool isLazy = IsKw("lazy") && Peek().Kind == TokenKind.Name && (Peek().Text == "import" || Peek().Text == "from");
            if (isLazy) _i++;
            if (AcceptKw("import"))
            {
                var s = At(new ImportStmt { IsLazy = isLazy }, tok);
                do
                {
                    var name = Cur;
                    var alias = At(new ImportAlias { Name = ParseDottedName() }, name);
                    if (AcceptKw("as")) alias.AsName = ExpectName();
                    s.Names.Add(Spanned(alias));
                } while (AcceptOp(","));
                return s;
            }
            if (AcceptKw("from"))
            {
                var s = At(new ImportFromStmt { IsLazy = isLazy }, tok);
                while (IsOp(".") || IsOp("..."))
                {
                    s.Level += Cur.Text.Length;
                    _i++;
                }
                s.Module = IsKw("import") ? "" : ParseDottedName();
                ExpectKw("import");
                if (IsOp("*")) { s.Names.Add(At(new ImportAlias { Name = "*" }, Cur)); _i++; return s; }
                bool paren = AcceptOp("(");
                do
                {
                    if (paren && IsOp(")")) break;
                    var name = Cur;
                    var alias = At(new ImportAlias { Name = ExpectName() }, name);
                    if (AcceptKw("as")) alias.AsName = ExpectName();
                    s.Names.Add(Spanned(alias));
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
            if (Cur.Kind == TokenKind.Op && Cur.Text.Length >= 2 && Cur.Text.EndsWith("=") &&
                Cur.Text != "==" && Cur.Text != "<=" && Cur.Text != ">=" && Cur.Text != "!=" && Cur.Text != ":=")
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
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block", needsMoreInput: AtEndOfInput, isIndentationError: true);
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
            throw Err("expected 'def' or 'class' after decorator", needsMoreInput: AtEndOfInput);
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
            var tok = isAsync ? _t[_i - 1] : Cur;
            ExpectKw("def");
            var f = At(new FunctionDef { Name = ExpectName(), Decorators = decorators, IsAsync = isAsync }, tok);
            f.TypeParams = ParseTypeParams();
            ExpectOp("(");
            bool keywordOnly = false;
            while (!IsOp(")"))
            {
                var ptok = Cur;
                var p = At(new Param(), ptok);
                if (AcceptOp("**")) p.Kind = ParamKind.KwArgs;
                else if (AcceptOp("*"))
                {
                    p.Kind = ParamKind.VarArgs;
                    keywordOnly = true;
                    if (AcceptOp(",")) continue;
                }
                else if (AcceptOp("/"))
                {
                    foreach (var previous in f.Params) previous.PositionalOnly = true;
                    if (!IsOp(")")) ExpectOp(",");
                    continue;
                }
                else p.KeywordOnly = keywordOnly;
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
                    if (Cur.Kind == TokenKind.Name && Peek().Is("="))
                    {
                        string keyword = ExpectName();
                        ExpectOp("=");
                        var value = ParseTest();
                        if (keyword == "metaclass") c.Metaclass = value;
                        else c.ClassKeywords.Add(new Arg { Name = keyword, Value = value });
                    }
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
            if (IsKw("elif")) s.OrElse.Add(Spanned(ParseIf()));
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
            if (IsKw("else") && s.Handlers.Count == 0) throw Err("expected 'except' or 'finally' block");
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            if (AcceptKw("finally")) s.FinalBody = ParseBlock();
            if (s.Handlers.Count == 0 && s.FinalBody.Count == 0) throw Err("expected 'except' or 'finally' block", needsMoreInput: AtEndOfInput);
            return s;
        }

        WithStmt ParseWith(bool isAsync)
        {
            var tok = Cur;
            ExpectKw("with");
            var s = At(new WithStmt { IsAsync = isAsync }, tok);
            // with (a() as x, b() as y): the items in parentheses (Python 3.10); otherwise (a, b) is an ordinary expression
            if (IsOp("("))
            {
                int save = _i;
                try
                {
                    _i++;
                    var items = new List<WithItem>();
                    while (!IsOp(")"))
                    {
                        var item = new WithItem { Context = ParseTest() };
                        if (AcceptKw("as")) item.Var = ParseOrExpr();
                        items.Add(item);
                        if (!AcceptOp(",")) break;
                    }
                    ExpectOp(")");
                    if (IsOp(":") && (items.Count > 1 || items.Exists(x => x.Var != null)))
                    {
                        s.Items.AddRange(items);
                        s.Body = ParseBlock();
                        return s;
                    }
                }
                catch (SyntaxError) { }
                _i = save;
            }
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
