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
    /// attribute (@Obsolete("..."), @Fact), as do the decorators Typhon implements itself (@property, @dataclass, ...).
    /// </summary>
    public static class Decorators
    {
        /// <summary>The prefix of a decorated function's undecorated body.</summary>
        public const string UndecoratedPrefix = "__undecorated_";

        /// <summary>Decorators Typhon implements itself (on functions or methods): never rewritten.</summary>
        public static readonly HashSet<string> Builtin = new HashSet<string>
        {
            "staticmethod", "classmethod", "property", "abstractmethod", "override", "final", "dataclass", "overload",
            "typing.override", "typing.final", "typing.overload", "abc.abstractmethod", "dataclasses.dataclass",
            "total_ordering", "functools.total_ordering",
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

        internal static bool IsStringEnumDecorator(Expr expression) =>
            SimpleEnumBase(expression) is "StrEnum" or "enum.StrEnum";

        internal static bool IsIntegerEnumDecorator(Expr expression) =>
            SimpleEnumBase(expression) is "IntEnum" or "enum.IntEnum";

        static string SimpleEnumBase(Expr expression) =>
            expression is CallExpr { Args.Count: 1 } decorator &&
            Name(decorator.Func) is "_simple_enum" or "enum._simple_enum"
                ? Name(decorator.Args[0].Value) : null;

        public static bool IsCachedProperty(Expr decorator, ModuleAst module)
        {
            if (decorator is NameExpr name)
                return module.Body.OfType<ImportFromStmt>().Any(import => import.Level == 0 && import.Module == "functools" &&
                    import.Names.Any(imported => imported.Name == "cached_property" && (imported.AsName ?? imported.Name) == name.Id));
            return decorator is AttributeExpr { Name: "cached_property", Target: NameExpr owner } &&
                module.Body.OfType<ImportStmt>().Any(import => import.Names.Any(imported => imported.Name == "functools" &&
                    (imported.AsName ?? imported.Name) == owner.Id));
        }

        static bool IsGlobalEnumDecorator(Expr decorator, ModuleAst module) =>
            decorator is NameExpr name
                ? module.Body.OfType<ImportFromStmt>().Any(statement => statement.Level == 0 && statement.Module == "enum" &&
                    statement.Names.Any(binding => binding.Name == "global_enum" && (binding.AsName ?? binding.Name) == name.Id))
                : decorator is AttributeExpr { Name: "global_enum", Target: NameExpr owner } &&
                    module.Body.OfType<ImportStmt>().Any(statement => statement.Names.Any(binding =>
                        binding.Name == "enum" && (binding.AsName ?? "enum") == owner.Id));

        static bool IsPythonDecorator(Expr d, HashSet<string> names)
        {
            string name = Name(d);
            if (name == null || Builtin.Contains(name) || name.EndsWith(".setter") || name.EndsWith(".getter") || name.EndsWith(".deleter")) return false;
            return names.Contains(Root(d));
        }

        // A descriptor on a function outside a class suite is a regular decorator call.
        static bool IsFunctionDecorator(Expr d, HashSet<string> names) =>
            Name(d) is "classmethod" or "staticmethod" or "property" || IsPythonDecorator(d, names);

        static void Rewrite(List<Stmt> body, HashSet<string> names, ModuleAst module)
        {
            for (int i = 0; i < body.Count; i++)
            {
                switch (body[i])
                {
                    case FunctionDef f:
                        Rewrite(f.Body, names, module);
                        if (f.Decorators.Count > 0 && f.Decorators.Any(d => IsFunctionDecorator(d, names)))
                        {
                            // decorators apply bottom-up; Typhon's own decorators keep working only below Python ones
                            Expr value = new NameExpr { Id = UndecoratedPrefix + f.Name, Line = f.Line, Col = f.Col };
                            var kept = new List<Expr>();
                            for (int k = f.Decorators.Count - 1; k >= 0; k--)
                            {
                                var d = f.Decorators[k];
                                if (!IsFunctionDecorator(d, names) && value is NameExpr) { kept.Insert(0, d); continue; }
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
                    {
                        DescriptorAssignments(c, module);
                        if (c.Bases.Any(basis => Name(basis) is "IntEnum" or "enum.IntEnum") &&
                            c.Decorators.RemoveAll(decorator => IsGlobalEnumDecorator(decorator, module)) != 0)
                            c.ExportsIntegerEnumMembers = true;
                        if (c.Decorators.Any(d => Name(d) is "total_ordering" or "functools.total_ordering"))
                        {
                            TotalOrdering(c);
                            c.Decorators.RemoveAll(d => Name(d) is "total_ordering" or "functools.total_ordering");
                        }
                        foreach (var m in c.Body.OfType<FunctionDef>())
                        {
                            Rewrite(m.Body, names, module);
                            m.IsCachedProperty = m.Decorators.Any(d => IsCachedProperty(d, module));
                            var python = m.Decorators.Where(d => !IsCachedProperty(d, module) && IsPythonDecorator(d, names)).ToList();
                            if (python.Count == 0) continue;
                            // A single outer descriptor can wrap the result of the Python decorators.
                            var rest = m.Decorators.Where(d => !python.Contains(d)).ToList();
                            bool descriptorOutside = rest.Count == 1 && Name(rest[0]) is "staticmethod" or "property" &&
                                m.Decorators.IndexOf(rest[0]) == 0;
                            if (rest.Count == 0 || descriptorOutside)
                            {
                                module.MethodDecorators[m] = python;
                                m.Decorators = rest;
                            }
                            else foreach (var d in python) module.UnappliedDecorators.Add(d);
                        }
                        foreach (var nested in c.Body.OfType<ClassDef>()) Rewrite(new List<Stmt> { nested }, names, module);
                        // a class decorator runs where the class is defined, with the class (registries, markers); its result
                        // must be the class itself (Typhon classes are not rebound)
                        var classPython = c.Decorators.Where(d => !IsStringEnumDecorator(d) && !IsIntegerEnumDecorator(d) && IsPythonDecorator(d, names)).ToList();
                        if (classPython.Count > 0)
                        {
                            Expr applied = new NameExpr { Id = c.Name, Line = c.Line, Col = c.Col };
                            for (int k = classPython.Count - 1; k >= 0; k--)
                                applied = new CallExpr { Func = classPython[k], Args = { new Arg { Value = applied } }, Line = classPython[k].Line, Col = classPython[k].Col };
                            c.Decorators.RemoveAll(classPython.Contains);
                            body.Insert(i + 1, new ExprStmt { Value = applied, Line = c.Line, Col = c.Col });
                            i++;
                        }
                        if (c.ExportsIntegerEnumMembers)
                            foreach (var assignment in c.Body.OfType<AssignStmt>())
                            {
                                if (assignment.Targets.Count != 1 || assignment.Targets[0] is not NameExpr member) continue;
                                body.Insert(++i, new AssignStmt
                                {
                                    Line = c.Line, Col = c.Col,
                                    Targets = { AstClone.Clone(member) },
                                    Value = new AttributeExpr
                                    {
                                        Line = c.Line, Col = c.Col, Name = member.Id,
                                        Target = new NameExpr { Line = c.Line, Col = c.Col, Id = c.Name },
                                    },
                                });
                            }
                        break;
                    }
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
        /// @functools.total_ordering: the comparison methods the class lacks, from the one it defines (__lt__, __le__, __gt__
        /// or __ge__, in that preference, as functools picks its root) and __eq__.
        /// </summary>
        /// <summary>
        /// In a class body, name = property(getter[, setter]) with methods of the class, and name = staticmethod(function):
        /// the decorated methods they stand for (@property def name(self): return self.getter() ..., @staticmethod def
        /// name(*args, **kwargs): return function(*args, **kwargs)).
        /// </summary>
        static void DescriptorAssignments(ClassDef c, ModuleAst module)
        {
            var methods = new HashSet<string>(c.Body.OfType<FunctionDef>().Select(f => f.Name));
            for (int i = 0; i < c.Body.Count; i++)
            {
                if (!(c.Body[i] is AssignStmt a) || a.Targets.Count != 1 || !(a.Targets[0] is NameExpr target) || !(a.Value is CallExpr call) ||
                    !(call.Func is NameExpr fn) || call.Args.Any(x => x.Star || x.DoubleStar))
                    continue;
                string src = null;
                var args = call.Args.Where(x => x.Name == null).Select(x => x.Value).ToList();
                if (fn.Id == "property" && args.Count >= 1 && args.Count <= 2 && args.All(x => x is NameExpr n && methods.Contains(n.Id)) &&
                    call.Args.All(x => x.Name == null || x.Name == "doc"))
                {
                    // A generated CLR property would reserve these method names. Keep the Python descriptor.
                    if (methods.Contains("get_" + target.Id) || methods.Contains("set_" + target.Id)) continue;
                    string get = ((NameExpr)args[0]).Id;
                    src = $"@property\ndef {target.Id}(self):\n    return self.{get}()\n";
                    if (args.Count == 2) src += $"@{target.Id}.setter\ndef {target.Id}(self, value):\n    self.{((NameExpr)args[1]).Id}(value)\n";
                }
                else if (fn.Id == "staticmethod" && args.Count == 1 && call.Args.Count == 1 && args[0] is NameExpr sfn &&
                         module.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == sfn.Id) is FunctionDef wrapped &&
                         wrapped.Params.All(x => x.Kind == ParamKind.Normal) && !wrapped.IsAsync)
                {
                    // name = staticmethod(f) with f a function of the module: a static method with f's parameters, calling it
                    var m = new FunctionDef { Name = target.Id, Returns = AstClone.Clone(wrapped.Returns), Line = a.Line, Col = a.Col };
                    m.Decorators.Add(new NameExpr { Id = "staticmethod", Line = a.Line, Col = a.Col });
                    foreach (var x in wrapped.Params) m.Params.Add(AstClone.Clone(x));
                    var fwd = new CallExpr { Func = new NameExpr { Id = sfn.Id, Line = a.Line, Col = a.Col }, Line = a.Line, Col = a.Col };
                    foreach (var x in wrapped.Params) fwd.Args.Add(new Arg { Value = new NameExpr { Id = x.Name, Line = a.Line, Col = a.Col } });
                    m.Body.Add(new ReturnStmt { Value = fwd, Line = a.Line, Col = a.Col });
                    c.Body[i] = m;
                    methods.Add(m.Name);
                    continue;
                }
                if (src == null) continue;
                var defs = Parser.ParseModule(src, "<" + fn.Id + ">").Body;
                foreach (var d in defs) SetPosition(d, a.Line, a.Col);
                c.Body.RemoveAt(i);
                c.Body.InsertRange(i, defs);
                i += defs.Count - 1;
            }
        }

        static void TotalOrdering(ClassDef c)
        {
            var defined = new HashSet<string>(c.Body.OfType<FunctionDef>().Select(f => f.Name));
            string root = new[] { "__lt__", "__le__", "__gt__", "__ge__" }.FirstOrDefault(defined.Contains);
            if (root == null) return;
            var derived = new Dictionary<string, Dictionary<string, string>>
            {
                ["__lt__"] = new Dictionary<string, string> { ["__gt__"] = "not r and not (self == other)", ["__le__"] = "r or self == other", ["__ge__"] = "not r" },
                ["__le__"] = new Dictionary<string, string> { ["__ge__"] = "not r or self == other", ["__lt__"] = "r and not (self == other)", ["__gt__"] = "not r" },
                ["__gt__"] = new Dictionary<string, string> { ["__lt__"] = "not r and not (self == other)", ["__ge__"] = "r or self == other", ["__le__"] = "not r" },
                ["__ge__"] = new Dictionary<string, string> { ["__le__"] = "not r or self == other", ["__gt__"] = "r and not (self == other)", ["__lt__"] = "not r" },
            };
            var rootDef = c.Body.OfType<FunctionDef>().First(f => f.Name == root);
            var other = rootDef.Params.Skip(1).FirstOrDefault()?.Annotation;
            foreach (var kv in derived[root])
            {
                if (defined.Contains(kv.Key)) continue;
                string src = $"def {kv.Key}(self, other):\n    r = self.{root}(other)\n    return {kv.Value}\n";
                var f = (FunctionDef)Parser.ParseModule(src, "<total_ordering>").Body[0];
                f.Params[1].Annotation = other;
                f.Returns = rootDef.Returns; // (C# pairs < with > only when they return the same type)
                SetPosition(f, c.Line, c.Col);
                c.Body.Add(f);
            }
        }

        static void SetPosition(Node root, int line, int col)
        {
            void Visit(object o)
            {
                switch (o)
                {
                    case null: return;
                    case Node n:
                        n.Line = line; n.Col = col;
                        foreach (var fi in n.GetType().GetFields())
                            if (fi.FieldType != typeof(string) && !fi.FieldType.IsPrimitive) Visit(fi.GetValue(n));
                        return;
                    case System.Collections.IEnumerable list when !(o is string):
                        foreach (var x in list) Visit(x);
                        return;
                }
            }
            Visit(root);
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
