// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    /// <summary>A function or constructor whose missing annotations the inference fills in.</summary>
    internal sealed class InferenceTarget
    {
        public string Key;              // "module.func" or "module.Class.method"
        public ModuleInfo Module;
        public ClassDef Class;          // null for module-level functions
        public FunctionDef Def;
        public int FirstParam;          // 1 when the first parameter is self
        /// <summary>Unannotated parameters are specialised per call signature (one overload per argument-type tuple).</summary>
        public bool InferParams;
        /// <summary>A method: its late-bound version is always emitted (calls through untyped receivers), so its body's calls count.</summary>
        public bool AlwaysFallback;
        /// <summary>The CLR result of an unannotated synchronous generator, known before any call is inferred.</summary>
        public string GeneratorReturnType;
        public List<Param> Params => Def.Params.Skip(FirstParam).ToList();
    }

    /// <summary>What one dry run of the emitter saw.</summary>
    internal sealed class Observations
    {
        public sealed class Call
        {
            public string Callee;
            public string[] Types;
            /// <summary>Key of the inference target whose dynamic fallback body contains the call (null: code that always runs).</summary>
            public string Context;
        }

        public readonly List<Call> Calls = new List<Call>();
        public readonly Dictionary<string, List<string>> Returns = new Dictionary<string, List<string>>();
        public readonly Dictionary<string, Dictionary<string, string>> Fields = new Dictionary<string, Dictionary<string, string>>();
        public readonly Dictionary<string, Dictionary<string, string>> MethodReturns = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>Element types added to containers that started empty: "scope|name" → types ("K→V" for dicts).</summary>
        public readonly Dictionary<string, List<string>> Elements = new Dictionary<string, List<string>>();

        /// <summary>
        /// Variables assigned a value of another type where no fresh C# variable can take it (in a nested block, or a hoisted
        /// variable): "scope|name" → the type the variable needs ("double" for an int that becomes a float, else "dynamic").
        /// </summary>
        public readonly Dictionary<string, string> Conflicts = new Dictionary<string, string>();

        /// <summary>Types assigned to locals that are declared ahead of their first assignment (assigned in a nested block): "scope|name" → types.</summary>
        public readonly Dictionary<string, List<string>> Hoisted = new Dictionary<string, List<string>>();

        /// <summary>
        /// Attributes created from outside a class: "ClassKey|attr" (obj.attr = v on an instance) or "ClassKey|static|attr"
        /// (Cls.ATTR = v, also for a class variable declared as None) → the assigned types.
        /// </summary>
        public readonly Dictionary<string, List<string>> ExternalFields = new Dictionary<string, List<string>>();

        public void AddExternal(string key, string type)
        {
            if (!ExternalFields.TryGetValue(key, out var list)) ExternalFields[key] = list = new List<string>();
            list.Add(TypeInference.Settled(type ?? TypeInference.Unknown));
        }

        /// <summary>Calls of a lambda bound to a local name: "scope|λname" → the argument types of each call.</summary>
        public readonly Dictionary<string, List<string[]>> LambdaCalls = new Dictionary<string, List<string[]>>();

        public void AddLambdaCall(string key, string[] types)
        {
            if (!LambdaCalls.TryGetValue(key, out var list)) LambdaCalls[key] = list = new List<string[]>();
            list.Add(types);
        }

        public void AddHoisted(string key, string type)
        {
            if (!Hoisted.TryGetValue(key, out var list)) Hoisted[key] = list = new List<string>();
            list.Add(TypeInference.Settled(type ?? TypeInference.Unknown));
        }

        public void AddConflict(string key, string type)
        {
            if (!Conflicts.TryGetValue(key, out var t) || t != "dynamic") Conflicts[key] = type;
        }

        /// <summary>
        /// Set while a function's dynamic fallback body is emitted. What it stores into containers is kept apart: a container
        /// typed code fills too takes the typed code's element types (a dynamic value converts to them when it is stored),
        /// only a container nothing else fills takes the fallback's.
        /// </summary>
        public bool InFallbackBody;
        public readonly Dictionary<string, List<string>> FallbackElements = new Dictionary<string, List<string>>();

        public void AddElement(string key, string type)
        {
            var table = InFallbackBody ? FallbackElements : Elements;
            if (!table.TryGetValue(key, out var list)) table[key] = list = new List<string>();
            list.Add(TypeInference.Settled(type ?? TypeInference.Unknown));
        }

        public void AddReturn(string key, string type)
        {
            if (!Returns.TryGetValue(key, out var list)) Returns[key] = list = new List<string>();
            list.Add(TypeInference.Settled(type));
        }
    }

    /// <summary>
    /// Whole-program type inference for unannotated Typhon code.
    ///
    /// The emitter already infers local variables (C# <c>var</c>) and simple expressions. What C# cannot infer are the
    /// types it needs spelled out: parameters, return types and fields. This pass runs the emitter in a dry mode that
    /// records, for every call of a project function or constructor, the argument types at the call site, and for every
    /// <c>return</c> the type of the returned value. Each distinct, fully known argument-type tuple becomes one typed
    /// overload of the function (monomorphisation, like Codon), so <c>f(1)</c> and <c>f(2.5)</c> keep Python's int/float
    /// behaviour. The untyped <c>dynamic</c> version is kept only when a call site that can actually run passes values
    /// of unknown type, so the program stays correct, and fully inferred programs contain no dynamic code (Native AOT).
    ///
    /// It is an optimistic fixpoint: pass k emits with the signatures derived from pass k-1. A recursive call whose
    /// return type is still being inferred contributes "no information" (<see cref="Pending"/>). If the signatures do not
    /// settle within <see cref="MaxPasses"/> passes, the inference is dropped and the code is emitted as written.
    /// </summary>
    internal sealed class TypeInference
    {
        public const string Unknown = "?";
        public const string Pending = "⊥";
        public const string NoneType = "None";
        /// <summary>Placeholder in a signature tuple for an annotated parameter (its declared type is used).</summary>
        public const string Declared = "@";
        const int MaxPasses = 40, MaxOverloads = 6;

        public readonly Dictionary<string, InferenceTarget> Targets = new Dictionary<string, InferenceTarget>();
        public Dictionary<string, List<string[]>> Signatures = new Dictionary<string, List<string[]>>();
        public HashSet<string> Fallback = new HashSet<string>();
        public Dictionary<string, string> Returns = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> Fields = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, string>> MethodReturns = new Dictionary<string, Dictionary<string, string>>();
        /// <summary>The element type of each container that started empty (list/set: T, dict: "K→V"; "dynamic" when they disagree).</summary>
        public Dictionary<string, string> Elements = new Dictionary<string, string>();
        /// <summary>Variables that need a wider type than their first value has (see Observations.Conflicts); kept across passes.</summary>
        public Dictionary<string, string> Conflicts = new Dictionary<string, string>();
        /// <summary>The type of each local declared ahead of its first assignment, when all its assignments agree ("dynamic" otherwise).</summary>
        public Dictionary<string, string> Hoisted = new Dictionary<string, string>();
        /// <summary>The type of each attribute created from outside its class (see Observations.ExternalFields).</summary>
        public Dictionary<string, string> ExternalFields = new Dictionary<string, string>();
        /// <summary>The parameter types of a lambda bound to a local name, when all its calls agree.</summary>
        public Dictionary<string, string[]> LambdaSigs = new Dictionary<string, string[]>();
        public Observations Obs;
        public int Passes;
        public bool Converged;

        public static string FuncKey(ModuleInfo m, ClassDef c, string name) => m.Name + "." + (c != null ? c.Name + "." : "") + name;
        public static string ClassKey(ModuleInfo m, string className) => m.Name + "." + className;
        public static string SigKey(string[] types) => types == null ? "" : string.Join(",", types);
        public static string ReturnKey(string key, string sig) => key + "|" + sig;
        /// <summary>A type that still contains a part being inferred (a container's element type) gives no information yet.</summary>
        public static string Settled(string t) => t != null && t != Pending && t.Contains(Pending) ? (t.Contains("→") && !t.Split('→').All(x => x.Contains(Pending)) ? t : Pending) : t;

        public static bool IsUnknown(string t) => t == null || t == Unknown || t == Pending || t == "dynamic" || t == "object" || t == "object?";

        public static void Run(ProjectContext p)
        {
            var ti = new TypeInference();
            p.Inference = ti;
            ti.CollectTargets(p);
            if (ti.Targets.Count == 0 && !p.Modules.Values.Any(m => HasEmptyContainers(m) || HasRebinding(m) || HasLambdaLocals(m) || HasExternalAttributes(m))) { ti.Converged = true; return; }
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                ti.Passes = pass + 1;
                ti.Obs = new Observations();
                foreach (var m in p.Modules.Values)
                {
                    if (m.ParseDiagnostics.Count > 0) continue;
                    try { new ModuleEmitter(m, p, new List<TyDiagnostic>(), dryRun: true).Emit(); }
                    catch (Exception) { /* the real emission reports the problem */ }
                }
                var before = ti.Snapshot();
                bool changed = ti.Derive();
#pragma warning disable RS1035
                if (changed && Environment.GetEnvironmentVariable("TYPHON_INFER_DEBUG") == "1")
#pragma warning restore RS1035
                    foreach (var line in ti.Snapshot().Except(before)) Console.Error.WriteLine($"[infer] pass {pass + 1} + {line}");
                ti.Obs = null;
                if (!changed) { ti.Converged = true; break; }
            }
#pragma warning disable RS1035 // a developer switch, read by the CLI process only
            if (Environment.GetEnvironmentVariable("TYPHON_INFER_DEBUG") == "1")
#pragma warning restore RS1035
            {
                Console.Error.WriteLine($"[infer] passes={ti.Passes} converged={ti.Converged}");
                foreach (var kv in ti.Signatures) Console.Error.WriteLine($"[infer] sig {kv.Key}: {string.Join(" | ", kv.Value.Select(SigKey))}");
                foreach (var kv in ti.Returns) Console.Error.WriteLine($"[infer] ret {kv.Key} = {kv.Value}");
                Console.Error.WriteLine($"[infer] fallback: {string.Join(", ", ti.Fallback)}");
                foreach (var kv in ti.Hoisted) Console.Error.WriteLine($"[infer] hoisted {kv.Key} = {kv.Value}");
                foreach (var kv in ti.Conflicts) Console.Error.WriteLine($"[infer] conflict {kv.Key} = {kv.Value}");
            }
            if (!ti.Converged)
            {
                ti.Signatures.Clear(); ti.Fallback.Clear(); ti.Returns.Clear(); ti.Fields.Clear(); ti.MethodReturns.Clear(); ti.Elements.Clear(); ti.Hoisted.Clear(); ti.ExternalFields.Clear(); ti.LambdaSigs.Clear();
            }
        }

        HashSet<string> Snapshot()
        {
            var r = new HashSet<string>();
            foreach (var kv in Signatures) r.Add($"sig {kv.Key}: {string.Join(" | ", kv.Value.Select(SigKey).OrderBy(x => x, StringComparer.Ordinal))}");
            foreach (var kv in Returns) r.Add($"ret {kv.Key} = {kv.Value}");
            foreach (var kv in Elements) r.Add($"elem {kv.Key} = {kv.Value}");
            foreach (var kv in Hoisted) r.Add($"hoisted {kv.Key} = {kv.Value}");
            foreach (var kv in ExternalFields) r.Add($"external {kv.Key} = {kv.Value}");
            foreach (var kv in Conflicts) r.Add($"conflict {kv.Key} = {kv.Value}");
            foreach (var f in Fallback) r.Add($"fallback {f}");
            foreach (var kv in Fields) foreach (var f in kv.Value) r.Add($"field {kv.Key}.{f.Key} = {f.Value}");
            return r;
        }

        /// <summary>[] / {} / set() / list() / dict(): a container whose element type comes from what is added to it later.</summary>
        public static bool IsEmptyContainer(Expr e)
        {
            if (IsNonePlaceholder(e)) return true;
            if (e is ListExpr l) return l.Elts.Count == 0;
            if (e is DictExpr d) return d.Keys.Count == 0;
            if (!(e is CallExpr c)) return false;
            string name = c.Func is NameExpr n ? n.Id : c.Func is AttributeExpr a && a.Target is NameExpr an && an.Id == "collections" ? a.Name : null;
            int positional = c.Args.Count(x => x.Name == null);
            switch (name)
            {
                case "list": case "dict": case "set": return c.Args.Count == 0;
                case "Counter": case "OrderedDict": return c.Args.Count == 0;
                case "deque": return positional == 0;                       // deque(maxlen=n) starts empty too
                case "defaultdict": return positional <= 1 && c.Args.Count <= 1; // defaultdict(list): the factory, no items
                default: return false;
            }
        }

        /// <summary>[None] * n (or n * [None]): a list of placeholders, typed (nullable) by what is stored into it later.</summary>
        public static bool IsNonePlaceholder(Expr e) =>
            e is BinOpExpr b && b.Op == "*" && (IsNoneList(b.Left) || IsNoneList(b.Right)) ||
            // [None, None]
            e is ListExpr nl && nl.Elts.Count > 0 && nl.Elts.All(x => x is ConstExpr nc && nc.Kind == ConstKind.None) ||
            // [None for _ in range(n)]
            e is ComprehensionExpr c && c.Kind == ComprehensionKind.List && c.Elt is ConstExpr ce && ce.Kind == ConstKind.None &&
            c.Generators.Count == 1 && c.Generators[0].Ifs.Count == 0 && !c.Generators[0].IsAsync;

        static bool IsNoneList(Expr e) => e is ListExpr l && l.Elts.Count == 1 && l.Elts[0] is ConstExpr c && c.Kind == ConstKind.None;

        /// <summary>The kind of an empty container: l(ist), s(et), d(ict), c(ounter), q (deque), o(rdered dict), D (defaultdict).</summary>
        public static char ContainerKind(Expr e)
        {
            if (e is DictExpr) return 'd';
            if (!(e is CallExpr c)) return 'l';
            string name = c.Func is NameExpr n ? n.Id : c.Func is AttributeExpr a ? a.Name : null;
            switch (name)
            {
                case "dict": return 'd';
                case "set": return 's';
                case "Counter": return 'c';
                case "deque": return 'q';
                case "OrderedDict": return 'o';
                case "defaultdict": return 'D';
                default: return 'l';
            }
        }

        public static bool IsMappingKind(char kind) => kind == 'd' || kind == 'o' || kind == 'D' || kind == 'c';

        /// <summary>Might a variable get values of different types (a name assigned twice, an assigned parameter, x /= y)? Then a dry run looks for conflicts.</summary>
        /// <summary>obj.attr = v / Cls.ATTR = v outside a method: the dry runs find the attributes such code creates.</summary>
        static bool HasExternalAttributes(ModuleInfo m)
        {
            if (m.Ast == null) return false;
            bool found = false;
            AstWalker.VisitAll(m.Ast.Body, n =>
            {
                if (found) return;
                if (n is AssignStmt a && a.Targets.Any(t => t is AttributeExpr at && !(at.Target is NameExpr sn && sn.Id == "self"))) found = true;
                if (n is AugAssignStmt au && au.Target is AttributeExpr aat && !(aat.Target is NameExpr asn && asn.Id == "self")) found = true;
            });
            return found;
        }

        static bool HasRebinding(ModuleInfo m)
        {
            if (m.Ast == null) return false;
            bool found = false;
            void Scan(List<Stmt> body, IEnumerable<string> parameters)
            {
                var seen = new HashSet<string>(parameters);
                AstWalker.VisitAll(body, n =>
                {
                    if (found) return;
                    switch (n)
                    {
                        case FunctionDef f: Scan(f.Body, f.Params.Select(p => p.Name)); break;
                        case AssignStmt a: foreach (var t in a.Targets) if (t is NameExpr tn && !seen.Add(tn.Id)) found = true; break;
                        case AugAssignStmt au when au.Target is NameExpr an && (seen.Contains(an.Id) && parameters.Contains(an.Id) || au.Op == "/"): found = true; break;
                    }
                });
            }
            Scan(m.Ast.Body, Enumerable.Empty<string>());
            return found;
        }

        /// <summary>name = lambda ...: its parameter types come from the calls (a dry run records them).</summary>
        static bool HasLambdaLocals(ModuleInfo m)
        {
            bool found = false;
            if (m.Ast != null) AstWalker.VisitAll(m.Ast.Body, n => { if (n is AssignStmt a && a.Value is LambdaExpr) found = true; });
            return found;
        }

        static bool HasEmptyContainers(ModuleInfo m)
        {
            bool found = false;
            if (m.Ast != null) AstWalker.VisitAll(m.Ast.Body, n => { if (n is Expr e && IsEmptyContainer(e)) found = true; });
            return found;
        }

        /// <summary>One element type for everything added to a container ("dynamic" when the types disagree or are unknown).</summary>
        static string UnifyElements(List<string> seen)
        {
            if (seen.Count > 0 && seen[0].Contains("→"))
            {
                var parts = seen.Where(x => x != Pending && x.Contains("→")).Select(x => x.Split('→')).ToList();
                if (parts.Count == 0) return null;
                string k = UnifyElements(parts.Select(x => x[0]).ToList()), v = UnifyElements(parts.Select(x => x[1]).ToList());
                return k == null || v == null ? null : k + "→" + v;
            }
            var known = seen.Where(x => x != Pending).ToList();
            if (known.Count == 0) return null;
            // None among the elements: the others' type, nullable ([None] * n filled later, xs.append(None))
            bool none = known.RemoveAll(x => x == NoneType) > 0;
            if (known.Count == 0 || known.Any(IsUnknown)) return "dynamic";
            var distinct = known.Distinct().ToList();
            if (distinct.Count == 1) return none && IsValueType(distinct[0]) && !distinct[0].EndsWith("?") ? distinct[0] + "?" : distinct[0];
            if (distinct.All(x => x == "long" || x == "double")) return "double";
            if (distinct.All(x => x == "long" || x == BigInt)) return BigInt; // big-int mode: machine ints and Python ints
            // tuples of one length: unified position by position ((Sphere, Plain) and (Plane, Checker): (dynamic, dynamic)),
            // so they stay tuples that unpack
            var tuples = distinct.Select(TupleParts).ToList();
            if (tuples.All(t => t != null) && tuples.Select(t => t.Count).Distinct().Count() == 1 && tuples[0].Count >= 2)
                return "(" + string.Join(", ", Enumerable.Range(0, tuples[0].Count).Select(i => UnifyElements(tuples.Select(t => t[i]).ToList()) ?? "dynamic")) + ")";
            return "dynamic";
        }

        /// <summary>The item types of a C# tuple type "(A, B<C, D>)" (null for other types).</summary>
        static List<string> TupleParts(string t)
        {
            if (t == null || t.Length < 2 || t[0] != '(' || t[t.Length - 1] != ')') return null;
            var parts = new List<string>();
            int depth = 0, start = 1;
            for (int i = 1; i < t.Length - 1; i++)
            {
                char c = t[i];
                if (c == '<' || c == '(' || c == '[') depth++;
                else if (c == '>' || c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) { parts.Add(t.Substring(start, i - start).Trim()); start = i + 1; }
            }
            parts.Add(t.Substring(start, t.Length - 1 - start).Trim());
            return parts.Any(x => x.Length == 0 || x.Contains(' ') && !x.Contains('<') && !x.Contains('(')) ? null : parts;
        }

        // ───────────────────────────── targets ─────────────────────────────

        void CollectTargets(ProjectContext p)
        {
            var usedAsValue = NamesUsedAsValues(p);
            // methods a Protocol declares: an implementation needs the interface's one signature, not overloads per call
            var protocolMethods = new HashSet<string>(p.Modules.Values.Where(m => m.Ast != null).SelectMany(m => m.Classes.Values)
                .Where(c => c.Bases.Any(b => ((b is SubscriptExpr s ? s.Target : b) is NameExpr n ? n.Id : (b as AttributeExpr)?.Name) == "Protocol"))
                .SelectMany(c => c.Body.OfType<FunctionDef>().Select(f => f.Name)));
            foreach (var m in p.Modules.Values)
            {
                if (m.ParseDiagnostics.Count > 0) continue;
                foreach (var f in m.Ast.Body.OfType<FunctionDef>())
                    // a function used as a value has a function type: calls through it are seen (and other uses keep its dynamic version)
                    Add(m, null, f, allowParams: (!usedAsValue.Contains(f.Name) || ModuleEmitter.IsFunctionValueCandidate(f)) && f.Decorators.All(IsCacheDecorator));
                foreach (var c in m.Classes.Values)
                {
                    if (IsSpecialClass(c)) continue;
                    foreach (var f in c.Body.OfType<FunctionDef>())
                    {
                        if (f.Name == "__init__") { Add(m, c, f, allowParams: true); continue; }
                        // other methods, when no other Typhon class overrides or is overridden by them: parameters inferred from
                        // the calls like a function's (an overload per signature, beside the late-bound version), except in a
                        // generic class and for methods a Protocol declares (return types only there)
                        if (f.Name.StartsWith("__") || f.Decorators.Count > 0 || m.Ast.MethodDecorators.ContainsKey(f) || IsOverridden(p, c, f.Name)) continue;
                        Add(m, c, f, allowParams: c.TypeParams.Count == 0 && !protocolMethods.Contains(f.Name));
                    }
                }
            }
        }

        void Add(ModuleInfo m, ClassDef c, FunctionDef f, bool allowParams)
        {
            if (f.TypeParams.Count > 0 || f.Params.Any(x => x.Kind != ParamKind.Normal)) return;
            int first = c != null ? 1 : 0;
            if (c != null && f.Params.Count == 0) return;
            bool unannotated = f.Params.Skip(first).Any(x => x.Annotation == null || IsBareContainer(x.Annotation));
            var locals = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(x => x.Name), null);
            bool inferReturn = (f.Returns == null || IsBareContainer(f.Returns)) && locals.HasValueReturn && !locals.HasYield;
            bool inferParams = allowParams && unannotated;
            string generatorReturn = locals.HasYield && !f.IsAsync && f.Returns == null ? ModuleEmitter.UnannotatedGeneratorReturnType(f) : null;
            if (!inferParams && !inferReturn && generatorReturn == null) return;
            var key = FuncKey(m, c, f.Name);
            Targets[key] = new InferenceTarget { Key = key, Module = m, Class = c, Def = f, FirstParam = first, InferParams = inferParams,
                                                 GeneratorReturnType = generatorReturn,
                                                 AlwaysFallback = inferParams && c != null && f.Name != "__init__" };
        }

        /// <summary>list / dict / set / tuple without type arguments: the element types come from the call sites.</summary>
        public static bool IsBareContainer(Expr annotation)
        {
            string name = annotation is NameExpr n ? n.Id : annotation is AttributeExpr a && a.Target is NameExpr t && t.Id == "typing" ? a.Name : null;
            return name != null && (name == "list" || name == "dict" || name == "set" || name == "tuple" || name == "frozenset" ||
                                    name == "List" || name == "Dict" || name == "Set" || name == "Tuple" || name == "FrozenSet" ||
                                    name == "defaultdict" || name == "Counter" || name == "deque" || name == "OrderedDict" || name == "DefaultDict" || name == "Deque");
        }

        /// <summary>@cache / @lru_cache / @lru_cache(maxsize=...) (functools): memoization, the function keeps its types.</summary>
        public static bool IsCacheDecorator(Expr d)
        {
            if (d is CallExpr c) d = c.Func;
            string name = d is NameExpr n ? n.Id : d is AttributeExpr a && a.Target is NameExpr t && t.Id == "functools" ? a.Name : null;
            return name == "cache" || name == "lru_cache";
        }

        static bool IsSpecialClass(ClassDef c) => c.Bases.Any(b =>
        {
            var n = (b is SubscriptExpr s ? s.Target : b) is NameExpr ne ? ne.Id : (b is AttributeExpr ae ? ae.Name : null);
            return n == "Protocol" || n == "Enum" || n == "IntEnum";
        }) || c.Decorators.Any(d => (d is NameExpr dn && dn.Id == "dataclass") || (d is CallExpr dc && dc.Func is NameExpr dcn && dcn.Id == "dataclass"));

        /// <summary>Does another Typhon class define the same method in an inheritance relation with this one?</summary>
        static bool IsOverridden(ProjectContext p, ClassDef c, string method)
        {
            var all = p.Modules.Values.SelectMany(m => m.Classes.Values).ToList();
            bool Defines(ClassDef k) => k.Body.OfType<FunctionDef>().Any(f => f.Name == method);
            IEnumerable<string> BaseNames(ClassDef k) => k.Bases.Select(b => (b is SubscriptExpr s ? s.Target : b) is NameExpr n ? n.Id : (b is AttributeExpr a ? a.Name : null)).Where(n => n != null);
            bool Derives(ClassDef k, ClassDef from, int depth)
            {
                if (depth > 20) return false;
                foreach (var bn in BaseNames(k))
                {
                    if (bn == from.Name) return true;
                    var bc = all.FirstOrDefault(x => x.Name == bn);
                    if (bc != null && Derives(bc, from, depth + 1)) return true;
                }
                return false;
            }
            // a .NET base class might declare the method virtual (ABC, object and Generic[T] declare nothing)
            if (BaseNames(c).Any(bn => bn != "ABC" && bn != "object" && bn != "Generic" && all.All(x => x.Name != bn))) return true;
            foreach (var other in all)
            {
                if (other == c || !Defines(other)) continue;
                if (Derives(other, c, 0) || Derives(c, other, 0)) return true;
            }
            return false;
        }

        /// <summary>Function names that appear anywhere other than in call position (passed as callbacks, stored, ...).</summary>
        static HashSet<string> NamesUsedAsValues(ProjectContext p)
        {
            var result = new HashSet<string>();
            foreach (var m in p.Modules.Values)
            {
                if (m.ParseDiagnostics.Count > 0) continue;
                // callees, and functions passed directly as call arguments (those become lambdas that call them)
                var callees = new HashSet<Node>();
                AstWalker.VisitAll(m.Ast.Body, n =>
                {
                    if (!(n is CallExpr c)) return;
                    callees.Add(c.Func);
                    foreach (var a in c.Args) if (a.Value is NameExpr) callees.Add(a.Value);
                });
                AstWalker.VisitAll(m.Ast.Body, n => { if (n is NameExpr ne && !callees.Contains(ne)) result.Add(ne.Id); });
            }
            return result;
        }

        // ───────────────────────────── fixpoint step ─────────────────────────────

        /// <summary>Derives signatures, fallbacks, return and field types from this pass's observations. True if anything changed.</summary>
        bool Derive()
        {
            // Which dynamic fallback bodies can run? Code outside fallback bodies always can; a fallback can when a running
            // call site passes unknown types, or when the function has no typed signature at all (then it is the function).
            var live = new HashSet<string>();
            Dictionary<string, List<string[]>> sigs = null;
            for (int iter = 0; iter < 20; iter++)
            {
                sigs = new Dictionary<string, List<string[]>>();
                var unknownCall = new HashSet<string>();
                foreach (var call in Obs.Calls)
                {
                    if (call.Context != null && !live.Contains(call.Context)) continue;
                    if (!Targets.TryGetValue(call.Callee, out var t) || !t.InferParams) continue;
                    // a definitely late-bound argument (dynamic) makes only its own parameter dynamic; an argument whose type is not
                    // known yet (or a call with nothing typed) needs the function's dynamic version
                    if (call.Types == null || call.Types.Any(x => IsUnknown(x) && x != "dynamic") || call.Types.All(x => x == "dynamic" || x == Declared))
                    { unknownCall.Add(call.Callee); continue; }
                    if (!sigs.TryGetValue(call.Callee, out var list)) sigs[call.Callee] = list = new List<string[]>();
                    if (!list.Any(s => SigKey(s) == SigKey(call.Types))) list.Add(call.Types);
                }
                foreach (var kv in sigs.ToList())
                    if (kv.Value.Count > MaxOverloads) { sigs.Remove(kv.Key); unknownCall.Add(kv.Key); }
                var newLive = new HashSet<string>(Targets.Values.Where(t => t.InferParams && (t.AlwaysFallback || unknownCall.Contains(t.Key) || !sigs.ContainsKey(t.Key))).Select(t => t.Key));
                if (newLive.SetEquals(live)) break;
                live = newLive;
            }

            var returns = new Dictionary<string, string>();
            foreach (var t in Targets.Values)
            {
                var variants = t.InferParams ? (sigs.TryGetValue(t.Key, out var s) ? s.Select(SigKey) : Enumerable.Empty<string>()) : new[] { "" };
                foreach (var v in variants)
                {
                    // a body emitted with this signature decides its return type for good: a type, or "dynamic" when the
                    // returns disagree. Only a signature not emitted yet stays open (Pending), so the optimism of a dry
                    // run never outlives the fixpoint.
                    string rk = ReturnKey(t.Key, v);
                    if (Obs.Returns.TryGetValue(rk, out var seen)) returns[rk] = Unify(seen) ?? "dynamic";
                }
            }

            var elements = new Dictionary<string, string>();
            foreach (var kv in Obs.Elements) if (UnifyElements(kv.Value) is string u) elements[kv.Key] = u;
            // (a container only fallback bodies filled this pass keeps what typed code gave it before: whether a typed body is
            // emitted can change from pass to pass, the container's type should not flip with it)
            foreach (var kv in Obs.FallbackElements)
                if (!Obs.Elements.ContainsKey(kv.Key))
                    if (Elements.TryGetValue(kv.Key, out var typedBefore) && !typedBefore.Contains("dynamic")) elements[kv.Key] = typedBefore;
                    else if (UnifyElements(kv.Value) is string fu) elements[kv.Key] = fu;
            var external = new Dictionary<string, string>();
            foreach (var kv in Obs.ExternalFields) if (UnifyElements(kv.Value) is string ext) external[kv.Key] = ext;
            // attributes only grow: one seen set on an object stays (the code that sets it may stop seeing the object's class
            // when that typing changes, args = _Options(); args.flag = None; ... args = table[k])
            foreach (var kv in ExternalFields) if (!external.ContainsKey(kv.Key)) external[kv.Key] = kv.Value;
            var lambdaSigs = new Dictionary<string, string[]>();
            foreach (var kv in Obs.LambdaCalls)
            {
                var calls = kv.Value.Where(c => c != null).ToList();
                if (calls.Count == 0 || calls.Count != kv.Value.Count || calls.Any(c => c.Length != calls[0].Length)) continue;
                var sig = new string[calls[0].Length];
                bool ok = true;
                for (int i = 0; i < sig.Length && ok; i++)
                {
                    var ts = calls.Select(c => c[i]).Distinct().ToList();
                    if (ts.Any(t => IsUnknown(t) || t.Contains(Pending))) ok = false;
                    else if (ts.Count == 1) sig[i] = ts[0];
                    else if (ts.All(t => t == "long" || t == "double")) sig[i] = "double";
                    else ok = false;
                }
                if (ok) lambdaSigs[kv.Key] = sig;
            }
            var hoisted = new Dictionary<string, string>();
            foreach (var kv in Obs.Hoisted)
            {
                var known = kv.Value.Where(x => x != Pending).Distinct().ToList();
                // None next to an object type: that type (a reference can be None); next to a value type: late-bound
                bool hasNone = known.Remove(NoneType);
                if (known.Count == 0) continue;
                string ht = known.Count == 1 && !IsUnknown(known[0]) ? known[0] : "dynamic";
                if (hasNone && (IsValueType(ht.TrimEnd('?')) || ht.StartsWith("("))) ht = "dynamic";
                hoisted[kv.Key] = ht;
            }
            // a type that keeps flipping between values it had before (a field copied out and written back) settles as dynamic
            Damp("ret", returns, Returns);
            Damp("elem", elements, Elements);
            Damp("hoisted", hoisted, Hoisted);
            Damp("external", external, ExternalFields);
            foreach (var kv in Obs.Fields) Damp("field:" + kv.Key, kv.Value, Fields.TryGetValue(kv.Key, out var pf) ? pf : null);
            foreach (var kv in Obs.MethodReturns) Damp("mret:" + kv.Key, kv.Value, MethodReturns.TryGetValue(kv.Key, out var pm) ? pm : null);
            bool changed = !SameSigs(sigs, Signatures) || !live.SetEquals(Fallback) || !SameMap(returns, Returns) ||
                           !SameNested(Obs.Fields, Fields) || !SameNested(Obs.MethodReturns, MethodReturns) || !SameMap(elements, Elements) ||
                           !SameMap(hoisted, Hoisted) || !SameMap(external, ExternalFields) ||
                           lambdaSigs.Count != LambdaSigs.Count || lambdaSigs.Any(kv => !LambdaSigs.TryGetValue(kv.Key, out var o) || SigKey(o) != SigKey(kv.Value));
            Elements = elements;
            Hoisted = hoisted;
            ExternalFields = external;
            LambdaSigs = lambdaSigs;
            // conflicts only grow: a variable made wider stays so (else the next pass would no longer see the conflict)
            foreach (var kv in Obs.Conflicts)
                if (!Conflicts.TryGetValue(kv.Key, out var had) || had != kv.Value && had != "dynamic") { Conflicts[kv.Key] = kv.Value; changed = true; }
            Signatures = sigs;
            Fallback = live;
            Returns = returns;
            Fields = Obs.Fields;
            MethodReturns = Obs.MethodReturns;
            return changed;
        }

        readonly Dictionary<string, List<string>> _history = new Dictionary<string, List<string>>();
        readonly Dictionary<string, int> _returnsToOld = new Dictionary<string, int>();
        readonly HashSet<string> _pinned = new HashSet<string>();

        /// <summary>
        /// Oscillation damping. Inference only refines (unknown → a type, a type → dynamic), so a value never comes back. One
        /// that goes back to a value it had before twice is a cycle through an unknown state (old = obj.f; ...; obj.f = old):
        /// it is pinned to dynamic, which is always valid, and the rest of the program still converges with its types.
        /// </summary>
        void Damp(string kind, Dictionary<string, string> next, Dictionary<string, string> prev)
        {
            foreach (var key in next.Keys.ToList())
            {
                string id = kind + "|" + key;
                string dyn = next[key] != null && next[key].Contains("→") ? "dynamic→dynamic" : "dynamic";
                if (_pinned.Contains(id)) { next[key] = dyn; continue; }
                if (prev == null || !prev.TryGetValue(key, out var old) || old == next[key]) continue;
                if (!_history.TryGetValue(id, out var seen)) _history[id] = seen = new List<string> { old };
                if (seen.Contains(next[key]))
                {
                    _returnsToOld[id] = _returnsToOld.TryGetValue(id, out var n) ? n + 1 : 1;
                    if (_returnsToOld[id] >= 2) { _pinned.Add(id); next[key] = dyn; continue; }
                }
                seen.Add(next[key]);
            }
        }

        /// <summary>The single type of all returned values; None mixed with a type T gives T (reference) or T? (value type).</summary>
        static string Unify(List<string> seen)
        {
            var known = seen.Where(x => x != Pending).ToList();
            if (known.Count == 0 || known.Any(x => IsUnknown(x) && x != Pending)) return null;
            bool hasNone = known.RemoveAll(x => x == NoneType) > 0;
            var distinct = known.Distinct().ToList();
            if (distinct.Count == 2 && distinct.All(x => x == "long" || x == BigInt)) distinct = new List<string> { BigInt };
            if (distinct.Count != 1) return null;
            string t = distinct[0];
            if (!hasNone || t.EndsWith("?")) return t;
            return IsValueType(t) ? t + "?" : t;
        }

        /// <summary>Python's int in big-int mode.</summary>
        public const string BigInt = "global::System.Numerics.BigInteger";

        static bool IsValueType(string t) => t == "int" || t == "long" || t == BigInt || t == "double" || t == "float" || t == "bool" || t == "char" || t == "decimal" || t.StartsWith("(");

        static bool SameSigs(Dictionary<string, List<string[]>> a, Dictionary<string, List<string[]>> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && kv.Value.Select(SigKey).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(o.Select(SigKey).OrderBy(x => x, StringComparer.Ordinal)));

        static bool SameMap(Dictionary<string, string> a, Dictionary<string, string> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o == kv.Value);

        static bool SameNested(Dictionary<string, Dictionary<string, string>> a, Dictionary<string, Dictionary<string, string>> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && SameMap(kv.Value, o));
    }
}
