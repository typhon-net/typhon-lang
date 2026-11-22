// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    /// <summary>
    /// Python locals are function-scoped, C# locals are block-scoped. For every local we compute the lowest common
    /// block of all its uses. If its first use is a plain assignment directly in that block we declare it inline
    /// ("var x = ..." keeps C# type inference). Otherwise it is hoisted to the top of the function with a type taken
    /// from an annotation, a cheap inference, or 'dynamic' as a last resort.
    /// </summary>
    internal sealed class LocalsResult
    {
        public readonly Dictionary<Node, HashSet<string>> InlineDecl = new Dictionary<Node, HashSet<string>>();
        public readonly List<KeyValuePair<string, Expr>> Hoisted = new List<KeyValuePair<string, Expr>>(); // name → annotation (may be null)
        public readonly Dictionary<string, Expr> HoistedFirstValue = new Dictionary<string, Expr>();
        /// <summary>Hoisted names bound only by for-loops (used after the loop, e.g. in its else block): the loops binding them.</summary>
        public readonly Dictionary<string, List<ForStmt>> HoistedLoops = new Dictionary<string, List<ForStmt>>();
        /// <summary>Every value assigned to a hoisted variable (when they disagree on the type, the variable is late-bound).</summary>
        public readonly Dictionary<string, List<Expr>> HoistedValues = new Dictionary<string, List<Expr>>();
        /// <summary>Hoisted variables bound inside expressions or patterns (walrus, match captures): their values, null when unknown.</summary>
        public readonly Dictionary<string, List<Expr>> HoistedCaptures = new Dictionary<string, List<Expr>>();
        /// <summary>Hoisted variables bound only by plain assignments (name = value): their type can come from what is assigned.</summary>
        public readonly HashSet<string> HoistedPlain = new HashSet<string>();
        public readonly Dictionary<string, Expr> Annotations = new Dictionary<string, Expr>();
        public readonly HashSet<string> AllLocals = new HashSet<string>();
        public readonly HashSet<string> AssignedParameters = new HashSet<string>();
        public readonly HashSet<string> Globals = new HashSet<string>();
        public readonly HashSet<string> LocalFunctions = new HashSet<string>();
        public bool HasYield;
        public bool HasAwait;
        public bool HasValueReturn;

        public bool IsInline(Node stmt, string name) => InlineDecl.TryGetValue(stmt, out var set) && set.Contains(name);
    }

    internal sealed class LocalsAnalyzer
    {
        /// <summary>ExprStore: assigned inside an expression or a match pattern (walrus, case capture): never declares inline, but its value types a hoisted variable.</summary>
        enum Kind { Load, DeclStore, AnnDecl, LoopTarget, WithTarget, OtherStore, ExprStore, FunctionStore }

        sealed class Occ
        {
            public string Name; public List<object> Path; public int Order; public Kind Kind; public Node Stmt; public Expr Value;
            public ForStmt Loop; // innermost enclosing for-loop whose target binds this name
        }

        readonly List<Occ> _occs = new List<Occ>();
        readonly LocalsResult _r = new LocalsResult();
        readonly HashSet<string> _params;
        readonly HashSet<string> _excluded;
        int _order;
        readonly Stack<HashSet<string>> _shadow = new Stack<HashSet<string>>();

        LocalsAnalyzer(IEnumerable<string> parameters, IEnumerable<string> excluded)
        {
            _params = new HashSet<string>(parameters);
            _excluded = new HashSet<string>(excluded ?? Enumerable.Empty<string>());
        }

        public static LocalsResult Analyze(List<Stmt> body, IEnumerable<string> parameters, IEnumerable<string> excluded)
        {
            var a = new LocalsAnalyzer(parameters, excluded);
            a.Walk(body, new List<object> { body });
            a.Decide(body);
            return a._r;
        }

        // enclosing for-loops and the names their targets bind (innermost last)
        readonly List<KeyValuePair<ForStmt, HashSet<string>>> _loops = new List<KeyValuePair<ForStmt, HashSet<string>>>();

        void Add(string name, List<object> path, Kind kind, Node stmt, Expr value = null)
        {
            foreach (var s in _shadow) if (s.Contains(name)) return;
            ForStmt binding = null;
            for (int i = _loops.Count - 1; i >= 0; i--)
                if (_loops[i].Value.Contains(name)) { binding = _loops[i].Key; break; }
            if (kind == Kind.LoopTarget) binding = stmt as ForStmt;
            _occs.Add(new Occ { Name = name, Path = path, Order = _order++, Kind = kind, Stmt = stmt, Value = value, Loop = binding });
        }

        static List<object> Sub(List<object> path, object block) { var p = new List<object>(path) { block }; return p; }

        void Walk(List<Stmt> stmts, List<object> path)
        {
            foreach (var s in stmts) WalkStmt(s, path);
        }

        void WalkStmt(Stmt s, List<object> path)
        {
            switch (s)
            {
                case AssignStmt a:
                    Loads(a.Value, path);
                    foreach (var t in a.Targets) Target(t, path, Kind.DeclStore, a, a.Targets.Count == 1 ? a.Value : null);
                    break;
                case AugAssignStmt aa:
                    Loads(aa.Value, path);
                    if (aa.Target is NameExpr an) { Add(an.Id, path, Kind.Load, aa); Add(an.Id, path, Kind.OtherStore, aa); }
                    else Loads(aa.Target, path);
                    break;
                case AnnAssignStmt ann:
                    if (ann.Value != null) Loads(ann.Value, path);
                    if (ann.Target is NameExpr n)
                    {
                        if (!_r.Annotations.ContainsKey(n.Id)) _r.Annotations[n.Id] = ann.Annotation;
                        Add(n.Id, path, ann.Value != null ? Kind.DeclStore : Kind.AnnDecl, ann, ann.Value);
                    }
                    else Loads(ann.Target, path);
                    break;
                case ExprStmt e: Loads(e.Value, path); break;
                case ReturnStmt r:
                    if (r.Value != null) { _r.HasValueReturn = true; Loads(r.Value, path); }
                    break;
                case RaiseStmt r: Loads(r.Exc, path); Loads(r.Cause, path); break;
                case AssertStmt a: Loads(a.Test, path); Loads(a.Msg, path); break;
                case DelStmt d: foreach (var t in d.Targets) Loads(t, path); break;
                case GlobalStmt g: foreach (var gn in g.Names) _r.Globals.Add(gn); break;
                case IfStmt i:
                    Loads(i.Test, path);
                    Walk(i.Body, Sub(path, i.Body));
                    Walk(i.OrElse, Sub(path, i.OrElse));
                    break;
                case WhileStmt w:
                    Loads(w.Test, path);
                    Walk(w.Body, Sub(path, w.Body));
                    Walk(w.OrElse, Sub(path, w.OrElse));
                    break;
                case ForStmt f:
                    if (f.IsAsync) _r.HasAwait = true;
                    Loads(f.Iter, path);
                    Target(f.Target, Sub(path, f.Body), Kind.LoopTarget, f, null);
                    var loopNames = new HashSet<string>();
                    AstWalker.VisitAll(f.Target, n => { if (n is NameExpr ne) loopNames.Add(ne.Id); });
                    _loops.Add(new KeyValuePair<ForStmt, HashSet<string>>(f, loopNames));
                    Walk(f.Body, Sub(path, f.Body));
                    _loops.RemoveAt(_loops.Count - 1);
                    Walk(f.OrElse, Sub(path, f.OrElse));
                    break;
                case WithStmt w:
                    if (w.IsAsync) _r.HasAwait = true;
                    foreach (var item in w.Items)
                    {
                        Loads(item.Context, path);
                        if (item.Var != null) Target(item.Var, Sub(path, w.Body), Kind.WithTarget, w, null);
                    }
                    Walk(w.Body, Sub(path, w.Body));
                    break;
                case TryStmt t:
                    Walk(t.Body, Sub(path, t.Body));
                    foreach (var h in t.Handlers)
                    {
                        Loads(h.Type, path);
                        if (h.Name != null) _shadow.Push(new HashSet<string> { h.Name });
                        Walk(h.Body, Sub(path, h.Body));
                        if (h.Name != null) _shadow.Pop();
                    }
                    Walk(t.OrElse, Sub(path, t.OrElse));
                    Walk(t.FinalBody, Sub(path, t.FinalBody));
                    break;
                case MatchStmt m:
                    Loads(m.Subject, path);
                    foreach (var c in m.Cases)
                    {
                        var casePath = Sub(path, c.Body);
                        foreach (var cap in PatternCaptures(c.Pattern, m.Subject)) Add(cap.Key, casePath, Kind.ExprStore, m, cap.Value);
                        AstWalker.VisitAll(PatternExprs(c.Pattern), n => { if (n is NameExpr ne) Add(ne.Id, path, Kind.Load, null); });
                        Loads(c.Guard, casePath);
                        Walk(c.Body, casePath);
                    }
                    break;
                case FunctionDef fd:
                    _r.LocalFunctions.Add(fd.Name);
                    foreach (var p in fd.Params) Loads(p.Default, path);
                    Add(fd.Name, path, Kind.FunctionStore, fd);
                    foreach (var d in fd.Decorators) Loads(d, path);
                    // free variables of the nested function count as uses at the definition site
                    var inner = Analyze(fd.Body, fd.Params.Select(p => p.Name), null);
                    var innerBound = new HashSet<string>(inner.AllLocals);
                    innerBound.UnionWith(fd.Params.Select(p => p.Name));
                    innerBound.UnionWith(inner.LocalFunctions);
                    foreach (var name in FreeNames(fd.Body))
                        if (!innerBound.Contains(name) || inner.Globals.Contains(name)) Add(name, path, Kind.Load, fd);
                    break;
                case ImportStmt _:
                case ImportFromStmt _:
                case PassStmt _:
                case BreakStmt _:
                case ContinueStmt _:
                    break;
                case ClassDef cls:
                    foreach (var parent in cls.Bases) Loads(parent, path);
                    foreach (var field in cls.Body.OfType<AssignStmt>()) Loads(field.Value, path);
                    Add(cls.Name, path, Kind.DeclStore, cls);
                    break;
            }
        }

        /// <summary>The names a pattern binds, each with an expression for its value (for typing; null when unknown).</summary>
        internal static List<KeyValuePair<string, Expr>> PatternCaptures(Pattern p, Expr subject)
        {
            var result = new List<KeyValuePair<string, Expr>>();
            void Visit(Pattern q, Expr value)
            {
                switch (q)
                {
                    case CapturePattern c: if (c.Name != null) result.Add(new KeyValuePair<string, Expr>(c.Name, value)); break;
                    case AsPattern a: Visit(a.Inner, value); result.Add(new KeyValuePair<string, Expr>(a.Name, value)); break;
                    case OrPattern o: foreach (var alt in o.Alternatives) Visit(alt, value); break;
                    case SequencePattern s:
                        for (int i = 0; i < s.Items.Count; i++)
                        {
                            Expr item = value == null || s.StarIndex >= 0 && i >= s.StarIndex ? null
                                : new SubscriptExpr { Target = value, Index = new ConstExpr { Kind = ConstKind.Int, Raw = i.ToString() }, Line = q.Line, Col = q.Col };
                            Visit(s.Items[i], item);
                        }
                        if (s.StarName != null) result.Add(new KeyValuePair<string, Expr>(s.StarName, null));
                        break;
                    case MappingPattern m:
                        foreach (var v in m.Values) Visit(v, null);
                        if (m.Rest != null) result.Add(new KeyValuePair<string, Expr>(m.Rest, null));
                        break;
                    case ClassPattern c:
                    {
                        // the subject as an instance of the class (for typing only: int(n) → n is an int, Point(x=a) → a has x's type)
                        Expr asCls = new CallExpr { Func = c.Cls, Line = q.Line, Col = q.Col };
                        if (IsBuiltinTypeName(c.Cls) && value != null) asCls = new CallExpr { Func = c.Cls, Args = { new Arg { Value = value } }, Line = q.Line, Col = q.Col };
                        for (int i = 0; i < c.Positional.Count; i++)
                            Visit(c.Positional[i], IsBuiltinTypeName(c.Cls) ? asCls : new AttributeExpr { Target = asCls, Name = MatchPositionPrefix + i, Line = q.Line, Col = q.Col });
                        for (int i = 0; i < c.KeywordPatterns.Count; i++)
                            Visit(c.KeywordPatterns[i], new AttributeExpr { Target = asCls, Name = c.KeywordNames[i], Line = q.Line, Col = q.Col });
                        break;
                    }
                }
            }
            Visit(p, subject);
            return result;
        }

        /// <summary>Attribute name standing for "positional sub-pattern i" of a class pattern (resolved through __match_args__ when typing).</summary>
        internal const string MatchPositionPrefix = "__match_pos_";

        static bool IsBuiltinTypeName(Expr e) => e is NameExpr n && (n.Id == "int" || n.Id == "float" || n.Id == "str" || n.Id == "bool" || n.Id == "list" || n.Id == "dict" || n.Id == "tuple" || n.Id == "set");

        /// <summary>The expressions a pattern reads (values like Color.RED, class names, mapping keys).</summary>
        internal static List<Expr> PatternExprs(Pattern p)
        {
            var result = new List<Expr>();
            void Visit(Pattern q)
            {
                switch (q)
                {
                    case ValuePattern v: result.Add(v.Value); break;
                    case AsPattern a: Visit(a.Inner); break;
                    case OrPattern o: foreach (var alt in o.Alternatives) Visit(alt); break;
                    case SequencePattern s: foreach (var i in s.Items) Visit(i); break;
                    case MappingPattern m: result.AddRange(m.Keys); foreach (var v in m.Values) Visit(v); break;
                    case ClassPattern c: result.Add(c.Cls); foreach (var v in c.Positional) Visit(v); foreach (var v in c.KeywordPatterns) Visit(v); break;
                }
            }
            Visit(p);
            return result;
        }

        static IEnumerable<string> FreeNames(List<Stmt> body)
        {
            var names = new HashSet<string>();
            AstWalker.VisitAll(body, n => { if (n is NameExpr ne) names.Add(ne.Id); });
            return names;
        }

        void Target(Expr t, List<object> path, Kind kind, Node stmt, Expr value)
        {
            switch (t)
            {
                case NameExpr n: Add(n.Id, path, kind, stmt, value); break;
                case TupleExpr tup:
                    var valueTuple = value as TupleExpr;
                    for (int i = 0; i < tup.Elts.Count; i++)
                        Target(tup.Elts[i], path, kind, stmt, valueTuple != null && i < valueTuple.Elts.Count ? valueTuple.Elts[i] : null);
                    break;
                case ListExpr list: foreach (var e in list.Elts) Target(e, path, kind, stmt, null); break;
                case StarredExpr st: Target(st.Value, path, kind, stmt, null); break;
                default: Loads(t, path); break; // attribute / subscript targets only read names
            }
        }

        void Loads(Expr e, List<object> path)
        {
            if (e == null) return;
            switch (e)
            {
                case NameExpr n: Add(n.Id, path, Kind.Load, null); return;
                case LambdaExpr l:
                    foreach (var value in l.Defaults) Loads(value, path);
                    bool hasYield = _r.HasYield;
                    _shadow.Push(new HashSet<string>(l.ParameterNames()));
                    Loads(l.Body, path);
                    _shadow.Pop();
                    _r.HasYield = hasYield;
                    return;
                case ComprehensionExpr c:
                    var bound = new HashSet<string>(); // grows as generators bind targets
                    _shadow.Push(bound);
                    foreach (var g in c.Generators)
                    {
                        Loads(g.Iter, path);
                        AstWalker.VisitAll(g.Target, n => { if (n is NameExpr ne) bound.Add(ne.Id); });
                        foreach (var cond in g.Ifs) Loads(cond, path);
                        if (g.IsAsync) _r.HasAwait = true;
                    }
                    Loads(c.Elt, path);
                    Loads(c.ValueElt, path);
                    _shadow.Pop();
                    return;
                case AwaitExpr a: _r.HasAwait = true; Loads(a.Value, path); return;
                case NamedExpr ne:
                    Loads(ne.Value, path);
                    // the target of ':=' belongs to the enclosing function, also inside a comprehension
                    var shadowed = _shadow.ToArray();
                    _shadow.Clear();
                    Add(ne.Target.Id, path, Kind.ExprStore, null, ne.Value);
                    for (int si = shadowed.Length - 1; si >= 0; si--) _shadow.Push(shadowed[si]);
                    return;
                case YieldExpr y: _r.HasYield = true; Loads(y.Value, path); return;
            }
            foreach (var child in AstWalker.Children(e)) Loads(child as Expr, path);
        }

        void Decide(List<Stmt> body)
        {
            var byName = new Dictionary<string, List<Occ>>();
            foreach (var o in _occs)
            {
                if (!byName.TryGetValue(o.Name, out var list)) byName[o.Name] = list = new List<Occ>();
                list.Add(o);
            }
            foreach (var kv in byName)
            {
                string name = kv.Key;
                var occs = kv.Value;
                if (!occs.Any(o => o.Kind != Kind.Load)) continue;              // only read → not a local
                if (_params.Contains(name)) { _r.AssignedParameters.Add(name); continue; }
                if (_excluded.Contains(name) || _r.Globals.Contains(name)) continue;
                if (_r.LocalFunctions.Contains(name))
                {
                    // A single unconditional def can be a CLR local function. Conditional or rebound names are variables.
                    var stores = occs.Where(o => o.Kind != Kind.Load).ToList();
                    if (stores.Count == 1 && stores[0].Kind == Kind.FunctionStore && stores[0].Path.Count == 1) continue;
                    _r.AllLocals.Add(name);
                    _r.Hoisted.Add(new KeyValuePair<string, Expr>(name, null));
                    continue;
                }
                _r.AllLocals.Add(name);

                var lca = occs[0].Path;
                foreach (var o in occs) lca = CommonPrefix(lca, o.Path);
                var first = occs.OrderBy(o => o.Order).First();
                bool declaring = first.Kind == Kind.DeclStore || first.Kind == Kind.AnnDecl || first.Kind == Kind.LoopTarget || first.Kind == Kind.WithTarget;
                if (declaring && first.Path.Count == lca.Count)
                {
                    if (!_r.InlineDecl.TryGetValue(first.Stmt, out var set)) _r.InlineDecl[first.Stmt] = set = new HashSet<string>();
                    set.Add(name);
                }
                else if (TrySplitIndependentUses(occs, lca))
                {
                    // e.g. `for w in a: ...` then `for w in b: ...`, or `b = xs[i]` in one loop and `for b in xs` in another:
                    // independent bindings, each gets its own (typed) C# variable instead of one hoisted 'dynamic'
                }
                else
                {
                    _r.Annotations.TryGetValue(name, out var ann);
                    _r.Hoisted.Add(new KeyValuePair<string, Expr>(name, ann));
                    var firstStore = occs.Where(o => (o.Kind == Kind.DeclStore || o.Kind == Kind.ExprStore) && o.Value != null).OrderBy(o => o.Order).FirstOrDefault();
                    if (firstStore != null) _r.HoistedFirstValue[name] = firstStore.Value;
                    else if (ann == null && occs.All(o => o.Kind == Kind.Load || o.Kind == Kind.LoopTarget || o.Kind == Kind.OtherStore))
                        _r.HoistedLoops[name] = occs.Where(o => o.Kind == Kind.LoopTarget && o.Stmt is ForStmt fs && fs.Target is NameExpr)
                                                    .Select(o => (ForStmt)o.Stmt).Distinct().ToList();
                    _r.HoistedValues[name] = occs.Where(o => (o.Kind == Kind.DeclStore || o.Kind == Kind.ExprStore) && o.Value != null).Select(o => o.Value).ToList();
                    if (occs.Any(o => o.Kind == Kind.ExprStore)) _r.HoistedCaptures[name] = occs.Where(o => o.Kind == Kind.ExprStore).Select(o => o.Value).ToList();
                    if (ann == null && occs.All(o => o.Kind == Kind.Load || o.Kind == Kind.DeclStore && o.Value != null && o.Stmt is AssignStmt pa &&
                                                     pa.Targets.All(t => t is NameExpr tn && tn.Id == name)))
                        _r.HoistedPlain.Add(name);
                }
            }
        }

        /// <summary>
        /// Live-range splitting light: if no use sits directly in the common block, group the uses by the child block they
        /// live in. When every group starts with an unconditional assignment at its own top level, the groups never see
        /// each other's values, so each group can declare its own variable.
        /// </summary>
        bool TrySplitIndependentUses(List<Occ> occs, List<object> lca)
        {
            if (occs.Any(o => o.Path.Count <= lca.Count)) return false;
            var groups = occs.GroupBy(o => o.Path[lca.Count]).ToList();
            if (groups.Count < 2) return false;
            var decls = new List<Node>();
            foreach (var g in groups)
            {
                var list = g.ToList();
                var glca = list[0].Path;
                foreach (var o in list) glca = CommonPrefix(glca, o.Path);
                var first = list.OrderBy(o => o.Order).First();
                bool declaring = first.Kind == Kind.DeclStore || first.Kind == Kind.AnnDecl || first.Kind == Kind.LoopTarget || first.Kind == Kind.WithTarget;
                if (!declaring || first.Path.Count != glca.Count) return false;
                decls.Add(first.Stmt);
            }
            string name = occs[0].Name;
            foreach (var stmt in decls)
            {
                if (!_r.InlineDecl.TryGetValue(stmt, out var set)) _r.InlineDecl[stmt] = set = new HashSet<string>();
                set.Add(name);
            }
            return true;
        }

        static List<object> CommonPrefix(List<object> a, List<object> b)
        {
            int n = 0;
            while (n < a.Count && n < b.Count && ReferenceEquals(a[n], b[n])) n++;
            return a.Take(n).ToList();
        }
    }

    internal static class AstWalker
    {
        public static void VisitAll(object root, Action<Node> visit)
        {
            if (root == null) return;
            if (root is System.Collections.IEnumerable list && !(root is string))
            {
                foreach (var item in list) VisitAll(item, visit);
                return;
            }
            if (root is Node n)
            {
                visit(n);
                foreach (var c in Children(n)) VisitAll(c, visit);
            }
        }

        /// <summary>Direct children of a node (expressions, statements and nested lists).</summary>
        public static IEnumerable<object> Children(Node n)
        {
            switch (n)
            {
                case AttributeExpr a: yield return a.Target; break;
                case CallExpr c: yield return c.Func; foreach (var arg in c.Args) yield return arg.Value; break;
                case SubscriptExpr s: yield return s.Target; yield return s.Index; break;
                case SliceExpr s: yield return s.Lower; yield return s.Upper; yield return s.Step; break;
                case BinOpExpr b: yield return b.Left; yield return b.Right; break;
                case UnaryExpr u: yield return u.Operand; break;
                case BoolOpExpr b: foreach (var v in b.Values) yield return v; break;
                case CompareExpr c: yield return c.Left; foreach (var v in c.Comparators) yield return v; break;
                case IfExpr i: yield return i.Test; yield return i.Body; yield return i.OrElse; break;
                case LambdaExpr l: foreach (var value in l.Defaults) yield return value; yield return l.Body; break;
                case TupleExpr t: foreach (var v in t.Elts) yield return v; break;
                case ListExpr t: foreach (var v in t.Elts) yield return v; break;
                case SetExpr t: foreach (var v in t.Elts) yield return v; break;
                case DictExpr d: foreach (var v in d.Keys) if (v != null) yield return v; foreach (var v in d.Values) yield return v; break;
                case ComprehensionExpr c:
                    yield return c.Elt; yield return c.ValueElt;
                    foreach (var g in c.Generators) { yield return g.Target; yield return g.Iter; foreach (var i in g.Ifs) yield return i; }
                    break;
                case AwaitExpr a: yield return a.Value; break;
                case YieldExpr y: yield return y.Value; break;
                case StarredExpr s: yield return s.Value; break;
                case NamedExpr ne: yield return ne.Target; yield return ne.Value; break;
                case MatchStmt m:
                    yield return m.Subject;
                    foreach (var c in m.Cases) { yield return LocalsAnalyzer.PatternExprs(c.Pattern); yield return c.Guard; yield return c.Body; }
                    break;
                case FStringExpr f: foreach (var p in f.Parts) if (p.Expr != null) yield return p.Expr; break;

                case ExprStmt e: yield return e.Value; break;
                case AssignStmt a: foreach (var t in a.Targets) yield return t; yield return a.Value; break;
                case AugAssignStmt a: yield return a.Target; yield return a.Value; break;
                case AnnAssignStmt a: yield return a.Target; yield return a.Value; break;
                case ReturnStmt r: yield return r.Value; break;
                case RaiseStmt r: yield return r.Exc; yield return r.Cause; break;
                case AssertStmt a: yield return a.Test; yield return a.Msg; break;
                case DelStmt d: foreach (var t in d.Targets) yield return t; break;
                case IfStmt i: yield return i.Test; yield return i.Body; yield return i.OrElse; break;
                case WhileStmt w: yield return w.Test; yield return w.Body; yield return w.OrElse; break;
                case ForStmt f: yield return f.Target; yield return f.Iter; yield return f.Body; yield return f.OrElse; break;
                case WithStmt w: foreach (var it in w.Items) { yield return it.Context; yield return it.Var; } yield return w.Body; break;
                case TryStmt t: yield return t.Body; foreach (var h in t.Handlers) yield return h; yield return t.OrElse; yield return t.FinalBody; break;
                case ExceptHandler h: yield return h.Type; yield return h.Body; break;
                case FunctionDef f: foreach (var p in f.Params) { yield return p.Default; } yield return f.Body; break;
                case ClassDef c:
                    yield return c.Metaclass;
                    yield return c.ClassKeywords.Select(argument => argument.Value);
                    yield return c.Body;
                    break;
            }
        }

        /// <summary>True if the statements contain the node type outside nested functions/lambdas/classes.</summary>
        public static bool ContainsOutsideNestedScopes(List<Stmt> body, Func<Node, bool> pred)
        {
            bool found = false;
            void Visit(object o)
            {
                if (found || o == null) return;
                if (o is System.Collections.IEnumerable list && !(o is string)) { foreach (var i in list) Visit(i); return; }
                if (!(o is Node n)) return;
                if (n is FunctionDef || n is ClassDef || n is LambdaExpr) return;
                if (pred(n)) { found = true; return; }
                foreach (var c in Children(n)) Visit(c);
            }
            Visit(body);
            return found;
        }
    }
}
