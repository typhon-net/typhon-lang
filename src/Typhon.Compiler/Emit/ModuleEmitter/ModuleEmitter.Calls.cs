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
    // Calls: Typhon functions and classes, builtins, Python methods of Python values, arguments and keywords.
    internal sealed partial class ModuleEmitter
    {
        sealed class IterableArgSpec { public int First; public bool All; public bool OnlyWhenSingle; }

        /// <summary>Builtins whose positional argument(s) are iterables: index of the first, whether all following ones are too.</summary>
        static readonly Dictionary<string, IterableArgSpec> IterableArgBuiltins = new Dictionary<string, IterableArgSpec>
        {
            ["list"] = new IterableArgSpec(), ["set"] = new IterableArgSpec(), ["frozenset"] = new IterableArgSpec(), ["tuple"] = new IterableArgSpec(),
            ["sorted"] = new IterableArgSpec(), ["sum"] = new IterableArgSpec(), ["any"] = new IterableArgSpec(), ["all"] = new IterableArgSpec(),
            ["enumerate"] = new IterableArgSpec(), ["reversed"] = new IterableArgSpec(),
            ["zip"] = new IterableArgSpec { All = true },
            ["map"] = new IterableArgSpec { First = 1, All = true }, ["filter"] = new IterableArgSpec { First = 1 },
            ["min"] = new IterableArgSpec { OnlyWhenSingle = true }, ["max"] = new IterableArgSpec { OnlyWhenSingle = true },
        };

        /// <summary>Calls whose result is never a dict, so iterating them needs no iterable() wrapper.</summary>
        static readonly HashSet<string> NonDictResults = new HashSet<string> { "range", "enumerate", "zip", "sorted", "reversed", "map", "filter", "list", "set", "tuple", "frozenset", "str", "open" };

        /// <summary>
        /// The Typhon function or constructor a call goes to (by name, module attribute, self.method or Class.method), and
        /// how many of its parameters the call does not pass explicitly (self/cls); null for anything else.
        /// </summary>
        FunctionDef TyphonCallee(CallExpr c, out int implicitParams)
        {
            implicitParams = 0;
            ModuleInfo mod = null; string name = null;
            if (c.Func is NameExpr n && !IsLocal(n.Id))
            {
                if (_m.Functions.Contains(n.Id) || _m.Classes.ContainsKey(n.Id)) { mod = _m; name = n.Id; }
                else if (_importedFromModules.TryGetValue(n.Id, out var imp)) { mod = imp.Key; name = imp.Value; }
            }
            else if (c.Func is AttributeExpr a)
            {
                if (DottedName(a.Target) is string dotted && ModuleRef(dotted) is ModuleInfo mref)
                    (mod, name) = ProjectExport(mref, a.Name);
                else if (a.Target is NameExpr sn && sn.Id == "self" && _cls != null)
                {
                    var m = MethodInHierarchy(_cls.Def, a.Name);
                    if (m != null) { implicitParams = HasDecorator(m.Decorators, "staticmethod") ? 0 : 1; return m; }
                    return null;
                }
                else if (a.Target is CallExpr sc && sc.Func is NameExpr scn && scn.Id == "super" && _cls != null)
                {
                    // super().m(...): the base class's method (super().__init__: the nearest base __init__)
                    var owner = a.Name == "__init__" ? NearestInitAncestor(_cls.Def) : null;
                    if (owner == null) for (var k = _cls.Def; k != null && owner == null; )
                        {
                            k = k.Bases.Count > 0 && ResolveBase(k.Bases[0], out var bpy, out _) ? bpy : null;
                            if (k != null && k.Body.OfType<FunctionDef>().Any(f => f.Name == a.Name)) owner = k;
                        }
                    var m = owner?.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                    if (m != null) implicitParams = HasDecorator(m.Decorators, "staticmethod") ? 0 : 1;
                    return m;
                }
                else if (a.Target is NameExpr cn && TryResolveType(cn.Id, 0, out _, out var pyCls) && pyCls != null)
                {
                    var m = pyCls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                    if (m != null) { implicitParams = HasDecorator(m.Decorators, "classmethod") ? 1 : 0; return m; }
                    return null;
                }
                // obj.m(...) with obj of a Typhon class whose method's parameters are inferred
                else if (MethodTarget(a) is InferenceTarget mt) { implicitParams = 1; return mt.Def; }
                else if (ClassKeyOfType(InferType(a.Target), out _) is string classKey && ClassDefOfKey(classKey) is ClassDef receiver &&
                    MethodInHierarchy(receiver, a.Name) is FunctionDef method)
                {
                    implicitParams = HasDecorator(method.Decorators, "staticmethod") ? 0 : 1;
                    return method;
                }
            }
            if (mod == null || name == null) return null;
            if (mod.Classes.TryGetValue(name, out var cls))
            {
                implicitParams = 1;
                return cls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__") ?? NearestInitAncestor(cls)?.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
            }
            return mod.Functions.Contains(name) ? mod.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name) : null;
        }

        /// <summary>
        /// Declared parameters by name, extra keywords into **kwargs, and extra positional arguments into a packed *args array.
        /// </summary>
        internal string KwargsCallArgs(FunctionDef callee, int implicitParams, List<Arg> args, bool constructorInitializer = false)
        {
            if (callee == null) return null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            var kw = ps.FirstOrDefault(p => p.Kind == ParamKind.KwArgs);
            if (kw == null && !NeedsPackedVarArgs(callee)) return null;
            if (kw == null && args.Any(a => a.DoubleStar)) return null;
            var va = ps.FirstOrDefault(p => p.Kind == ParamKind.VarArgs);
            var fixedPs = ps.Where(p => p.Kind == ParamKind.Normal).ToList();
            // parameters after *args are keyword-only: positional arguments fill only those before it
            var positionalPs = va == null ? fixedPs : ps.TakeWhile(p => p.Kind == ParamKind.Normal).ToList();
            var parts = new List<string>(); var extras = new List<string>(); var spreads = new List<string>(); var rest = new List<string>();
            int pos = 0, restAt = -1;
            string AssignedValue(Arg arg, Param parameter)
            {
                string type = parameter.Annotation != null ? MapType(parameter.Annotation, parameter) : null;
                string value = TupleLiteralValue(arg.Value, type) ??
                    (type is null or "dynamic" or "object" or "object?" ? EmitLooseValue(arg.Value) : EmitValue(arg.Value));
                if (!constructorInitializer || InferType(arg.Value) != "dynamic") return value;
                return $"({(type is null or "dynamic" ? "object?" : type)}){Paren(value)}";
            }
            void AddRest(string argument)
            {
                if (restAt < 0) { restAt = parts.Count; parts.Add(null); }
                rest.Add(argument);
            }
            foreach (var a in args)
            {
                if (a.DoubleStar) { spreads.Add($"(object?){Paren(EmitExpr(a.Value))}"); continue; }
                if (a.Star)
                {
                    if (va == null || pos < positionalPs.Count) return null;
                    AddRest($".. {EmitIterable(a.Value)}");
                    continue;
                }
                if (a.Name == null)
                {
                    if (pos < positionalPs.Count)
                    {
                        var positionalParameter = positionalPs[pos++];
                        parts.Add($"{Esc(positionalParameter.Name)}: {AssignedValue(a, positionalParameter)}");
                    }
                    else if (va != null) AddRest(va.Annotation == null ? EmitLooseValue(a.Value) : EmitValue(a.Value));
                    else { Error(a.Value, $"too many positional arguments for '{callee.Name}'"); return null; }
                }
                else if (fixedPs.FirstOrDefault(p => p.Name == a.Name) is Param parameter)
                    parts.Add($"{Esc(a.Name)}: {AssignedValue(a, parameter)}");
                else if (kw != null) extras.Add($"[{CsString(a.Name)}] = {EmitLooseValue(a.Value)}");
                else { Error(a.Value, $"unexpected keyword argument '{a.Name}' for '{callee.Name}'"); return null; }
            }
            if (restAt >= 0)
                parts[restAt] = $"{Esc(va.Name)}: ({(va.Annotation != null ? MapType(va.Annotation, va) : "object")}[])[{string.Join(", ", rest)}]";
            if (extras.Count > 0 || spreads.Count > 0)
            {
                string dict = $"new OrderedDictionary<string, dynamic>() {{ {string.Join(", ", extras)} }}";
                if (spreads.Count > 0) dict = $"{Rt("__kwargs")}({dict}, {string.Join(", ", spreads)})";
                parts.Add($"{Esc(kw.Name)}: {dict}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// f(a, *xs) into a Typhon function: the starred sequence fills the remaining fixed parameters (tuples by Item1..n, other
        /// sequences by index with Python's count check) or the function's own *args. Null when no star needs expanding.
        /// </summary>
        string ExpandStarArgs(CallExpr c)
        {
            if (!c.Args.Any(a => a.Star)) return null;
            var callee = TyphonCallee(c, out int implicitParams);
            if (callee == null || c.Args.Count(a => a.Star) != 1 || c.Args.Any(a => a.DoubleStar)) return null;
            var fixedParams = callee.Params.Skip(implicitParams).TakeWhile(p => p.Kind == ParamKind.Normal).ToList();
            bool hasVarArgs = callee.Params.Any(p => p.Kind == ParamKind.VarArgs);
            int starAt = c.Args.FindIndex(a => a.Star);
            var before = c.Args.Take(starAt).ToList();
            var after = c.Args.Skip(starAt + 1).ToList();
            if (before.Any(a => a.Name != null) || after.Any(a => a.Name == null)) return null;
            var star = c.Args[starAt].Value;
            if (hasVarArgs && before.Count >= fixedParams.Count)
                return null; // spreads into the function's own *args: the params array takes the sequence as is
            // the parameters the star fills: a tuple's arity is known; a list fills the remaining required parameters
            var remaining = fixedParams.Skip(before.Count).Where(p => !after.Any(a => a.Name == p.Name)).ToList();
            var tupleTypes = TupleElementTypes(InferType(star));
            if (tupleTypes == null && !hasVarArgs && remaining.Any(parameter => parameter.Default != null) &&
                KwargsSpreadCall(c, bindPositionalSpread: true) is string runtimeSpread)
                return runtimeSpread;
            int take = tupleTypes != null ? tupleTypes.Count : remaining.Count(p => p.Default == null);
            if (take > remaining.Count || take == 0) return null;
            string source = EmitExpr(star), prefix = null;
            if (!IsSimpleOperand(star))
            {
                string tmp = NewTmp("s");
                if (_lambdaParams.Count == 0) { _w.Line($"var {tmp} = {source};"); source = tmp; }
                else { prefix = $"{Paren(source)} switch {{ var {tmp} => "; source = tmp; }
            }
            var items = new List<string>();
            for (int i = 0; i < take; i++) items.Add(tupleTypes != null ? $"{source}.Item{i + 1}" : $"{Rt("__unpack")}({source}, {i}, {take})");
            var args = before.Select(a => EmitExpr(a.Value)).Concat(items).Concat(after.Select(a => $"{Esc(a.Name)}: {EmitExpr(a.Value)}"));
            string func = c.Func is NameExpr cfn ? EmitName(cfn) : EmitCallee(c.Func);
            string call = TryTypeExpr(c.Func, out var typeCode) ? $"new {typeCode}({string.Join(", ", args)})" : $"{func}({string.Join(", ", args)})";
            return prefix != null ? $"({prefix}{call} }})" : call;
        }

        /// <summary>Positions where a Typhon callee declares the parameter's type (an empty [] / {} there takes that type).</summary>
        System.Func<int, bool> TypedParamPositions(CallExpr c)
        {
            var callee = TyphonCallee(c, out int implicitParams);
            if (callee == null) return TryDataclassConstructor(c, out _) ? i => ParamTypeOf(c, i, null) != null : null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            return i => i >= 0 && i < ps.Count && ps[i].Kind == ParamKind.Normal && ps[i].Annotation != null;
        }

        bool TryDataclassConstructor(CallExpr call, out ClassDef cls)
        {
            cls = null;
            return DottedName(call.Func) is string name && TryResolveType(name, 0, out _, out cls) &&
                cls != null && HasDecorator(cls.Decorators, "dataclass");
        }

        /// <summary>The C# type of a Typhon callee's annotated parameter (by position, or by name for a keyword argument), or null.</summary>
        string ParamTypeOf(CallExpr c, int pos, string name)
        {
            var callee = TyphonCallee(c, out int implicitParams);
            // a dataclass's generated constructor: its fields in order
            if (callee == null && TryDataclassConstructor(c, out var dcls))
            {
                var fields = dcls.Body.OfType<AnnAssignStmt>().Where(a => a.Target is NameExpr && !(a.Annotation is SubscriptExpr cv && DottedName(cv.Target)?.EndsWith("ClassVar") == true)).ToList();
                var field = name != null ? fields.FirstOrDefault(a => ((NameExpr)a.Target).Id == name) : pos >= 0 && pos < fields.Count ? fields[pos] : null;
                return field != null ? MapType(field.Annotation, field) : null;
            }
            if (callee == null) return null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            var p = name != null ? ps.FirstOrDefault(x => x.Name == name) : pos >= 0 && pos < ps.Count ? ps[pos] : null;
            if (p?.Annotation == null || p.Kind != ParamKind.Normal) return null;
            // a bare list / dict parameter is typed from its calls (list → List<long>): not the annotation's dynamic form
            string mapped = MapType(p.Annotation, p);
            return TypeInference.IsBareContainer(p.Annotation) && mapped != null && mapped.Contains("dynamic") ? null : mapped;
        }

        /// <summary>{} / set() / [] for a parameter of an interface type (Mapping → IReadOnlyDictionary, ...): a new concrete container, or null.</summary>
        static string ConcreteEmpty(Expr value, string paramType)
        {
            if (!paramType.EndsWith(">")) return null;
            string args = GenericArg(paramType);
            if (paramType.StartsWith("IReadOnlyDictionary<") || paramType.StartsWith("IDictionary<")) return $"new OrderedDictionary<{args}>()";
            if (paramType.StartsWith("IReadOnlySet<") || paramType.StartsWith("ISet<")) return $"new HashSet<{args}>()";
            return null;
        }

        string EmptyArgument(Expr value, string paramType) =>
            (paramType == null ? null : ConcreteEmpty(value, paramType)) ??
            EmitExpr(value, targetTyped: paramType is not ("dynamic" or "object" or "object?"));

        /// <summary>Is the keyword argument's parameter of a Typhon callee annotated (so an empty literal takes its type)?</summary>
        bool TypedKeyword(CallExpr c, string name)
        {
            var callee = TyphonCallee(c, out _);
            return callee == null ? ParamTypeOf(c, -1, name) != null :
                callee.Params.Any(p => p.Name == name && p.Kind == ParamKind.Normal && p.Annotation != null);
        }

        string EmitArgs(List<Arg> args, System.Func<int, bool> iterableAt = null, IReadOnlyList<Semantics.MemberSig> overloads = null, System.Func<int, bool> targetTypedAt = null,
                        bool dynamicCall = false, System.Func<string, bool> typedKeywords = null, System.Func<int, string, string> paramTypeAt = null,
                        bool typedFunctionArgs = false, IReadOnlyList<Semantics.MemberSig> functionShapes = null, bool typhonCallee = false)
        {
            functionShapes ??= overloads;
            var parts = new List<string>();
            int position = 0, positional = 0;
            // a call to a Typhon function with some late-bound arguments: the overload inferred for exactly these argument types
            // (its parameters there are dynamic), bound statically by passing them as object; dispatched at run time instead,
            // it could be ambiguous with a typed overload ((object, object, long) and (double, double, double) for 1.0, 2.0, 3)
            bool partlyDynamic = typhonCallee && args.Any(a => a.Name == null && !a.Star && !a.DoubleStar && InferType(a.Value) == "dynamic") &&
                                 args.Any(a => InferType(a.Value) != "dynamic");
            foreach (var a in args)
            {
                if (a.DoubleStar) { Unsupported(a.Value, "**kwargs arguments"); continue; }
                int argPos = a.Name == null && !a.Star ? positional++ : -1;
                if (iterableAt != null && a.Name == null && !a.Star && iterableAt(position++))
                {
                    parts.Add(EmitIterable(a.Value));
                    continue;
                }
                if (overloads != null && overloads.Count > 0 && !a.Star && NarrowingFor(overloads, args, a, argPos) is string narrowTo)
                {
                    string narrowed = $"checked(({narrowTo})({EmitExpr(a.Value)}))";
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {narrowed}" : narrowed);
                    continue;
                }
                // big-int mode: where an overload keeps Python ints exact (factorial, pow, gcd, abs, ...), 64-bit values and
                // literals go to it as BigIntegers (C# would pick the long overload, which raises OverflowError)
                if (_big && overloads != null && overloads.Count > 0 && !a.Star && IsIntegral(InferType(a.Value)) && InferType(a.Value) != "bool" &&
                    !args.Any(o => o != a && (InferType(o.Value) == "double" || InferType(o.Value) == "float")) && // max(x, 100) with a float x: float
                    (IsIntLiteral(a.Value) || InferType(a.Value) != TypeInference.BigInt) && BigOverloadAt(overloads, args, a, argPos))
                {
                    string promoted = IsIntLiteral(a.Value) ? EmitValue(a.Value) : $"(({TypeInference.BigInt}){Paren(EmitExpr(a.Value))})";
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {promoted}" : promoted);
                    continue;
                }
                if (!a.Star && a.Value is TupleExpr && TupleLiteralValue(a.Value, paramTypeAt?.Invoke(argPos, a.Name)) is string tupleArgument)
                {
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {tupleArgument}" : tupleArgument);
                    continue;
                }
                // [1, 2, 3] for a list[Any] / bare list parameter: built as that type (C# lists are invariant)
                if (!a.Star && (a.Value is ListExpr || a.Value is DictExpr || a.Value is SetExpr) && !IsEmptyLiteral(a.Value) &&
                    paramTypeAt?.Invoke(argPos, a.Name) is string cpt && cpt.Contains("dynamic") && PyType(InferType(a.Value)) != cpt)
                {
                    // (for an Any parameter itself the literal keeps its own type: a collection expression has no target type there)
                    string typedLit = cpt == "dynamic" ? EmitExpr(a.Value) : $"({cpt}){EmitExpr(a.Value, targetTyped: true)}";
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {typedLit}" : typedLit);
                    continue;
                }
                if (a.Name == null && !a.Star && targetTypedAt != null && targetTypedAt(argPos) && IsEmptyLiteral(a.Value))
                {
                    parts.Add(EmptyArgument(a.Value, paramTypeAt?.Invoke(argPos, null)));
                    continue;
                }
                if (a.Name != null && typedKeywords != null && IsEmptyLiteral(a.Value) && typedKeywords(a.Name))
                {
                    parts.Add($"{Esc(a.Name)}: {EmptyArgument(a.Value, paramTypeAt?.Invoke(-1, a.Name))}");
                    continue;
                }
                // a module function passed to a Typhon function whose parameters are inferred: its function type (that
                // parameter is specialised for it); passed to a late-bound call: a callable object
                // (also where no overload of a .NET callee takes a function there: print(f), xs.append(f), str(f))
                var paramsHere = functionShapes?.Select(o => a.Name != null ? o.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(o, argPos)).Where(x => x != null).ToList();
                bool noDelegateHere = paramsHere != null && paramsHere.Count > 0 && paramsHere.All(x => !x.TakesFunction);
                // (and to an unannotated parameter of a Typhon function, even in a call that runs its dynamic version)
                bool untypedTyphonParam = typhonCallee && !a.Star && paramTypeAt?.Invoke(argPos, a.Name) == null;
                if (!a.Star && a.Value is NameExpr typeName && BuiltinTypeValue(typeName) is string typeValue &&
                    (untypedTyphonParam || paramTypeAt?.Invoke(argPos, a.Name) is "dynamic" or "object" or "object?"))
                {
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {typeValue}" : typeValue);
                    continue;
                }
                if (!a.Star && !a.DoubleStar && a.Value is LambdaExpr callable &&
                    (noDelegateHere || untypedTyphonParam || paramTypeAt?.Invoke(argPos, a.Name) is "dynamic" or "object" or "object?"))
                {
                    string closure = EmitRuntimeLambda(callable);
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {closure}" : closure);
                    continue;
                }
                bool boxedArgument = noDelegateHere || untypedTyphonParam ||
                    paramTypeAt?.Invoke(argPos, a.Name) is "dynamic" or "object" or "object?";
                // Object arguments need a Python callable, including functions imported under a local alias.
                if (!a.Star && !a.DoubleStar && a.Value is (NameExpr or AttributeExpr) &&
                    (boxedArgument || dynamicCall && paramsHere == null) && BoundMethodValue(a.Value) is string bound)
                {
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {bound}" : bound);
                    continue;
                }
                if (a.Value is NameExpr fnArg && !a.Star &&
                    (typedFunctionArgs || dynamicCall || boxedArgument) &&
                    (FunctionValue(fnArg, typed: typedFunctionArgs) ??
                        (boxedArgument && !typedFunctionArgs ? BuiltinFunctionAsValue(fnArg) : null)) is string fnValue)
                {
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {fnValue}" : fnValue);
                    continue;
                }
                // a builtin passed as a function (map(str, xs), key=len): C# cannot convert an overloaded method group whose
                // parameter is object/generic to Func<int, string>, but a lambda binds the right overload at the call site
                // (where the parameter takes no function, issubclass(bool, int), the builtin is its type object: below)
                string v = a.Value is NameExpr vn && ValueBuiltins.Contains(vn.Id) && !IsShadowed(vn.Id) && !_renames.ContainsKey(vn.Id) &&
                           !(noDelegateHere && BuiltinTypeValues.ContainsKey(vn.Id) && paramsHere.All(x => x.GenericType == "object" || x.GenericType == "global::System.Type" || x.GenericType == "System.Type"))
                    ? (_big && vn.Id == "int" ? $"(__v => {Rt("__bigint")}(__v))"
                       // (map(repr, items) with items late-bound: nothing gives the lambda's parameter a type: object, the builtin's object overload)
                       : args.Any(o => o != a && !o.Star && InferType(o.Value) is var ot && (ot == null || ot == "dynamic" || ot.Contains("<dynamic>") || ot == "object"))
                       ? $"((dynamic __v) => {EmitName(vn)}((object?)__v))"
                       : $"(__v => {EmitName(vn)}(__v))")
                    // str.upper / str.lower / list.copy passed as a function: a lambda calling the method on its argument
                    : a.Value is AttributeExpr um && um.Target is NameExpr umt && BuiltinTypeNames.Contains(umt.Id) && !IsShadowed(umt.Id) && !BuiltinClassMethods.Contains(um.Name) &&
                      !(um.Name.StartsWith("__") && um.Name.EndsWith("__")) // (int.__name__ is a value)
                    ? BuiltinStringMethodValue(um) is string stringMethod
                        ? $"(__v => {Rt("__invoke")}((object?){stringMethod}, [(object?)__v]))"
                        : $"(__v => __v.{Esc(um.Name)}())"
                    // a fully annotated function for a System.Delegate parameter (app.MapGet("/todos/{id}", get_todo)): the method
                    // group itself, so the callee sees its parameter names, defaults and attributes
                    : a.Value is NameExpr dg && paramsHere != null && paramsHere.Any(x => x.IsDelegateBase) &&
                      FunctionRef(dg, out _, out var dgDef) && dgDef.TypeParams.Count == 0 && dgDef.Params.All(x => x.Annotation != null && x.Kind == ParamKind.Normal)
                    ? EmitName(dg)
                    : a.Value is NameExpr fv && FunctionValueLambda(fv) is string lambda ? lambda
                    : a.Value is NameExpr mg && FunctionRef(mg, out _, out _) ? EmitName(mg) // a method group, for a delegate parameter
                    : a.Value is NameExpr printer && IsBuiltinPrintValue(printer) && paramsHere?.Any(p => p.TakesFunction) == true
                    ? EmitName(printer)
                    : ModuleOrClassValue(a.Value) is string objectValue ? objectValue // hasattr(os, "sep"), {weakref.ref: ...}
                    : !dynamicCall && a.Value is AttributeExpr methodArg &&
                      (paramsHere?.Any(p => p.TakesFunction) == true ||
                       paramTypeAt?.Invoke(argPos, a.Name) is string callableType &&
                       (callableType is "Action" or "global::System.Action" ||
                        callableType.StartsWith("Func<") || callableType.StartsWith("global::System.Func<") ||
                        callableType.StartsWith("Action<") || callableType.StartsWith("global::System.Action<")))
                    ? EmitAttribute(methodArg, allowMethodGroup: true)
                    // A dynamic call needs a delegate whose parameter types are also visible while emitting the body.
                    : dynamicCall && a.Value is LambdaExpr dl && !dl.RequiresArgumentBinding
                    ? $"(({EmitLambdaLocal(dl, null, out string lambdaCode)}){lambdaCode})"
                    : EmitExpr(a.Value);
                if (dynamicCall && v.StartsWith("(__v =>")) v = $"((Func<dynamic, dynamic>){v})";
                // Late-bound calls and inferred BigInteger overloads need the Python integer type at the call site.
                else if (!a.Star && IsIntLiteral(a.Value) &&
                    (dynamicCall || _big && typhonCallee && paramTypeAt?.Invoke(argPos, a.Name) is (null or "dynamic" or "object" or "object?" or TypeInference.BigInt)))
                    v = EmitValue(a.Value);
                // a list for a .NET array parameter (WebApplication.CreateBuilder(sys.argv[1:])): its items as an array
                if (!a.Star && paramsHere != null && paramsHere.Count > 0 && paramsHere.Any(x => x.IsArray) && !paramsHere.Any(x => x.TakesFunction) &&
                    InferType(a.Value) is string lat && lat.StartsWith("List<"))
                    v = $"{Paren(v)}.ToArray()";
                if (partlyDynamic && !a.Star && !a.DoubleStar && InferType(a.Value) == "dynamic")
                {
                    string type = paramTypeAt?.Invoke(argPos, a.Name);
                    v = TypeInference.IsUnknown(type) ? $"(object?){Paren(v)}" : $"({type})(dynamic?){Paren(v)}";
                }
                // None for an inferred parameter: the call runs the function's dynamic version (null alone would be ambiguous
                // between its typed overloads for string and List<long>)
                else if (typhonCallee && !a.Star && IsNone(a.Value) && paramTypeAt != null && paramTypeAt(argPos, a.Name) == null) v = "(object?)null";
                // *xs into a params array (print(*xs), .NET params methods): spread into an array of the parameter's element type
                // (a call dispatched at run time, f(node, *rest) with node late-bound: the collection expression would have no
                // target type, the spread is an array)
                if (a.Star && !(InferType(a.Value) is string at && at.EndsWith("[]")))
                    v = dynamicCall || args.Any(o => o != a && !o.Star && !o.DoubleStar && InferType(o.Value) == "dynamic") || InferType(a.Value) == "dynamic"
                        ? $"global::System.Linq.Enumerable.ToArray({Rt("__iter_dyn")}((object){Paren(EmitExpr(a.Value))}))"
                        : $"[.. {EmitIterable(a.Value)}]";
                parts.Add(a.Name != null ? $"{Esc(a.Name)}: {v}" : v);
            }
            return string.Join(", ", parts);
        }

        static readonly HashSet<string> BuiltinTypeNames = new HashSet<string> { "str", "list", "dict", "set", "int", "float", "tuple", "frozenset", "bytes", "bytearray" };
        /// <summary>Class methods of built-in types (called on the type, not on a value).</summary>
        static readonly HashSet<string> BuiltinClassMethods = new HashSet<string> { "fromkeys", "maketrans", "fromhex", "from_bytes" };

        /// <summary>Overloaded single-argument builtins that are wrapped in a lambda when passed as a function value.</summary>
        bool IsBuiltinPrintValue(NameExpr name) =>
            name.Id == "print" && !_m.IsGdScript && !IsShadowed(name.Id) && StdlibOwner(name.Id) == null;

        static readonly HashSet<string> ValueBuiltins = new HashSet<string> { "str", "repr", "int", "float", "bool", "len", "abs", "ord", "chr", "hex", "bin", "oct", "round", "sorted", "sum", "min", "max", "list", "tuple", "set", "iter" };

        static readonly HashSet<string> SpecialBuiltins = new HashSet<string> { "isinstance", "cast", "type", "slice", "print", "object", "list", "dict", "set", "super" };

        /// <summary>The inference key of a local / self field that started as an empty container, and its kind.</summary>
        string ElementKey(Expr receiver, out char kind)
        {
            kind = 'l';
            if (receiver is NameExpr n && !_renames.ContainsKey(n.Id) && _emptyContainers.TryGetValue(n.Id, out kind)) return _scopeKey + "|" + n.Id;
            if (receiver is NameExpr mn && !IsLocal(mn.Id) && _emptyModuleFields.TryGetValue(mn.Id, out kind)) return ModuleFieldKey(mn.Id);
            if (receiver is AttributeExpr a && a.Target is NameExpr s && s.Id == "self" && _cls != null && _emptyFields.TryGetValue(a.Name, out kind))
                return TypeInference.ClassKey(_m, _cls.Name) + "|self." + a.Name;
            // obj.items[k] = v / obj.items.append(v) from outside the class: the field of obj's class
            if (receiver is AttributeExpr oa && !(oa.Target is NameExpr os && os.Id == "self") && ClassKeyOfType(InferType(oa.Target), out _) is string ck &&
                ClassDefOfKey(ck) is ClassDef cdef && CollectImplicitFields(cdef).TryGetValue(oa.Name, out var fieldInfo) &&
                fieldInfo.Key == null && fieldInfo.Value.Value != null && TypeInference.IsEmptyContainer(fieldInfo.Value.Value))
            {
                kind = TypeInference.ContainerKind(fieldInfo.Value.Value);
                return ck + "|self." + oa.Name;
            }
            return null;
        }

        /// <summary>Records (in a dry run) what a call adds to a container that started empty: its element type, for its declaration.</summary>
        void RecordAdded(CallExpr c)
        {
            // d.setdefault(k, []).append(v): the dict's values are lists of v's type (likewise .add for sets)
            if (Obs != null && c.Func is AttributeExpr inner && (inner.Name == "append" || inner.Name == "add") && c.Args.Count == 1 &&
                inner.Target is CallExpr sd && sd.Func is AttributeExpr sda && sda.Name == "setdefault" && sd.Args.Count == 2 &&
                TypeInference.IsEmptyContainer(sd.Args[1].Value) && ElementKey(sda.Target, out char dk) is string dkey && (dk == 'd' || dk == 'o'))
            {
                string container = TypeInference.ContainerKind(sd.Args[1].Value) == 's' ? "HashSet" : "List";
                string et = PyType(InferType(c.Args[0].Value)) ?? TypeInference.Unknown;
                Obs.AddElement(dkey, (PyType(InferType(sd.Args[0].Value)) ?? TypeInference.Unknown) + "→" + $"{container}<{et}>");
            }
            // groups[k].append(v) on defaultdict(list) / defaultdict(set): its values are lists (sets) of v's type
            if (Obs != null && c.Func is AttributeExpr dd && (dd.Name == "append" || dd.Name == "add" || dd.Name == "extend") && c.Args.Count == 1 &&
                dd.Target is SubscriptExpr dsub && ElementKey(dsub.Target, out char ddk) is string ddkey && ddk == 'D')
            {
                string container = dd.Name == "add" ? "HashSet" : "List";
                string et = dd.Name == "extend" ? PyType(ElementTypeOf(InferType(c.Args[0].Value))) : PyType(InferType(c.Args[0].Value));
                Obs.AddElement(ddkey, (PyType(InferType(dsub.Index)) ?? TypeInference.Unknown) + "→" + $"{container}<{et ?? TypeInference.Unknown}>");
            }
            // heapq.heappush(h, x) / bisect.insort(a, x): x is an element of the list (like h.append(x))
            if (Obs != null && c.Args.Count >= 2 && c.Args[0].Name == null && c.Args[1].Name == null && StdlibModuleOf(c, out string sname) is string smod &&
                (smod == "Typhon.Stdlib.heapq" && (sname == "heappush" || sname == "heappushpop" || sname == "heapreplace") ||
                 smod == "Typhon.Stdlib.bisect_" && sname.StartsWith("insort")) &&
                ElementKey(c.Args[0].Value, out char hk) is string hkey && (hk == 'l' || hk == 'q'))
                Obs.AddElement(hkey, PyType(InferType(c.Args[1].Value)) ?? TypeInference.Unknown);
            if (Obs == null || !(c.Func is AttributeExpr fa) || ElementKey(fa.Target, out char kind) is not string key) return;
            var args = c.Args.Where(a => a.Name == null && !a.Star).Select(a => a.Value).ToList();
            string T(Expr e) => PyType(InferType(e)) ?? TypeInference.Unknown;
            bool sequence = kind == 'l' || kind == 'q';
            switch (fa.Name)
            {
                case "append" when args.Count == 1 && sequence: Obs.AddElement(key, T(args[0])); break;
                case "appendleft" when args.Count == 1 && kind == 'q': Obs.AddElement(key, T(args[0])); break;
                case "insert" when args.Count == 2 && sequence: Obs.AddElement(key, T(args[1])); break;
                case "add" when args.Count == 1 && kind == 's': Obs.AddElement(key, T(args[0])); break;
                case "extend" when args.Count == 1 && sequence:
                case "extendleft" when args.Count == 1 && kind == 'q':
                case "update" when kind == 's' && args.Count >= 1:
                    foreach (var a in args) Obs.AddElement(key, PyType(ElementTypeOf(InferType(a)))); break;
                case "update" when kind == 'c' && args.Count == 1:
                    Obs.AddElement(key, (InferType(args[0]) == "string" ? "string" : PyType(ElementTypeOf(InferType(args[0]))) ?? TypeInference.Unknown) + "→long"); break;
                // setdefault(k, []) only says "a list": what is appended to it (below) says of what
                case "setdefault" when kind == 'd' && args.Count == 2 && !TypeInference.IsEmptyContainer(args[1]): Obs.AddElement(key, T(args[0]) + "→" + T(args[1])); break;
                case "update" when (kind == 'd' || kind == 'o') && args.Count == 1:
                {
                    var dt = InferType(args[0]);
                    var kv = IsDictType(dt) ? SplitTypeArgs(GenericArg(dt)) : null;
                    Obs.AddElement(key, kv != null ? PyType(kv[0]) + "→" + PyType(kv[1]) : TypeInference.Unknown + "→" + TypeInference.Unknown);
                    break;
                }
            }
        }

        /// <summary>
        /// Does appending a value of type v to a list of e need a list of any items? Numbers of another kind (an int list
        /// getting floats), another container or class, or a late-bound value for a list of containers (C# lists of different
        /// item types do not convert, not even at run time). A late-bound scalar converts when it is stored, so it does not.
        /// </summary>
        bool AppendWidens(string e, string v, bool arithmetic = false)
        {
            if (e == null || v == null || e == v || e == "dynamic" || e == "object" || v == TypeInference.Pending || e.Contains(TypeInference.Pending) || v.Contains(TypeInference.Pending)) return false;
            if (IsNumeric(e) && IsNumeric(v)) return !(e == "double" && IsIntegral(v));
            bool container = e.StartsWith("List<") || IsDictType(e) || IsSetType(e) || e.StartsWith("(") || e.StartsWith(PyTuplePrefix);
            // (xs.append(xs[-1] + d) with a late-bound d on a list of numbers: the sum may be a float, like a variable's)
            if (v == "dynamic") return container || arithmetic && IsNumeric(e);
            if (e == v + "?" || DerivesFromType(v, e)) return false;
            return container || ClassKeyOfType(e, out _) != null || IsNumeric(e) || e == "string" || e == "bool";
        }

        /// <summary>f(args) where f is an instance of a Typhon class with __call__: the call f.__call__(args), or null.</summary>
        CallExpr CallableObjectCall(CallExpr c)
        {
            if (c.Func is NameExpr cn && !IsLocal(cn.Id) && (TryResolveType(cn.Id, 0, out _, out _) || _m.Functions.Contains(cn.Id))) return null;
            if (c.Func is LambdaExpr) return null;
            string t = InferType(c.Func);
            if (t == null || ClassKeyOfType(t, out _) is not string key || ClassDefOfKey(key) is not ClassDef cd || !DefinesInHierarchy(cd, "__call__")) return null;
            return new CallExpr { Func = new AttributeExpr { Target = c.Func, Name = "__call__", Line = c.Line, Col = c.Col }, Args = c.Args, Line = c.Line, Col = c.Col };
        }

        // A late-bound field may hold a Python class, which is callable even though CLR System.Type is not.
        bool LateCallableField(AttributeExpr attribute) => InferType(attribute) == "dynamic" && SourceCallableField(attribute);

        bool SourceCallableField(AttributeExpr attribute)
        {
            if (DottedName(attribute.Target) is string name && !IsLocal(name.Split('.')[0]) &&
                TryResolveType(name, 0, out _, out var declaringClass) && declaringClass != null)
                return ClassVariableOwner(declaringClass, attribute.Name) != null;

            var receiver = attribute.Target is NameExpr { Id: "self" } && HasSelfInScope() ? _cls?.Def :
                ClassKeyOfType(InferType(attribute.Target), out _) is string key ? ClassDefOfKey(key) : null;
            return receiver != null && (ImplicitFieldOwner(receiver, attribute.Name) != null ||
                ClassVariableOwner(receiver, attribute.Name) != null || HasLiteralSlot(receiver, attribute.Name));
        }

        bool HasLiteralSlot(ClassDef definition, string name, int depth = 0) =>
            depth < 20 && (LiteralSlotNames(definition).Contains(name) ||
                TyphonBases(definition).Any(basis => HasLiteralSlot(basis, name, depth + 1)));

        bool IsConditionalModuleBinding(Expr value)
        {
            if (value is NameExpr name && !IsLocal(name.Id))
            {
                if (_m.ConditionalGlobals.Contains(name.Id) || _runtimeProjectBindings.ContainsKey(name.Id)) return true;
                if (_importedFromModules.TryGetValue(name.Id, out var imported))
                    return imported.Key.ConditionalGlobals.Contains(imported.Value);
                return _wildcardModules.Any(m => m.ConditionalGlobals.Contains(name.Id));
            }
            if (value is AttributeExpr member && DottedName(member.Target) is string dotted &&
                ModuleRef(dotted) is ModuleInfo module)
            {
                var (owner, binding) = ProjectExport(module, member.Name);
                return binding != null && owner?.ConditionalGlobals.Contains(binding) == true || UsesRuntimeProjectMember(module, member.Name);
            }
            return false;
        }

        bool CustomAllocatorTarget(Expr target, out string type)
        {
            type = null;
            return DottedName(target) is string dotted && TryResolveType(dotted, 0, out _, out var definition) &&
                UsesCustomAllocation(definition) && TryTypeExpr(target, out type);
        }

        bool UsesRuntimeIteratorMember(AttributeExpr member)
        {
            if (_m.IsGdScript || member.Name != "__iter__") return false;
            string receiverType = InferType(member.Target);
            return IsUnknownType(receiverType) || IsPythonContainer(receiverType) || TupleElementTypes(receiverType) != null ||
                receiverType?.StartsWith(PyTuplePrefix, StringComparison.Ordinal) == true;
        }

        string EmitCall(CallExpr c, bool targetTyped)
        {
            if (!_m.IsGdScript && EmitTypeSlotCall(c) is string typeSlot) return typeSlot;
            if (!_m.IsGdScript && c.Func is AttributeExpr overridden &&
                overridden.Target is not CallExpr { Func: NameExpr { Id: "super" } } &&
                TyphonCallee(c, out int boundParameters) is FunctionDef { NeedsOverrideBinding: true } && boundParameters == 1)
                return InvokeLogicalMethod(overridden, $"(object?){Paren(EmitExpr(overridden.Target))}", c.Args);
            if (!_m.IsGdScript && c.Func is AttributeExpr packedField && c.Args.Any(argument => argument.Star || argument.DoubleStar) &&
                SourceCallableField(packedField))
                return InvokeLogicalMethod(packedField, $"(object?){Paren(EmitExpr(packedField.Target))}", c.Args);
            if (c.Func is AttributeExpr notEqual && UsesInheritedNotEqual(notEqual))
                return InvokeLogicalMethod(notEqual, $"(object?){Paren(EmitValue(notEqual.Target))}", c.Args);
            if (c.Func is AttributeExpr member && (UsesRuntimeIteratorMember(member) ||
                IsRuntimeObjectAttribute(member) || UsesBuiltinStringComparison(member)))
                return InvokeLogicalMethod(member, $"(object?){Paren(EmitExpr(member.Target))}", c.Args);
            if (StdlibModuleOf(c, out string chainName) == "Typhon.Stdlib.itertools" && chainName == "chain" &&
                c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
            {
                string element = ChainElementType(c.Args.Select(a => a.Value));
                if (element != "dynamic")
                    return $"global::Typhon.Stdlib.itertools.chain<{element}>({string.Join(", ", c.Args.Select(a => EmitIterable(a.Value)))})";
                return $"global::Typhon.Stdlib.itertools.chain({string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitValue(a.Value))}"))})";
            }
            if (CustomAllocatorTarget(c.Func, out var allocatorType))
            {
                var values = c.Args.Where(argument => argument.Name == null && !argument.DoubleStar)
                    .Select(argument => argument.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(argument.Value))})"
                        : $"(object?){Paren(EmitLooseValue(argument.Value))}");
                string positional = $"[{string.Join(", ", values)}]";
                return c.Args.Any(argument => argument.Name != null || argument.DoubleStar)
                    ? $"{Rt("__invoke_kw")}(typeof({allocatorType}), {positional}, {KeywordArgs(c.Args)})"
                    : $"{Rt("__invoke")}(typeof({allocatorType}), {positional})";
            }
            if (!_m.IsGdScript && (BuiltinTypeBase(c.Func) && c.Args.Count == 3 ||
                IsMetaclassExpression(c.Func) && TryTypeExpr(c.Func, out _)))
            {
                string metaclass = BuiltinTypeBase(c.Func) ? "global::System.Type" : MapType(c.Func, c);
                var arguments = c.Args.Where(arg => arg.Name == null && !arg.DoubleStar)
                    .Select((arg, index) => arg.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(arg.Value))})"
                        : $"(object?){Paren(ClassNamespace(arg.Value, index))}");
                return $"{Rt("__invoke_kw")}(typeof({metaclass}), [{string.Join(", ", arguments)}], {KeywordArgs(c.Args)})";

                string ClassNamespace(Expr value, int index)
                {
                    // A metaclass can add members of any type to its initial namespace.
                    if (index != 2 || c.Args.Take(2).Any(arg => arg.Star) ||
                        value is not DictExpr dictionary || dictionary.Keys.Any(key => key == null))
                        return EmitLooseValue(value);
                    var entries = dictionary.Keys.Select((key, entry) =>
                        $"({EmitValue(key)}, {EmitLooseValue(dictionary.Values[entry])})");
                    return $"{Rt("__dict")}<object, object?>({string.Join(", ", entries)})";
                }
            }
            if (!_m.IsGdScript && IsMetaclassInstance(c.Func) && TryTypeExpr(c.Func, out var metaclassTarget))
            {
                var values = c.Args.Where(a => a.Name == null && !a.DoubleStar)
                    .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})"
                        : $"(object?){Paren(EmitLooseValue(a.Value))}");
                return $"{Rt("__construct_metaclass")}<{metaclassTarget}>([{string.Join(", ", values)}], {KeywordArgs(c.Args)})";
            }
            if (c.Func is AttributeExpr dictMethod && dictMethod.Name != "fromkeys" &&
                DictionaryDescriptorReceiver(dictMethod) is string dictType)
                return InvokeLogicalMethod(dictMethod, dictType, c.Args);
            if (BaseQualifiedCall(c) is string baseCall) return baseCall;
            if (c.Func is AttributeExpr nativeMethod && NativeUnboundMethodReceiver(nativeMethod) is string receiverType)
                return InvokeLogicalMethod(nativeMethod, receiverType, c.Args);
            if (DirectBuiltinSuper(c, "dict", "update") != null)
                return EmitDictionaryUpdate(c, "this", useBase: true);
            if (c.Func is AttributeExpr { Name: "update" } update && UsesDictionaryUpdate(c, update))
                return EmitDictionaryUpdate(c, $"(object){Paren(EmitExpr(update.Target))}");
            if (IsConditionalModuleBinding(c.Func) ||
                c.Func is NameExpr classMember && LateClassSlot(classMember.Id) != null ||
                c.Func is NameExpr local && IsLocal(local.Id) && KnownType(local.Id) is string localType &&
                    (PrimitiveCsTypes.Contains(localType) || IsPythonContainer(localType) ||
                     !_m.IsGdScript && localType is ("object" or "object?")) ||
                c.Func is AttributeExpr nested && (IsNestedTypeAccess(nested) || InstanceMethodMayBeShadowed(nested) ||
                    TypeObjectMember(nested) || IsNarrowedInstanceAttribute(nested) || MetaclassMember(nested, out _)))
            {
                string nestedCallee = $"(object?){Paren(EmitExpr(c.Func))}";
                var nestedArguments = c.Args.Where(a => a.Name == null && !a.DoubleStar)
                    .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
                string nestedPositional = $"[{string.Join(", ", nestedArguments)}]";
                return c.Args.Any(a => a.Name != null || a.DoubleStar)
                    ? $"{Rt("__invoke_kw")}({nestedCallee}, {nestedPositional}, {KeywordArgs(c.Args)})"
                    : $"{Rt("__invoke")}({nestedCallee}, {nestedPositional})";
            }
            if (c.Func is NameExpr aliasName && AliasTarget(aliasName) is Expr aliasTarget)
                return EmitCall(new CallExpr { Func = aliasTarget, Args = c.Args, Line = c.Line, Col = c.Col }, targetTyped);
            if (CallableObjectCall(c) is CallExpr objectCall) return EmitCall(objectCall, targetTyped);
            if (Obs != null) RecordCall(CallTarget(c, out _), c.Args);
            RecordAdded(c);
            if (EmitFunctionValueCall(c) is string fnValueCall) return fnValueCall;
            if (Obs != null && c.Func is NameExpr lfn && IsLocal(lfn.Id) && _lambdaLocals.TryGetValue(lfn.Id, out var lcalled) && !lcalled.RequiresArgumentBinding)
                Obs.AddLambdaCall(_scopeKey + "|λ" + lfn.Id, c.Args.Count == lcalled.Params.Count && c.Args.All(a => a.Name == null && !a.Star)
                    ? c.Args.Select(a => TypeInference.Settled(PyType(InferType(a.Value)) ?? TypeInference.Unknown)).ToArray() : null);
            if (c.Func is NameExpr gfn && gfn.Id == "globals" && c.Args.Count == 0 && !IsShadowed("globals")) return EmitGlobals();
            if (_m.RuntimeCodeMode != null && c.Func is NameExpr specialName)
            {
                if (specialName.Id == "__typhon_export")
                    return $"__typhon_execution.Export({EmitValue(c.Args[0].Value)}, {EmitLooseValue(c.Args[1].Value)})";
                if (specialName.Id == "__typhon_source_import")
                    return $"global::Typhon.Runtime.Builtins.__runtime_source_import(__typhon_execution.Globals, {string.Join(", ", c.Args.Select(a => EmitValue(a.Value)))})";
                if (specialName.Id == "locals" && c.Args.Count == 0 && !IsShadowed("locals") && AtRuntimeModuleScope)
                    return "((dynamic)__typhon_execution.Locals)";
            }
            if (c.Func is NameExpr localName && localName.Id == "locals" && c.Args.Count == 0 && !IsShadowed("locals"))
                return EmitLocals();
            if (c.Args.Count == 1 && !c.Args[0].Star && !c.Args[0].DoubleStar &&
                StdlibModuleOf(c, out string factory) == "Typhon.Stdlib.functools" && factory == "cmp_to_key")
            {
                var comparer = c.Args[0].Value;
                string comparison = BoundMethodValue(comparer);
                if (comparison == null && LateBoundArg(comparer)) comparison = EmitLooseValue(comparer);
                if (comparison != null)
                    return $"global::Typhon.Stdlib.functools.__cmp_to_key((object){Paren(comparison)})";
            }
            if (BinaryPowerCall(c) is BinOpExpr power) return EmitBinOp(power);
            if (SpecialFormCall(c, targetTyped) is string special) return special;
            if (PythonCall(c, targetTyped) is string python) return python;
            if (LatePythonMethodCall(c) is string logicalMethod) return logicalMethod;
            // a call that is none of those: a Typhon function or class, a .NET method or constructor, typed by its arguments
            string instanceStatic = StaticThroughInstance(c.Func, out bool classMethodBinding);
            string func = c.Func is NameExpr cfn ? EmitName(cfn) : instanceStatic ?? EmitCallee(c.Func);
            if (c.Func is LambdaExpr || c.Func is IfExpr) func = Paren(func);
            var typedAt = TypedParamPositions(c);
            if (typedAt == null && c.Func is AttributeExpr pm && RuntimeExtensionMethods.Contains(pm.Name) && IsPythonContainer(InferType(pm.Target)) &&
                !InferType(pm.Target).Contains("dynamic"))
                typedAt = i => true;
            // Untyped runtime parameters need concrete collection values because C# cannot target-type them as object.
            if (typedAt == null && StdlibModuleOf(c, out string runtimeName) is string runtimeModule)
            {
                var methods = _r.GetMembers(runtimeModule, runtimeName).Where(m => m.IsMethod && m.IsStatic).ToList();
                typedAt = i =>
                {
                    // Each select() iterable has its own element type, so an empty literal must supply one.
                    if (runtimeModule == "Typhon.Stdlib.select_" && runtimeName == "select") return false;
                    var parameters = methods.Select(m => ParamAt(m, i)).Where(p => p != null).ToList();
                    return parameters.Count == 0 || parameters.Any(p => p.GenericType is not ("object" or "object?" or "dynamic"));
                };
            }
            // a call with a late-bound argument or receiver is dispatched dynamically: lambdas need a delegate type there
            bool dynamicCall = classMethodBinding || c.Args.Any(a => InferType(a.Value) == "dynamic") ||
                c.Func is AttributeExpr da && (InferType(da.Target) == "dynamic" || TypeObjectMember(da) || IsConditionalClassMethodReference(da));
            var typhonTarget = CallTarget(c, out _);
            bool typedFunctionArgs = typhonTarget != null && TypedCall(typhonTarget, c.Args);
            var overloads = DotNetOverloads(c);
            // which parameters take functions: the .NET overloads, or a builtin's (print(f), str(f) take the function object)
            var shapes = overloads ?? (c.Func is NameExpr bfn && !IsShadowed(bfn.Id) && c.Args.Any(x => x.Value is NameExpr || x.Value is AttributeExpr)
                ? _r.GetMembers("Typhon.Runtime.Builtins", bfn.Id).Where(m => m.IsMethod && m.IsStatic).ToList() : null);
            if ((shapes == null || shapes.Count == 0) && c.Func is AttributeExpr extension &&
                RuntimeExtensionMethods.Contains(extension.Name) && IsPythonContainer(InferType(extension.Target)))
                shapes = _r.GetMembers("Typhon.Runtime.Builtins", extension.Name)
                    .Where(method => method.IsMethod && method.IsStatic && method.Parameters.Count > 0)
                    .Select(method => new Semantics.MemberSig { Parameters = method.Parameters.Skip(1).ToList() }).ToList();
            // d.get(k) / s.expandtabs(n) with a late-bound argument on a typed receiver: the runtime's extension method called as
            // the static method it is (C# cannot dispatch an extension method dynamically, CS1973; a static one it can)
            if (dynamicCall && c.Func is AttributeExpr em && InferType(em.Target) is string emt && emt != "dynamic" && !emt.Contains("dynamic>") &&
                IsPythonContainer(emt) && // (str and the containers: their Python methods are the runtime's extensions; bytes has its own)
                !c.Args.Any(x => x.Star || x.DoubleStar) && MemberType(emt, em.Name, method: true) == null &&
                _r.GetMembers("Typhon.Runtime.Builtins", em.Name).Any(m => m.IsMethod && m.IsStatic && m.Parameters.Count >= 1) &&
                !(em.Target is NameExpr emn && (ModuleRef(emn.Id) != null || _dotnetAliases.ContainsKey(emn.Id) || TryResolveType(emn.Id, 0, out _, out _))))
            {
                string rest = EmitArgs(c.Args, dynamicCall: true);
                // (by its full name: using static does not bring extension methods in as plain names)
                return $"global::Typhon.Runtime.Builtins.{Esc(em.Name)}({EmitExpr(em.Target)}{(rest.Length > 0 ? ", " + rest : "")})";
            }
            Func<int, bool> iterableAt = null;
            if (StdlibModuleOf(c, out string iterableName) == "Typhon.Stdlib.itertools" &&
                iterableName is "filterfalse" or "takewhile" or "dropwhile")
                iterableAt = i => i == 1;
            var nestedDefinition = c.Func is NameExpr nestedName && IsLocalFunctionName(nestedName.Id) &&
                _localFunctionSigs.TryGetValue(nestedName.Id, out var localSignature) ? localSignature.Def : null;
            string ArgumentType(int i, string name) => nestedDefinition == null
                ? ParamTypeOf(c, i, name) : AnnotatedParamType(nestedDefinition, i, name);
            return $"{func}({EmitArgs(c.Args, iterableAt: iterableAt, overloads: overloads, targetTypedAt: typedAt, dynamicCall: dynamicCall, typedKeywords: name => TypedKeyword(c, name), paramTypeAt: ArgumentType, typedFunctionArgs: typedFunctionArgs, functionShapes: shapes, typhonCallee: TyphonCallee(c, out _) != null || nestedDefinition != null)})";
        }

        /// <summary>
        /// Python's special forms: open() in binary mode, sum of tuples, d.get/pop/setdefault with late-bound keys, re/struct/array
        /// calls typed by their literal arguments, frozenset, super().__init__ in two-phase construction, base calls, and
        /// conversions of late-bound values. Null when the call is none of them.
        /// </summary>
        string SpecialFormCall(CallExpr c, bool targetTyped)
        {
            if (!_m.IsGdScript && c.Func is NameExpr { Id: "frozendict" } frozen && !IsShadowed(frozen.Id) &&
                c.Args.Any(argument => argument.Name != null || argument.DoubleStar))
            {
                var values = c.Args.Where(argument => argument.Name == null && !argument.DoubleStar)
                    .Select(argument => argument.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(argument.Value))})" :
                        $"(object?){Paren(EmitLooseValue(argument.Value))}");
                string type = BuiltinTypeValues[frozen.Id];
                return $"(({type}){Rt("__invoke_kw")}(typeof({type}), [{string.Join(", ", values)}], {KeywordArgs(c.Args)}))";
            }

            if (c.Func is AttributeExpr receiverMember && IsReboundReceiver(receiverMember.Target))
                return InvokeLogicalMethod(receiverMember, $"(object?){Paren(EmitExpr(receiverMember.Target))}", c.Args);
            if ((_scope?.ReboundReceiver != null || _scope?.LocalClassOwner != null) && !IsShadowed("super") && c.Func is AttributeExpr
                { Target: CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } parent } superMember)
                return InvokeLogicalMethod(superMember, EmitCall(parent, targetTyped: false), c.Args);
            if (c.Func is AttributeExpr { Name: "update" } mapUpdate && c.Args.Count == 1 &&
                c.Args[0] is Arg { Name: null, Star: false, DoubleStar: false } mapping &&
                InferType(mapUpdate.Target) is string mapType && !mapType.StartsWith("IReadOnlyDictionary<") &&
                DictTypes(mapType) is List<string> targetTypes &&
                DictTypes(InferType(mapping.Value)) is List<string> sourceTypes && !targetTypes.SequenceEqual(sourceTypes))
                return $"{Rt("__dict_update")}({EmitExpr(mapUpdate.Target)}, {EmitExpr(mapping.Value)})";
            if (c.Args.Count == 1 && c.Args[0].Star &&
                StdlibModuleOf(c, out var pathMethod) == "Typhon.Stdlib.os_+path" && pathMethod == "join")
                return $"global::Typhon.Stdlib.os_.path.__join_paths((object?){Paren(EmitExpr(c.Args[0].Value))})";
            // KeyError(key): Python's str() of a KeyError is the key's repr (KeyError: 'k')
            if (c.Func is NameExpr ke && ke.Id == "KeyError" && !IsShadowed("KeyError") && c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star)
                return $"new global::System.Collections.Generic.KeyNotFoundException({Rt("repr")}((object?){Paren(EmitExpr(c.Args[0].Value))}))";
            // open(path, "rb"): a binary file (reads and writes bytes)
            if (c.Func is NameExpr ofn && ofn.Id == "open" && !IsShadowed("open") && OpenMode(c)?.Contains('b') == true)
                return $"{Rt("open_binary")}({EmitExpr(c.Args[0].Value)}, {CsString(OpenMode(c))})";
            if (c.Func is NameExpr dynamicOpen && dynamicOpen.Id == "open" && !IsShadowed("open") &&
                OpenMode(c) == null && c.Args.All(a => !a.Star && !a.DoubleStar))
                return $"{Rt("open_dynamic")}({EmitArgs(c.Args)})";
            // sum(tuples, ()): concatenation of the tuples
            if (c.Func is NameExpr tsn && tsn.Id == "sum" && !IsShadowed("sum") && TupleSum(c) is string tsum && tsum.StartsWith(PyTuplePrefix))
                return $"{Rt("__tupsum")}<{GenericArg(tsum)}>({EmitIterable(c.Args[0].Value)})";
            // Each argument to set.update is iterable. Pass it as an object to keep the C# call statically bound.
            if (c.Func is AttributeExpr { Name: "update" } update && InferType(update.Target) is string setType &&
                setType.StartsWith("HashSet<", StringComparison.Ordinal) && c.Args.Count > 0 &&
                c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
            {
                string arguments = string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitExpr(a.Value))}"));
                return $"{Rt("__set_update")}({EmitExpr(update.Target)}, {arguments})";
            }

            // d.get(k, dflt) / d.pop(k) / d.setdefault(k, v) with a late-bound argument: converted to the dict's key/value types
            // (dynamic dispatch never finds the runtime's extension methods)
            if (c.Func is AttributeExpr { Name: "get" } lookup &&
                (IsUnknownType(InferType(lookup.Target)) || InferType(lookup.Target) == "object?") &&
                !(lookup.Target is CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } && !IsShadowed("super")) &&
                c.Args.Count is 1 or 2 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
            {
                string callable = $"{Rt("getattr")}((object?){Paren(EmitValue(lookup.Target))}, \"get\")";
                return $"{Rt("__invoke")}((object?){Paren(callable)}, [{string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitLooseValue(a.Value))}"))}])";
            }
            if (c.Func is AttributeExpr { Name: "get" } typedLookup && InferType(typedLookup.Target) is string lookupMappingType &&
                DictTypes(lookupMappingType) is List<string> lookupMappingTypes && !lookupMappingType.StartsWith("IReadOnlyDictionary<", StringComparison.Ordinal) &&
                c.Args.Count == 1 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar) &&
                (LateBoundArg(c.Args[0].Value) || Incompatible(lookupMappingTypes[0], InferType(c.Args[0].Value))))
            {
                string lookupMapping = $"(global::System.Collections.IDictionary){Paren(EmitExpr(typedLookup.Target))}";
                string arguments = string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitLooseValue(a.Value))}"));
                string result = PythonMethodType(lookupMappingType, "get", c);
                string lookupValue = $"{Rt("__dict_get")}({lookupMapping}, {arguments})";
                return result == null || IsUnknownType(result) || result.Contains(TypeInference.Pending)
                    ? $"(dynamic?){lookupValue}" : $"(({result}){lookupValue})";
            }
            if (c.Func is AttributeExpr dg && (dg.Name == "get" || dg.Name == "pop" || dg.Name == "setdefault") && DictTypes(InferType(dg.Target)) is List<string> dkv &&
                c.Args.Count >= 1 && c.Args.Count <= 2 && c.Args.All(x => x.Name == null && !x.Star) &&
                (dkv[0] == "dynamic" || c.Args.Any(x => LateBoundArg(x.Value)) ||
                    c.Args.Count == 2 && IsUnknownType(dkv[1]) && TypeInference.IsEmptyContainer(c.Args[1].Value)) &&
                !dkv.Any(x => x.Contains(TypeInference.Pending)))
            {
                string Slot(int i) { string t = i == 0 ? dkv[0] : dkv[1]; return t == "dynamic" ? "object" : t; }
                var dargs = c.Args.Select((x, i) =>
                {
                    string value = EmitExpr(x.Value, targetTyped: i == 1 && !IsUnknownType(dkv[1]));
                    return LateBoundArg(x.Value) || i == 0 && dkv[0] == "dynamic" ? $"({Slot(i)}){Paren(value)}" : value;
                });
                return $"{Paren(EmitExpr(dg.Target))}.{dg.Name}({string.Join(", ", dargs)})";
            }
            // A native module function has overloads. In reduce, its operands come from the actual iterator.
            if (c.Args.Count is 2 or 3 && c.Args.All(arg => !arg.Star && !arg.DoubleStar) &&
                c.Args[0].Name == null && c.Args[1].Name == null && (c.Args.Count == 2 || c.Args[2].Name is null or "initial") &&
                StdlibModuleOf(c, out string reduction) == "Typhon.Stdlib.functools" && reduction == "reduce")
            {
                string callable = c.Args[0].Value switch
                {
                    AttributeExpr attribute => NativeModuleMethodValue(attribute),
                    NameExpr name => NativeImportedMethodValue(name),
                    _ => null,
                };
                if (callable != null)
                {
                    string Argument(Arg arg) => $"(object?){Paren(EmitValue(arg.Value))}";
                    return $"global::Typhon.Stdlib.functools.__reduce({callable}, {string.Join(", ", c.Args.Skip(1).Select(Argument))})";
                }
            }
            // re.findall / pat.findall with a literal pattern: strs or tuples of strs, statically
            if (FindallCallForm(c) is string findall)
            {
                string recv = c.Func is AttributeExpr ffa ? EmitExpr(ffa.Target) + "." : "";
                return $"{recv}{findall}({EmitArgs(c.Args)})";
            }
            // struct.unpack("<HH", b): a tuple of the format's item types
            if (StructUnpackTypes(c) is List<string> sut)
            {
                string mod = c.Func is AttributeExpr sfa ? EmitExpr(sfa.Target) + "." : "";
                string call = $"{mod}{(c.Func is AttributeExpr sa ? sa.Name : ((NameExpr)c.Func).Id)}({EmitArgs(c.Args)})";
                var items = sut.Select((t, i) => $"{mod}__item<{t}>(__a[{i}])").ToList();
                return $"({call} switch {{ var __a => {(items.Count == 1 ? $"ValueTuple.Create({items[0]})" : "(" + string.Join(", ", items) + ")")} }})";
            }
            // array.array("d", xs): a typed array (statically a list of its element type)
            if (ArrayCallForm(c, out var arrayElem) is string arrayForm)
            {
                string mod = c.Func is AttributeExpr afa ? EmitExpr(afa.Target) + "." : "";
                return $"((List<{arrayElem}>){mod}{arrayForm}({EmitArgs(c.Args)}))";
            }
            // frozenset(xs): statically a set (the value hashes by its contents)
            if (c.Func is NameExpr fzc && fzc.Id == "frozenset" && !IsShadowed("frozenset") && c.Args.Count <= 1 && InferType(c) is string fzt && fzt.StartsWith("HashSet<"))
                return $"(({fzt}){EmitName(fzc)}({EmitArgs(c.Args)}))";
            // super().__init__(...) inside a two-phase __init__: the base's __init__ method (nothing for object's)
            if (c.Func is AttributeExpr si && si.Name == "__init__" && si.Target is CallExpr ssc && ssc.Func is NameExpr ssn && ssn.Id == "super" && _cls != null)
            {
                var initOwner = NearestInitAncestor(_cls.Def);
                if (initOwner == null)
                    return BaseHasMember(_cls.Def.Bases, "__init__", requireOverridable: false)
                        ? $"base.__init__({string.Join(", ", c.Args.Select(a => (a.Name != null ? Esc(a.Name) + ": " : "") + $"(object?){Paren(EmitValue(a.Value))}"))})" : "";
                if (_p.TryFindClass(_m, initOwner.Name, out var ic, out var io))
                {
                    if (Obs != null) RecordCall(InitTarget(io, ic), c.Args);
                    if (TwoPhaseClasses.Contains(initOwner) && initOwner.TypeParams.Count == 0)
                    {
                        var args = c.Args.Where(a => a.Name == null && !a.DoubleStar)
                            .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
                        return $"{Rt("__base_init")}(this, typeof({ModuleClassName(io)}.{Esc(initOwner.Name)}), [{string.Join(", ", args)}], {KeywordArgs(c.Args)})";
                    }
                }
            }
            if (SuperListAppend(c) is Expr directBase)
                return $"{Rt("__invoke")}((object?){Rt("getattr")}(typeof({MapType(directBase, c)}), {CsString("append")}), [this, {EmitLooseValue(c.Args[0].Value)}])";
            if (DirectBuiltinSuper(c, "dict", "__delitem__") != null &&
                c.Args.Count == 1 && c.Args[0] is { Name: null, Star: false, DoubleStar: false })
                return $"{Rt("__dict_delete")}(this, {EmitLooseValue(c.Args[0].Value)})";

            // super().m(...) in a class whose bases have no m (a mixin compiled on its own: the next class in an MRO it is
            // mixed into has it): AttributeError when it runs, as in Python
            if (c.Func is AttributeExpr nsa && nsa.Target is CallExpr nsc && nsc.Func is NameExpr nsn && nsn.Id == "super" && nsc.Args.Count == 0 &&
                nsa.Name != "__init__" && _scope?.ClassArgument == null && _cls != null && SuperLacks(nsa.Name))
                return nsa.Name == "__format__" && c.Args.Count == 1 && c.Args[0] is Arg { Name: null, Star: false, DoubleStar: false } formatSpec
                    ? $"{Rt("__object_format__")}(this, {EmitExpr(formatSpec.Value)})"
                    : $"{Rt("__no_super")}({CsString(nsa.Name)})";
            // base.m(...) cannot be dispatched dynamically: late-bound arguments go as object (binding to the dynamic overload)
            if (c.Func is AttributeExpr sm && sm.Target is CallExpr smc && smc.Func is NameExpr smn && smn.Id == "super" && smc.Args.Count == 0 &&
                _scope?.ClassArgument == null && c.Args.Any(x => InferType(x.Value) == "dynamic") &&
                c.Args.All(x => x.Name == null && !x.Star && !x.DoubleStar))
                return $"base.{Esc(sm.Name)}({string.Join(", ", c.Args.Select(x => InferType(x.Value) == "dynamic" ? $"(object?){Paren(EmitValue(x.Value))}" : EmitValue(x.Value)))})";
            if (c.Func is NameExpr { Id: "AttributeError" } && !IsShadowed("AttributeError"))
            {
                var args = c.Args.Where(a => a.Name == null && !a.DoubleStar).Select(a =>
                    a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitValue(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
                string keywords = c.Args.Any(a => a.Name != null || a.DoubleStar) ? ", " + KeywordArgs(c.Args) : "";
                return $"{Rt("__attribute_error")}([{string.Join(", ", args)}]{keywords})";
            }
            if (c.Func is NameExpr osError && !IsShadowed(osError.Id) &&
                osError.Id is "OSError" or "IOError" or "EnvironmentError" &&
                c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
                return $"new global::Typhon.Runtime.PyOSError({string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitValue(a.Value))}"))})";
            if (c.Func is NameExpr { Id: "FileNotFoundError" } fileMissing && !IsShadowed(fileMissing.Id) &&
                c.Args.Count >= 2 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
                return $"{Rt("__file_not_found_error")}([{string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitValue(a.Value))}"))}])";
            // big-int mode: int(x) parses / truncates to a BigInteger
            if (_big && c.Func is NameExpr bi && bi.Id == "int" && !IsShadowed("int") && c.Args.Count >= 1 && c.Args.All(x => x.Name == null || x.Name == "base"))
                return $"{Rt("__bigint")}({string.Join(", ", c.Args.Select(x => InferType(x.Value) == "dynamic" ? $"(object){Paren(EmitExpr(x.Value))}" : EmitExpr(x.Value)))})";
            // int(x) / float(x) / str(x) / len(x) of a late-bound value: passed as object, the call binds statically and keeps
            // its result type (the runtime converts by the value's run-time type) instead of becoming dynamic itself
            if (c.Func is NameExpr scv && StaticConversions.Contains(scv.Id) && !IsShadowed(scv.Id) && c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star &&
                (InferType(c.Args[0].Value) == "dynamic" || IsDynamicallyDispatched(c.Args[0].Value) ||
                 scv.Id == "len" && InferType(c.Args[0].Value) is null or "object" or "object?"))
                return $"{EmitName(scv)}((object){Paren(EmitExpr(c.Args[0].Value))})";
            if (_big && c.Func is NameExpr { Id: "range" } && !IsShadowed("range") &&
                c.Args.Count is >= 1 and <= 3 && c.Args.All(argument => argument.Name == null && !argument.Star && !argument.DoubleStar))
                return $"{Rt("__range_big")}({string.Join(", ", c.Args.Select(argument => $"(object?){Paren(EmitValue(argument.Value))}"))})";
            // range(n) with a late-bound n: converted to a Python int, so the range binds statically
            if (c.Func is NameExpr rgn && rgn.Id == "range" && !IsShadowed("range") && c.Args.All(x => x.Name == null && !x.Star) &&
                c.Args.Any(x => InferType(x.Value) == "dynamic" || IsDynamicallyDispatched(x.Value)))
                return $"{EmitName(rgn)}({string.Join(", ", c.Args.Select(x => InferType(x.Value) == "dynamic" || IsDynamicallyDispatched(x.Value) ? $"checked((long)({EmitExpr(x.Value)}))" : EmitExpr(x.Value)))})";
            if (c.Func is NameExpr fn && SpecialBuiltins.Contains(fn.Id) && !IsShadowed(fn.Id))
            {
                switch (fn.Id)
                {
                    case "isinstance":
                    {
                        if (c.Args.Count != 2) break;
                        var candidate = c.Args[0].Value;
                        var classInfo = c.Args[1].Value;
                        if (candidate is LambdaExpr || (classInfo is TupleExpr alternatives
                            ? alternatives.Elts.Any(x => !IsTypeish(x) || NeedsRuntimeClassInfo(x))
                            : !IsTypeish(classInfo) || NeedsRuntimeClassInfo(classInfo)))
                            return $"global::Typhon.Stdlib.builtins.isinstance((object?){Paren(EmitLooseValue(candidate))}, (object?){Paren(EmitValue(classInfo))})";
                        string v = EmitLooseValue(candidate);
                        string vt = InferType(c.Args[0].Value);
                        // big-int mode: a value of unknown type that is a Python int may be a BigInteger or a 64-bit count (len())
                        string Is(Expr t)
                        {
                            string mt = MapType(t, c);
                            if (mt == "dynamic" && DottedName(t) is string lateClass &&
                                TryResolveType(lateClass, 0, out var lateMeta, out _) && lateMeta != null)
                                return $"global::Typhon.Stdlib.builtins.isinstance((object?){Paren(v)}, typeof({CsTypeName(lateMeta)}))";
                            if (t is NameExpr builtin && !IsShadowed(builtin.Id) &&
                                (builtin.Id is "list" or "dict" or "set" || !_m.IsGdScript && builtin.Id is "int" or "str"))
                                return $"global::Typhon.Stdlib.builtins.isinstance((object?){Paren(v)}, typeof({mt}))";
                            if (t is NameExpr { Id: "super" } && !IsShadowed("super"))
                                mt = "global::Typhon.Stdlib.builtins.super";
                            if (DottedName(t) is string abcName && TryResolveType(abcName, 0, out var abcMeta, out var abc) &&
                                (abcMeta is ("Typhon.Stdlib.types+FunctionType" or "Typhon.Stdlib.types+GeneratorType" or
                                    "Typhon.Stdlib.types+AsyncGeneratorType" or "Typhon.Stdlib.types+CoroutineType") ||
                                 abc?.TypeParams.Count > 0 || MayHaveVirtualSubclasses(abcMeta, abc)))
                            {
                                string type = abc != null ? ClassTypeValue(mt, abc) : $"typeof({CsTypeName(abcMeta)})";
                                return $"global::Typhon.Stdlib.builtins.isinstance((object?){Paren(v)}, {type})";
                            }
                            // a class whose members were copied into classes with several bases: their objects are instances too
                            if (DottedName(t) is string tn && TryResolveType(tn, 0, out _, out var mixin) && mixin != null && _p.MixedInto.TryGetValue(mixin, out var into))
                                return "(" + string.Join(" || ", new[] { $"{v} is {mt}" }.Concat(into.Select(x => $"{v} is {ModuleClassName(x.Module)}.{Esc(x.Class.Name)}"))) + ")";
                            return mt == TypeInference.BigInt && IsUnknownType(vt) ? $"({v} is {mt} || {v} is long)" : $"{v} is {mt}";
                        }
                        if (c.Args[1].Value is TupleExpr types) return "(" + string.Join(" || ", types.Elts.Select(Is)) + ")";
                        return $"({Is(c.Args[1].Value)})";
                    }
                    case "cast":
                        if (c.Args.Count != 2) break;
                        return $"(({MapType(c.Args[0].Value, c)}){Paren(EmitExpr(c.Args[1].Value))})";
                    case "type":
                        if (c.Args.Count != 1) break;
                        return $"{Rt(TypeOfBuiltin)}((object?){Paren(c.Args[0].Value is LambdaExpr lambda ? EmitRuntimeLambda(lambda) : EmitValue(c.Args[0].Value))})";
                    case "slice":
                        if (c.Args.Count is < 1 or > 3 || c.Args.Any(a => a.Name != null || a.Star || a.DoubleStar)) break;
                        return $"new global::Typhon.Runtime.PySlice({string.Join(", ", c.Args.Select(a => $"(object?){Paren(EmitValue(a.Value))}"))})";
                    case "print":
                        if (c.Args.Any(a => a.Name != null))
                        {
                            var pos = c.Args.Where(a => a.Name == null).Select(a => a.Star ? ".. " + EmitIterable(a.Value) : EmitExpr(a.Value));
                            var kws = c.Args.Where(a => a.Name == "sep" || a.Name == "end" || a.Name == "file" || a.Name == "flush").Select(a => $"{a.Name}: {(a.Name == "file" ? "(object?)" + Paren(EmitExpr(a.Value)) : EmitExpr(a.Value))}");
                            return $"{Rt("print_kw")}([{string.Join(", ", pos)}]{string.Concat(kws.Select(k => ", " + k))})";
                        }
                        break;
                    case "object":
                        if (c.Args.Count == 0) return "new object()";
                        break;
                    case "list":
                    case "dict":
                    case "set":
                        if (fn.Id == "dict" && HasDictKeywords(c))
                        {
                            string Item(Arg arg)
                            {
                                var value = arg.Value;
                                // Class bodies can use the unbound functions they define.
                                string code = ClassBodyMethod(value) is string method
                                    ? $"new global::Typhon.Runtime.PyBoundMethod(typeof({_cls.CsSelfType}), {CsString(method)}, includeNonPublic: true)"
                                    : EmitLooseValue(value);
                                return $"[{CsString(arg.Name)}] = {code}";
                            }
                            return $"new {KeywordDictType(c)} {{ {string.Join(", ", c.Args.Select(Item))} }}";
                        }
                        if (fn.Id == "dict" && c.Args.Any(a => a.Name != null || a.DoubleStar))
                        {
                            if (DictKeywordSource(c, out var dictMapping) is string key)
                                return $"{Rt("__dict_with_keywords")}<{key}>(" +
                                    $"(IEnumerable<KeyValuePair<{key}, object?>>){Paren(EmitExpr(dictMapping))}, {KeywordArgs(c.Args)})";
                            var values = c.Args.Where(a => a.Name == null && !a.DoubleStar)
                                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})"
                                    : $"(object?){Paren(EmitLooseValue(a.Value))}");
                            return $"{Rt("__invoke_kw")}(typeof(OrderedDictionary<object, object>), " +
                                $"[{string.Join(", ", values)}], {KeywordArgs(c.Args)})";
                        }
                        if (c.Args.Count == 0)
                        {
                            if (targetTyped) return "new()";
                            string what = fn.Id == "list" ? "List<dynamic>" : fn.Id == "dict" ? "OrderedDictionary<dynamic, dynamic>" : "HashSet<dynamic>";
                            ImplicitDynamic(c, $"the element type of '{fn.Id}()'");
                            return $"new {what}()";
                        }
                        break;
                    case "super":
                        if (c.Args.Count == 0)
                        {
                            if (_m.IsGdScript || Receivers.Contains(c) && _scope?.HasSelf == true && _scope.ReboundReceiver == null) return "base";
                            return ImplicitSuperObject();
                        }
                        if (c.Args.Count <= 2 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
                        {
                            var arguments = c.Args.Select(a => $"(object?){Paren(EmitValue(a.Value))}");
                            return $"global::Typhon.Stdlib.builtins.super.__make__({string.Join(", ", arguments)})";
                        }
                        break;
                }
            }
            return null;
        }

        string ImplicitSuperObject()
        {
            const string type = "global::Typhon.Stdlib.builtins.super";
            if (_scope?.LocalClassOwner is string owner && _scope.LocalClassReceiver is string receiver)
                return $"{type}.__make__({owner}, (object?){EmitName(new NameExpr { Id = receiver })})";
            if (_cls != null && _scope?.HasSelf == true)
                return $"{type}.__make__(typeof({_cls.CsSelfType}), (object?){_scope.ReboundReceiver ?? "this"})";
            if (_cls != null && _scope?.ClassArgument is string classArgument)
                return $"{type}.__make__(typeof({_cls.CsSelfType}), {Esc(classArgument)})";
            return $"{type}.__without_args__()";
        }

        static bool HasDictKeywords(CallExpr call) => call.Args.Count > 0 && call.Args.All(a => a.Name != null);

        string DictKeywordSource(CallExpr call, out Expr mapping)
        {
            mapping = null;
            var positional = call.Args.Where(argument => argument.Name == null && !argument.DoubleStar).ToList();
            if (positional.Count != 1 || positional[0].Star) return null;
            mapping = positional[0].Value;
            return DictTypes(InferType(mapping)) is List<string> types &&
                (types[0] is "string" or "dynamic" or "object" or "object?") &&
                (types[1] is "dynamic" or "object" or "object?") ? types[0] : null;
        }


        /// <summary>Base.method(self, ...): an explicit call of the direct base implementation.</summary>
        string BaseQualifiedCall(CallExpr call)
        {
            if (_cls == null || !HasSelfInScope() || _cls.Def.Bases.Count == 0 ||
                !(call.Func is AttributeExpr member) || DottedName(member.Target) is not string qualifier ||
                call.Args.Count == 0 || !(call.Args[0].Value is NameExpr { Id: "self" }) || IsReboundReceiver(call.Args[0].Value) ||
                call.Args[0].Name != null || !TryResolveType(qualifier, 0, out var targetMeta, out var targetClass) ||
                !ResolveBase(_cls.Def.Bases[0], out var parentClass, out var parentMeta)) return null;
            if (targetClass != null && member.Name == "__init__" &&
                TwoPhaseClasses.Contains(targetClass) && Defines(targetClass, "__init__") && targetClass.TypeParams.Count == 0 &&
                DerivesFrom(_cls.Def, targetClass) &&
                TryTypeExpr(member.Target, out string ancestor))
            {
                var positional = call.Args.Skip(1).Where(arg => arg.Name == null && !arg.DoubleStar)
                    .Select(arg => arg.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(arg.Value))})"
                                            : $"(object?){Paren(EmitLooseValue(arg.Value))}");
                return $"{Rt("__base_init")}(this, typeof({ancestor}), [{string.Join(", ", positional)}], {KeywordArgs(call.Args.Skip(1).ToList())})";
            }
            if (targetMeta != parentMeta || targetClass != parentClass || call.Args.Any(arg => arg.Star || arg.DoubleStar)) return null;

            var overloads = parentMeta == null ? null :
                _r.GetMembers(parentMeta, member.Name).Where(m => m.IsMethod && !m.IsStatic).ToList();
            if (parentClass == null && overloads?.Count is not > 0) return null;
            if (parentClass != null && (MethodInHierarchy(parentClass, member.Name) is not FunctionDef method ||
                HasDecorator(method.Decorators, "staticmethod") || HasDecorator(method.Decorators, "classmethod") ||
                HasDecorator(method.Decorators, "property"))) return null;
            var arguments = call.Args.Skip(1).ToList();
            if (!arguments.Any(a => InferType(a.Value) == "dynamic" || IsDynamicallyDispatched(a.Value)))
                return $"base.{Esc(member.Name)}({EmitArgs(arguments, overloads: overloads)})";

            string Argument(Arg arg, int index)
            {
                string code = EmitValue(arg.Value);
                if (InferType(arg.Value) == "dynamic" || IsDynamicallyDispatched(arg.Value))
                {
                    string type = ParamTypeOf(call, index + 1, arg.Name) ??
                        overloads?.Select(m => arg.Name == null ? ParamAt(m, index)?.GenericType :
                            m.Parameters.FirstOrDefault(p => p.Name == arg.Name)?.GenericType).FirstOrDefault(t => t != null);
                    code = $"({(type == null || type == "dynamic" ? "object?" : type)}){Paren(code)}";
                }
                return arg.Name == null ? code : $"{Esc(arg.Name)}: {code}";
            }
            return $"base.{Esc(member.Name)}({string.Join(", ", arguments.Select(Argument))})";
        }

        string ClassBodyMethod(Expr value) =>
            _scope == null && _cls != null && value is NameExpr name && Defines(_cls.Def, name.Id) ? name.Id : null;

        string KeywordDictType(CallExpr call)
        {
            var types = call.Args.Select(a => ClassBodyMethod(a.Value) == null ? PyType(InferType(a.Value)) : null).Distinct().ToList();
            string value = types.Count == 1 && !TypeInference.IsUnknown(types[0]) ? types[0] : "dynamic";
            return $"OrderedDictionary<string, {value}>";
        }

        /// <summary>
        /// obj.m(...) / Cls.m(...) of a method decorated in Python, where the class is known: the decorators' result called
        /// with the instance and the arguments as written (keyword arguments stay keyword arguments, as the wrapper sees them
        /// in Python). Null for anything else (the method under its own name forwards then).
        /// </summary>
        string DecoratedMethodCall(CallExpr c)
        {
            if (!(c.Func is AttributeExpr a)) return null;
            ClassDef cd; string type; string receiver = null;
            if (a.Target is NameExpr tn && !IsLocal(tn.Id) && TryResolveType(tn.Id, 0, out _, out var pyCls) && pyCls != null)
            {
                if (!TryTypeExpr(a.Target, out type)) return null;
                cd = pyCls;
            }
            else
            {
                bool self = a.Target is NameExpr sn && sn.Id == "self" && _cls != null && HasSelfInScope();
                type = self ? _cls.CsSelfType : InferType(a.Target);
                cd = self ? _cls.Def : type != null && ClassKeyOfType(type, out _) is string key ? ClassDefOfKey(key) : null;
                receiver = self ? "this" : EmitExpr(a.Target);
            }
            for (var k = cd; k != null; )
            {
                var m = k.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                if (m != null)
                {
                    if (k.TypeParams.Count > 0 || !_p.Modules.Values.Any(mi => mi.Ast != null && mi.Ast.MethodDecorators.ContainsKey(m))) return null;
                    bool isStatic = HasDecorator(m.Decorators, "staticmethod");
                    if (receiver != null && !isStatic)
                        return InvokeLogicalMethod(a, $"(object?){Paren(receiver)}", c.Args);
                    var positional = c.Args.Where(x => x.Name == null && !x.DoubleStar)
                        .Select(x => x.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(x.Value))})" : $"(object?){Paren(EmitLooseValue(x.Value))}");
                    bool keywords = c.Args.Any(x => x.Name != null || x.DoubleStar);
                    return $"{Rt("__invoke_kw")}((object?){type}.__decorated_{a.Name}, [{string.Join(", ", positional)}], {(keywords ? KeywordArgs(c.Args) : "null")})";
                }
                if (k.Bases.Count == 0 || !ResolveBase(k.Bases[0], out var bpy, out _) || !TryTypeExpr(k.Bases[0] is SubscriptExpr bs ? bs.Target : k.Bases[0], out type)) return null;
                k = bpy;
            }
            return null;
        }

        /// <summary>
        /// obj.m(...) / self.m(...) where m is a @staticmethod or @classmethod of obj's Typhon class.
        /// A classmethod binds to the receiver's actual class, including a subclass.
        /// </summary>
        string StaticThroughInstance(Expr func, out bool classMethodBinding)
        {
            classMethodBinding = false;
            if (!(func is AttributeExpr a) || !(a.Target is NameExpr tn)) return null;
            if (TryResolveType(tn.Id, 0, out _, out _) && !IsLocal(tn.Id)) return null; // already through the class
            string t = tn.Id == "self" && _cls != null && HasSelfInScope() ? _cls.CsSelfType : InferType(a.Target);
            ClassDef cd = _cls != null && t == _cls.CsSelfType ? _cls.Def : t != null && ClassKeyOfType(t, out _) is string key ? ClassDefOfKey(key) : null;
            for (var k = cd; k != null; k = k.Bases.Count > 0 && ResolveBase(k.Bases[0], out var bpy, out _) ? bpy : null)
            {
                var m = k.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                if (m == null) continue;
                classMethodBinding = HasDecorator(m.Decorators, "classmethod");
                if (classMethodBinding)
                    return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
                return HasDecorator(m.Decorators, "staticmethod") ? $"{t}.{Esc(a.Name)}" : null;
            }
            return null;
        }

        /// <summary>The keyword arguments of a late-bound call as a dict: name=value pairs and **mappings.</summary>
        /// <summary>
        /// f(x, **opts) / obj.m(**opts) / Cls(**opts) / Cls.m(**opts) with a callee known here: which parameters the mapping
        /// fills is known only at run time, so the call binds there, by parameter name (the function's value, the bound
        /// method or the class, through reflection); its value has the type the call has.
        /// </summary>
        /// <summary>A call's function as C# code: an attribute naming a class or function stays its name (not a type object).</summary>
        string EmitCallee(Expr f) => f is AttributeExpr fa ? EmitAttribute(fa) : EmitExpr(f);

        /// <summary>The call an expression statement consists of, while it is emitted.</summary>
        CallExpr _statementCall;

        string KwargsSpreadCall(CallExpr c, bool bindPositionalSpread = false)
        {
            bool namedAttributeCall = !_m.IsGdScript && c.Func is AttributeExpr member && InferType(member.Target) == "dynamic" &&
                c.Args.Any(a => a.Name != null) && TyphonCallee(c, out _) == null;
            if (!bindPositionalSpread && !namedAttributeCall && !c.Args.Any(a => a.DoubleStar)) return null;
            string callee = null;
            if (c.Func is NameExpr n && !IsLocal(n.Id) && !_moduleFieldTypes.ContainsKey(n.Id))
            {
                if (TryResolveType(n.Id, 0, out _, out var cls) && cls != null) callee = $"typeof({MapType(n, n)})";
                else if (TyphonCallee(c, out _) != null)
                {
                    if (_importedFromModules.TryGetValue(n.Id, out var imported))
                        callee = RuntimeProjectMember(imported.Key, imported.Value);
                    else if (_m.Functions.Contains(n.Id))
                        callee = RuntimeProjectMember(_m, n.Id);
                    else
                        callee = $"(object?){Paren(EmitLooseValue(c.Func))}";
                }
            }
            else if (c.Func is AttributeExpr inherited &&
                inherited.Target is CallExpr { Func: NameExpr { Id: "super" } } superCall && !IsShadowed("super"))
            {
                string proxy = superCall.Args.Count == 0 ? ImplicitSuperObject() : EmitExpr(superCall);
                return InvokeLogicalMethod(inherited, $"(object?){Paren(proxy)}", c.Args);
            }
            else if (c.Func is AttributeExpr a)
            {
                if (DottedName(a.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) && ModuleRef(dotted) is ModuleInfo module)
                    callee = TyphonCallee(c, out _) == null ? null : RuntimeProjectMember(module, a.Name);
                else if (a.Target is NameExpr cn && !IsLocal(cn.Id) && TryResolveType(cn.Id, 0, out _, out var ccls) && ccls != null)
                    callee = $"new global::Typhon.Runtime.PyBoundMethod(typeof({MapType(cn, cn)}), {CsString(a.Name)})";
                else if (DotNetStaticMeta(a.Target) is string metadata && _r.GetMembers(metadata, a.Name).Any(m => m.IsMethod && m.IsStatic))
                    callee = $"new global::Typhon.Runtime.PyBoundMethod(typeof({CsTypeName(metadata)}), {CsString(a.Name)})";
                else if (InferType(a.Target) is "dynamic" or "object" or "object?" or "global::System.Type" or "System.Type")
                    callee = $"(object?){Rt("getattr")}((object?){Paren(EmitValue(a.Target))}, {CsString(a.Name)})";
                else if (InferType(a.Target) is string && !IsDotNetMethod(a))
                    callee = BoundMethodValue(a) ?? $"(object?){Rt("getattr")}((object?){Paren(EmitValue(a.Target))}, {CsString(a.Name)})";
            }
            if (callee == null) return null;
            // the function's own (dynamic) version may be what the call reaches
            if (Obs != null && CallTarget(c, out _) is InferenceTarget t && t.InferParams)
                Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = null, Context = _ctxFallbackKey });
            var spreadArgs = c.Args.Where(x => x.Name == null && !x.DoubleStar)
                .Select(x => x.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(x.Value))})" : $"(object?){Paren(EmitLooseValue(x.Value))}");
            string call = $"{Rt("__invoke_kw")}((object?){Paren(callee)}, [{string.Join(", ", spreadArgs)}], {KeywordArgs(c.Args)})";
            string type = c == _statementCall ? null : InferType(c); // (a cast is no statement: f(**opts) on its own line)
            return type != null && type != "dynamic" && type != "void" && !TypeInference.IsUnknown(type) && type != TypeInference.Pending ? $"(({type}){call})" : call;
        }

        string KeywordArgs(List<Arg> args)
        {
            var spreads = args.Where(a => a.DoubleStar).Select(a => $"(object?){Paren(EmitExpr(a.Value))}");
            var named = args.Where(a => a.Name != null).Select(a => $"({CsString(a.Name)}, (object?){Paren(EmitLooseValue(a.Value))})");
            return $"{Rt("__kwdict")}([{string.Join(", ", spreads)}], [{string.Join(", ", named)}])";
        }

        bool IsDataclassReplace(CallExpr c) =>
            c.Args.Count >= 1 && c.Args[0].Name == null && !c.Args[0].Star &&
            (c.Func is AttributeExpr ra && ra.Name == "replace" && DottedName(ra.Target) == "dataclasses" && !IsShadowed("dataclasses") ||
             c.Func is NameExpr rn && rn.Id == "replace" && !IsLocal("replace") && StdlibOwner("replace") == "Typhon.Stdlib.dataclasses");

        /// <summary>dataclasses.replace(obj, field=value, ...): a copy with those fields changed, of obj's type.</summary>
        string DataclassReplaceCall(CallExpr c)
        {
            if (!IsDataclassReplace(c)) return null;
            string t = InferType(c.Args[0].Value);
            string call = $"global::Typhon.Stdlib.dataclasses.__replace((object){Paren(EmitExpr(c.Args[0].Value))}, {KeywordArgs(c.Args)})";
            return t != null && t != "dynamic" && t != TypeInference.Pending ? $"(({t}){call})" : call;
        }

        /// <summary>
        /// A call of a runtime/.NET method or constructor whose last parameter is named __kwargs (the runtime's form of
        /// Python's **kwargs: parser.set_defaults(func=handler), argparse.Namespace(a=1)): the keyword arguments it has no
        /// parameter for, and **mappings, go into that dict. Null for other calls.
        /// </summary>
        string DotNetKwargsCall(CallExpr c)
        {
            bool keywords = c.Args.Any(a => a.Name != null || a.DoubleStar);
            bool isType = TryTypeExpr(c.Func, out var typeCode);
            if (!keywords && !isType) return null;
            var overloads = isType ? DotNetConstructors(c.Func) : DotNetOverloads(c);
            var target = overloads?.FirstOrDefault(o => o.Parameters.Count > 0 && o.Parameters[o.Parameters.Count - 1].Name == "__kwargs");
            if (target == null) return null;
            if (isType && target.Parameters.Any(parameter => parameter.IsPackedArguments))
            {
                var values = c.Args.Where(a => a.Name == null && !a.DoubleStar).Select(a => a.Star
                    ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})"
                    : $"(object?){Paren(EmitLooseValue(a.Value))}");
                string call = $"{Rt("__invoke_kw")}(typeof({typeCode}), [{string.Join(", ", values)}], {KeywordArgs(c.Args)})";
                return c == _statementCall ? call : $"(({typeCode}){call})";
            }
            if (!keywords) return null;
            if (c.Func is AttributeExpr member && target.Parameters.Any(parameter => parameter.IsPackedArguments))
                return InvokeLogicalMethod(member, $"(object?){Paren(EmitValue(member.Target))}", c.Args);
            var known = new HashSet<string>(target.Parameters.Select(p => p.Name));
            var direct = c.Args.Where(a => !a.DoubleStar && (a.Name == null || known.Contains(a.Name))).ToList();
            var extra = c.Args.Where(a => a.DoubleStar || a.Name != null && !known.Contains(a.Name)).ToList();
            if (extra.Count == 0) return null;
            string args = EmitArgs(direct, overloads: overloads);
            string callee = isType ? $"new {typeCode}" : c.Func is NameExpr fn ? EmitName(fn) : EmitCallee(c.Func);
            return $"{callee}({args}{(args.Length > 0 ? ", " : "")}__kwargs: {KeywordArgs(extra)})";
        }

        /// <summary>
        /// asyncio.run(main()): the coroutine is started by the runtime, on the event loop it installs first (a coroutine
        /// called here would already run its first steps on no loop at all).
        /// </summary>
        string AsyncioRunCall(CallExpr c)
        {
            bool isRun = c.Func is AttributeExpr ra && ra.Name == "run" && (DottedName(ra.Target) is string rt && (_dotnetAliases.TryGetValue(rt, out var rm) ? rm : null) == "Typhon.Stdlib.asyncio_") ||
                         c.Func is NameExpr rn && rn.Id == "run" && !IsLocal("run") && StdlibOwner("run") == "Typhon.Stdlib.asyncio_";
            if (!isRun || c.Args.Count != 1 || c.Args[0].Name != null || c.Args[0].Star || !(c.Args[0].Value is CallExpr)) return null;
            return $"global::Typhon.Stdlib.asyncio_.run(() => {EmitExpr(c.Args[0].Value)})";
        }

        bool UsesDictionaryUpdate(CallExpr call, AttributeExpr method)
        {
            if (method.Target is CallExpr { Func: NameExpr { Id: "super" } } || DotNetStaticMeta(method.Target) != null) return false;
            string receiver = InferType(method.Target);
            if (!IsDictType(receiver) && !IsUnknownType(receiver)) return false;
            if (call.Args.Any(arg => arg.Name != null || arg.DoubleStar)) return true;
            if (MemberType(receiver, method.Name, method: true) != null) return false;
            return call.Args.Count == 0 || call.Args.Any(arg => LateBoundArg(arg.Value));
        }

        string EmitDictionaryUpdate(CallExpr call, string receiver, bool useBase = false)
        {
            var values = call.Args.Where(a => a.Name == null && !a.DoubleStar).Select(a => a.Star
                ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})"
                : $"(object?){Paren(EmitLooseValue(a.Value))}");
            string update = useBase ? "__dict_base_update" : "__dict_update_keywords";
            return $"{Rt(update)}({receiver}, [{string.Join(", ", values)}], {KeywordArgs(call.Args)})";
        }

        Expr SuperListAppend(CallExpr call) =>
            call.Args.Count == 1 && call.Args[0] is { Name: null, Star: false, DoubleStar: false }
                ? DirectBuiltinSuper(call, "list", "append") : null;

        Expr DirectBuiltinSuper(CallExpr call, string builtin, string member) =>
            HasSelfInScope() && _cls?.Def.Bases.FirstOrDefault() is Expr directBase &&
            DottedName(directBase is SubscriptExpr genericBase ? genericBase.Target : directBase) == builtin &&
            call.Func is AttributeExpr { Target: CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } } method &&
            method.Name == member && !IsShadowed("super") && !IsShadowed(builtin)
            ? directBase : null;

        bool SuperLacks(string name)
        {
            var bases = _cls.Def.Bases.Where(b => !(DottedName(b is SubscriptExpr bs ? bs.Target : b) is "object" or "Protocol" or "typing.Protocol" or "ABC" or "abc.ABC" or "Generic" or "typing.Generic")).ToList();
            if (bases.Any(b => !ResolveBase(b, out _, out _))) return false;
            return !BaseHasMember(bases, name, requireOverridable: false);
        }

        /// <summary>functools.partial(f, *args, **keywords) with keywords: the keywords go in a dict.</summary>
        string PartialCall(CallExpr c)
        {
            bool isPartial = c.Func is AttributeExpr pa && pa.Name == "partial" && DottedName(pa.Target) == "functools" && !IsShadowed("functools") ||
                             c.Func is NameExpr pn && pn.Id == "partial" && !IsLocal("partial") && StdlibOwner("partial") == "Typhon.Stdlib.functools";
            if (!isPartial || c.Args.Count == 0 || c.Args[0].Name != null || c.Args[0].Star) return null;
            var positional = c.Args.Skip(1).Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
            return $"global::Typhon.Stdlib.functools.__partial_kw((object?){Paren(EmitLooseValue(c.Args[0].Value))}, [{string.Join(", ", positional)}], {KeywordArgs(c.Args)})";
        }

        string RuntimeStarCall(CallExpr call)
        {
            if (!call.Args.Any(a => a.Star) || StdlibModuleOf(call, out string name) is not string owner)
                return null;
            var members = _r.GetMembers(owner, name).Where(member => member.IsStatic).ToList();
            bool value = members.Any(member => !member.IsMethod);
            if (!value && (members.Count == 0 || members.Any(member =>
                member.TypeParams?.Count > 0 || member.Parameters.Any(parameter => parameter.IsParams))))
                return null;
            var positional = call.Args.Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})"
                                   : $"(object?){Paren(EmitLooseValue(a.Value))}");
            string bound = $"new global::Typhon.Runtime.PyBoundMethod(typeof({CsTypeName(owner)}), {CsString(name)})";
            string args = $"[{string.Join(", ", positional)}]";
            bool keywords = call.Args.Any(a => a.Name != null || a.DoubleStar);
            if (value)
            {
                string callee = $"(object?){Paren(EmitExpr(call.Func))}";
                return keywords ? $"{Rt("__invoke_kw")}({callee}, {args}, {KeywordArgs(call.Args)})" : $"{Rt("__invoke")}({callee}, {args})";
            }
            return keywords ? $"{bound}.__call_kw__({args}, {KeywordArgs(call.Args)})" : $"{bound}.__call__({args})";
        }

        string StringFormatCall(CallExpr call)
        {
            if (call.Func is not AttributeExpr { Name: "format" } member) return null;
            Expr template = member.Target;
            List<Arg> args = call.Args;
            bool builtin = template is NameExpr { Id: "str" } && !IsShadowed("str");
            if (builtin)
            {
                if (args.Count == 0 || args[0] is not Arg { Name: null, Star: false, DoubleStar: false }) return null;
                template = args[0].Value;
                args = args.Skip(1).ToList();
            }
            string type = InferType(template);
            bool knownString = builtin || type == "string" || template is ConstExpr { Kind: ConstKind.Str };
            if (!knownString && !(type is "object?" || IsUnknownType(type) && DotNetStaticMeta(template) == null)) return null;

            var positional = args.Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})"
                                    : $"(object?){Paren(EmitLooseValue(a.Value))}");
            string keywords = args.Any(a => a.Name != null || a.DoubleStar) ? KeywordArgs(args) : "null";
            string receiver = Paren(knownString ? EmitExpr(template) : EmitValue(template));
            return knownString
                ? $"global::Typhon.Runtime.Builtins.str_format((string){receiver}, [{string.Join(", ", positional)}], {keywords})"
                : $"global::Typhon.Runtime.Builtins.str_format_method((object?){receiver}, [{string.Join(", ", positional)}], {keywords})";
        }

        /// <summary>
        /// Python calls that need their own C#: fixed-size conversions, **kwargs, late-bound callables and receivers, containers
        /// started empty, methods called through a built-in type, str.format, generic calls, builtins consuming iterables, and
        /// Python methods of Python values. Null when the call is an ordinary one.
        /// </summary>
        string PythonCall(CallExpr c, bool targetTyped)
        {
            if (StringFormatCall(c) is string stringFormat) return stringFormat;

            if (c.Func is AttributeExpr { Name: "register" } reg && DottedName(reg.Target) is string name &&
                TryResolveType(name, 0, out _, out var abc) && abc != null && HasVirtualSubclasses(abc) &&
                !DefinesInHierarchy(abc, "register") && c.Args.Count == 1 &&
                c.Args[0] is Arg { Name: null, Star: false, DoubleStar: false } subtype)
                return $"{Rt("__register_abc")}(typeof({MapType(reg.Target, reg)}), (global::System.Type){Paren(EmitValue(subtype.Value))})";

            // int32(x), uint8(x), ...: explicit (checked) conversions to .NET's fixed-size numbers, for .NET APIs that need them
            if (c.Func is NameExpr cv && !IsShadowed(cv.Id) && NumericConversions.TryGetValue(cv.Id, out var convType) &&
                c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star && !c.Args[0].DoubleStar)
                return $"checked(({convType})({EmitExpr(c.Args[0].Value)}))";

            // a Typhon function or class with **kwargs: extra keyword arguments go into its kwargs dict
            if (TyphonCallee(c, out int kwImplicit) is FunctionDef kwCallee &&
                KwargsCallArgs(kwCallee, kwImplicit, c.Args) is string kwArgs)
            {
                if (TryTypeExpr(c.Func, out var kwType)) return $"new {kwType}({kwArgs})";
                if (c.Func is AttributeExpr bound && StaticThroughInstance(bound, out _) != null)
                    return InvokeLogicalMethod(bound, $"(object?){Paren(EmitExpr(bound.Target))}", c.Args);
                return $"{(c.Func is NameExpr kfn ? EmitName(kfn) : EmitCallee(c.Func))}({kwArgs})";
            }
            if (PartialCall(c) is string partialCall) return partialCall;
            if (AsyncioRunCall(c) is string asyncioRun) return asyncioRun;
            if (DataclassReplaceCall(c) is string replaceCall) return replaceCall;
            if (DecoratedMethodCall(c) is string decoratedCall) return decoratedCall;
            if (DotNetKwargsCall(c) is string kwargsCall) return kwargsCall;
            if (ExpandStarArgs(c) is string expanded) return expanded;
            if (KwargsSpreadCall(c) is string spreadCall) return spreadCall;
            if (RuntimeStarCall(c) is string runtimeStar) return runtimeStar;
            // table[name](x, *rest) / cls(n, n) with cls a late-bound parameter: the callable is known only at run time (a
            // function value, a bound method, a delegate or a class), so the call is made there
            // (also f(*args, **kwargs) / f(x, key=v) on such a callable, and f(*args) on a value of a function type: a
            // decorator's wrapper forwarding its arguments to the function it wraps)
            bool keywords = c.Args.Any(a => a.Name != null || a.DoubleStar);
            bool unpacks = c.Args.Any(a => a.Star || a.DoubleStar);
            ModuleInfo starOwner = null; FunctionDef starDef = null;
            bool fnTypeStar = unpacks && FunctionOfType(InferType(c.Func), out starOwner, out starDef);
            bool runtimeCallable = c.Func is BoolOpExpr or IfExpr ||
                c.Func is AttributeExpr attribute &&
                (attribute.Name == "__class__" || LateCallableField(attribute) || IsInstanceClassDefault(attribute) ||
                    unpacks && UsesRuntimeSelfAttribute(attribute)) ||
                InferType(c.Func) is "global::System.Type" or "System.Type" ||
                DotNetMeta(InferType(c.Func)) is string nativeCallable &&
                _r.GetMembers(nativeCallable, "__call__").Any(method => method.IsMethod && !method.IsStatic) && !TryTypeExpr(c.Func, out _);
            bool typedGenericConstructor = IsBuiltinGenericAlias(c.Func) && TryTypeExpr(c.Func, out _);
            if (!typedGenericConstructor && (runtimeCallable || !(c.Func is AttributeExpr)) &&
                (runtimeCallable || c.Func is LambdaExpr || unpacks || c.Func is NameExpr || c.Func is SubscriptExpr || c.Func is CallExpr) &&
                (runtimeCallable || c.Func is LambdaExpr || InferType(c.Func) == "dynamic" ||
                 IsTypeParamType(InferType(c.Func)) || c.Func is NameExpr fnl && IsLocal(fnl.Id) && InferType(c.Func) == null && !IsLocalFunctionName(fnl.Id) ||
                 c.Func is CallExpr && InferType(c.Func) == null && MaybeLateBound(c.Func) || fnTypeStar) &&
                (!keywords || !(c.Func is NameExpr kfn0 && TyphonCallee(c, out _) != null && !IsLocal(kfn0.Id))))
            {
                // the function's own (dynamic) version is what the call reaches
                if (fnTypeStar && Obs != null && TargetOf(starOwner, null, starDef.Name) is InferenceTarget st && st.InferParams)
                    Obs.Calls.Add(new Observations.Call { Callee = st.Key, Types = null, Context = _ctxFallbackKey });
                var spreadArgs = c.Args.Where(a => a.Name == null && !a.DoubleStar)
                    .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
                string positional = $"[{string.Join(", ", spreadArgs)}]";
                string callable = c.Func is LambdaExpr lambda ? EmitRuntimeLambda(lambda)
                    : ImportedBuiltinTypeValue(c.Func) ?? EmitExpr(c.Func);
                string callee = $"(object?){Paren(callable)}";
                if (!keywords) return $"{Rt("__invoke")}({callee}, {positional})";
                return $"{Rt("__invoke_kw")}({callee}, {positional}, {KeywordArgs(c.Args)})";
            }
            // A runtime attribute may come from a descriptor or __getattr__ before its arguments are unpacked.
            if (c.Func is AttributeExpr spreadMethod && InferType(spreadMethod.Target) == "dynamic" &&
                c.Args.Any(arg => arg.Star) && !c.Args.Any(arg => arg.Name != null || arg.DoubleStar))
                return InvokeLogicalMethod(spreadMethod, $"(object?){Paren(EmitExpr(spreadMethod.Target))}", c.Args);
            if (TryTypeExpr(c.Func, out var typeCode))
            {
                if (c.Args.Count == 1 && c.Args[0] is Arg { Name: null or "value", Star: false, DoubleStar: false } enumValue &&
                    DottedName(c.Func) is string enumName && TryResolveType(enumName, 0, out _, out var enumClass) &&
                    enumClass != null && IsStringEnum(enumClass))
                    return $"(({typeCode})global::Typhon.Runtime.PyStrEnum.Lookup(typeof({typeCode}), (object?){Paren(EmitValue(enumValue.Value))}))";
                if (Obs != null && TryDataclassConstructor(c, out _))
                    for (int i = 0; i < c.Args.Count; i++)
                    {
                        var argument = c.Args[i];
                        if (ElementKey(argument.Value, out char kind) is string key && kind == 'd' &&
                            DictTypes(ParamTypeOf(c, i, argument.Name)) is List<string> types &&
                            types.All(type => !UnsureType(type)))
                            Obs.AddExpectedElement(key, types[0] + "→" + types[1]);
                    }
                if (c.Args.Count == 0 && CallTarget(c, out bool constructor) is InferenceTarget target && constructor &&
                    target.InferParams && target.Params.Count > 0 && target.Params.All(p => p.Default != null) &&
                    Ti.Signatures.TryGetValue(target.Key, out var signatures) &&
                    signatures.Count + (Ti.Fallback.Contains(target.Key) ? 1 : 0) > 1)
                    return $"(({typeCode}){Rt("__invoke")}(typeof({typeCode}), []))";
                return $"new {typeCode}({EmitArgs(c.Args, overloads: DotNetConstructors(c.Func), targetTypedAt: TypedParamPositions(c), typedKeywords: name => TypedKeyword(c, name), paramTypeAt: (i, name) => ParamTypeOf(c, i, name), typhonCallee: TyphonCallee(c, out _) != null)})";
            }

            // Counter() / deque() / OrderedDict() / defaultdict(f) where no declaration gives the element type
            if (TypeInference.IsEmptyContainer(c) && "cqoD".IndexOf(TypeInference.ContainerKind(c)) >= 0 && c.Func is NameExpr ecn && _staticTypes.Contains("Typhon.Stdlib.collections"))
            {
                char ek = TypeInference.ContainerKind(c);
                string d = ImplicitDynamic(c, $"the element type of '{ecn.Id}()'");
                string fv = ek == 'D' ? DefaultFactoryValueType(c) ?? (c.Args.Count > 0 && c.Args[0].Value is NameExpr f0 ? (f0.Id == "list" ? $"List<{d}>" : f0.Id == "set" ? $"HashSet<{d}>" : d) : d) : d;
                string type = ek == 'c' ? $"global::Typhon.Runtime.Counter<{d}>" : ek == 'q' ? $"global::Typhon.Runtime.deque<{d}>"
                    : ek == 'o' ? $"global::Typhon.Runtime.PyOrderedDict<{d}, {d}>" : $"global::Typhon.Runtime.defaultdict<{d}, {fv}>";
                return EmitEmptyContainer(c, type, ek);
            }

            // chain is backed by native functions, with its Python class method exposed separately.
            if (c.Func is AttributeExpr { Name: "from_iterable" } chain &&
                NativeChain(chain.Target) && c.Args.Count == 1 && c.Args[0] is Arg { Name: null, Star: false, DoubleStar: false })
                return $"global::Typhon.Stdlib.itertools.chain_from_iterable({EmitArgs(c.Args)})";

            if (c.Func is AttributeExpr { Name: "__repr__" or "__wrapped__" } descriptor &&
                descriptor.Target is not CallExpr { Func: NameExpr { Id: "super" } } &&
                c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
                return $"{Rt("__invoke")}((object?){Paren(EmitExpr(descriptor))}, [{string.Join(", ", c.Args.Select(a => EmitLooseValue(a.Value)))}])";

            // methods called through a built-in type: dict.fromkeys(keys) / str.maketrans(a, b) are functions,
            // str.upper(s) / list.append(xs, x) are method calls on the first argument
            if (c.Func is AttributeExpr baseObject && BaseObjectMethod(baseObject) is string helper &&
                c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
            {
                string call = $"global::Typhon.Runtime.Builtins.{helper}({string.Join(", ", c.Args.Select(a => EmitLooseValue(a.Value)))})";
                return helper == "__object_new" ? $"((dynamic){call})" : call;
            }
            if (c.Func is AttributeExpr { Target: NameExpr bytesClass, Name: "maketrans" } &&
                !IsShadowed(bytesClass.Id) && bytesClass.Id is "bytes" or "bytearray")
                return $"{PyBytesType}.maketrans({EmitArgs(c.Args)})";
            if (c.Func is AttributeExpr bt && bt.Target is NameExpr btn && BuiltinTypeNames.Contains(btn.Id) && !IsShadowed(btn.Id))
            {
                if (btn.Id == "float" && bt.Name == "fromhex" && c.Args.Count == 1 &&
                    c.Args[0] is Arg { Name: null, Star: false, DoubleStar: false } hexString)
                    return $"{Rt("__float_fromhex")}((object?){Paren(EmitExpr(hexString.Value))})";
                if (BuiltinClassMethods.Contains(bt.Name))
                {
                    if (btn.Id == "dict" && bt.Name == "fromkeys" && c.Args.Count == 2 &&
                        c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar))
                        return $"{Rt(bt.Name)}({EmitArgs(new List<Arg> { c.Args[0] })}, {EmitValue(c.Args[1].Value)})";
                    string method = _big && btn.Id == "int" && bt.Name == "from_bytes" ? "from_bytes_big" : bt.Name;
                    return $"{Rt(method)}({EmitArgs(c.Args)})";
                }
                if (c.Args.Count >= 1 && c.Args[0].Name == null && !c.Args[0].Star)
                {
                    var rest = c.Args.Skip(1).ToList();
                    string recv = EmitExpr(c.Args[0].Value);
                    if (c.Args[0].Value is ConstExpr || c.Args[0].Value is BinOpExpr || c.Args[0].Value is UnaryExpr) recv = Paren(recv);
                    return $"{recv}.{Esc(bt.Name)}({EmitArgs(rest)})";
                }
            }

            // str.maketrans(mapping) is a static Python method even when accessed through a string value.
            if (c.Func is AttributeExpr translation && translation.Name == "maketrans" && InferType(translation.Target) == "string" &&
                c.Args.Count == 1 && c.Args[0] is Arg { Name: null, Star: false, DoubleStar: false } table)
                return $"global::Typhon.Runtime.Builtins.maketrans({EmitExpr(translation.Target)}, (global::System.Collections.IDictionary){Paren(EmitExpr(table.Value))})";

            // generic method call with explicit type arguments: f[int](x)  →  f<int>(x)
            if (c.Func is SubscriptExpr gs && !(gs.Index is SliceExpr))
            {
                var targs = gs.Index is TupleExpr t ? t.Elts : new List<Expr> { gs.Index };
                bool moduleGlobal = gs.Target is AttributeExpr member && DottedName(member.Target) is string moduleName &&
                    ModuleRef(moduleName)?.Globals.Contains(member.Name) == true;
                if (targs.All(IsTypeish) && !moduleGlobal && !(gs.Target is NameExpr gn && IsLocal(gn.Id)) &&
                    InferType(gs.Target) is not "global::Typhon.Runtime.PyAttrDict" && !IsDictType(InferType(gs.Target)))
                    return $"{EmitExpr(gs.Target)}<{string.Join(", ", targs.Select(x => MapType(x, c)))}>({EmitArgs(c.Args)})";
            }

            // builtins that consume iterables: a dict argument must contribute its keys (sorted(d), list(d), ", ".join(d))
            if (c.Func is NameExpr ifn && !IsShadowed(ifn.Id) && IterableArgBuiltins.TryGetValue(ifn.Id, out var spec))
            {
                if (ifn.Id == "map" && c.Args.Count >= 3 && c.Args[0].Name == null &&
                    c.Args.All(argument => argument.Name is null or "strict" && !argument.Star && !argument.DoubleStar))
                {
                    string callable = EmitLooseValue(c.Args[0].Value);
                    var inputs = c.Args.Skip(1).Where(argument => argument.Name == null)
                        .Select(argument => $"(object?){Paren(EmitValue(argument.Value))}");
                    string strict = c.Args.FirstOrDefault(argument => argument.Name == "strict") is Arg flag
                        ? EmitValue(flag.Value) : "false";
                    return $"{Rt("__map_many")}((object?){Paren(callable)}, [{string.Join(", ", inputs)}], (object?){Paren(strict)})";
                }
                if (ifn.Id == "zip" && c.Args.Count == 3 && c.Args[2] is Arg { Name: "strict" } &&
                    c.Args.Take(2).All(arg => arg.Name == null && !arg.Star && !arg.DoubleStar))
                    return $"{Rt("__zip_strict")}({EmitArgs(c.Args, i => true)})";
                if (ifn.Id == "filter" && c.Args.Count == 2 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar) &&
                    IsNone(c.Args[0].Value))
                    return $"{Rt("__filter_truthy")}({EmitIterable(c.Args[1].Value)})";
                if (ifn.Id is "map" or "filter" && c.Args.Count == 2 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar) &&
                    IsUnknownType(IterElementType(InferType(c.Args[1].Value))) && BoundMethodValue(c.Args[0].Value) is string bound)
                    return $"{EmitName(ifn)}({bound}, {EmitIterable(c.Args[1].Value)})";
                // A class is callable in Python. Give map a delegate that constructs a new instance for each item.
                if (ifn.Id == "map" && c.Args.Count == 2 && c.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar) &&
                    c.Args[0].Value is NameExpr mappedClass && !IsLocal(mappedClass.Id) && TryTypeExpr(mappedClass, out var mappedType))
                    return $"{EmitName(ifn)}(__v => new {mappedType}(__v), {EmitIterable(c.Args[1].Value)})";
                int positional = c.Args.Count(a => a.Name == null && !a.Star && !a.DoubleStar);
                if (!(spec.OnlyWhenSingle && positional != 1))
                    return $"{EmitName(ifn)}({EmitArgs(c.Args, i => i >= spec.First && (spec.All || i == spec.First))})";
            }
            if (c.Func is AttributeExpr ja && ja.Name == "join" && c.Args.Count == 1 && c.Args[0].Name == null &&
                (ja.Target is ConstExpr jc && jc.Kind == ConstKind.Str || InferType(ja.Target) == "string"))
                return $"global::Typhon.Runtime.Builtins.join({EmitExpr(ja.Target)}, {EmitIterable(c.Args[0].Value)})";

            // xs.append(value) with a late-bound argument: C# dispatches the call dynamically, and dynamic dispatch never finds
            // extension methods, so Typhon's list/dict/set/str methods are called as the static methods they are
            // (also an argument C# may see as dynamic although Typhon types it: a dynamically dispatched call or a call that may
            // reach a function's dynamic version; and any call on a class deriving from list/dict/set)
            // xs.append(0.5) on a local list of ints (xs = [0]): Python's list holds both, so the variable becomes a list of any
            if (Obs != null && c.Func is AttributeExpr wa && (wa.Name == "append" || wa.Name == "insert") && wa.Target is NameExpr wn && IsLocal(wn.Id) &&
                InferType(wa.Target) is string wt && wt.StartsWith("List<") && c.Args.Count >= 1 &&
                (InferType(c.Args[c.Args.Count - 1].Value) ?? (MaybeLateBound(c.Args[c.Args.Count - 1].Value) ? "dynamic" : null)) is string wv &&
                AppendWidens(GenericArg(wt), wv, c.Args[c.Args.Count - 1].Value is BinOpExpr wb && ArithmeticOps.Contains(wb.Op)))
                Obs.AddConflict(_scopeKey + "|" + wn.Id, "List<dynamic>");
            if (c.Func is AttributeExpr em && RuntimeExtensionMethods.Contains(em.Name) && (IsPythonContainer(InferType(em.Target)) || IsBuiltinSubclass(InferType(em.Target))) &&
                !(ClassKeyOfType(InferType(em.Target), out _) is string containerKey && ClassDefOfKey(containerKey) is ClassDef containerClass &&
                    MethodInHierarchy(containerClass, em.Name) != null) &&
                em.Name != "get" && em.Name != "pop" && !c.Args.Any(a => a.Star || a.DoubleStar || a.Name != null) &&
                (c.Args.Any(a => !(a.Value is ConstExpr) && !IsEmptyLiteral(a.Value)) || IsBuiltinSubclass(InferType(em.Target))) &&
                _r.GetMembers("Typhon.Runtime.Builtins", em.Name).Count > 0)
            {
                string recv = EmitExpr(em.Target);
                // append/insert take the list's item type. In a list[Callable] that type can itself be a delegate.
                string receiverType = InferType(em.Target);
                bool addsListItem = receiverType?.StartsWith("List<") == true && em.Name is "append" or "insert";
                var rest = EmitArgs(c.Args, targetTypedAt: i => true,
                    paramTypeAt: (i, _) => addsListItem && i == c.Args.Count - 1 ? GenericArg(receiverType) : null);
                return $"global::Typhon.Runtime.Builtins.{Esc(em.Name)}({recv}{(rest.Length > 0 ? ", " + rest : "")})";
            }
            // m.items() / keys() / values() / get(k, d) of a Mapping[K, V] (IReadOnlyDictionary)
            if (c.Func is AttributeExpr ro && InferType(ro.Target) is string rot && rot.StartsWith("IReadOnlyDictionary<") &&
                (ro.Name == "items" || ro.Name == "keys" || ro.Name == "values" || ro.Name == "get" && c.Args.Count == 2))
                return $"{Rt("__ro_" + ro.Name)}({EmitExpr(ro.Target)}{(c.Args.Count > 0 ? ", " + EmitArgs(c.Args) : "")})";

            // d.strip(), d.split(","), d.items() on a late-bound value: dynamic dispatch never finds extension methods, so a
            // Python value (str, list, dict, set, number) goes to the runtime's static method; anything else (a Typhon
            // object, a .NET object) keeps its own method
            if (IsLateRuntimeMethodCall(c, out var dm))
            {
                var methods = _r.GetMembers("Typhon.Runtime.Builtins", dm.Name)
                    .Where(method => method.IsMethod && method.IsStatic);
                if (!methods.Any(method => RuntimeExtensionAccepts(method, c.Args)))
                    return InvokeLogicalMethod(dm, $"(object?){Paren(EmitExpr(dm.Target))}", c.Args);
                string r = NewTmp("r");
                string rest = EmitArgs(c.Args, dynamicCall: true);
                string sep = rest.Length > 0 ? ", " : "";
                string receiver = $"((dynamic){Paren(EmitExpr(dm.Target))})";
                string ownMethod = $"{r}.{Esc(dm.Name)}({rest})";
                if (c.Args.All(a => a.Name == null))
                {
                    // A Python method compiled as void still yields None when its result is used.
                    var values = c.Args.Select(a => $"(object?){Paren(EmitLooseValue(a.Value))}");
                    string arguments = $"[{string.Join(", ", values)}]";
                    if (RuntimeExtensionMethods.Contains(dm.Name) &&
                        _r.GetMembers("Typhon.Runtime.Builtins", dm.Name).Any(method => method.IsMethod && method.IsStatic && method.ReturnsVoid))
                        return $"{Rt("__invoke")}((object?){Rt("getattr")}((object?){receiver}, {CsString(dm.Name)}), {arguments})";
                    ownMethod = $"{Rt("__invoke")}((object?){Rt("getattr")}((object?){r}, {CsString(dm.Name)}), {arguments})";
                }
                return $"({receiver} is var {r} && {Rt("__uses_builtin_method")}({r}, {CsString(dm.Name)}) ? global::Typhon.Runtime.Builtins.{Esc(dm.Name)}({r}{sep}{rest}) : {ownMethod})";
            }
            return null;
        }

        bool RuntimeExtensionAccepts(Semantics.MemberSig method, List<Arg> args)
        {
            int positional = args.Count(arg => arg.Name == null) + 1; // the receiver is the first argument
            var keywords = args.Where(arg => arg.Name != null).Select(arg => arg.Name).ToList();
            if (!Applicable(method, positional, keywords)) return false;
            int index = 1;
            foreach (var arg in args)
            {
                var parameter = arg.Name == null ? ParamAt(method, index++) : method.Parameters.FirstOrDefault(p => p.Name == arg.Name);
                if (Incompatible(parameter?.GenericType?.TrimEnd('?'), InferType(arg.Value)?.TrimEnd('?'))) return false;
            }
            return true;
        }

        string InvokeLogicalMethod(AttributeExpr method, string receiver, List<Arg> args)
        {
            string callable = $"(object?){Rt("getattr")}({receiver}, {CsString(method.Name)})";
            var positional = args.Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
            string values = $"[{string.Join(", ", positional)}]";
            return args.Any(a => a.Name != null || a.DoubleStar)
                ? $"{Rt("__invoke_kw")}({callable}, {values}, {KeywordArgs(args)})"
                : $"{Rt("__invoke")}({callable}, {values})";
        }

        HashSet<string> _packedMethodNames;
        HashSet<string> _classMethodNames;

        string LatePythonMethodCall(CallExpr call)
        {
            if (_m.IsGdScript || call.Func is not AttributeExpr method || !LatePythonMethodReceiver(method.Target) ||
                call.Args.Any(a => a.Star || a.DoubleStar)) return null;

            if (_packedMethodNames == null)
            {
                var declaredMethods = AllClasses().SelectMany(c => c.Body.OfType<FunctionDef>()).ToList();
                _packedMethodNames = new HashSet<string>(declaredMethods.Where(NeedsPackedVarArgs).Select(f => f.Name));
                _classMethodNames = new HashSet<string>(declaredMethods.Where(f => HasDecorator(f.Decorators, "classmethod") ||
                    HasDecorator(f.Decorators, "staticmethod")).Select(f => f.Name));
            }
            bool packedMethod = _packedMethodNames.Contains(method.Name);
            bool classMethod = _classMethodNames.Contains(method.Name);
            if (!packedMethod && !classMethod) return null;

            string receiver = $"(object?){Paren(EmitExpr(method.Target))}";
            string temporary = NewTmp("pythonReceiver");
            var checks = new List<string>();
            if (classMethod) checks.Add($"{temporary} is global::System.Type");
            if (packedMethod) checks.Add($"{Rt("__has_packed_method")}({temporary}, {CsString(method.Name)})");
            string requiresBinding = string.Join(" || ", checks);
            string python = InvokeLogicalMethod(method, temporary, call.Args);
            string ordinary = $"((dynamic){temporary}).{Esc(method.Name)}({EmitArgs(call.Args, dynamicCall: true)})";
            // Only one branch evaluates the arguments. CLR calls keep their original overload and void-call behavior.
            return _statementCall == call
                ? $"{{ var {temporary} = {receiver}; if ({requiresBinding}) _ = {python}; else {ordinary}; }}"
                : $"(({receiver} is var {temporary} && ({requiresBinding})) ? {python} : {ordinary})";
        }

        bool IsLateRuntimeMethodCall(CallExpr call, out AttributeExpr method)
        {
            method = call.Func as AttributeExpr;
            return method != null && !call.Args.Any(a => a.Star || a.DoubleStar) &&
                LateRuntimeMethodReceiver(method.Target, call.Args) && IsRuntimeMethod(method.Name, call.Args);
        }

        bool LateRuntimeMethodReceiver(Expr receiver, List<Arg> arguments, bool statement = false)
        {
            if (LatePythonMethodReceiver(receiver, statement)) return true;
            // C# still knows a local's initializer type, but a late argument disables extension binding.
            return receiver is NameExpr local && IsLocal(local.Id) && InferType(receiver) == null &&
                arguments.Any(argument => LateBoundArg(argument.Value));
        }

        bool LatePythonMethodReceiver(Expr receiver, bool statement = false)
        {
            string type = InferType(receiver);
            // An unresolved local can still have a concrete C# type inferred from its initializer.
            // Late subscripts, on the other hand, explicitly return dynamic from the runtime.
            if (type is ("dynamic" or "object" or "object?") || receiver is CallExpr && MaybeLateBound(receiver)) return true;
            if (receiver is SubscriptExpr item && UsesRuntimeSubscriptReceiver(item.Target, InferType(item.Target))) return true;
            if (type != null || receiver is not AttributeExpr attribute) return false;
            return DottedName(attribute) is string dotted && IsLocal(dotted.Split('.')[0]) ||
                statement && attribute.Target is not NameExpr { Id: "self" } && InferType(attribute.Target) == null;
        }

        bool NativeChain(Expr receiver)
        {
            if (receiver is NameExpr name)
            {
                if (IsShadowed(name.Id)) return false;
                if ((AliasTarget(name) ?? ImportAlias(name)) is AttributeExpr imported) receiver = imported;
                else return name.Id == "chain" && StdlibOwner(name.Id) == "Typhon.Stdlib.itertools";
            }
            return receiver is AttributeExpr { Name: "chain" } member &&
                DotNetStaticMeta(member.Target) == "Typhon.Stdlib.itertools";
        }

        static readonly HashSet<string> StaticConversions = new HashSet<string> { "int", "float", "str", "repr", "len", "bytes", "bytearray", "dir" };

        /// <summary>Is the name a Python method the runtime implements as a static (extension) method (strip, split, items, ...)?</summary>
        bool IsRuntimeMethod(string name)
        {
            if (name == "format") return false; // value.format(...) has its own dispatch; the builtin format(value, spec) is separate.
            if (!_runtimeMethods.TryGetValue(name, out bool yes))
                _runtimeMethods[name] = yes = RuntimeExtensionMethods.Contains(name) ||
                    _r.GetMembers("Typhon.Runtime.Builtins", name).Any(m => m.IsMethod && m.IsStatic && m.Parameters.Count >= 1);
            return yes;
        }
        readonly Dictionary<string, bool> _runtimeMethods = new Dictionary<string, bool>();

        /// <summary>
        /// A runtime method of that name that can take these arguments (count and keyword names). obj.find(update=True) on a
        /// late-bound obj only has the runtime's branch when some str/list/dict method could be meant; otherwise the call goes
        /// to the object's own method alone (a Typhon class with a find method).
        /// </summary>
        bool IsRuntimeMethod(string name, List<Arg> args)
        {
            if (!IsRuntimeMethod(name)) return false;
            var overloads = _r.GetMembers("Typhon.Runtime.Builtins", name).Where(m => m.IsMethod && m.IsStatic && m.Parameters.Count >= 1).ToList();
            if (overloads.Count == 0) return true;
            int positional = args.Count(a => a.Name == null);
            var keywords = args.Where(a => a.Name != null).Select(a => a.Name).ToList();
            return overloads.Any(m =>
            {
                var ps = m.Parameters.Skip(1).ToList(); // the receiver comes first
                bool hasParams = ps.Count > 0 && ps[ps.Count - 1].IsParams;
                int fixedCount = hasParams ? ps.Count - 1 : ps.Count;
                if (!hasParams && positional > fixedCount) return false;
                for (int i = positional; i < fixedCount; i++)
                    if (!ps[i].IsOptional && !keywords.Contains(ps[i].Name)) return false;
                return keywords.All(k => ps.Take(fixedCount).Skip(positional).Any(x => x.Name == k));
            });
        }

        /// <summary>Python methods of str/list/dict/set that the Typhon runtime provides as extension methods.</summary>
        static readonly HashSet<string> RuntimeExtensionMethods = new HashSet<string>
        {
            "append", "extend", "insert", "pop", "remove", "index", "count", "sort", "reverse", "copy", "clear", "items", "keys", "values", "get",
            "setdefault", "update", "popitem", "add", "discard", "union", "difference", "intersection", "symmetric_difference", "issubset",
            "issuperset", "isdisjoint", "difference_update", "intersection_update", "symmetric_difference_update", "join", "split", "rsplit",
            "strip", "lstrip", "rstrip", "replace", "startswith", "endswith", "find", "rfind", "partition", "rpartition", "zfill", "center",
            "ljust", "rjust", "removeprefix", "removesuffix",
        };
    }
}
