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

        internal static bool IsFunctionValueCandidate(FunctionDef f) =>
            f.TypeParams.Count == 0 && f.Decorators.Count == 0;

        static HashSet<string> ComputeFunctionValueKeys(ProjectContext p)
        {
            var names = new HashSet<string>();
            foreach (var m in p.Modules.Values)
            {
                if (m.Ast == null || m.ParseDiagnostics.Count > 0) continue;
                var callees = new HashSet<Node>();
                var aliases = new List<ImportAlias>();
                // Python method decorators are stored separately after syntax lowering.
                var roots = m.Ast.Body.Cast<Node>().Concat(m.Ast.MethodDecorators.Values.SelectMany(decorators => decorators));
                AstWalker.VisitAll(roots, n =>
                {
                    if (n is CallExpr c) callees.Add(c.Func);
                    if (n is ImportFromStmt imp) aliases.AddRange(imp.Names.Where(a => a.AsName != null));
                });
                AstWalker.VisitAll(roots, n => { if (n is NameExpr ne && !callees.Contains(ne)) names.Add(ne.Id); });
                if (m.RuntimeCodeMode != null) names.UnionWith(m.Functions);
                foreach (var a in aliases) if (names.Contains(a.AsName)) names.Add(a.Name);
            }
            var keys = new HashSet<string>();
            foreach (var m in p.Modules.Values)
                if (m.Ast != null && m.ParseDiagnostics.Count == 0)
                    foreach (var f in m.Ast.Body.OfType<FunctionDef>().Concat(m.ReboundImplementations).Distinct())
                        if (names.Contains(f.Name) && IsFunctionValueCandidate(f)) keys.Add(TypeInference.FuncKey(m, null, f.Name));
            return keys;
        }

        static string ModuleClassName(ModuleInfo m) => m.QualifiedClassName;

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

        string ProjectFunctionAttributeValue(AttributeExpr attribute)
        {
            if (DottedName(attribute.Target) is not string dotted || IsLocal(dotted.Split('.')[0]) ||
                ModuleRef(dotted) is not ModuleInfo module || !module.Functions.Contains(attribute.Name) ||
                ProjectModuleValue(attribute.Target) is not string value) return null;
            if (Obs != null && TargetOf(module, null, attribute.Name) is InferenceTarget target && target.InferParams)
                Obs.Calls.Add(new Observations.Call { Callee = target.Key, Types = null, Context = _ctxFallbackKey });
            return $"{Rt("getattr")}({value}, {CsString(attribute.Name)})";
        }

        /// <summary>The nested functions emitted so far: their definitions and C# signatures (for their values).</summary>
        readonly Dictionary<string, (FunctionDef Def, Signature Sig)> _localFunctionSigs = new Dictionary<string, (FunctionDef, Signature)>();

        /// <summary>Does the name denote a nested function of an enclosing scope (not a variable or parameter)?</summary>
        bool IsLocalFunctionName(string name)
        {
            if (_renames.ContainsKey(name)) return false;
            foreach (var set in _lambdaParams) if (set.ContainsKey(name)) return false;
            for (var s = _scope; s != null; s = s.Parent)
            {
                if (s.Params.Contains(name)) return false;
                if (s.Locals == null) continue;
                if (s.Locals.LocalFunctions.Contains(name)) return !s.Locals.AllLocals.Contains(name);
                if (s.Locals.AllLocals.Contains(name)) return false;
            }
            return false;
        }

        /// <summary>
        /// A nested function as a value (a decorator's wrapper returned, a closure stored or passed where its type is not
        /// kept): a PyClosure that calls it with any arguments, keyword arguments by name. Null if the name is not one.
        /// </summary>
        string LocalFunctionValue(NameExpr n)
        {
            if (!_localFunctionSigs.TryGetValue(n.Id, out var lf) || !IsLocalFunctionName(n.Id) || lf.Def.TypeParams.Count > 0) return null;
            return LocalFunctionObject(n.Id) ?? ClosureCode(lf.Def, lf.Sig, Esc(lf.Def.Name), null);
        }

        string LocalFunctionObject(string name)
        {
            if (!IsLocalFunctionName(name)) return null;
            for (var scope = _scope; scope != null; scope = scope.Parent)
                if (scope.Locals?.LocalFunctions.Contains(name) == true)
                    return scope.LocalFunctionObjects.TryGetValue(name, out var value) ? value : null;
            return null;
        }

        /// <summary>
        /// A PyClosure calling a function or method with any arguments: keyword arguments go to their parameters by name,
        /// the rest to **kwargs; typed parameters get the arguments converted. With <paramref name="selfType"/>, the first
        /// argument is the instance the method <paramref name="callee"/> is called on.
        /// </summary>
        string ClosureCode(FunctionDef f, Signature sig, string callee, string selfType)
        {
            string name = CsString(f.PyName ?? f.Name);
            var all = selfType != null ? f.Params.Skip(1).ToList() : f.Params;
            var normal = all.Where(p => p.Kind == ParamKind.Normal).ToList();
            bool varargs = all.Any(p => p.Kind == ParamKind.VarArgs), kwargs = all.Any(p => p.Kind == ParamKind.KwArgs);
            int required = normal.Count(p => p.Default == null);
            string names = $"new string[] {{ {string.Join(", ", normal.Select(p => CsString(p.Name)))} }}";
            string Conv(Param p, string x)
            {
                string t = sig.Scope.Types.TryGetValue(p.Name, out var pt) ? pt : "dynamic";
                return t == "dynamic" || t == "object" || t == "object?" ? x : $"({t})(dynamic?){x}";
            }
            bool bindPythonArguments = !_m.IsGdScript &&
                normal.Any(p => p.KeywordOnly || p.PositionalOnly || p.Default != null);
            string Call(int count)
            {
                var args = normal.Take(count).Select((p, i) =>
                {
                    string value = Conv(p, $"__p[{i}]");
                    if (!bindPythonArguments || !p.KeywordOnly) return value;
                    string name = IsParamAlias(f, p) ? "__alias_" + p.Name : Esc(p.Name);
                    return $"{name}: {value}";
                }).ToList();
                if (varargs)
                {
                    var vp = all.First(p => p.Kind == ParamKind.VarArgs);
                    string vt = vp.Annotation == null ? "object" : MapType(vp.Annotation, vp);
                    string rest = count < normal.Count ? $"global::System.Array.Empty<{vt}>()" :
                        vt == "object" || vt == "dynamic" ? $"__p[{normal.Count}..]"
                        : $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(__p[{normal.Count}..], __x => ({vt})(dynamic?)__x))";
                    args.Add($"{Esc(vp.Name)}: {rest}");
                }
                if (kwargs)
                {
                    string accepted = bindPythonArguments ? "[]" : names;
                    args.Add($"{Esc(all.Single(p => p.Kind == ParamKind.KwArgs).Name)}: " +
                        $"global::Typhon.Runtime.PyClosure.Kw(__kw, {accepted})");
                }
                string call = $"{(selfType != null ? "__self." : "")}{callee}({string.Join(", ", args)})";
                return sig.ReturnType == "void" ? $"{{ {call}; return null; }}" : $"return {call};";
            }
            var body = new StringBuilder();
            if (selfType != null) body.Append($"var __self = ({selfType})__a[0]!; __a = __a[1..]; ");
            if (bindPythonArguments)
            {
                const string closure = "global::Typhon.Runtime.PyClosure";
                string Default(Param p) => p.Default == null ? $"{closure}.RequiredArgument" :
                    IsParamAlias(f, p) ? "null" :
                    _defaultSources.TryGetValue(p, out string source) ? $"(object?){Paren(source)}" :
                    $"(object?){Paren(EmitValue(p.Default))}";
                string defaults = $"new object?[] {{ {string.Join(", ", normal.Select(Default))} }}";
                int positional = normal.Count(p => !p.KeywordOnly), positionalOnly = normal.Count(p => p.PositionalOnly);
                body.Append($"var __p = {closure}.BindArguments({name}, {names}, __defaults, " +
                    $"{positional}, {positionalOnly}, __a, __kw, {(varargs ? "true" : "false")}, " +
                    $"{(kwargs ? "true" : "false")}, out __kw); ");
                body.Append(Call(normal.Count));
                return NewClosure($"{defaults}, (__a, __kw, __defaults)");
            }
            body.Append($"var __p = global::Typhon.Runtime.PyClosure.Args({name}, {names}, __a, __kw, {(kwargs ? "true" : "false")}); ");
            for (int k = required; k <= normal.Count; k++)
            {
                string condition = varargs && k == normal.Count ? ">=" : "==";
                body.Append($"if (__p.Length {condition} {k}) {Call(k)} ");
            }
            body.Append($"throw global::Typhon.Runtime.PyClosure.ArgCountError({name}, {required}, {(varargs ? "int.MaxValue" : normal.Count.ToString())}, __p.Length);");
            return NewClosure("(__a, __kw)");

            string NewClosure(string arguments)
            {
                string doc = f.Body.FirstOrDefault() is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } text } ? CsString(text.Str) : "null";
                string signature = _m.IsGdScript ? "" : $", _signature = () => {SignatureValue(f)}";
                return $"new global::Typhon.Runtime.PyClosure({name}, {arguments} => {{ {body} }}) " +
                    $"{{ __module__ = {CsString(PythonModuleName)}, __qualname__ = {CsString(sig.Scope.FunctionQualName)}, __doc__ = {doc}{signature} }}";
            }
        }

        /// <summary>A value passed where its static type is lost (a late-bound call's argument): functions become callable objects.</summary>
        string EmitLooseValue(Expr e)
        {
            if (e is LambdaExpr lambda) return EmitRuntimeLambda(lambda);
            if (BoundMethodValue(e) is string bound) return bound;
            if (e is NameExpr n)
            {
                if (LocalFunctionValue(n) is string closure) return closure;
                if (FunctionValue(n, typed: false) is string fv) return fv;
            }
            return EmitValue(e);
        }

        /// <summary>A call through a value of a function type: a direct call of the function (null if the callee is not one).</summary>
        string EmitFunctionValueCall(CallExpr c)
        {
            if (c.Func is NameExpr fn && !IsLocal(fn.Id) && !_moduleFieldTypes.ContainsKey(fn.Id)) return null; // a plain call by name
            if (c.Args.Any(a => a.Star || a.DoubleStar)) return null; // f(*args, **kwargs): made at run time (PythonCall)
            if (!FunctionOfType(InferType(c.Func), out var owner, out var def)) return null;
            if (def.Params.Any(p => p.Kind != ParamKind.Normal))
            {
                string function = EmitExpr(c.Func);
                var positional = c.Args.Where(a => a.Name == null).Select(a => $"(object?){Paren(EmitLooseValue(a.Value))}");
                return $"{Rt("__invoke_kw")}({function}, [{string.Join(", ", positional)}], {KeywordArgs(c.Args)})";
            }
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
            t.InferParams && !UsesFallbackForOmittedArguments(t, args) &&
            ArgTypes(t, args) is string[] types && !types.Any(x => TypeInference.IsUnknown(x));

        /// <summary>The type of a call through a value of a function type (null if the callee is not one).</summary>
        string FunctionValueCallType(CallExpr c)
        {
            if (c.Func is NameExpr fn && !IsLocal(fn.Id) && !_moduleFieldTypes.ContainsKey(fn.Id)) return null;
            if (c.Args.Any(a => a.Star || a.DoubleStar)) return null;
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
            var locals = LocalsAnalyzer.Analyze(f.Body, ps.Select(p => p.Name), null);
            bool returnsValue = f.IsAsync || locals.HasYield || locals.HasValueReturn ||
                                f.Returns != null && !(f.Returns is ConstExpr rc && rc.Kind == ConstKind.None);
            if (!_m.IsGdScript)
            {
                string kind = f.IsAsync ? (locals.HasYield ? "AsyncGenerator" : "Coroutine")
                    : locals.HasYield ? "Generator" : "Ordinary";
                _w.Line($"[global::Typhon.Runtime.PySource({CsString(_path)}, {CsString(PythonModuleName)}, global::Typhon.Runtime.PyCallableKind.{kind}, line: {f.Line})]");
            }
            _w.Line($"internal sealed class {cls} : global::Typhon.Runtime.PyFunction");
            _w.Open();
            _w.Line($"public static readonly {cls} Instance = new {cls}();");
            string doc = f.Body.Count > 0 && f.Body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? CsString(dc.Str) : null;
            string module = _m.IsGdScript ? "" : $"__module__ = {CsString(PythonModuleName)}; _signature = {SignatureProviderName(f)}; ";
            string source = "";
            if (!_m.IsGdScript)
            {
                var positional = ps.Where(parameter => parameter.Kind == ParamKind.Normal && !parameter.KeywordOnly).ToList();
                var keywords = ps.Where(parameter => parameter.Kind == ParamKind.Normal && parameter.KeywordOnly).ToList();
                string arity = $"({positional.Count}, {positional.Count(parameter => parameter.PositionalOnly)}, {keywords.Count})";
                var parameters = positional.Concat(keywords).Concat(ps.Where(parameter => parameter.Kind != ParamKind.Normal))
                    .Select(parameter => parameter.Name);
                string names = $"new string[] {{ {string.Join(", ", parameters.Concat(locals.AllLocals).Distinct().Select(CsString))} }}";
                long flags = 0x3L; // OPTIMIZED and NEWLOCALS
                if (ps.Any(parameter => parameter.Kind == ParamKind.VarArgs)) flags |= 0x4;
                if (ps.Any(parameter => parameter.Kind == ParamKind.KwArgs)) flags |= 0x8;
                if (f.IsAsync) flags |= locals.HasYield ? 0x200 : 0x80;
                else if (locals.HasYield) flags |= 0x20;
                if (doc != null) flags |= 0x4000000;
                if (HasFutureAnnotations) flags |= FutureAnnotationsFlag;

                if (_m.RuntimeCodeMode == null && f.EndOffset > f.StartOffset && f.EndOffset <= _m.SourceText?.Length)
                {
                    string text = CsString(_m.SourceText.Substring(f.StartOffset, f.EndOffset - f.StartOffset));
                    source = $"_code = new global::Typhon.Runtime.RuntimeFunctionCode(__name__, {CsString(_path)}, {f.Line}, " +
                        $"{text}, {(_big ? "true" : "false")}, {flags}, {arity}, {names}); ";
                }
                else
                    source = $"_code = new global::Typhon.Stdlib.types.CodeType(__name__, {CsString(_path)}, {f.Line}, {flags}, " +
                        $"arity: {arity}, variables: new {PyTuplePrefix}string>({names})); ";
            }
            _w.Line($"{cls}() : base({CsString(f.PyName ?? f.Name)}) {{ {module}{source}{(doc != null ? $"__doc__ = {doc}; " : "")}}}");
            if (ps.Any(p => p.Kind != ParamKind.Normal))
            {
                var signature = BuildSignature(f, dropFirst: false);
                _w.Line($"static readonly global::Typhon.Runtime.PyClosure __closure = {ClosureCode(f, signature, Esc(f.Name), null)};");
                _w.Line("public override object? __call__(object?[] args) => __closure.__call__(args);");
                _w.Line("public override object? __call_kw__(object?[] args, IDictionary<string, object?>? kwargs) => __closure.__call_kw__(args, kwargs);");
                _w.Close();
                return;
            }
            // parameter names: keyword arguments of a late-bound call (f(*args, **kwargs) in a decorator's wrapper) by name
            if (ps.Count > 0 && ps.All(x => x.Kind == ParamKind.Normal))
                _w.Line($"protected override string[] __params__ => new[] {{ {string.Join(", ", ps.Select(x => CsString(x.Name)))} }};");
            if (ps.Any(p => p.Default != null))
            {
                _w.Line($"static readonly global::Typhon.Runtime.PyBoundMethod __bound = new global::Typhon.Runtime.PyBoundMethod(typeof({ModuleClassName(_m)}), {CsString(f.Name)}, includeNonPublic: true);");
                _w.Line("public override object? __call__(object?[] args) => __bound.__call__(args);");
                _w.Line("public override object? __call_kw__(object?[] args, IDictionary<string, object?>? kw) => __bound.__call_kw__(args, kw);");
            }
            else
            {
                _w.Line("public override object? __call__(object?[] a)");
                _w.Open();
                var args = Enumerable.Range(0, ps.Count).Select(i => $"(dynamic?)a[{i}]");
                string call = $"{Esc(f.Name)}({string.Join(", ", args)})";
                _w.Line(returnsValue ? $"if (a.Length == {ps.Count}) return {call};" : $"if (a.Length == {ps.Count}) {{ {call}; return null; }}");
                _w.Line($"throw ArgCount({CsString(f.Name)}, {ps.Count}, {ps.Count}, a.Length);");
                _w.Close();
            }
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
                if (!delegates.Add(d)) return;
                // A definition-time default can make the CLR parameter nullable even when the Python annotation is not.
                string function = Esc(f.Name);
                if (f.Params.Any(parameter => parameter.Default != null))
                {
                    string arguments = string.Join(", ", Enumerable.Range(0, types.Count).Select(index => "__arg" + index));
                    function = $"({arguments}) => {function}({arguments})";
                }
                _w.Line($"public static implicit operator {d}({cls} _) => {function};");
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
        /// <summary>Names a renaming import binds to a standard module's member (from math import pi as _pi): values too.</summary>
        readonly Dictionary<string, Expr> _importAliases = new Dictionary<string, Expr>();

        Expr ImportAlias(NameExpr n) => !IsLocal(n.Id) && !_moduleFieldTypes.ContainsKey(n.Id) && _importAliases.TryGetValue(n.Id, out var t) ? t : null;

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
                if (s is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr n && !_m.Functions.Contains(n.Id) &&
                    !_m.Classes.ContainsKey(n.Id) && !_m.ReboundFunctions.Contains(n.Id) &&
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
            return !IsLocal(n.Id) && _functionAliases.TryGetValue(n.Id, out var m) ? m : ImportAlias(n);
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

        Dictionary<string, string> LambdaParameters(IEnumerable<string> names) =>
            names.Distinct().ToDictionary(p => p, p => IsLocal(p) ? NewTmp("lambda") : Esc(p));

        string EmitRuntimeLambda(LambdaExpr lambda)
        {
            const string closure = "global::Typhon.Runtime.PyClosure";
            const string name = "\"<lambda>\"";
            string args = NewTmp("args"), kwargs = NewTmp("kwargs"), names = NewTmp("params");
            string bound = NewTmp("bound");
            int count = lambda.Params.Count;
            var bindings = new Scope();
            var code = new StringBuilder();
            var parameters = LambdaParameters(lambda.ParameterNames());
            int required = count - lambda.Defaults.Count;
            bool hasDefaults = required != count;
            string defaults = NewTmp("defaults");
            // These values are evaluated in the defining scope and captured once by the closure.
            string captured = hasDefaults
                ? $"new object?[] {{ {string.Join(", ", lambda.Defaults.Select(d => $"(object?){Paren(d is LambdaExpr inner ? EmitRuntimeLambda(inner) : EmitLooseValue(d))}"))} }}, "
                : "";
            string defaultArgument = hasDefaults ? ", " + defaults : "";
            code.Append($"new {closure}({name}, {captured}({args}, {kwargs}{defaultArgument}) => {{ ");
            code.Append($"string[] {names} = [{string.Join(", ", lambda.Params.Select(CsString))}]; ");
            code.Append($"var {bound} = {closure}.Args({name}, {names}, {args}, {kwargs}, {(lambda.KwArgs != null ? "true" : "false")}{defaultArgument}); ");
            string condition = lambda.VarArgs == null ? "!=" : "<";
            string max = lambda.VarArgs == null ? count.ToString() : "int.MaxValue";
            string arity = $"{closure}.ArgCountError({name}, {required}, {max}, {bound}.Length)";
            if (hasDefaults) arity = $"new global::System.InvalidCastException({arity}.Message)";
            code.Append($"if ({bound}.Length {condition} {count}) throw {arity}; ");
            for (int i = 0; i < count; i++)
            {
                bindings.Types[lambda.Params[i]] = "dynamic";
                code.Append($"dynamic {parameters[lambda.Params[i]]} = {bound}[{i}]; ");
            }
            if (lambda.VarArgs != null)
            {
                string tuple = PyTuplePrefix + "dynamic>";
                bindings.Types[lambda.VarArgs] = tuple;
                code.Append($"var {parameters[lambda.VarArgs]} = new {tuple}({bound}[{count}..]); ");
            }
            if (lambda.KwArgs != null)
            {
                bindings.Types[lambda.KwArgs] = "OrderedDictionary<string, dynamic>";
                code.Append($"OrderedDictionary<string, dynamic> {parameters[lambda.KwArgs]} = {closure}.Kw({kwargs}, {names}); ");
            }
            _comprehensionTypes.Add(bindings);
            _lambdaParams.Push(parameters);
            try
            {
                string body = IsGeneratorLambda(lambda) ? GeneratorLambdaBody(lambda.Body) : LambdaValueStatements(lambda.Body);
                return code.Append($" {body} }})").ToString();
            }
            finally { _lambdaParams.Pop(); _comprehensionTypes.Remove(bindings); }
        }

        readonly Stack<string> _lambdaGenerators = new Stack<string>();

        static bool IsGeneratorLambda(LambdaExpr lambda) =>
            AstWalker.ContainsOutsideNestedScopes(new List<Stmt> { new ReturnStmt { Value = lambda.Body } }, node => node is YieldExpr);

        string GeneratorLambdaBody(Expr expression)
        {
            string generator = NewTmp("gen");
            _lambdaGenerators.Push(generator);
            try
            {
                return $"var {generator} = new {PyGeneratorPrefix}object?>(); " +
                    $"{generator}.__start_async(async () => {{ {LambdaValueStatements(expression)} }}); return {generator};";
            }
            finally { _lambdaGenerators.Pop(); }
        }

        string LambdaValueStatements(Expr body) =>
            LambdaReturnsVoid(body) ? $"{EmitExpr(body)}; return null;" : $"return {EmitValue(body)};";

        bool LambdaReturnsVoid(Expr body)
        {
            if (InferType(body) == "void") return true;
            if (body is not CallExpr call) return false;
            if (call.Func is NameExpr { Id: "print" } && !IsShadowed("print")) return true;
            if (call.Func is AttributeExpr { Target: NameExpr { Id: "self" } } && HasSelfInScope() &&
                TyphonCallee(call, out int implicitParams) is FunctionDef source && implicitParams != 0 &&
                _cls?.Def.Body.Contains(source) == true && !source.IsAsync && !_m.Ast.MethodDecorators.ContainsKey(source))
            {
                var locals = LocalsAnalyzer.Analyze(source.Body, source.Params.Select(parameter => parameter.Name), null);
                if (!locals.HasYield && (source.Returns != null
                    ? MapType(source.Returns, source, isReturn: true) == "void"
                    : !locals.HasValueReturn && !ImplicitMethodResultIsDynamic(source) &&
                        DotNetBaseMethod(_cls.Def, source)?.ReturnsDynamic != true))
                    return true;
            }
            int positional = call.Args.Count(arg => arg.Name == null && !arg.Star && !arg.DoubleStar);
            var named = call.Args.Where(arg => arg.Name != null).Select(arg => arg.Name).ToList();
            var overloads = DotNetOverloads(call);
            if (overloads == null && call.Func is NameExpr name && !IsShadowed(name.Id))
                overloads = _r.GetMembers("Typhon.Runtime.Builtins", name.Id).Where(m => m.IsMethod && m.IsStatic).ToList();
            var methods = overloads?.Where(method => Applicable(method, positional, named)).ToList();
            return methods is { Count: > 0 } && methods.All(method => method.ReturnsVoid);
        }

        /// <summary>A lambda bound to a local name: Func&lt;T..., R&gt; when its calls give the parameter types, else a delegate of dynamic values.</summary>
        string EmitLambdaLocal(LambdaExpr lam, string key, out string code)
        {
            if (lam.RequiresArgumentBinding || IsGeneratorLambda(lam))
            {
                code = EmitRuntimeLambda(lam);
                return "dynamic";
            }
            string[] sig = key != null && Ti != null && Ti.LambdaSigs.TryGetValue(key, out var ls) && ls.Length == lam.Params.Count ? ls : null;
            var bindings = new Scope();
            for (int i = 0; i < lam.Params.Count; i++) bindings.Types[lam.Params[i]] = sig?[i] ?? "dynamic";
            _comprehensionTypes.Add(bindings);
            var parameters = LambdaParameters(lam.Params);
            _lambdaParams.Push(parameters);
            try
            {
                string ret = sig != null ? PyType(InferType(lam.Body)) : null;
                bool typed = sig != null && ret != null && ret != TypeInference.Pending && ret != "void" && !ret.Contains(TypeInference.Pending);
                string body = LambdaReturnsVoid(lam.Body) ? $"{{ {LambdaValueStatements(lam.Body)} }}" :
                    typed ? EmitExpr(lam.Body, targetTyped: true) : EmitValue(lam.Body);
                var ps = lam.Params.Select((p, i) => $"{(typed ? sig[i] : "dynamic")} {parameters[p]}");
                code = $"(({string.Join(", ", ps)}) => {body})";
                var types = (typed ? sig : lam.Params.Select(_ => "dynamic")).Concat(new[] { typed ? ret : "dynamic" });
                return $"global::System.Func<{string.Join(", ", types)}>";
            }
            finally { _lambdaParams.Pop(); _comprehensionTypes.Remove(bindings); }
        }
    }
}
