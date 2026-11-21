// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;

namespace Typhon.Compiler.Syntax
{
    /// <summary>
    /// Decorators written in Python, applied the way Python applies them. A decorated function
    /// <code>
    /// @d1
    /// @d2(arg)
    /// def f(x): ...
    /// </code>
    /// becomes the undecorated function under another name and an assignment at the def's place, f = d1(d2(arg)(__undecorated_f)),
    /// so that inference and emission see ordinary code: the decorators run when the def would, f holds what they return, and
    /// calls of f are calls of that value. This applies to functions in modules and in function bodies; a decorator that names
    /// no Python callable (one the module neither defines, assigns, receives nor imports from a Python module) stays a .NET
    /// attribute (@Obsolete("..."), @Fact), as do the decorators Typhon implements itself (@property, @dataclass, @cache, ...).
    /// </summary>
    public static class Decorators
    {
        /// <summary>The prefix of a decorated function's undecorated body.</summary>
        public const string UndecoratedPrefix = "__undecorated_";

        /// <summary>Decorators Typhon implements itself (on functions or methods): never rewritten.</summary>
        public static readonly HashSet<string> Builtin = new HashSet<string>
        {
            "staticmethod", "classmethod", "property", "abstractmethod", "override", "final", "dataclass", "overload",
            "cache", "lru_cache", "functools.cache", "functools.lru_cache",
            "typing.override", "typing.final", "typing.overload", "abc.abstractmethod", "dataclasses.dataclass",
        };

        public static void Apply(ModuleAst module)
        {
            var names = PythonNames(module.Body);
            Rewrite(module.Body, names, module);
        }

        /// <summary>The dotted name of a decorator (its callee for a call: @d(arg) → d).</summary>
        public static string Name(Expr d)
        {
            if (d is CallExpr c) d = c.Func;
            if (d is NameExpr n) return n.Id;
            if (d is AttributeExpr a) return Name(a.Target) is string t ? t + "." + a.Name : null;
            return null;
        }

        static string Root(Expr d) => Name(d)?.Split('.')[0];

        static bool IsPythonDecorator(Expr d, HashSet<string> names)
        {
            string name = Name(d);
            if (name == null || Builtin.Contains(name) || name.EndsWith(".setter") || name.EndsWith(".getter") || name.EndsWith(".deleter")) return false;
            return names.Contains(Root(d));
        }

        static void Rewrite(List<Stmt> body, HashSet<string> names, ModuleAst module)
        {
            for (int i = 0; i < body.Count; i++)
            {
                switch (body[i])
                {
                    case FunctionDef f:
                        Rewrite(f.Body, names, module);
                        if (f.Decorators.Count > 0 && f.Decorators.Any(d => IsPythonDecorator(d, names)))
                        {
                            // decorators apply bottom-up; Typhon's own decorators keep working only below Python ones (@cache)
                            Expr value = new NameExpr { Id = UndecoratedPrefix + f.Name, Line = f.Line, Col = f.Col };
                            var kept = new List<Expr>();
                            for (int k = f.Decorators.Count - 1; k >= 0; k--)
                            {
                                var d = f.Decorators[k];
                                if (!IsPythonDecorator(d, names) && value is NameExpr) { kept.Insert(0, d); continue; }
                                value = new CallExpr { Func = d, Args = { new Arg { Value = value } }, Line = d.Line, Col = d.Col };
                            }
                            f.Decorators = kept;
                            f.PyName = f.Name;
                            string name = f.Name;
                            f.Name = UndecoratedPrefix + name;
                            body.Insert(i + 1, new AssignStmt { Targets = { new NameExpr { Id = name, Line = f.Line, Col = f.Col } }, Value = value, Line = f.Line, Col = f.Col });
                            i++;
                        }
                        break;
                    case ClassDef c:
                        foreach (var d in c.Decorators.Where(d => IsPythonDecorator(d, names))) module.UnappliedDecorators.Add(d);
                        foreach (var m in c.Body.OfType<FunctionDef>())
                        {
                            foreach (var d in m.Decorators.Where(d => IsPythonDecorator(d, names))) module.UnappliedDecorators.Add(d);
                            Rewrite(m.Body, names, module);
                        }
                        foreach (var nested in c.Body.OfType<ClassDef>()) Rewrite(new List<Stmt> { nested }, names, module);
                        break;
                    case IfStmt s: Rewrite(s.Body, names, module); Rewrite(s.OrElse, names, module); break;
                    case WhileStmt s: Rewrite(s.Body, names, module); Rewrite(s.OrElse, names, module); break;
                    case ForStmt s: Rewrite(s.Body, names, module); Rewrite(s.OrElse, names, module); break;
                    case WithStmt s: Rewrite(s.Body, names, module); break;
                    case TryStmt s:
                        Rewrite(s.Body, names, module);
                        foreach (var h in s.Handlers) Rewrite(h.Body, names, module);
                        Rewrite(s.OrElse, names, module);
                        Rewrite(s.FinalBody, names, module);
                        break;
                    case MatchStmt s: foreach (var mc in s.Cases) Rewrite(mc.Body, names, module); break;
                }
            }
        }

