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
    // Constructors: inherited ones, typed variants, and Python's two-phase construction (allocate, then __init__).
    internal sealed partial class ModuleEmitter
    {
        IReadOnlyDictionary<string, string> _inheritedTypeBindings;

        string MapInheritedType(Expr annotation, ClassDef owner, IReadOnlyDictionary<string, string> bindings, bool isReturn = false)
        {
            var previous = _inheritedTypeBindings;
            _inheritedTypeBindings = bindings;
            try { return MapType(annotation, owner, isReturn); }
            finally { _inheritedTypeBindings = previous; }
        }

        /// <summary>
        /// Python inherits __init__, C# does not inherit constructors: a class without __init__ gets one constructor per
        /// constructor of its base (the nearest Typhon __init__, or the .NET base's public/protected constructors).
        /// </summary>
        void EmitInheritedConstructors(ClassContext ctx, List<Expr> bases, ClassDef baseOwner = null, int depth = 0,
            IReadOnlyDictionary<string, string> bindings = null)
        {
            // a subclass of a two-phase class allocates through it too
            if (depth == 0 && NearestInitAncestor(ctx.Def) is ClassDef twoPhaseOwner && TwoPhaseClasses.Contains(twoPhaseOwner) && !ctx.NoInitDone)
            {
                _w.Line($"private protected {Esc(ctx.Name)}({NoInitType} _) : base(_) {{ }}");
                ctx.NoInitDone = true;
            }
            var resolver = baseOwner == null ? this : ClassDeclarationResolver(baseOwner);
            foreach (var b in bases)
            {
                if (depth > 20) continue;
                if (resolver.BuiltinListBase(b))
                {
                    string name = Esc(ctx.Name);
                    _w.Line($"public {name}() {{ }}");
                    _w.Line($"public {name}([global::Typhon.Runtime.PyPositionalOnly] object? iterable) {{ {Rt("__list_initialize")}(this, [iterable], null); }}");
                    return;
                }
                if (!resolver.ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (IsProtocol(py)) continue;
                    var parentBindings = new Dictionary<string, string>();
                    if (b is SubscriptExpr genericBase && py.TypeParams.Count > 0)
                    {
                        var arguments = genericBase.Index is TupleExpr tuple ? tuple.Elts : new List<Expr> { genericBase.Index };
                        for (int i = 0; i < Math.Min(arguments.Count, py.TypeParams.Count); i++)
                            parentBindings[py.TypeParams[i].Name] = resolver.MapInheritedType(arguments[i], baseOwner ?? ctx.Def, bindings);
                    }
                    var parentResolver = ClassDeclarationResolver(py);
                    string ParameterType(Param parameter) => parentBindings.Count > 0
                        ? parentResolver.MapInheritedType(parameter.Annotation, py, parentBindings)
                        : MapType(parameter.Annotation, py);
                    var init = py.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
                    if (init == null)
                    {
                        if (HasDecorator(py.Decorators, "dataclass")) return;
                        EmitInheritedConstructors(ctx, py.Bases, py, depth + 1, parentBindings);
                        return;
                    }
                    var initOwner = _p.DeclaringModule(py) ?? _m;
                    if (InitTarget(initOwner, py) is InferenceTarget it &&
                        it.InferParams && Ti.Signatures.TryGetValue(it.Key, out var baseSigs))
                    {
                        bool emitFallback = Ti.Fallback.Contains(it.Key);
                        foreach (var types in baseSigs)
                        {
                            var tps = it.Params.Select((p, i) => $"{(types[i] == TypeInference.Declared ? ParameterType(p) : types[i])} {Esc(p.Name)}").ToList();
                            var defaults = new string[tps.Count];
                            for (int i = 0; i < tps.Count; i++)
                            {
                                var p = it.Params[i];
                                string pt = types[i] == TypeInference.Declared ? ParameterType(p) : types[i];
                                if (p.Default == null || emitFallback) continue;
                                if (!ClassDeclarationResolver(py).IsConstantDefault(p.Default))
                                {
                                    if (p.Annotation != null || InferType(p.Default) == pt)
                                    {
                                        tps[i] = DefinitionDefaultParameter(pt, Esc(p.Name), out defaults[i]);
                                    }
                                    continue;
                                }
                                if (pt == "dynamic" || pt == "object")
                                {
                                    if (!IsConstantDefault(p.Default)) continue;
                                    if (IsNone(p.Default)) defaults[i] = "null";
                                    else
                                    {
                                        tps[i] = WithOptionalDefault(tps[i], EmitValue(p.Default));
                                        defaults[i] = "";
                                    }
                                }
                                else if (InferType(p.Default) == pt || IsNone(p.Default) && pt.EndsWith("?"))
                                    defaults[i] = EmitExpr(p.Default, targetTyped: true);
                            }
                            ApplyParameterDefaults(tps, defaults);
                            // (a dynamic parameter goes as object: a constructor initializer cannot be dispatched dynamically)
                            var baseArgs = it.Params.Select((p, i) => types[i] == "dynamic" ? $"(object?){Esc(p.Name)}" : Esc(p.Name));
                            _w.Line($"{SurfaceVisibility("public", string.Join(", ", tps))} {Esc(ctx.Name)}({string.Join(", ", tps)}) : base({string.Join(", ", baseArgs)}) {{ }}");
                        }
                        _w.Line();
                        if (!emitFallback) return;
                    }
                    if (init.Params.Skip(1).Any(parameter => parameter.Kind != ParamKind.Normal))
                    {
                        var previousBindings = parentResolver._inheritedTypeBindings;
                        parentResolver._inheritedTypeBindings = parentBindings;
                        try
                        {
                            var signature = parentResolver.BuildSignature(init, dropFirst: true, forOverrideSlot: true);
                            string visibility = SurfaceVisibility("public", signature.Params);
                            _w.Line($"{visibility} {Esc(ctx.Name)}({signature.Params}) : base({signature.ForwardedConstructorArguments}) {{ }}");
                            _w.Line();
                        }
                        finally
                        {
                            parentResolver._inheritedTypeBindings = previousBindings;
                        }
                        return;
                    }

                    var ps = new List<string>(); var names = new List<string>(); var baseDefaults = new List<string>();
                    foreach (var p in init.Params.Skip(1))
                    {
                        string type = p.Annotation != null ? ParameterType(p) : "dynamic";
                        // (a late-bound parameter with a number/bool/str default: C# allows only null as a dynamic parameter's
                        // default, but honours [Optional, DefaultParameterValue(...)])
                        if (p.Default != null && !ClassDeclarationResolver(py).IsConstantDefault(p.Default))
                        {
                            // The base constructor already reads its captured definition-time default when omitted.
                            ps.Add(DefinitionDefaultParameter(type, Esc(p.Name), out var omitted));
                            baseDefaults.Add(omitted);
                        }
                        else if (p.Default != null && (type == "dynamic" || type == "object") && !IsNone(p.Default))
                        {
                            ps.Add(WithOptionalDefault($"{type} {Esc(p.Name)}", EmitValue(p.Default)));
                            baseDefaults.Add("");
                        }
                        else
                        {
                            ps.Add($"{type} {Esc(p.Name)}");
                            baseDefaults.Add(p.Default != null ? EmitExpr(p.Default, targetTyped: true) : null);
                        }
                        // a constructor initializer cannot be dispatched dynamically: late-bound values go as object
                        names.Add(type == "dynamic" ? $"(object?){Esc(p.Name)}" : Esc(p.Name));
                    }
                    ApplyParameterDefaults(ps, baseDefaults);
                    _w.Line($"{SurfaceVisibility("public", string.Join(", ", ps))} {Esc(ctx.Name)}({string.Join(", ", ps)}) : base({string.Join(", ", names)}) {{ }}");
                    _w.Line();
                    return;
                }
                // class CycleError(ValueError) without an __init__: Python's exception constructor, any arguments, kept as its args
                // (the .NET base's constructors would bind CycleError("msg", nodes) to (string message, Exception inner))
                if (meta != null && !meta.StartsWith("Typhon.") && ExceptionMetaMap.ContainsValue(meta))
                {
                    _w.Line($"public {Esc(ctx.Name)}(params object?[]? args) : base(global::Typhon.Runtime.PyExc.Text(global::Typhon.Runtime.PyExc.Norm(args))) {{ Data[\"__args__\"] = global::Typhon.Runtime.PyExc.Norm(args); }}");
                    _w.Line();
                    return;
                }
                if (meta != null)
                {
                    var ctors = _r.GetConstructors(meta);
                    foreach (var c in ctors)
                    {
                        var ps = c.Parameters.Select((parameter, index) =>
                        {
                            string modifier = c.Signature?.Parameters[index].IsParams == true ? "params " : "";
                            return $"{modifier}{parameter.Key} {Esc(parameter.Value)}";
                        }).ToList();
                        if (c.Defaults.Count == ps.Count)
                        {
                            var defaults = c.Defaults.ToArray();
                            for (int i = 0; i < defaults.Length; i++)
                            {
                                if (defaults[i] == null || defaults[i] == "null" || c.Parameters[i].Key is not ("object" or "dynamic"))
                                    continue;
                                ps[i] = WithOptionalDefault(ps[i], defaults[i]);
                                defaults[i] = "";
                            }
                            ApplyParameterDefaults(ps, defaults);
                        }
                        _w.Line($"{(c.IsProtected ? "protected" : "public")} {Esc(ctx.Name)}({string.Join(", ", ps)}) : base({string.Join(", ", c.Parameters.Select(kv => Esc(kv.Value)))}) {{ }}");
                    }
                    if (ctors.Count > 0) _w.Line();
                    return;
                }
            }
        }

        void EmitConstructor(FunctionDef f, ClassContext ctx, List<Expr> bases)
        {
            var t = TargetOf(_m, ctx.Def, "__init__");
            if (t != null && t.Class != ctx.Def) t = null;
            if (t != null && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
            {
                foreach (var types in sigs) { EmitConstructorVariant(f, ctx, bases, types, null); _w.Line(); }
                if (Ti.Fallback.Contains(t.Key)) EmitConstructorVariant(f, ctx, bases, null, t.Key);
                return;
            }
            EmitConstructorVariant(f, ctx, bases, null, t != null && t.InferParams ? t.Key : null);
        }

        void EmitConstructorVariant(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes, string fallbackKey)
        {
            var savedCtx = _ctxFallbackKey; _ctxFallbackKey = fallbackKey;
            if (Obs != null) Obs.InFallbackBody = fallbackKey != null;
            var savedScopeKey = _scopeKey; _scopeKey = TypeInference.FuncKey(_m, ctx.Def, "__init__") + "#" + TypeInference.SigKey(paramTypes);
            try { EmitConstructorCore(f, ctx, bases, paramTypes); }
            finally { _ctxFallbackKey = savedCtx; _scopeKey = savedScopeKey; if (Obs != null) Obs.InFallbackBody = savedCtx != null; }
        }


        const string NoInitType = "global::Typhon.Runtime.NoInit";

        static bool IsSuperInit(Stmt st) => st is ExprStmt es && es.Value is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" &&
                                            a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super";

        /// <summary>Does __init__ run its base class's __init__ somewhere other than as its first statement?</summary>
        /// <summary>An __init__ without super().__init__(...) (Base.__init__(self, ...) is normalized to it).</summary>
        static bool NeverCallsBaseInit(FunctionDef init)
        {
            bool found = false;
            AstWalker.VisitAll(init.Body, n =>
            {
                if (n is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" && a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super") found = true;
            });
            return !found;
        }

        static bool DefersBaseInit(FunctionDef init)
        {
            int first = init.Body.Count > 0 && init.Body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? 1 : 0;
            bool found = false, deferred = false;
            AstWalker.VisitAll(init.Body, n =>
            {
                if (n is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" && a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super")
                {
                    found = true;
                    if (!(init.Body.Count > first && init.Body[first] is ExprStmt es && es.Value == c)) deferred = true;
                }
            });
            return found && deferred;
        }

        bool ForwardsBaseInit(ClassDef cls, FunctionDef init)
        {
            if (NearestInitAncestor(cls) is not ClassDef parent || parent.TypeParams.Count > 0 ||
                parent.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__") is not FunctionDef baseInit ||
                baseInit.Params.Any(p => p.Kind != ParamKind.Normal)) return false;
            return AstWalker.ContainsOutsideNestedScopes(init.Body, node =>
                node is CallExpr { Func: AttributeExpr { Name: "__init__", Target: CallExpr { Func: NameExpr { Id: "super" } } } } call &&
                call.Args.Any(a => a.Star || a.DoubleStar));
        }

        readonly Dictionary<ModuleInfo, ModuleEmitter> _classDeclarationResolvers = new Dictionary<ModuleInfo, ModuleEmitter>();

        // Base names belong to the module in which the class was declared.
        ModuleEmitter ClassDeclarationResolver(ClassDef cls)
        {
            var owner = _p.DeclaringModule(cls);
            if (owner == null || owner == _m && _scope == null) return this;
            if (_classDeclarationResolvers.TryGetValue(owner, out var resolver)) return resolver;
            resolver = CreateClassDeclarationResolver(owner, _p);
            _classDeclarationResolvers[owner] = resolver;
            return resolver;
        }

        internal static ModuleEmitter CreateClassDeclarationResolver(ModuleInfo owner, ProjectContext project)
        {
            var resolver = new ModuleEmitter(owner, project, new List<TyDiagnostic>(), dryRun: true);
            foreach (var import in TyphonCompiler.Imports(owner))
            {
                if (import is ImportStmt plain) resolver.ProcessImport(plain);
                else if (import is ImportFromStmt from) resolver.ProcessImportFrom(from);
            }
            return resolver;
        }

        internal bool HasRuntimeBaseMember(ClassDef definition, string name, bool instanceMethod = false, int depth = 0)
        {
            if (depth > 20) return false;
            foreach (var basis in definition.Bases)
            {
                if (!ResolveBase(basis, out var python, out var runtime)) continue;
                if (python != null && ClassDeclarationResolver(python).HasRuntimeBaseMember(python, name, instanceMethod, depth + 1))
                    return true;
                if (runtime != null && (instanceMethod
                    ? _r.GetMembers(runtime, name).Any(member => member.IsMethod && !member.IsStatic)
                    : _r.HasMember(runtime, name))) return true;
            }
            return false;
        }

        /// <summary>The nearest Typhon ancestor (along first bases) that defines __init__; null if none (or a .NET base comes first).</summary>
        ClassDef NearestInitAncestor(ClassDef c)
        {
            var k = c;
            for (int depth = 0; depth < 50; depth++)
            {
                if (k.Bases.Count == 0) return null;
                if (!ClassDeclarationResolver(k).ResolveBase(k.Bases[0], out var py, out _) || py == null) return null;
                if (py.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")) return py;
                k = py;
            }
            return null;
        }

        HashSet<ClassDef> TwoPhaseClasses
        {
            get
            {
                if (_p.TwoPhaseClasses != null) return _p.TwoPhaseClasses;
                var set = new HashSet<ClassDef>();
                foreach (var c in AllClasses())
                {
                    var init = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
                    if (UsesCustomAllocation(c) || HasListBase(c))
                        for (var owner = CustomInitializerOwner(c); owner != null && set.Add(owner); owner = NearestInitAncestor(owner)) { }
                    bool explicitBaseInitializer = c.Body.OfType<FunctionDef>().Any(method =>
                        AstWalker.ContainsOutsideNestedScopes(method.Body, node =>
                            node is CallExpr
                            {
                                Func: AttributeExpr
                                {
                                    Name: "__init__", Target: CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 2 } proxy
                                }
                            } && proxy.Args[0].Value is NameExpr owner && owner.Id == c.Name));
                    if (explicitBaseInitializer)
                        for (var owner = NearestInitAncestor(c); owner != null && set.Add(owner); owner = NearestInitAncestor(owner)) { }
                    // (an __init__ that never calls its base's: Python allows it, the base is then only allocated)
                    if (init == null || !(DefersBaseInit(init) || ForwardsBaseInit(c, init) ||
                        NeverCallsBaseInit(init) && NearestInitAncestor(c) != null)) continue;
                    for (var b = NearestInitAncestor(c); b != null && set.Add(b); b = NearestInitAncestor(b)) { }
                }
                return _p.TwoPhaseClasses = set;
            }
        }

        Dictionary<string, string> _baseInitializerArguments;

        bool IsBuiltinExceptionName(Expr expression) =>
            expression is NameExpr name && ExceptionMetaMap.ContainsKey(name.Id) && !IsShadowed(name.Id);

        bool HasNativeExceptionBase(ClassDef definition) => definition.Bases.Any(basis =>
            ResolveBase(basis, out var source, out var metadata) && source == null &&
            metadata != null && !metadata.StartsWith("Typhon.") && ExceptionMetaMap.ContainsValue(metadata));

        void EmitConstructorCore(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes)
        {
            string key = TypeInference.FuncKey(_m, ctx.Def, f.Name);
            var sig = BuildSignature(f, dropFirst: true, paramTypes: paramTypes,
                keepSpecializedDefaults: Ti?.Fallback.Contains(key) != true);
            sig.Scope.ReturnType = "void";
            sig.Scope.HasSelf = true;
            sig.Scope.Params.Remove(f.Params[0].Name);
            bool twoPhase = TwoPhaseClasses.Contains(ctx.Def);
            if (twoPhase || DefersBaseInit(f) || ForwardsBaseInit(ctx.Def, f) ||
                NeverCallsBaseInit(f) && NearestInitAncestor(ctx.Def) != null)
            {
                // allocate with the base's allocation-only constructor; super().__init__(...) runs where the body has it
                string alloc = NearestInitAncestor(ctx.Def) != null ? $" : base(default({NoInitType}))" : "";
                if (HasNativeExceptionBase(ctx.Def))
                    sig.Prologue.Insert(0, "global::Typhon.Runtime.PyExc.Initialize(this, []);");
                EmitDocComment(f.Body);
                LineDirective(f);
                EmitAttributes(f.Decorators);
                if (twoPhase)
                {
                    if (!ctx.NoInitDone) { _w.Line($"private protected {Esc(ctx.Name)}({NoInitType} _){alloc} {{ }}"); ctx.NoInitDone = true; }
                    _w.Line($"{SurfaceVisibility("public", sig.Params)} {Esc(ctx.Name)}({sig.Params}){alloc} {{ __init__({sig.ForwardedConstructorArguments}); }}");
                    LineDirective(f);
                    string visibility = SurfaceVisibility("protected", sig.Params) == "internal" ? "private protected" : "protected";
                    _w.Line($"{visibility} {(NearestInitAncestor(ctx.Def) != null ? "new " : "")}void __init__({sig.Params})");
                }
                else _w.Line($"{SurfaceVisibility("public", sig.Params)} {Esc(ctx.Name)}({sig.Params}){alloc}");
                EmitSignatureBody(f.Body, sig);
                return;
            }
            var body = f.Body;
            string baseCall = "";
            int first = body.Count > 0 && body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? 1 : 0;
            // super().__init__(args), or the older Base.__init__(self, args): the base constructor
            List<Arg> superArgs = null;
            if (body.Count > first && body[first] is ExprStmt es && es.Value is CallExpr call && call.Func is AttributeExpr at && at.Name == "__init__")
            {
                if (at.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super")
                {
                    bool packedList = !_m.IsGdScript && !IsShadowed("list") &&
                        bases.FirstOrDefault() is Expr firstBase &&
                        DottedName(firstBase is SubscriptExpr generic ? generic.Target : firstBase) == "list" &&
                        call.Args.Any(argument => argument.Star || argument.DoubleStar || argument.Name != null);
                    if (!packedList) superArgs = call.Args;
                }
                else if (DottedName(at.Target) is string bname && bases.Any(b => DottedName(b is SubscriptExpr bs ? bs.Target : b) == bname) &&
                         call.Args.Count >= 1 && call.Args[0].Value is NameExpr selfArg && selfArg.Id == f.Params[0].Name && call.Args[0].Name == null)
                    superArgs = call.Args.Skip(1).ToList();
            }
            // super().__init__(*args, **kwargs) in a class without a base (a mixin's cooperative __init__, compiled on its own):
            // object's __init__, which does nothing
            if (superArgs != null && bases.Count == 0)
            {
                body = body.Where((s, i) => i != first).ToList();
                sig.Locals = LocalsAnalyzer.Analyze(body, f.Params.Select(p => p.Name), null);
                superArgs = null;
            }
            if (!_m.IsGdScript && superArgs != null && bases.Count > 0 && HasNativeExceptionBase(ctx.Def) &&
                IsBuiltinExceptionName(bases[0]) && superArgs.All(argument => argument.Name == null && !argument.DoubleStar))
                superArgs = null; // Python's exception arguments are assigned in the initializer body.

            if (superArgs != null)
            {
                bool copyDictionary = bases[0] is NameExpr { Id: "dict" } && !IsShadowed("dict") &&
                    superArgs.Count == 1 && superArgs[0] is Arg { Name: null, Star: false, DoubleStar: false };
                var saved = _scope; _scope = sig.Scope;
                var savedBaseArguments = _baseInitializerArguments;
                _baseInitializerArguments = sig.DefaultArguments;
                var baseInit = NearestInitAncestor(ctx.Def)?.Body.OfType<FunctionDef>().FirstOrDefault(fd => fd.Name == "__init__");
                string kwBase = KwargsCallArgs(baseInit, 1, superArgs, constructorInitializer: true);
                if (copyDictionary) kwBase = $"{Rt("dict")}((object?){Paren(EmitValue(superArgs[0].Value))})";
                // CLR constructor initializers require static binding even when a Python argument is dynamic.
                bool NeedsStaticBinding(Arg arg) => IsDynamicallyDispatched(arg.Value) ||
                    arg.Value is CallExpr && IsUnknownType(InferType(arg.Value)) || InferType(arg.Value) == "dynamic";
                bool lateBound = superArgs.Any(NeedsStaticBinding) && superArgs.All(x => !x.Star && !x.DoubleStar);
                string BaseArgument(Arg arg, int index)
                {
                    string value = EmitValue(arg.Value);
                    if (NeedsStaticBinding(arg))
                    {
                        var parameter = arg.Name == null ? baseInit?.Params.Skip(1).ElementAtOrDefault(index) :
                            baseInit?.Params.Skip(1).FirstOrDefault(p => p.Name == arg.Name);
                        string type = parameter?.Annotation != null ? MapType(parameter.Annotation, parameter) : null;
                        value = $"({(type == null || type == "dynamic" ? "object?" : type)}){Paren(value)}";
                    }
                    return arg.Name == null ? value : $"{Esc(arg.Name)}: {value}";
                }
                baseCall = " : base(" + (kwBase ?? (lateBound ? string.Join(", ", superArgs.Select(BaseArgument)) : EmitArgs(superArgs))) + ")";
                if (Obs != null && bases.Count > 0 && ResolveBase(bases[0], out var basePy, out _) && basePy != null &&
                    _p.TryFindClass(_m, basePy.Name, out var bcls, out var bowner))
                    RecordCall(InitTarget(bowner, bcls), superArgs);
                _baseInitializerArguments = savedBaseArguments;
                _scope = saved;
                body = body.Where((s, i) => i != first).ToList();
                sig.Locals = LocalsAnalyzer.Analyze(body, f.Params.Select(p => p.Name), null);
            }
            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            _w.Line($"{SurfaceVisibility(Visibility(f.Name, false).Replace("private", "public"), sig.Params)} {Esc(ctx.Name)}({sig.Params}){baseCall}");
            EmitSignatureBody(body, sig);
        }
    }
}
