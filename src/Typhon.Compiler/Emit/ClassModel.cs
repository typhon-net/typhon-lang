// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    /// <summary>
    /// Completes Python class hierarchies so they map onto CLR classes, before inference and emission.
    ///
    /// Python looks members up at run time; the CLR needs them declared where the static type can see them:
    /// <list type="bullet">
    /// <item>A method that only subclasses define (the base is abstract by convention) gets a stub in the topmost Typhon
    /// base class, raising AttributeError like Python would for an object without it. Code typed as the base calls it
    /// virtually and reaches the subclass's override.</item>
    /// <item>A method alias in a class body (<c>__rmul__ = __mul__</c>) becomes a second method with the same body.</item>
    /// </list>
    /// Attributes created from outside a class (<c>obj.attr = v</c>, <c>Cls.CONST = v</c>) are found by the inference's dry
    /// runs instead (their types depend on the values), see <see cref="Observations.ExternalFields"/>.
    /// </summary>
    internal static class ClassModel
    {
        public static void Complete(ProjectContext p)
        {
            foreach (var m in p.Modules.Values)
                if (m.Ast != null && m.ParseDiagnostics.Count == 0) { HoistLocalClasses(m); SynthesizeNamedTuples(m); }
            // several bases: one stays the C# base, the others' members are copied in, in MRO order
            MultipleInheritance.Apply(p);
            foreach (var m in p.Modules.Values)
            {
                if (m.Ast == null || m.ParseDiagnostics.Count > 0) continue;
                foreach (var c in m.Classes.Values) { AddMethodAliases(c); NormalizeSuper(c); }
            }
            AddBaseStubs(p);
        }

        /// <summary>
        /// A class defined inside a function that uses none of the function's variables is the same class at module level
        /// (a CLR class cannot be local): it moves there, renamed only if the module already has that name.
        /// </summary>
        static void HoistLocalClasses(ModuleInfo m)
        {
            var body = m.Ast.Body;
            for (int fi = 0; fi < body.Count; fi++)
            {
                if (!(body[fi] is FunctionDef f)) continue;
                var locals = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(x => x.Name), null);
                var functionNames = new HashSet<string>(locals.AllLocals.Concat(f.Params.Select(x => x.Name)).Concat(locals.LocalFunctions));
                foreach (var cls in f.Body.OfType<ClassDef>().ToList())
                {
                    var used = new HashSet<string>();
                    AstWalker.VisitAll(cls, n => { if (n is NameExpr ne) used.Add(ne.Id); });
                    foreach (var b in cls.Bases) AstWalker.VisitAll(b, n => { if (n is NameExpr ne) used.Add(ne.Id); });
                    used.Remove(cls.Name);
                    // names the class body binds itself (methods' locals) are not captures; a crude but safe test: any
                    // function variable it mentions keeps it local
                    if (used.Any(u => functionNames.Contains(u) && u != cls.Name && !ClassBinds(cls, u))) continue;
                    string name = cls.Name;
                    if (m.Classes.ContainsKey(name) || m.Functions.Contains(name) || m.Globals.Contains(name))
                    {
                        name = f.Name + "_" + cls.Name;
                        string old = cls.Name;
                        AstWalker.VisitAll(f.Body, n => { if (n is NameExpr ne && ne.Id == old) ne.Id = name; });
                        cls.Name = name;
                    }
                    f.Body.Remove(cls);
                    body.Insert(fi, cls);
                    fi++;
                    m.Classes[name] = cls;
                }
            }
        }

        // ───────────────────────────── namedtuple ─────────────────────────────

        static bool IsNamedTupleCall(Expr e) => e is CallExpr c && (c.Func is NameExpr n && n.Id == "namedtuple" || c.Func is AttributeExpr a && a.Name == "namedtuple");

        /// <summary>namedtuple("P", "x y") / ("P", "x, y") / ("P", ["x", "y"]): the field names (null if not literal).</summary>
        static List<string> NamedTupleFieldsOf(CallExpr c)
        {
            var spec = c.Args.Count > 1 && c.Args[1].Name == null ? c.Args[1].Value : c.Args.FirstOrDefault(a => a.Name == "field_names")?.Value;
            switch (spec)
            {
                case ConstExpr s when s.Kind == ConstKind.Str: return s.Str.Replace(',', ' ').Split(new[] { ' ', '\t', '\n' }, System.StringSplitOptions.RemoveEmptyEntries).ToList();
                case ListExpr l when l.Elts.All(x => x is ConstExpr xc && xc.Kind == ConstKind.Str): return l.Elts.Select(x => ((ConstExpr)x).Str).ToList();
                case TupleExpr t when t.Elts.All(x => x is ConstExpr xc && xc.Kind == ConstKind.Str): return t.Elts.Select(x => ((ConstExpr)x).Str).ToList();
                default: return null;
            }
        }

        /// <summary>
        /// Point = namedtuple("Point", "x y"), class Position(namedtuple("Position", "a b")), class P(NamedTuple): the class
        /// with an __init__ taking the fields (so call sites type them) and NamedTupleFields set (the emitter adds the tuple
        /// protocol: unpacking, indexing, iteration, len, equality, hashing, ordering, repr, _replace, _asdict, _fields, _make).
        /// </summary>
        static void SynthesizeNamedTuples(ModuleInfo m)
        {
            var body = m.Ast.Body;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i] is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr target && IsNamedTupleCall(a.Value) &&
                    NamedTupleFieldsOf((CallExpr)a.Value) is List<string> fields)
                {
                    var cls = new ClassDef { Name = target.Id, Line = a.Line, Col = a.Col };
                    MakeNamedTuple(cls, fields, ((CallExpr)a.Value).Args.FirstOrDefault(x => x.Name == "defaults")?.Value, annotated: null);
                    body[i] = cls;
                    m.Classes[cls.Name] = cls;
                    m.Globals.Remove(cls.Name);
                    continue;
                }
                if (body[i] is ClassDef c && c.NamedTupleFields == null)
                {
                    var ntBase = c.Bases.FirstOrDefault(IsNamedTupleCall) as CallExpr;
                    if (ntBase != null && NamedTupleFieldsOf(ntBase) is List<string> bf)
                    {
                        c.Bases.Remove(ntBase);
                        MakeNamedTuple(c, bf, ntBase.Args.FirstOrDefault(x => x.Name == "defaults")?.Value, annotated: null);
                        continue;
                    }
                    var typed = c.Bases.FirstOrDefault(b => b is NameExpr bn && bn.Id == "NamedTuple" || b is AttributeExpr ba && ba.Name == "NamedTuple");
                    if (typed != null)
                    {
                        c.Bases.Remove(typed);
                        var anns = c.Body.OfType<AnnAssignStmt>().Where(x => x.Target is NameExpr).ToList();
                        MakeNamedTuple(c, anns.Select(x => ((NameExpr)x.Target).Id).ToList(), null, anns);
                    }
                }
            }
        }

        static void MakeNamedTuple(ClassDef c, List<string> fields, Expr defaults, List<AnnAssignStmt> annotated)
        {
            c.NamedTupleFields = fields;
            if (c.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__" || f.Name == "__new__")) return;
            var init = new FunctionDef { Name = "__init__", Line = c.Line, Col = c.Col };
            init.Params.Add(new Param { Name = "self", Line = c.Line, Col = c.Col });
            var defaultValues = defaults is TupleExpr dt ? dt.Elts : defaults is ListExpr dl ? dl.Elts : new List<Expr>();
            for (int i = 0; i < fields.Count; i++)
            {
                var ann = annotated?[i];
                int fromEnd = fields.Count - i;
                Expr dflt = ann?.Value ?? (fromEnd <= defaultValues.Count ? defaultValues[defaultValues.Count - fromEnd] : null);
                init.Params.Add(new Param { Name = fields[i], Annotation = ann?.Annotation, Default = dflt, Line = c.Line, Col = c.Col });
                init.Body.Add(new AssignStmt
                {
                    Line = c.Line, Col = c.Col,
                    Targets = { new AttributeExpr { Target = new NameExpr { Id = "self", Line = c.Line, Col = c.Col }, Name = fields[i], Line = c.Line, Col = c.Col } },
                    Value = new NameExpr { Id = fields[i], Line = c.Line, Col = c.Col },
                });
            }
            // typing.NamedTuple: the annotations stay (they declare the typed fields), their defaults move to __init__
            if (annotated != null) foreach (var an in annotated) an.Value = null;
            c.Body.Insert(0, init);
            // a docstring-only or empty class body was `pass`
            c.Body.RemoveAll(st => st is PassStmt);
        }

        /// <summary>Does a class body bind the name itself (a method parameter or local, a class variable)?</summary>
        static bool ClassBinds(ClassDef c, string name)
        {
            foreach (var st in c.Body)
            {
                if (st is FunctionDef f)
                {
                    if (f.Params.Any(x => x.Name == name)) return true;
                    var loc = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(x => x.Name), null);
                    if (loc.AllLocals.Contains(name)) return true;
                }
                if (st is AssignStmt a && a.Targets.Any(t => t is NameExpr tn && tn.Id == name)) return true;
                if (st is AnnAssignStmt aa && aa.Target is NameExpr an && an.Id == name) return true;
            }
            return false;
        }

        /// <summary>
        /// super(Cls, self).m(...) → super().m(...); Base.__init__(self, ...) → super().__init__(...) (Base the class's first
        /// base): one form for the emitter.
        /// </summary>
        static void NormalizeSuper(ClassDef c)
        {
            string firstBase = c.Bases.Count > 0 ? ((c.Bases[0] is SubscriptExpr s ? s.Target : c.Bases[0]) is NameExpr bn ? bn.Id : null) : null;
            foreach (var f in c.Body.OfType<FunctionDef>())
            {
                string self = f.Params.Count > 0 ? f.Params[0].Name : null;
                AstWalker.VisitAll(f.Body, n =>
                {
                    if (n is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super" && sc.Args.Count == 2 && sc.Args[1].Value is NameExpr si && si.Id == self)
                        sc.Args.Clear();
                    if (n is CallExpr ic && ic.Func is AttributeExpr ia && ia.Name == "__init__" && ia.Target is NameExpr itn && itn.Id == firstBase &&
                        ic.Args.Count >= 1 && ic.Args[0].Name == null && ic.Args[0].Value is NameExpr ia0 && ia0.Id == self)
                    {
                        ia.Target = new CallExpr { Func = new NameExpr { Id = "super", Line = ia.Line, Col = ia.Col }, Line = ia.Line, Col = ia.Col };
                        ic.Args.RemoveAt(0);
                    }
                });
            }
            foreach (var nested in c.Body.OfType<ClassDef>()) NormalizeSuper(nested);
        }

        /// <summary>__rmul__ = __mul__ in a class body: a method of the new name with the same parameters and body.</summary>
        static void AddMethodAliases(ClassDef c)
        {
            for (int i = 0; i < c.Body.Count; i++)
            {
                if (!(c.Body[i] is AssignStmt a) || a.Targets.Count != 1 || !(a.Targets[0] is NameExpr alias) || !(a.Value is NameExpr original)) continue;
                var def = c.Body.Take(i).OfType<FunctionDef>().LastOrDefault(f => f.Name == original.Id);
                if (def == null) continue;
                c.Body[i] = new FunctionDef
                {
                    Name = alias.Id, TypeParams = def.TypeParams, Params = def.Params, Returns = def.Returns, Body = def.Body,
                    Decorators = def.Decorators, IsAsync = def.IsAsync, Line = a.Line, Col = a.Col,
                };
            }
            foreach (var nested in c.Body.OfType<ClassDef>()) AddMethodAliases(nested);
        }

        static void AddBaseStubs(ProjectContext p)
        {
            var owner = new Dictionary<ClassDef, ModuleInfo>();
            foreach (var m in p.Modules.Values)
                if (m.Ast != null && m.ParseDiagnostics.Count == 0)
                    foreach (var c in m.Classes.Values) owner[c] = m;

            List<ClassDef> Bases(ClassDef c)
            {
                var result = new List<ClassDef>();
                foreach (var b in c.Bases)
                {
                    string name = (b is SubscriptExpr s ? s.Target : b) is NameExpr n ? n.Id : b is AttributeExpr at ? at.Name : null;
                    if (name != null && p.TryFindClass(owner[c], name, out var bc, out _) && bc != c && owner.ContainsKey(bc)) result.Add(bc);
                }
                return result;
            }
            List<ClassDef> Ancestors(ClassDef c)
            {
                var result = new List<ClassDef>();
                var todo = new Stack<ClassDef>(Bases(c));
                while (todo.Count > 0 && result.Count < 50)
                {
                    var k = todo.Pop();
                    if (result.Contains(k)) continue;
                    result.Add(k);
                    foreach (var b in Bases(k)) todo.Push(b);
                }
                return result;
            }
            bool Defines(ClassDef c, string name) => c.Body.Any(s => s is FunctionDef f && f.Name == name ||
                                                                     s is AssignStmt a && a.Targets.Any(t => t is NameExpr tn && tn.Id == name) ||
                                                                     s is AnnAssignStmt aa && aa.Target is NameExpr an && an.Id == name);

            // (topmost base, method) → its definitions in subclasses
            var wanted = new Dictionary<(ClassDef Root, string Name), List<FunctionDef>>();
            foreach (var d in owner.Keys)
            {
                var ancestors = Ancestors(d);
                if (ancestors.Count == 0) continue;
                // the topmost Typhon base along the first-base chain (a single-inheritance hierarchy)
                var root = d;
                for (int depth = 0; depth < 50; depth++) { var bs = Bases(root); if (bs.Count == 0) break; root = bs[0]; }
                if (root == d || IsSpecial(root)) continue;
                foreach (var f in d.Body.OfType<FunctionDef>())
                {
                    if (f.Name.StartsWith("__") && f.Name.EndsWith("__") || f.Decorators.Count > 0 || f.Params.Count == 0) continue;
                    if (MultipleInheritance.IsGenerated(f.Name)) continue; // (multiple inheritance's own hooks and copies)
                    if (ancestors.Any(a => Defines(a, f.Name))) continue;
                    if (!wanted.TryGetValue((root, f.Name), out var list)) wanted[(root, f.Name)] = list = new List<FunctionDef>();
                    list.Add(f);
                }
            }
            foreach (var kv in wanted)
            {
                var defs = kv.Value;
                var first = defs[0];
                // one C# signature for all of them: the same parameter count, no annotations (or the same ones), no *args
                bool same = defs.All(f => f.Params.Count == first.Params.Count && f.Params.All(x => x.Kind == ParamKind.Normal) &&
                                          f.Params.Skip(1).All(x => x.Annotation == null) && f.Returns == null && f.IsAsync == first.IsAsync &&
                                          f.TypeParams.Count == 0);
                if (!same) continue;
                var root = kv.Key.Root;
                var stub = new FunctionDef { Name = kv.Key.Name, Line = root.Line, Col = root.Col, IsAsync = first.IsAsync };
                foreach (var prm in first.Params) stub.Params.Add(new Param { Name = prm.Name, Default = prm.Default, Kind = ParamKind.Normal, Line = root.Line, Col = root.Col });
                stub.Body.Add(new RaiseStmt
                {
                    Line = root.Line, Col = root.Col,
                    Exc = new CallExpr
                    {
                        Line = root.Line, Col = root.Col,
                        Func = new NameExpr { Id = "AttributeError", Line = root.Line, Col = root.Col },
                        Args = { new Arg { Value = new ConstExpr { Kind = ConstKind.Str, Str = $"'{root.Name}' object has no attribute '{kv.Key.Name}'", Raw = "''", Line = root.Line, Col = root.Col } } },
                    },
                });
                root.Body.Add(stub);
            }
        }

        static bool IsSpecial(ClassDef c) => c.Bases.Any(b =>
        {
            var n = (b is SubscriptExpr s ? s.Target : b) is NameExpr ne ? ne.Id : (b is AttributeExpr ae ? ae.Name : null);
            return n == "Protocol" || n == "Enum" || n == "IntEnum" || n == "Exception" || n == "BaseException";
        });
    }
}
