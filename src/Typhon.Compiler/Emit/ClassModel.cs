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
            {
                if (m.Ast == null || m.ParseDiagnostics.Count > 0) continue;
                HoistBlockClasses(m);
                HoistLocalClasses(m);
                SynthesizeNamedTuples(m);
                if (!ShadowsObject(m))
                {
                    foreach (var c in m.Classes.Values) LowerObjectStrAliases(c);
                    LowerSingletonClasses(m);
                }
            }
            // several bases: one stays the C# base, the others' members are copied in, in MRO order
            MultipleInheritance.Apply(p);
            foreach (var m in p.Modules.Values)
            {
                if (m.Ast == null || m.ParseDiagnostics.Count > 0) continue;
                foreach (var c in m.Classes.Values) { DropTypingOnlyMembers(c); NormalizeSelfNames(c); AddMethodAliases(c); NormalizeSuper(c); }
            }
            AddBaseStubs(p);
        }

        internal const string AllocateSingleton = "__typhon_allocate_singleton__";

        static bool ShadowsObject(ModuleInfo m) =>
            m.Classes.ContainsKey("object") || m.Functions.Contains("object") || m.Globals.Contains("object") ||
            m.Ast.Body.Any(s => s is ImportStmt im && im.Names.Any(a => (a.AsName ?? a.Name.Split('.')[0]) == "object") ||
                                s is ImportFromStmt fr && fr.Names.Any(a => (a.AsName ?? a.Name) == "object"));

        /// <summary>Bind object.__str__ as a method that uses the instance's repr, including on subclasses.</summary>
        static void LowerObjectStrAliases(ClassDef c)
        {
            bool shadowsObject = false;
            for (int i = 0; i < c.Body.Count; i++)
            {
                var s = c.Body[i];
                if (s is ClassDef nested) LowerObjectStrAliases(nested);
                if (s is ClassDef { Name: "object" } or FunctionDef { Name: "object" } or AnnAssignStmt { Target: NameExpr { Id: "object" } } ||
                    s is AssignStmt bind && bind.Targets.Any(t => t is NameExpr { Id: "object" }))
                    shadowsObject = true;
                if (shadowsObject || s is not AssignStmt { Value: AttributeExpr { Name: "__str__", Target: NameExpr { Id: "object" } } builtin } a ||
                    a.Targets.Count != 1 || a.Targets[0] is not NameExpr { Id: "__str__" }) continue;

                c.Body[i] = new FunctionDef
                {
                    Name = "__str__", Line = a.Line, Col = a.Col,
                    Params = { new Param { Name = "self", Line = a.Line, Col = a.Col } },
                    Returns = new NameExpr { Id = "str", Line = a.Line, Col = a.Col },
                    Body = { new ReturnStmt
                    {
                        Line = a.Line, Col = a.Col,
                        Value = new CallExpr
                        {
                            Func = builtin, Line = a.Line, Col = a.Col,
                            Args = { new Arg { Value = new NameExpr { Id = "self", Line = a.Line, Col = a.Col } } },
                        },
                    } },
                };
            }
        }

        /// <summary>Apply @object.__new__ to a plain class, keeping the real type behind the singleton's Python name.</summary>
        static void LowerSingletonClasses(ModuleInfo m)
        {
            var body = m.Ast.Body;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i] is not ClassDef c || c.TypeParams.Count != 0 || c.NamedTupleFields != null ||
                    c.Bases.Any(b => b is not NameExpr { Id: "object" }) || c.Decorators.Count != 1 ||
                    c.Decorators[0] is not AttributeExpr { Name: "__new__", Target: NameExpr { Id: "object" } } ||
                    !m.Classes.TryGetValue(c.Name, out var registered) || registered != c)
                    continue;

                // The CLR type keeps the original short name for type(obj).__name__, while the module binds that name to the instance.
                var scope = new ClassDef { Name = "_typhon_singleton_" + c.Name, Line = c.Line, Col = c.Col };
                while (BoundElsewhere(body, scope)) scope.Name += "_";
                c.Decorators.Clear();
                c.UninitializedSingleton = true;
                scope.Body.Add(c);
                var owner = new NameExpr { Id = scope.Name, Line = c.Line, Col = c.Col };
                var hiddenType = new AttributeExpr { Target = owner, Name = c.Name, Line = c.Line, Col = c.Col };
                var allocator = new AttributeExpr { Target = hiddenType, Name = AllocateSingleton, Line = c.Line, Col = c.Col };
                body[i] = scope;
                body.Insert(++i, new AssignStmt
                {
                    Targets = { new NameExpr { Id = c.Name, Line = c.Line, Col = c.Col } },
                    Value = new CallExpr { Func = allocator, Line = c.Line, Col = c.Col },
                    Line = c.Line, Col = c.Col,
                });
                m.Classes.Remove(c.Name);
                m.Classes[scope.Name] = scope;
                m.Globals.Add(c.Name);
            }
        }

        /// <summary>
        /// A class defined inside a function that uses none of the function's variables is the same class at module level
        /// (a CLR class cannot be local): it moves there, renamed only if the module already has that name.
        /// </summary>
        /// <summary>
        /// class C: ... in a module-level if / try block (if HAVE_SSL: class IMAP4_SSL(IMAP4), try: ... else: class
        /// ABCMeta): a class of the module, defined before the block, when nothing else at module level binds its name
        /// (Typhon's classes exist from the start; the block's other statements stay where they are).
        /// </summary>
        static void HoistBlockClasses(ModuleInfo m)
        {
            var body = m.Ast.Body;
            for (int i = 0; i < body.Count; i++)
            {
                if (!(body[i] is IfStmt || body[i] is TryStmt)) continue;
                var found = new List<(List<Stmt> Owner, ClassDef Cls)>();
                CollectBlockClasses(body[i], found);
                foreach (var (owner, cls) in found)
                {
                    if (m.Classes.ContainsKey(cls.Name) || m.Functions.Contains(cls.Name) || found.Count(x => x.Cls.Name == cls.Name) > 1 ||
                        BoundElsewhere(body, cls)) continue;
                    owner.Remove(cls);
                    if (owner.Count == 0) owner.Add(new PassStmt { Line = cls.Line, Col = cls.Col });
                    body.Insert(i, cls);
                    i++;
                    m.Classes[cls.Name] = cls;
                }
            }
        }

        static void CollectBlockClasses(Stmt s, List<(List<Stmt> Owner, ClassDef Cls)> into)
        {
            void Scan(List<Stmt> block)
            {
                foreach (var st in block)
                    if (st is ClassDef c) into.Add((block, c));
                    else if (st is IfStmt || st is TryStmt) CollectBlockClasses(st, into);
            }
            if (s is IfStmt i) { Scan(i.Body); Scan(i.OrElse); }
            else if (s is TryStmt t) { Scan(t.Body); foreach (var h in t.Handlers) Scan(h.Body); Scan(t.OrElse); Scan(t.FinalBody); }
        }

        /// <summary>Is the class's name bound by anything else in the module (an assignment, an import, a def, another class)?</summary>
        static bool BoundElsewhere(List<Stmt> body, ClassDef cls)
        {
            bool bound = false;
            AstWalker.VisitAll(body, n =>
            {
                switch (n)
                {
                    case ClassDef c when c != cls && c.Name == cls.Name: bound = true; break;
                    case FunctionDef f when f.Name == cls.Name: bound = true; break;
                    case ImportFromStmt imf when imf.Names.Any(a => (a.AsName ?? a.Name) == cls.Name): bound = true; break;
                    case ImportStmt im when im.Names.Any(a => (a.AsName ?? a.Name.Split('.')[0]) == cls.Name): bound = true; break;
                    case AssignStmt a when a.Targets.Any(t => t is NameExpr tn && tn.Id == cls.Name): bound = true; break;
                }
            });
            return bound;
        }

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

        /// <summary>The fields known for a tuple literal or a previously declared namedtuple.</summary>
        static List<string> NamedTupleFieldTuple(Expr spec, Dictionary<string, List<string>> known)
        {
            switch (spec)
            {
                case TupleExpr tuple when tuple.Elts.All(x => x is ConstExpr { Kind: ConstKind.Str }):
                    return tuple.Elts.Select(x => ((ConstExpr)x).Str).ToList();
                case AttributeExpr { Name: "_fields", Target: NameExpr owner } when known.TryGetValue(owner.Id, out var fields):
                    return fields;
                case BinOpExpr { Op: "+" } sum:
                    var left = NamedTupleFieldTuple(sum.Left, known);
                    var right = NamedTupleFieldTuple(sum.Right, known);
                    return left != null && right != null ? left.Concat(right).ToList() : null;
                default:
                    return null;
            }
        }

        /// <summary>The fields of a namedtuple, when its field names are known during compilation.</summary>
        static List<string> NamedTupleFieldsOf(CallExpr c, Dictionary<string, List<string>> known)
        {
            var spec = c.Args.Count > 1 && c.Args[1].Name == null ? c.Args[1].Value : c.Args.FirstOrDefault(a => a.Name == "field_names")?.Value;
            switch (spec)
            {
                case ConstExpr s when s.Kind == ConstKind.Str: return s.Str.Replace(',', ' ').Split(new[] { ' ', '\t', '\n' }, System.StringSplitOptions.RemoveEmptyEntries).ToList();
                case ListExpr l when l.Elts.All(x => x is ConstExpr xc && xc.Kind == ConstKind.Str): return l.Elts.Select(x => ((ConstExpr)x).Str).ToList();
                default: return NamedTupleFieldTuple(spec, known);
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
            var known = new Dictionary<string, List<string>>();
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i] is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr target && IsNamedTupleCall(a.Value) &&
                    NamedTupleFieldsOf((CallExpr)a.Value, known) is List<string> fields)
                {
                    var cls = new ClassDef { Name = target.Id, Line = a.Line, Col = a.Col };
                    MakeNamedTuple(cls, fields, ((CallExpr)a.Value).Args.FirstOrDefault(x => x.Name == "defaults")?.Value, annotated: null);
                    body[i] = cls;
                    m.Classes[cls.Name] = cls;
                    m.Globals.Remove(cls.Name);
                    known[cls.Name] = fields;
                    continue;
                }
                if (body[i] is ClassDef c && c.NamedTupleFields == null)
                {
                    var ntBase = c.Bases.FirstOrDefault(IsNamedTupleCall) as CallExpr;
                    if (ntBase != null && NamedTupleFieldsOf(ntBase, known) is List<string> bf)
                    {
                        c.Bases.Remove(ntBase);
                        MakeNamedTuple(c, bf, ntBase.Args.FirstOrDefault(x => x.Name == "defaults")?.Value, annotated: null);
                        RewriteNamedTupleSuperRepr(c);
                        known[c.Name] = bf;
                        continue;
                    }
                    var typed = c.Bases.FirstOrDefault(b => b is NameExpr bn && bn.Id == "NamedTuple" || b is AttributeExpr ba && ba.Name == "NamedTuple");
                    if (typed != null)
                    {
                        c.Bases.Remove(typed);
                        var anns = c.Body.OfType<AnnAssignStmt>().Where(x => x.Target is NameExpr).ToList();
                        MakeNamedTuple(c, anns.Select(x => ((NameExpr)x.Target).Id).ToList(), null, anns);
                    }
                    var inherited = c.Bases.Count == 1 && c.Bases[0] is NameExpr baseName &&
                        known.TryGetValue(baseName.Id, out var inheritedFields) && !ClassBinds(c, "_fields") ? inheritedFields : null;
                    if (inherited != null && !c.Body.OfType<FunctionDef>().Any(m => m.Name == "__init__") &&
                        c.Body.OfType<FunctionDef>().FirstOrDefault(m => m.Name == "__new__") is FunctionDef allocator)
                        LowerNamedTupleNew(allocator, inherited, initializeBase: true);
                    if ((c.NamedTupleFields ?? inherited) is List<string> classFields) known[c.Name] = classFields;
                    else known.Remove(c.Name);
                }
                else if (body[i] is AssignStmt reassignment)
                    foreach (var name in reassignment.Targets.OfType<NameExpr>()) known.Remove(name.Id);
                else if (body[i] is AnnAssignStmt { Value: not null, Target: NameExpr annotation }) known.Remove(annotation.Id);
                else if (body[i] is FunctionDef function) known.Remove(function.Name);
            }
        }

        static void MakeNamedTuple(ClassDef c, List<string> fields, Expr defaults, List<AnnAssignStmt> annotated)
        {
            c.NamedTupleFields = fields;
            if (c.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")) return;
            if (c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__new__") is FunctionDef allocate)
            {
                c.NamedTupleCustomNew = LowerNamedTupleNew(allocate, fields);
                return;
            }
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

        /// <summary>Lower a namedtuple allocator that returns the instance built by a single super().__new__ call.</summary>
        static bool LowerNamedTupleNew(FunctionDef method, List<string> fields, bool initializeBase = false)
        {
            if (method.IsAsync || method.TypeParams.Count != 0 || method.Decorators.Count != 0 || method.Params.Count == 0 ||
                method.Params[0].Kind != ParamKind.Normal || method.Params.Skip(1).Any(p => p.Name == "self") ||
                method.Body.LastOrDefault() is not ReturnStmt returned) return false;

            string className = method.Params[0].Name;
            CallExpr BaseAllocation(Expr expr) =>
                expr is CallExpr call && call.Func is AttributeExpr { Name: "__new__", Target: CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } } &&
                call.Args.FirstOrDefault() is Arg { Name: null, Star: false, DoubleStar: false, Value: NameExpr cls } && cls.Id == className
                    ? call : null;

            CallExpr allocation = BaseAllocation(returned.Value);
            Stmt allocateAt = returned;
            NameExpr instance = null;
            if (allocation == null && returned.Value is NameExpr result)
            {
                var assignments = method.Body.OfType<AssignStmt>()
                    .Where(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr name && name.Id == result.Id)
                    .ToList();
                if (assignments.Count != 1) return false;
                var assignment = assignments[0];
                allocation = BaseAllocation(assignment.Value);
                allocateAt = assignment;
                instance = (NameExpr)assignment.Targets[0];
            }
            if (allocation == null) return false;

            if (instance != null)
            {
                var remainder = method.Body.Where(s => s != allocateAt && s != returned).ToList();
                var locals = LocalsAnalyzer.Analyze(remainder, System.Array.Empty<string>(), null);
                if (method.Params.Any(p => p.Name == instance.Id) || locals.AllLocals.Contains(instance.Id) ||
                    locals.Globals.Contains(instance.Id) || locals.LocalFunctions.Contains(instance.Id))
                    return false;
            }

            // This constructor form cannot return a different object or use cls after the allocation.
            bool otherUse = AstWalker.ContainsOutsideNestedScopes(method.Body,
                n => n is ReturnStmt && n != returned || n is YieldExpr);
            AstWalker.VisitAll(method.Body, n =>
            {
                if (n is NameExpr name && (name.Id == className && name != allocation.Args[0].Value ||
                    name.Id == "self" && instance?.Id != "self")) otherUse = true;
            });
            if (otherUse) return false;

            AttributeExpr Field(string name) => new AttributeExpr
            {
                Target = new NameExpr { Id = "self", Line = allocateAt.Line, Col = allocateAt.Col },
                Name = name, Line = allocateAt.Line, Col = allocateAt.Col,
            };
            AssignStmt Assign(Expr target, Expr value) => new AssignStmt
            {
                Targets = { target }, Value = value, Line = allocateAt.Line, Col = allocateAt.Col,
            };

            var args = allocation.Args.Skip(1).ToList();
            var initialize = new List<Stmt>();
            if (initializeBase)
            {
                // A CLR base initializer precedes the constructor body. Only move an allocation that already runs first.
                int at = method.Body.IndexOf(allocateAt);
                if (at > 1 || at == 1 && method.Body[0] is not ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } } ||
                    args.Count != fields.Count || args.Any(a => a.Star || a.DoubleStar))
                    return false;
                var super = (AttributeExpr)allocation.Func;
                initialize.Add(new ExprStmt
                {
                    Line = allocateAt.Line, Col = allocateAt.Col,
                    Value = new CallExpr
                    {
                        Line = allocation.Line, Col = allocation.Col, Args = args,
                        Func = new AttributeExpr { Target = super.Target, Name = "__init__", Line = super.Line, Col = super.Col },
                    },
                });
            }
            else if (fields.Count > 0 && args.Count == 1 && args[0].Star)
                initialize.Add(Assign(new TupleExpr { Elts = fields.Select(f => (Expr)Field(f)).ToList(), Line = allocateAt.Line, Col = allocateAt.Col }, args[0].Value));
            else
            {
                if (args.Count != fields.Count || args.Any(a => a.Star || a.DoubleStar)) return false;
                var assigned = new HashSet<string>();
                for (int i = 0; i < args.Count; i++)
                {
                    string name = args[i].Name ?? fields[i];
                    if (!fields.Contains(name) || !assigned.Add(name)) return false;
                    initialize.Add(Assign(Field(name), args[i].Value));
                }
            }
            if (instance != null && instance.Id != "self")
                initialize.Add(Assign(instance, new NameExpr { Id = "self", Line = instance.Line, Col = instance.Col }));

            int position = method.Body.IndexOf(allocateAt);
            method.Body.Remove(returned);
            method.Body.Remove(allocateAt);
            method.Body.InsertRange(position, initialize);
            method.Name = "__init__";
            method.Params[0].Name = "self";
            method.Returns = null;
            return true;
        }

        internal const string NamedTupleRepr = "__typhon_namedtuple_repr__";

        /// <summary>A namedtuple factory base is represented by the same CLR class as its subclass.</summary>
        static void RewriteNamedTupleSuperRepr(ClassDef c)
        {
            var repr = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__repr__" && f.Params.Count != 0);
            if (repr == null) return;
            AstWalker.VisitAll(repr.Body, n =>
            {
                if (n is CallExpr { Args.Count: 0, Func: AttributeExpr { Name: "__repr__", Target: CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } } } call)
                    call.Func = new AttributeExpr
                    {
                        Target = new NameExpr { Id = repr.Params[0].Name, Line = call.Line, Col = call.Col },
                        Name = NamedTupleRepr, Line = call.Line, Col = call.Col,
                    };
            });
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

        /// <summary>__class_getitem__ = classmethod(GenericAlias): generic aliases for typing (list[int]-style), nothing at run time here.</summary>
        static void DropTypingOnlyMembers(ClassDef c)
        {
            c.Body.RemoveAll(s => s is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr n && n.Id == "__class_getitem__");
            foreach (var nested in c.Body.OfType<ClassDef>()) DropTypingOnlyMembers(nested);
        }

        /// <summary>
        /// A method's first parameter is the instance whatever it is called (cls in a metaclass's methods, mcs, klass); a
        /// classmethod's is the class. The rest of the compiler knows them as self and cls: such a parameter is renamed,
        /// with its uses (unless the method uses the other name for something else).
        /// </summary>
        static void NormalizeSelfNames(ClassDef c)
        {
            foreach (var f in c.Body.OfType<FunctionDef>())
            {
                if (f.Params.Count == 0 || f.Params[0].Kind != ParamKind.Normal) continue;
                bool isStatic = f.Decorators.Any(d => d is NameExpr dn && dn.Id == "staticmethod");
                bool isClass = f.Decorators.Any(d => d is NameExpr dn && dn.Id == "classmethod") || f.Name is "__new__" or "__init_subclass__" or "__class_getitem__";
                if (isStatic) continue;
                string want = isClass ? "cls" : "self", have = f.Params[0].Name;
                if (have == want || f.Name == "__new__") continue;
                bool clash = f.Params.Skip(1).Any(p => p.Name == want);
                AstWalker.VisitAll(f.Body, n => { if (n is NameExpr ne && ne.Id == want) clash = true; });
                if (clash) continue;
                f.Params[0].Name = want;
                AstWalker.VisitAll(f.Body, n => { if (n is NameExpr ne && ne.Id == have) ne.Id = want; });
            }
            foreach (var nested in c.Body.OfType<ClassDef>()) NormalizeSelfNames(nested);
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
            // Python's text protocols require a string, including when a class aliases an unannotated method.
            foreach (var f in c.Body.OfType<FunctionDef>())
                if (f.Returns == null && f.Name is "__str__" or "__repr__")
                    f.Returns = new NameExpr { Id = "str", Line = f.Line, Col = f.Col };
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