        /// <summary>
        /// The names Python code binds anywhere in the module: functions, classes, assignment targets, parameters, and imports
        /// of Python modules (lowercase or relative: import functools, from .util import timed, from contextlib import
        /// contextmanager; a .NET namespace such as Xunit or System is capitalised).
        /// </summary>
        static HashSet<string> PythonNames(List<Stmt> body)
        {
            var names = new HashSet<string>();
            void Target(Expr t)
            {
                switch (t)
                {
                    case NameExpr n: names.Add(n.Id); break;
                    case TupleExpr te: foreach (var x in te.Elts) Target(x); break;
                    case ListExpr le: foreach (var x in le.Elts) Target(x); break;
                    case StarredExpr se: Target(se.Value); break;
                }
            }
            void Walk(List<Stmt> stmts)
            {
                foreach (var s in stmts)
                    switch (s)
                    {
                        case FunctionDef f:
                            names.Add(f.Name);
                            foreach (var p in f.Params) names.Add(p.Name);
                            Walk(f.Body);
                            break;
                        case ClassDef c: names.Add(c.Name); Walk(c.Body); break;
                        case AssignStmt a: foreach (var t in a.Targets) Target(t); break;
                        case AnnAssignStmt a: Target(a.Target); break;
                        case ForStmt fs: Target(fs.Target); Walk(fs.Body); Walk(fs.OrElse); break;
                        case IfStmt s2: Walk(s2.Body); Walk(s2.OrElse); break;
                        case WhileStmt s2: Walk(s2.Body); Walk(s2.OrElse); break;
                        case WithStmt w: foreach (var it in w.Items) if (it.Var != null) Target(it.Var); Walk(w.Body); break;
                        case TryStmt t: Walk(t.Body); foreach (var h in t.Handlers) Walk(h.Body); Walk(t.OrElse); Walk(t.FinalBody); break;
                        case MatchStmt m: foreach (var mc in m.Cases) Walk(mc.Body); break;
                        case ImportStmt imp:
                            foreach (var a in imp.Names)
                                if (IsPythonModule(a.Name)) names.Add(a.AsName ?? a.Name.Split('.')[0]);
                            break;
                        case ImportFromStmt imp:
                            if (imp.Level > 0 || IsPythonModule(imp.Module ?? ""))
                                foreach (var a in imp.Names) if (a.Name != "*") names.Add(a.AsName ?? a.Name);
                            break;
                    }
            }
            Walk(body);
            return names;
        }

        static bool IsPythonModule(string dotted) => dotted.Length > 0 && (char.IsLower(dotted[0]) || dotted[0] == '_');
    }
}
