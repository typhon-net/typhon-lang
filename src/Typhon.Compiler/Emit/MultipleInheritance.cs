// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    /// <summary>
    /// Multiple inheritance on a single-inheritance runtime. A class with several bases keeps one of them as its C# base
    /// class (the primary: the .NET class if there is one, else a named tuple base or the base with the longest
    /// ancestry), and the members of the other classes in its method resolution order (C3, as Python
    /// computes it) are copied into it before inference: the winning definition of each name under its own name, and every
    /// method of those classes under a hidden name (__mro_Class_name) that super() and Class.method(self, ...) calls reach
    /// in MRO order. The class then behaves as in Python for the common shapes (mixins before or after the real base,
    /// cooperative super() chains whose classes call super().__init__ first); isinstance(x, Mixin) is also true for the
    /// classes Mixin was copied into.
    /// </summary>
    internal static class MultipleInheritance
    {
        static readonly HashSet<string> MarkerBases = new HashSet<string>
        {
            "object", "Protocol", "typing.Protocol", "Generic", "typing.Generic", "ABC", "abc.ABC",
        };

        public static string Hidden(ClassDef k, string name) => "__mro_" + k.Name + "_" + name;
        /// <summary>A base-chain class's method called past the classes that override it (its body, under another name).</summary>
        public static string Direct(ClassDef k, string name) => "_direct_" + k.Name + "_" + name;
        public static bool IsGenerated(string name) => name.StartsWith("__mro_") || name.StartsWith("_super_") || name.StartsWith("_direct_");

        sealed class Context
        {
            public ProjectContext Project;
            public readonly Dictionary<ClassDef, ModuleInfo> Owner = new Dictionary<ClassDef, ModuleInfo>();
            public readonly Dictionary<ClassDef, List<object>> Mro = new Dictionary<ClassDef, List<object>>();
            public readonly Dictionary<ClassDef, List<(Expr Expr, object Node)>> RealBases = new Dictionary<ClassDef, List<(Expr Expr, object Node)>>();
        }

        public static void Apply(ProjectContext p)
        {
            var cx = new Context { Project = p };
            foreach (var m in p.Modules.Values)
                if (m.Ast != null && m.ParseDiagnostics.Count == 0)
                    foreach (var c in AllClasses(m.Ast.Body)) cx.Owner[c] = m;
            // every class's real bases and MRO, from the bases as written (before any class is rewritten)
            foreach (var kv in cx.Owner)
                cx.RealBases[kv.Key] = kv.Key.Bases.Where(b => !MarkerBases.Contains(DottedName(b is SubscriptExpr s ? s.Target : b) ?? ""))
                                                   .Select(b => (b, (object)ResolveClass(cx, kv.Value, b) ?? ("opaque:" + DottedName(b is SubscriptExpr s2 ? s2.Target : b))))
                                                   .ToList();
            foreach (var c in cx.Owner.Keys) Linearize(cx, c, new HashSet<ClassDef>());
            foreach (var c in cx.Owner.Keys.Where(c => cx.RealBases[c].Count >= 2).ToList()) Flatten(cx, c);
        }

        static IEnumerable<ClassDef> AllClasses(IEnumerable<Stmt> body)
        {
            foreach (var s in body)
                if (s is ClassDef c)
                {
                    yield return c;
                    foreach (var n in AllClasses(c.Body)) yield return n;
                }
        }

        static string DottedName(Expr e) => e is NameExpr n ? n.Id : e is AttributeExpr a && DottedName(a.Target) is string t ? t + "." + a.Name : null;

        /// <summary>The Typhon class a base expression names (in its module, imported from a project module), or null.</summary>
        static ClassDef ResolveClass(Context cx, ModuleInfo m, Expr b)
        {
            if (b is SubscriptExpr s) b = s.Target;
            if (b is NameExpr n)
            {
                if (m.Classes.TryGetValue(n.Id, out var local)) return local;
                foreach (var imp in m.Ast.Body.OfType<ImportFromStmt>())
                    foreach (var a in imp.Names)
                        if ((a.AsName ?? a.Name) == n.Id && cx.Project.FindModule(Absolute(m, imp)) is ModuleInfo from && from.Classes.TryGetValue(a.Name, out var imported))
                            return imported;
            }
            if (b is AttributeExpr at && at.Target is NameExpr mn)
                foreach (var imp in m.Ast.Body.OfType<ImportStmt>())
                    foreach (var al in imp.Names)
                        if ((al.AsName ?? al.Name) == mn.Id && cx.Project.FindModule(al.Name) is ModuleInfo mod && mod.Classes.TryGetValue(at.Name, out var c))
                            return c;
            return null;
        }

        static string Absolute(ModuleInfo m, ImportFromStmt imp)
            => m.ResolveImport(imp.Module, imp.Level);

        /// <summary>C3 linearization (Python's MRO): the class, then a merge of its bases' linearizations and the bases. Null if none exists.</summary>
        static List<object> Linearize(Context cx, ClassDef c, HashSet<ClassDef> visiting)
        {
            if (cx.Mro.TryGetValue(c, out var known)) return known;
            if (!visiting.Add(c)) return null;
            var seqs = new List<List<object>>();
            var bases = cx.RealBases.TryGetValue(c, out var rb) ? rb : new List<(Expr Expr, object Node)>();
            foreach (var (_, node) in bases)
            {
                var l = node is ClassDef bc ? Linearize(cx, bc, visiting) : new List<object> { node };
                if (l == null) { visiting.Remove(c); return null; }
                seqs.Add(new List<object>(l));
            }
            seqs.Add(bases.Select(b => b.Node).ToList());
            var result = new List<object> { c };
            while (seqs.Any(q => q.Count > 0))
            {
                object head = null;
                foreach (var q in seqs.Where(q => q.Count > 0))
                    if (!seqs.Any(other => other.Skip(1).Contains(q[0]))) { head = q[0]; break; }
                if (head == null) { visiting.Remove(c); return null; } // no consistent order (Python raises TypeError)
                result.Add(head);
                foreach (var q in seqs) if (q.Count > 0 && Equals(q[0], head)) q.RemoveAt(0);
            }
            visiting.Remove(c);
            cx.Mro[c] = result;
            return result;
        }

        static bool Defines(object k, string name) => k is ClassDef kd && kd.Body.Any(s => s is FunctionDef f && f.Name == name);
        static bool DefinesAttribute(ClassDef k, string name) =>
            k.Body.Any(s => s is FunctionDef f && f.Name == name || s is AnnAssignStmt a && a.Target is NameExpr an && an.Id == name ||
                            s is AssignStmt asg && asg.Targets.Any(t => t is NameExpr tn && tn.Id == name));

        static void Flatten(Context cx, ClassDef c)
        {
            var m = cx.Owner[c];
            var mro = cx.Mro.TryGetValue(c, out var l) ? l : null;
            if (mro == null) return; // (Python's TypeError: Cannot create a consistent method resolution order; C# then reports the bases)
            var bases = cx.RealBases[c];
            var opaque = bases.Where(b => !(b.Node is ClassDef)).ToList();
            if (opaque.Count > 1) return; // two .NET base classes: nothing to copy them from
            // Keep the tuple layout when a fixed-field tuple also inherits mixin methods.
            // Otherwise prefer the longest ancestry, a real constructor, a non-mixin name, and source order.
            var primary = opaque.Count == 1 ? opaque[0]
                : bases.Select((b, i) => (b, i))
                       .OrderByDescending(x => cx.Mro.TryGetValue((ClassDef)x.b.Node, out var ancestry) &&
                            ancestry.OfType<ClassDef>().Any(basis => basis.NamedTupleFields != null))
                       .ThenByDescending(x => cx.Mro.TryGetValue((ClassDef)x.b.Node, out var pl) ? pl.Count : 0)
                       .ThenByDescending(x => cx.Mro.TryGetValue((ClassDef)x.b.Node, out var pm) && pm.OfType<ClassDef>().Any(HasRealInit))
                       .ThenByDescending(x => !((ClassDef)x.b.Node).Name.EndsWith("Mixin"))
                       .ThenBy(x => x.i).First().b;
            var primaryMro = primary.Node is ClassDef pcd && cx.Mro.TryGetValue(pcd, out var pml) ? pml : new List<object> { primary.Node };
            var flattened = mro.Skip(1).OfType<ClassDef>().Where(k => !primaryMro.Contains(k)).ToList();

            // names a copied method uses from its own module must exist where it is copied to
            foreach (var k in flattened.Where(k => cx.Owner[k] != m))
            {
                var km = cx.Owner[k];
                var missing = new SortedSet<string>();
                foreach (var f in k.Body.OfType<FunctionDef>())
                    AstWalker.VisitAll(f.Body, node =>
                    {
                        if (node is NameExpr n && (km.Functions.Contains(n.Id) || km.Classes.ContainsKey(n.Id) || km.Globals.Contains(n.Id)) &&
                            !(m.Functions.Contains(n.Id) || m.Classes.ContainsKey(n.Id) || m.Globals.Contains(n.Id) || ImportsName(m, n.Id)))
                            missing.Add(n.Id);
                    });
                if (missing.Count > 0)
                {
                    m.ParseDiagnostics.Add(new TyDiagnostic
                    {
                        Id = "TY0003", Severity = Severity.Error, Path = m.Path, Line = c.Line, Col = c.Col,
                        Message = $"class '{c.Name}' inherits from '{k.Name}' (module {km.Name}), whose methods use {string.Join(", ", missing.Select(x => "'" + x + "'"))} " +
                                  $"of its module: import {(missing.Count == 1 ? "it" : "them")} here as well (multiple inheritance copies those methods into '{c.Name}')",
                    });
                    return;
                }
            }

            var added = new List<Stmt>();
            object Winner(string name) => mro.FirstOrDefault(k => Defines(k, name));
            foreach (var k in flattened)
            {
                var methods = k.Body.OfType<FunctionDef>().Where(method => !IsGenerated(method.Name)).ToLookup(method => method.Name);
                foreach (var group in methods)
                {
                    string name = group.Key;
                    var defs = group.ToList();
                    if (Equals(Winner(name), k) && !Defines(c, name) && name != "__init__")
                        foreach (var d in defs) added.Add(CopyMethod(d, d.Name, k, c));
                    // every plain method also under its hidden name: what super() / Class.method(self) calls reach
                    if (defs.Count == 1 && defs[0].Decorators.Count == 0)
                    {
                        var hidden = CopyMethod(defs[0], Hidden(k, name), k, c);
                        if (name == "__init__" && !PrepareInit(m, c, k, hidden, mro, flattened)) return;
                        added.Add(hidden);
                    }
                }
                // class-level fields and class variables the class does not define itself
                foreach (var s in k.Body)
                {
                    string target = s is AnnAssignStmt a && a.Target is NameExpr an ? an.Id : s is AssignStmt asg && asg.Targets.Count == 1 && asg.Targets[0] is NameExpr tn ? tn.Id : null;
                    if (target == null || DefinesAttribute(c, target) || added.Any(x => x is AnnAssignStmt xa && xa.Target is NameExpr xn && xn.Id == target ||
                                                                                        x is AssignStmt xs && xs.Targets[0] is NameExpr xt && xt.Id == target))
                        continue;
                    if (mro.TakeWhile(x => !Equals(x, k)).OfType<ClassDef>().Any(x => DefinesAttribute(x, target))) continue;
                    if (primaryMro.OfType<ClassDef>().Any(x => DefinesAttribute(x, target))) continue;
                    added.Add(AstClone.Clone(s));
                }
                if (!cx.Project.MixedInto.TryGetValue(k, out var list)) cx.Project.MixedInto[k] = list = new List<(ClassDef, ModuleInfo)>();
                list.Add((c, m));
            }

            c.Body.AddRange(added);
            // super() and Class.method(self, ...) in the class's own methods and in the copies, in MRO order
            foreach (var f in c.Body.OfType<FunctionDef>())
            {
                object context = f.Name.StartsWith("__mro_") ? flattened.FirstOrDefault(k => f.Name.StartsWith("__mro_" + k.Name + "_")) ?? (object)c
                               : added.Contains(f) ? flattened.FirstOrDefault(k => Defines(k, f.Name)) ?? (object)c : c;
                RewriteSuperCalls(f, context, mro, flattened, primaryMro, c);
            }
            RunMixinInits(c, flattened, mro, cx, primary.Node);
            CooperativeHooks(c, flattened, mro, primaryMro, cx);
            c.Bases = c.Bases.Where(b => b == primary.Expr || MarkerBases.Contains(DottedName(b is SubscriptExpr s ? s.Target : b) ?? "")).ToList();
        }

        /// <summary>The hook through which a class's super().m(...) reaches the next class in the object's MRO.</summary>
        public static string Hook(ClassDef k, string name) => "_super_" + k.Name + "_" + name;

        /// <summary>
        /// Cooperative super() through the base class chain: when, in this class's MRO, a copied class comes between a base
        /// class X and the class X's super().m(...) reaches on its own (D(B, C) with B(A), C(A): B's super() is C for a D,
        /// A for a B), X calls m through an overridable hook (_super_X_m, which does super().m(...)), and this class
        /// overrides the hook to call the copied class's m. An __init__ chain likewise: X's hook runs right after its
        /// super().__init__(...), and this class's override runs the copied classes' initializers there, innermost first.
        /// </summary>
        static void CooperativeHooks(ClassDef c, List<ClassDef> flattened, List<object> mro, List<object> primaryMro, Context cx)
        {
            foreach (var x in primaryMro.OfType<ClassDef>())
            {
                foreach (var f in x.Body.OfType<FunctionDef>().Where(f => f.Decorators.Count == 0 && f.Params.Count > 0).ToList())
                {
                    string self = f.Params[0].Name;
                    var calls = new List<CallExpr>();
                    AstWalker.VisitAll(f.Body, n => { if (n is CallExpr sc && sc.Func is AttributeExpr sa && IsSuperCall(sa.Target, self)) calls.Add(sc); });
                    foreach (var call in calls)
                    {
                        var a = (AttributeExpr)call.Func;
                        if (a.Name == "__init__") continue;
                        if (!(ProviderAfter(mro, x, a.Name) is ClassDef next) || !flattened.Contains(next)) continue;
                        var target = cx.Mro.TryGetValue(x, out var xm) ? ProviderAfter(xm, x, a.Name) as ClassDef : null;
                        var signature = (target ?? next).Body.OfType<FunctionDef>().First(m => m.Name == a.Name);
                        bool returnsValue = ReturnsValue(signature);
                        string hook = Hook(x, a.Name);
                        if (!x.Body.OfType<FunctionDef>().Any(m => m.Name == hook))
                            x.Body.Add(HookMethod(hook, signature, returnsValue, new AttributeExpr { Target = new CallExpr { Func = new NameExpr { Id = "super" } }, Name = a.Name }, x));
                        a.Target = new NameExpr { Id = self, Line = a.Line, Col = a.Col };
                        a.Name = hook;
                        if (!c.Body.OfType<FunctionDef>().Any(m => m.Name == hook))
                            c.Body.Add(HookMethod(hook, signature, returnsValue, new AttributeExpr { Target = new NameExpr { Id = "self" }, Name = Hidden(next, signature.Name) }, c));
                    }
                }
                // __init__: the copied classes between x and the next base-chain __init__ in this class's MRO
                var init = x.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
                if (init == null || init.Params.Count == 0) continue;
                string s0 = init.Params[0].Name;
                int idx = init.Body.FindIndex(st => st is ExprStmt e && e.Value is CallExpr sc && sc.Func is AttributeExpr sa && sa.Name == "__init__" && IsSuperCall(sa.Target, s0));
                if (idx < 0) continue;
                var between = new List<ClassDef>();
                for (object at = x; ProviderAfter(mro, at, "__init__") is ClassDef k && flattened.Contains(k); at = k)
                {
                    between.Add(k);
                    if (!CallsSuperInit(k.Body.OfType<FunctionDef>().First(m => m.Name == "__init__"))) break;
                }
                if (between.Count == 0) continue;
                string initHook = Hook(x, "__init__");
                if (!x.Body.OfType<FunctionDef>().Any(m => m.Name == initHook))
                {
                    x.Body.Add(new FunctionDef { Name = initHook, Params = { new Param { Name = "self", Line = x.Line, Col = x.Col } }, Body = { new PassStmt { Line = x.Line, Col = x.Col } }, Line = x.Line, Col = x.Col });
                    init.Body.Insert(idx + 1, new ExprStmt
                    {
                        Line = init.Line, Col = init.Col,
                        Value = new CallExpr { Func = new AttributeExpr { Target = new NameExpr { Id = s0 }, Name = initHook }, Line = init.Line, Col = init.Col },
                    });
                }
                if (!c.Body.OfType<FunctionDef>().Any(m => m.Name == initHook))
                    c.Body.Add(new FunctionDef
                    {
                        Name = initHook, Params = { new Param { Name = "self", Line = c.Line, Col = c.Col } }, Line = c.Line, Col = c.Col,
                        Body = Enumerable.Reverse(between).Select(k => (Stmt)new ExprStmt
                        {
                            Line = c.Line, Col = c.Col,
                            Value = new CallExpr { Func = new AttributeExpr { Target = new NameExpr { Id = "self" }, Name = Hidden(k, "__init__") }, Line = c.Line, Col = c.Col },
                        }).ToList(),
                    });
            }
        }

        static bool ReturnsValue(FunctionDef f) =>
            f.Returns != null ? !(f.Returns is ConstExpr rc && rc.Kind == ConstKind.None) : LocalsAnalyzer.Analyze(f.Body, f.Params.Select(p => p.Name), null).HasValueReturn;

        /// <summary>def hook(self, params): [return] callee(params), with the parameters of the method the hook stands for.</summary>
        static FunctionDef HookMethod(string name, FunctionDef signature, bool returnsValue, Expr callee, ClassDef at)
        {
            var ps = new List<Param> { new Param { Name = "self", Line = at.Line, Col = at.Col } };
            var call = new CallExpr { Func = callee, Line = at.Line, Col = at.Col };
            foreach (var p in signature.Params.Skip(1))
            {
                var copy = AstClone.Clone(p);
                ps.Add(copy);
                call.Args.Add(p.Kind == ParamKind.VarArgs ? new Arg { Value = new NameExpr { Id = p.Name }, Star = true }
                            : p.Kind == ParamKind.KwArgs ? new Arg { Value = new NameExpr { Id = p.Name }, DoubleStar = true }
                            : new Arg { Value = new NameExpr { Id = p.Name } });
            }
            Stmt body = returnsValue ? new ReturnStmt { Value = call, Line = at.Line, Col = at.Col } : new ExprStmt { Value = call, Line = at.Line, Col = at.Col };
            return new FunctionDef { Name = name, Params = ps, Returns = AstClone.Clone(signature.Returns), Body = { body }, Line = at.Line, Col = at.Col };
        }

        /// <summary>An __init__ that takes its own parameters (not only *args/**kwargs to pass on).</summary>
        static bool HasRealInit(ClassDef k) =>
            k.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__" && f.Params.Skip(1).Any(p => p.Kind == ParamKind.Normal));

        static bool ImportsName(ModuleInfo m, string name) =>
            m.Ast.Body.OfType<ImportFromStmt>().Any(i => i.Names.Any(a => (a.AsName ?? a.Name) == name || a.Name == "*")) ||
            m.Ast.Body.OfType<ImportStmt>().Any(i => i.Names.Any(a => (a.AsName ?? a.Name.Split('.')[0]) == name));

        /// <summary>A method of <paramref name="from"/> copied into <paramref name="into"/>: its annotations naming the class name the new one.</summary>
        static FunctionDef CopyMethod(FunctionDef d, string name, ClassDef from, ClassDef into)
        {
            var copy = AstClone.Clone(d);
            copy.Name = name;
            foreach (var p in copy.Params) p.Annotation = Retarget(p.Annotation, from, into);
            copy.Returns = Retarget(copy.Returns, from, into);
            return copy;
        }

        static Expr Retarget(Expr annotation, ClassDef from, ClassDef into)
        {
            if (annotation is NameExpr n && n.Id == from.Name) return new NameExpr { Id = into.Name, Line = n.Line, Col = n.Col };
            if (annotation is ConstExpr s && s.Kind == ConstKind.Str && s.Str == from.Name) return new NameExpr { Id = into.Name, Line = s.Line, Col = s.Col };
            return annotation;
        }

        /// <summary>The class after <paramref name="context"/> in the MRO that defines the method (null: none).</summary>
        static object ProviderAfter(List<object> mro, object context, string name)
        {
            int i = mro.IndexOf(context);
            return i < 0 ? null : mro.Skip(i + 1).FirstOrDefault(k => Defines(k, name));
        }

        static bool IsSuperCall(Expr e, string self) =>
            e is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super" && (sc.Args.Count == 0 || sc.Args.Count == 2 && sc.Args[1].Value is NameExpr si && si.Id == self);

        static void RewriteSuperCalls(FunctionDef f, object context, List<object> mro, List<ClassDef> flattened, List<object> primaryMro, ClassDef into)
        {
            if (f.Params.Count == 0) return;
            string self = f.Params[0].Name;
            AstWalker.VisitAll(f.Body, node =>
            {
                if (!(node is CallExpr call) || !(call.Func is AttributeExpr a)) return;
                // super().m(...): the next class in the MRO with m; a copied class's version is its hidden copy
                if (IsSuperCall(a.Target, self))
                {
                    if (a.Name == "__init__") return;
                    var next = ProviderAfter(mro, context, a.Name) as ClassDef;
                    if (next != null && flattened.Contains(next))
                    {
                        a.Target = new NameExpr { Id = self, Line = a.Line, Col = a.Col };
                        a.Name = Hidden(next, a.Name);
                    }
                    // a base-chain class, but another one before it overrides m (C#'s base.m would reach that one): its body,
                    // called directly
                    else if (next != null && !Equals(context, into) && primaryMro.Contains(next) &&
                             primaryMro.TakeWhile(k => !Equals(k, next)).Any(k => Defines(k, a.Name)))
                    {
                        string direct = Direct(next, a.Name);
                        if (!next.Body.OfType<FunctionDef>().Any(m => m.Name == direct))
                        {
                            var copy = AstClone.Clone(next.Body.OfType<FunctionDef>().First(m => m.Name == a.Name));
                            copy.Name = direct;
                            next.Body.Add(copy);
                        }
                        a.Target = new NameExpr { Id = self, Line = a.Line, Col = a.Col };
                        a.Name = direct;
                    }
                    return;
                }
                // Class.m(self, ...) with Class copied into this class: its hidden copy
                if (a.Target is NameExpr cn && call.Args.Count >= 1 && call.Args[0].Name == null && call.Args[0].Value is NameExpr a0 && a0.Id == self &&
                    flattened.FirstOrDefault(k => k.Name == cn.Id) is ClassDef named && Defines(named, a.Name))
                {
                    a.Target = new NameExpr { Id = self, Line = a.Line, Col = a.Col };
                    a.Name = Hidden(named, a.Name);
                    call.Args.RemoveAt(0);
                }
            });
        }

        /// <summary>
        /// A copied class's __init__ as a hidden method: its super().__init__(...) cannot run the C# base class's constructor
        /// from there, so it must be the first statement and only forward (super().__init__(), or *args/**kwargs as received;
        /// the construction then runs it before the copied rest), or reach no further class. False (with an error) otherwise.
        /// </summary>
        static bool PrepareInit(ModuleInfo m, ClassDef c, ClassDef k, FunctionDef hidden, List<object> mro, List<ClassDef> flattened)
        {
            string self = hidden.Params.Count > 0 ? hidden.Params[0].Name : "self";
            int first = hidden.Body.Count > 0 && hidden.Body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? 1 : 0;
            var superInits = new List<Stmt>();
            AstWalker.VisitAll(hidden.Body, n =>
            {
                if (n is CallExpr sc && sc.Func is AttributeExpr sa && sa.Name == "__init__" && IsSuperCall(sa.Target, self)) superInits.Add(null);
            });
            if (superInits.Count == 0) return true;
            if (superInits.Count == 1 && hidden.Body.Count > first && hidden.Body[first] is ExprStmt es && es.Value is CallExpr ic &&
                ic.Func is AttributeExpr ia && ia.Name == "__init__" && IsSuperCall(ia.Target, self) && ForwardsOnly(ic, hidden))
            {
                hidden.Body.RemoveAt(first); // (the construction runs the rest of the chain before this method)
                return true;
            }
            m.ParseDiagnostics.Add(new TyDiagnostic
            {
                Id = "TY0003", Severity = Severity.Error, Path = m.Path, Line = hidden.Line, Col = hidden.Col,
                Message = $"class '{c.Name}' inherits from '{k.Name}', whose __init__ calls super().__init__(...) other than as its first statement " +
                          "passing on its own arguments: not supported with multiple inheritance yet",
            });
            return false;
        }

        static bool ForwardsOnly(CallExpr superInit, FunctionDef f)
        {
            var star = f.Params.FirstOrDefault(p => p.Kind == ParamKind.VarArgs)?.Name;
            var kw = f.Params.FirstOrDefault(p => p.Kind == ParamKind.KwArgs)?.Name;
            return superInit.Args.All(a => a.Star && a.Value is NameExpr sn && sn.Id == star || a.DoubleStar && a.Value is NameExpr kn && kn.Id == kw);
        }

        /// <summary>
        /// The copied classes' __init__ run when the class is constructed, as their super().__init__ chain does in Python:
        /// after the base class's (the class's own super().__init__(...)), innermost first. A class without __init__ gets one
        /// that takes the base class's parameters.
        /// </summary>
        static void RunMixinInits(ClassDef c, List<ClassDef> flattened, List<object> mro, Context cx, object primaryNode)
        {
            var inits = flattened.Where(k => Defines(k, "__init__") && k.Body.OfType<FunctionDef>().Count(f => f.Name == "__init__") == 1).ToList();
            // (only the ones the cooperative chain reaches: a class whose own __init__ does not call super().__init__ stops it)
            var reached = new List<ClassDef>();
            var own = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
            object at = c;
            if (own != null && !CallsSuperInit(own)) return;
            while (ProviderAfter(mro, at, "__init__") is ClassDef next && inits.Contains(next))
            {
                reached.Add(next);
                var nextInit = next.Body.OfType<FunctionDef>().First(f => f.Name == "__init__");
                if (!CallsSuperInit(nextInit)) break;
                at = next;
            }
            if (reached.Count == 0) return;
            if (own == null && reached.Count == 1)
            {
                var initializer = reached[0].Body.OfType<FunctionDef>().First(f => f.Name == "__init__");
                if (!CallsSuperInit(initializer))
                {
                    var target = new AttributeExpr { Target = new NameExpr { Id = "self" }, Name = Hidden(reached[0], "__init__") };
                    var invoke = new CallExpr { Func = target, Line = c.Line, Col = c.Col };
                    foreach (Param parameter in initializer.Params.Skip(1))
                        invoke.Args.Add(new Arg
                        {
                            Value = new NameExpr { Id = parameter.Name },
                            Name = parameter.KeywordOnly ? parameter.Name : null,
                            Star = parameter.Kind == ParamKind.VarArgs,
                            DoubleStar = parameter.Kind == ParamKind.KwArgs,
                        });
                    c.Body.Add(new FunctionDef
                    {
                        Name = "__init__",
                        Params = initializer.Params.Select(parameter => AstClone.Clone(parameter)).ToList(),
                        Body = new List<Stmt> { new ExprStmt { Value = invoke, Line = c.Line, Col = c.Col } },
                        Line = c.Line,
                        Col = c.Col,
                    });
                    return;
                }
            }
            var calls = Enumerable.Reverse(reached).Select(k => (Stmt)new ExprStmt
            {
                Line = c.Line, Col = c.Col,
                Value = new CallExpr { Func = new AttributeExpr { Target = new NameExpr { Id = "self" }, Name = Hidden(k, "__init__") }, Line = c.Line, Col = c.Col },
            }).ToList();
            if (own == null)
            {
                // def __init__(self, <the base class's parameters>): super().__init__(...); then the copied inits
                var baseInit = primaryNode is ClassDef pc && cx.Mro.TryGetValue(pc, out var pm) ? pm.OfType<ClassDef>().Select(k => k.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__")).FirstOrDefault(f => f != null) : null;
                var ps = new List<Param> { new Param { Name = "self", Line = c.Line, Col = c.Col } };
                var superCall = new CallExpr { Func = new AttributeExpr { Target = new CallExpr { Func = new NameExpr { Id = "super" } }, Name = "__init__" }, Line = c.Line, Col = c.Col };
                if (baseInit != null)
                    foreach (var bp in baseInit.Params.Skip(1))
                    {
                        ps.Add(AstClone.Clone(bp));
                        superCall.Args.Add(bp.Kind == ParamKind.VarArgs ? new Arg { Value = new NameExpr { Id = bp.Name }, Star = true }
                                         : bp.Kind == ParamKind.KwArgs ? new Arg { Value = new NameExpr { Id = bp.Name }, DoubleStar = true }
                                         : new Arg { Value = new NameExpr { Id = bp.Name } });
                    }
                var body = new List<Stmt>();
                if (baseInit != null) body.Add(new ExprStmt { Value = superCall, Line = c.Line, Col = c.Col });
                body.AddRange(calls);
                c.Body.Add(new FunctionDef { Name = "__init__", Params = ps, Body = body, Line = c.Line, Col = c.Col });
                return;
            }
            // after the class's own super().__init__(...) statement (C# runs the base constructor first)
            int idx = own.Body.FindIndex(s => s is ExprStmt e && e.Value is CallExpr sc && sc.Func is AttributeExpr sa && sa.Name == "__init__" &&
                                               IsSuperCall(sa.Target, own.Params.Count > 0 ? own.Params[0].Name : "self"));
            // (a super().__init__ that reached a copied class: the base class's constructor runs instead, with no arguments
            // when the copied ones only forward what they get)
            own.Body.InsertRange(idx < 0 ? 0 : idx + 1, calls);
        }

        static bool CallsSuperInit(FunctionDef f)
        {
            string self = f.Params.Count > 0 ? f.Params[0].Name : "self";
            bool found = false;
            AstWalker.VisitAll(f.Body, n => { if (n is CallExpr sc && sc.Func is AttributeExpr sa && sa.Name == "__init__" && IsSuperCall(sa.Target, self)) found = true; });
            return found;
        }
    }
}
