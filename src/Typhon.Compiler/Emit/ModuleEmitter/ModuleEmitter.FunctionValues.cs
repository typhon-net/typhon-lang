// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    // Functions as values (each a PyFunction subclass), lambdas in variables, and names that stand for a function.
    internal sealed partial class ModuleEmitter
    {
        //
        // A module function used as a value (passed, assigned, stored, returned) has its own type: a generated sealed
        // subclass of PyFunction with a single instance. Inference sees that type like any other, so a parameter that
        // receives the function is specialised per function (one overload each, like a template argument), and a call
        // through a value of that type is a direct, statically typed call of the function, recorded for its signatures.

        const string FnPrefix = "__fn_";

        HashSet<string> FunctionValueKeys => _p.FunctionValueKeys ??= ComputeFunctionValueKeys(_p);

        internal static bool IsFunctionValueCandidate(FunctionDef f) => f.TypeParams.Count == 0 && f.Decorators.Count == 0 && f.Params.All(p => p.Kind == ParamKind.Normal);

        static HashSet<string> ComputeFunctionValueKeys(ProjectContext p)
        {
            var names = new HashSet<string>();
            foreach (var m in p.Modules.Values)
            {
                if (m.Ast == null || m.ParseDiagnostics.Count > 0) continue;
                var callees = new HashSet<Node>();
                var aliases = new List<ImportAlias>();
                AstWalker.VisitAll(m.Ast.Body, n =>
                {
                    if (n is CallExpr c) callees.Add(c.Func);
                    if (n is ImportFromStmt imp) aliases.AddRange(imp.Names.Where(a => a.AsName != null));
                });
                AstWalker.VisitAll(m.Ast.Body, n => { if (n is NameExpr ne && !callees.Contains(ne)) names.Add(ne.Id); });
                foreach (var a in aliases) if (names.Contains(a.AsName)) names.Add(a.Name);
            }
            var keys = new HashSet<string>();
            foreach (var m in p.Modules.Values)
                if (m.Ast != null && m.ParseDiagnostics.Count == 0)
                    foreach (var f in m.Ast.Body.OfType<FunctionDef>())
                        if (names.Contains(f.Name) && IsFunctionValueCandidate(f)) keys.Add(TypeInference.FuncKey(m, null, f.Name));
            return keys;
        }

        static string ModuleClassName(ModuleInfo m) => "global::" + (string.IsNullOrEmpty(m.Namespace) ? "" : m.Namespace + ".") + Esc(m.ClassName);

        /// <summary>The C# type of a module function used as a value.</summary>
        static string FunctionValueType(ModuleInfo m, string name) => ModuleClassName(m) + "." + FnPrefix + name;

        /// <summary>Does the name denote a module function that has a function type (and which)?</summary>
        bool FunctionRef(NameExpr n, out ModuleInfo owner, out FunctionDef def)
        {
            owner = null; def = null;
            if (IsLocal(n.Id) || _renames.ContainsKey(n.Id) || _moduleFieldTypes.ContainsKey(n.Id)) return false;
            if (_m.Functions.Contains(n.Id)) owner = _m;
            else if (_importedFromModules.TryGetValue(n.Id, out var imp) && imp.Key.Functions.Contains(imp.Value)) owner = imp.Key;
            if (owner == null) return false;
            string name = owner == _m ? n.Id : _importedFromModules[n.Id].Value;
            if (!FunctionValueKeys.Contains(TypeInference.FuncKey(owner, null, name))) return false;
            def = owner.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name);
            return def != null;
        }

        /// <summary>The function a function type denotes (null if the type is not one).</summary>
        bool FunctionOfType(string t, out ModuleInfo owner, out FunctionDef def)
        {
            owner = null; def = null;
            if (t == null || !t.StartsWith("global::") || !t.Contains("." + FnPrefix)) return false;
            foreach (var m in _p.Modules.Values)
            {
                string prefix = ModuleClassName(m) + "." + FnPrefix;
                if (!t.StartsWith(prefix) || m.Ast == null) continue;
                string name = t.Substring(prefix.Length);
                def = m.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name);
                if (def != null) { owner = m; return true; }
            }
            return false;
        }

        /// <summary>
        /// A module function as a value: its function type's instance. Where the value goes somewhere its type is not
        /// kept (<paramref name="typed"/> false: a list, a field, a .NET call, a return value), it may later be called with
        /// any argument types, so the function keeps its dynamic version.
        /// </summary>
        string FunctionValue(NameExpr n, bool typed)
        {
            if (!FunctionRef(n, out var owner, out var def)) return null;
            if (!typed && Obs != null && TargetOf(owner, null, def.Name) is InferenceTarget t && t.InferParams)
                Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = null, Context = _ctxFallbackKey });
            return FunctionValueType(owner, def.Name) + ".Instance";
        }

        /// <summary>A call through a value of a function type: a direct call of the function (null if the callee is not one).</summary>
        string EmitFunctionValueCall(CallExpr c)
        {
            if (c.Func is NameExpr fn && !IsLocal(fn.Id) && !_moduleFieldTypes.ContainsKey(fn.Id)) return null; // a plain call by name
            if (!FunctionOfType(InferType(c.Func), out var owner, out var def)) return null;
            var target = TargetOf(owner, null, def.Name);
            if (Obs != null && target != null) RecordCall(target, c.Args);
            return $"{ModuleClassName(owner)}.{Esc(def.Name)}({EmitArgs(c.Args, typedFunctionArgs: target != null && TypedCall(target, c.Args), typhonCallee: true, paramTypeAt: (i, name) => AnnotatedParamType(def, i, name))})";
        }

        string AnnotatedParamType(FunctionDef def, int pos, string name)
        {
            var p = name != null ? def.Params.FirstOrDefault(x => x.Name == name) : pos >= 0 && pos < def.Params.Count ? def.Params[pos] : null;
            return p?.Annotation != null && p.Kind == ParamKind.Normal ? MapType(p.Annotation, p) : null;
        }

        /// <summary>
        /// Does a call reach a typed overload of an inferred function? Then a function passed to it keeps its function type;
        /// a call with an argument of unknown type runs the dynamic version, which may call the function with anything.
        /// </summary>
        bool TypedCall(InferenceTarget t, List<Arg> args) =>
            t.InferParams && ArgTypes(t, args) is string[] types && !types.Any(x => TypeInference.IsUnknown(x));

        /// <summary>The type of a call through a value of a function type (null if the callee is not one).</summary>
        string FunctionValueCallType(CallExpr c)
        {
            if (c.Func is NameExpr fn && !IsLocal(fn.Id) && !_moduleFieldTypes.ContainsKey(fn.Id)) return null;
            if (!FunctionOfType(InferType(c.Func), out var owner, out var def)) return null;
            if (TargetOf(owner, null, def.Name) is InferenceTarget t && InferredCallType(t, c.Args) is string inferred) return inferred;
            if (def.Returns != null && !TypeInference.IsBareContainer(def.Returns))
            {
                string rt = MapType(def.Returns, def, isReturn: true);
                return rt == "void" ? null : rt;
            }
            return null;
        }

        /// <summary>The class of a module function's function type: its instance, a call with any arguments, conversions to delegates.</summary>
        void EmitFunctionValueClass(FunctionDef f)
        {
            string cls = FnPrefix + f.Name;
            var ps = f.Params;
            int required = ps.Count(p => p.Default == null);
            var locals = LocalsAnalyzer.Analyze(f.Body, ps.Select(p => p.Name), null);
            bool returnsValue = f.IsAsync || locals.HasYield || locals.HasValueReturn ||
                                f.Returns != null && !(f.Returns is ConstExpr rc && rc.Kind == ConstKind.None);
            _w.Line($"internal sealed class {cls} : global::Typhon.Runtime.PyFunction");
            _w.Open();
            _w.Line($"public static readonly {cls} Instance = new {cls}();");
            _w.Line($"{cls}() : base({CsString(f.Name)}) {{ }}");
            _w.Line("public override object? __call__(object?[] a)");
            _w.Open();
            for (int n = required; n <= ps.Count; n++)
            {
                string call = $"{Esc(f.Name)}({string.Join(", ", Enumerable.Range(0, n).Select(i => $"(dynamic?)a[{i}]"))})";
                _w.Line(returnsValue ? $"if (a.Length == {n}) return {call};" : $"if (a.Length == {n}) {{ {call}; return null; }}");
            }
            _w.Line($"throw ArgCount({CsString(f.Name)}, {required}, {ps.Count}, a.Length);");
            _w.Close();
            // conversions to the delegate types of its known signatures (a Callable[[int], int] parameter, Func<long, long>)
            var delegates = new HashSet<string>();
            // (a generator's C# type depends on how it is emitted: no conversion; an async function returns a Task)
            bool isGenerator = AstWalker.ContainsOutsideNestedScopes(f.Body, n => n is YieldExpr);
            void Conversion(List<string> types, string ret)
            {
                if (isGenerator || types.Count > 15 || types.Concat(new[] { ret }).Any(x => x == null || x.Contains("dynamic") || x.Contains(TypeInference.Pending) || x.Contains(TypeInference.Unknown))) return;
                if (f.IsAsync) ret = ret == "void" ? "global::System.Threading.Tasks.Task" : $"global::System.Threading.Tasks.Task<{ret}>";
                string d = ret == "void" ? (types.Count == 0 ? "global::System.Action" : $"global::System.Action<{string.Join(", ", types)}>")
                                         : $"global::System.Func<{string.Join(", ", types.Concat(new[] { ret }))}>";
                if (delegates.Add(d)) _w.Line($"public static implicit operator {d}({cls} _) => {Esc(f.Name)};");
            }
            if (ps.All(p => p.Annotation != null) && (f.Returns != null || !returnsValue))
                Conversion(ps.Select(p => MapType(p.Annotation, p)).ToList(), f.Returns != null ? MapType(f.Returns, f, isReturn: true) : "void");
            if (TargetOf(_m, null, f.Name) is InferenceTarget t && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
                foreach (var sig in sigs)
                {
                    var types = sig.Select((x, i) => x == TypeInference.Declared ? MapType(ps[i].Annotation, ps[i]) : x).ToList();
                    string ret = !returnsValue ? "void" : f.Returns != null ? MapType(f.Returns, f, isReturn: true)
                        : Ti.Returns.TryGetValue(TypeInference.ReturnKey(t.Key, TypeInference.SigKey(sig)), out var r) ? r : null;
                    Conversion(types, ret);
                }
            _w.Close();
        }


        /// <summary>Module names bound once to a function (icount = itertools.count) and only ever called: calls go to it.</summary>
        readonly Dictionary<string, Expr> _functionAliases = new Dictionary<string, Expr>();

        /// <summary>Is the expression a reference to a function: a standard-library or .NET static method, or a module function?</summary>
        bool IsFunctionReference(Expr e)
        {
            switch (e)
            {
                case AttributeExpr fa:
                    if (ModuleRef(DottedName(fa.Target) ?? "") is ModuleInfo mi) return mi.Functions.Contains(fa.Name);
                    if (DotNetStaticMeta(fa.Target) is string sm)
                    {
                        var members = _r.GetMembers(sm, fa.Name);
                        return members.Count > 0 && members.All(m => m.IsMethod && m.IsStatic);
                    }
                    return false;
                case NameExpr n:
                    if (_m.Functions.Contains(n.Id)) return true;
                    if (_m.Classes.ContainsKey(n.Id) || _moduleFieldTypes.ContainsKey(n.Id)) return false;
                    return _staticTypes.Any(st => st.StartsWith("Typhon.Stdlib.") && _r.GetMembers(st, n.Id) is var ms && ms.Count > 0 && ms.All(m => m.IsMethod && m.IsStatic));
                default:
                    return false;
            }
        }

        /// <summary>Is every use of the name in these statements a call (name(...)), apart from the given binding?</summary>
        static bool OnlyCalled(IEnumerable<Node> roots, string name, Node except)
        {
            var callees = new HashSet<Node>();
            int loads = 0, called = 0, binds = 0;
            foreach (var root in roots)
                AstWalker.VisitAll(root, n =>
                {
                    if (n is CallExpr c) callees.Add(c.Func);
                    if (n is AssignStmt a && a != except) foreach (var t in a.Targets) AstWalker.VisitAll(t, x => { if (x is NameExpr tn && tn.Id == name) binds++; });
                    if (n is AugAssignStmt au && au.Target is NameExpr an && an.Id == name) binds++;
                    if (n is GlobalStmt g && g.Names.Contains(name)) binds++;
                });
            foreach (var root in roots)
                AstWalker.VisitAll(root, n => { if (n is NameExpr ne && ne.Id == name) { loads++; if (callees.Contains(ne)) called++; } });
            // the binding's own target counts as a load in the walk: subtract it
            int ownTargets = except is AssignStmt ea ? ea.Targets.Count(t => t is NameExpr tn && tn.Id == name) : 0;
            return binds == 0 && loads - ownTargets == called && called > 0;
        }

        void FindFunctionAliases(List<Stmt> body)
        {
            _functionAliases.Clear();
            foreach (var s in body)
                if (s is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr n && !_m.Functions.Contains(n.Id) && !_m.Classes.ContainsKey(n.Id) &&
                    IsFunctionReference(a.Value) && OnlyCalled(body, n.Id, a))
                {
                    _functionAliases[n.Id] = a.Value;
                    _aliasTargetNodes.Add(a.Value);
                }
        }

        readonly Dictionary<Param, bool> _paramAliases = new Dictionary<Param, bool>();

        /// <summary>
        /// def solve(..., bisect=bisect): a parameter whose default is a function, which the body only calls and no call in the
        /// program passes. A common idiom (a global made a fast local); the name then stands for the function.
        /// </summary>
        bool IsParamAlias(FunctionDef f, Param p)
        {
            if (f == null || p.Default == null || p.Annotation != null || p.Kind != ParamKind.Normal) return false;
            if (_paramAliases.TryGetValue(p, out var known)) return known;
            bool result = IsFunctionReference(p.Default) && OnlyCalled(f.Body, p.Name, null) && !PassedAnywhere(f, p);
            _paramAliases[p] = result;
            return result;
        }

        /// <summary>Does any call of a function with this name pass the parameter (by position or by name)?</summary>
        bool PassedAnywhere(FunctionDef f, Param p)
        {
            int index = f.Params.IndexOf(p);
            bool method = _p.Modules.Values.Any(m => m.Ast != null && m.Classes.Values.Any(c => c.Body.Contains(f)));
            int positional = method ? index - 1 : index;
            bool passed = false;
            foreach (var m in _p.Modules.Values.Where(m => m.Ast != null))
                AstWalker.VisitAll(m.Ast.Body, n =>
                {
                    if (passed || !(n is CallExpr c)) return;
                    string callee = c.Func is NameExpr cn ? cn.Id : c.Func is AttributeExpr ca ? ca.Name : null;
                    if (callee != f.Name) return;
                    if (c.Args.Any(a => a.Star || a.DoubleStar || a.Name == p.Name) || c.Args.Count(a => a.Name == null) > positional) passed = true;
                });
            return passed;
        }

        /// <summary>The expressions aliases stand for (bisect in bisect=bisect): they name the function itself, never the alias.</summary>
        readonly HashSet<Node> _aliasTargetNodes = new HashSet<Node>();

        /// <summary>The function a called name stands for (a parameter alias in scope, or a module alias), or null.</summary>
        Expr AliasTarget(NameExpr n)
        {
            if (_aliasTargetNodes.Contains(n)) return null;
            for (var sc = _scope; sc != null; sc = sc.Parent)
            {
                if (sc.Aliases.TryGetValue(n.Id, out var t)) return t;
                if (sc.Params.Contains(n.Id) || sc.Types.ContainsKey(n.Id)) return null;
            }
            return !IsLocal(n.Id) && _functionAliases.TryGetValue(n.Id, out var m) ? m : null;
        }

        /// <summary>
        /// A module-level Typhon function passed as an argument (map(f, xs), sorted(xs, key=f)) becomes a lambda: a method
        /// group with dynamic or overloaded parameters cannot convert to Func&lt;int, ...&gt;, a lambda binds per call.
        /// The call inside the lambda has argument types inference cannot see, so the function keeps its dynamic version.
        /// </summary>
        string FunctionValueLambda(NameExpr n)
        {
            if (IsLocal(n.Id) || _renames.ContainsKey(n.Id)) return null;
            FunctionDef def = null; ModuleInfo owner = null;
            if (_m.Functions.Contains(n.Id)) { owner = _m; def = _m.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == n.Id); }
            else if (_importedFromModules.TryGetValue(n.Id, out var imp) && imp.Key.Functions.Contains(imp.Value))
            { owner = imp.Key; def = imp.Key.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == imp.Value); }
            if (def == null || def.TypeParams.Count > 0 || def.Params.Any(p => p.Kind != ParamKind.Normal)) return null;
            // a fully annotated function becomes a lambda with typed parameters: generic callees (cmp_to_key, map, sorted) infer
            // their type arguments from it, which they cannot do from a method group
            if (def.Params.All(p => p.Annotation != null))
            {
                var required = def.Params.Where(p => p.Default == null).ToList();
                if (required.Count == 0) return null;
                var typed = required.Select((p, i) => $"{MapType(p.Annotation, p)} __a{i}");
                return $"(({string.Join(", ", typed)}) => {EmitName(n)}({string.Join(", ", required.Select((_, i) => "__a" + i))}))";
            }
            if (Obs != null && TargetOf(owner, null, def.Name) is InferenceTarget t) Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = null, Context = _ctxFallbackKey });
            var ps = def.Params.Where(p => p.Default == null).Select((p, i) => "__a" + i).ToList();
            string call = $"{EmitName(n)}({string.Join(", ", ps)})";
            return ps.Count == 1 ? $"({ps[0]} => {call})" : $"(({string.Join(", ", ps)}) => {call})";
        }

        /// <summary>Lambdas bound to local names in this function (calls through them type their parameters).</summary>
        readonly Dictionary<string, LambdaExpr> _lambdaLocals = new Dictionary<string, LambdaExpr>();

        /// <summary>A lambda bound to a local name: Func&lt;T..., R&gt; when its calls give the parameter types, else a delegate of dynamic values.</summary>
        string EmitLambdaLocal(LambdaExpr lam, string key, out string code)
        {
            string[] sig = key != null && Ti != null && Ti.LambdaSigs.TryGetValue(key, out var ls) && ls.Length == lam.Params.Count ? ls : null;
            var bindings = new Scope();
            for (int i = 0; i < lam.Params.Count; i++) bindings.Types[lam.Params[i]] = sig?[i] ?? "dynamic";
            _comprehensionTypes.Add(bindings);
            _lambdaParams.Push(new HashSet<string>(lam.Params));
            try
            {
                string ret = sig != null ? PyType(InferType(lam.Body)) : null;
                bool typed = sig != null && ret != null && ret != TypeInference.Pending && ret != "void" && !ret.Contains(TypeInference.Pending);
                string body = typed ? EmitExpr(lam.Body, targetTyped: true) : EmitValue(lam.Body);
                var ps = lam.Params.Select((p, i) => $"{(typed ? sig[i] : "dynamic")} {Esc(p)}");
                code = $"(({string.Join(", ", ps)}) => {body})";
                var types = (typed ? sig : lam.Params.Select(_ => "dynamic")).Concat(new[] { typed ? ret : "dynamic" });
                return $"global::System.Func<{string.Join(", ", types)}>";
            }
            finally { _lambdaParams.Pop(); _comprehensionTypes.Remove(bindings); }
        }
    }
}
