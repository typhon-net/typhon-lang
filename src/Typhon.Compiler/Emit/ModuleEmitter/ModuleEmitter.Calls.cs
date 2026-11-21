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
                if (DottedName(a.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) && ModuleRef(dotted) is ModuleInfo mref) { mod = mref; name = a.Name; }
                else if (a.Target is NameExpr sn && sn.Id == "self" && _cls != null)
                {
                    var m = _cls.Def.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
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
            }
            if (mod == null) return null;
            if (mod.Classes.TryGetValue(name, out var cls))
            {
                implicitParams = 1;
                return cls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__") ?? NearestInitAncestor(cls)?.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
            }
            return mod.Functions.Contains(name) ? mod.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name) : null;
        }

        /// <summary>
        /// The arguments of a call to a Typhon function with **kwargs: declared parameters by name, extra keyword arguments
        /// (and **mappings) collected into the kwargs dict, extra positional ones into the *args array. Null if the callee
        /// has no **kwargs.
        /// </summary>
        internal string KwargsCallArgs(FunctionDef callee, int implicitParams, List<Arg> args)
        {
            if (callee == null) return null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            var kw = ps.FirstOrDefault(p => p.Kind == ParamKind.KwArgs);
            if (kw == null || args.Any(a => a.Star)) return null;
            var va = ps.FirstOrDefault(p => p.Kind == ParamKind.VarArgs);
            var fixedPs = ps.Where(p => p.Kind == ParamKind.Normal).ToList();
            // parameters after *args are keyword-only: positional arguments fill only those before it
            var positionalPs = va == null ? fixedPs : ps.TakeWhile(p => p.Kind == ParamKind.Normal).ToList();
            var parts = new List<string>(); var extras = new List<string>(); var spreads = new List<string>(); var rest = new List<string>();
            int pos = 0;
            foreach (var a in args)
            {
                if (a.DoubleStar) { spreads.Add($"(object?){Paren(EmitExpr(a.Value))}"); continue; }
                if (a.Name == null)
                {
                    if (pos < positionalPs.Count) parts.Add($"{Esc(positionalPs[pos++].Name)}: {EmitValue(a.Value)}");
                    else if (va != null) rest.Add(EmitValue(a.Value));
                    else { Error(a.Value, $"too many positional arguments for '{callee.Name}'"); return null; }
                }
                else if (fixedPs.Any(p => p.Name == a.Name)) parts.Add($"{Esc(a.Name)}: {EmitValue(a.Value)}");
                else extras.Add($"[{CsString(a.Name)}] = {EmitValue(a.Value)}");
            }
            if (rest.Count > 0) parts.Add($"{Esc(va.Name)}: new {(va.Annotation != null ? MapType(va.Annotation, va) : "object")}[] {{ {string.Join(", ", rest)} }}");
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
            string func = c.Func is NameExpr cfn ? EmitName(cfn) : EmitExpr(c.Func);
            string call = TryTypeExpr(c.Func, out var typeCode) ? $"new {typeCode}({string.Join(", ", args)})" : $"{func}({string.Join(", ", args)})";
            return prefix != null ? $"({prefix}{call} }})" : call;
        }

        /// <summary>Positions where a Typhon callee declares the parameter's type (an empty [] / {} there takes that type).</summary>
        System.Func<int, bool> TypedParamPositions(CallExpr c)
        {
            var callee = TyphonCallee(c, out int implicitParams);
            if (callee == null) return null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            return i => i >= 0 && i < ps.Count && ps[i].Kind == ParamKind.Normal && ps[i].Annotation != null;
        }

        /// <summary>The C# type of a Typhon callee's annotated parameter (by position, or by name for a keyword argument), or null.</summary>
        string ParamTypeOf(CallExpr c, int pos, string name)
        {
            var callee = TyphonCallee(c, out int implicitParams);
            // a dataclass's generated constructor: its fields in order
            if (callee == null && c.Func is NameExpr dcn && TryResolveType(dcn.Id, 0, out _, out var dcls) && dcls != null && HasDecorator(dcls.Decorators, "dataclass"))
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

        /// <summary>Is the keyword argument's parameter of a Typhon callee annotated (so an empty literal takes its type)?</summary>
        bool TypedKeyword(CallExpr c, string name)
        {
            var callee = TyphonCallee(c, out _);
            return callee != null && callee.Params.Any(p => p.Name == name && p.Kind == ParamKind.Normal && p.Annotation != null);
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
                // [1, 2, 3] for a list[Any] / bare list parameter: built as that type (C# lists are invariant)
                if (!a.Star && (a.Value is ListExpr || a.Value is DictExpr || a.Value is SetExpr) && !IsEmptyLiteral(a.Value) &&
                    paramTypeAt?.Invoke(argPos, a.Name) is string cpt && cpt.Contains("dynamic") && PyType(InferType(a.Value)) != cpt)
                {
                    string typedLit = EmitExpr(a.Value, targetTyped: true);
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: ({cpt}){typedLit}" : $"({cpt}){typedLit}");
                    continue;
                }
                if (a.Name == null && !a.Star && targetTypedAt != null && targetTypedAt(argPos) && IsEmptyLiteral(a.Value))
                {
                    parts.Add(paramTypeAt?.Invoke(argPos, null) is string pt && ConcreteEmpty(a.Value, pt) is string concrete ? concrete : EmitExpr(a.Value, targetTyped: true));
                    continue;
                }
                if (a.Name != null && typedKeywords != null && IsEmptyLiteral(a.Value) && typedKeywords(a.Name))
                {
                    string ev = paramTypeAt?.Invoke(-1, a.Name) is string pt && ConcreteEmpty(a.Value, pt) is string concrete ? concrete : EmitExpr(a.Value, targetTyped: true);
                    parts.Add($"{Esc(a.Name)}: {ev}");
                    continue;
                }
                // a module function passed to a Typhon function whose parameters are inferred: its function type (that
                // parameter is specialised for it); passed to a late-bound call: a callable object
                // (also where no overload of a .NET callee takes a function there: print(f), xs.append(f), str(f))
                var paramsHere = functionShapes?.Select(o => a.Name != null ? o.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(o, argPos)).Where(x => x != null).ToList();
                bool noDelegateHere = paramsHere != null && paramsHere.Count > 0 && paramsHere.All(x => !x.TakesFunction);
                // (and to an unannotated parameter of a Typhon function, even in a call that runs its dynamic version)
                bool untypedTyphonParam = typhonCallee && !a.Star && paramTypeAt?.Invoke(argPos, a.Name) == null;
                if (a.Value is NameExpr fnArg && !a.Star && (typedFunctionArgs || dynamicCall || noDelegateHere || untypedTyphonParam) && FunctionValue(fnArg, typed: typedFunctionArgs) is string fnValue)
                {
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {fnValue}" : fnValue);
                    continue;
                }
                // a builtin passed as a function (map(str, xs), key=len): C# cannot convert an overloaded method group whose
                // parameter is object/generic to Func<int, string>, but a lambda binds the right overload at the call site
                string v = a.Value is NameExpr vn && ValueBuiltins.Contains(vn.Id) && !IsShadowed(vn.Id) && !_renames.ContainsKey(vn.Id)
                    ? (_big && vn.Id == "int" ? $"(__v => {Rt("__bigint")}(__v))" : $"(__v => {EmitName(vn)}(__v))")
                    // str.upper / str.lower / list.copy passed as a function: a lambda calling the method on its argument
                    : a.Value is AttributeExpr um && um.Target is NameExpr umt && BuiltinTypeNames.Contains(umt.Id) && !IsShadowed(umt.Id) && !BuiltinClassMethods.Contains(um.Name)
                    ? $"(__v => __v.{Esc(um.Name)}())"
                    // a fully annotated function for a System.Delegate parameter (app.MapGet("/todos/{id}", get_todo)): the method
                    // group itself, so the callee sees its parameter names, defaults and attributes
                    : a.Value is NameExpr dg && paramsHere != null && paramsHere.Any(x => x.IsDelegateBase) &&
                      FunctionRef(dg, out _, out var dgDef) && dgDef.TypeParams.Count == 0 && dgDef.Params.All(x => x.Annotation != null && x.Kind == ParamKind.Normal)
                    ? EmitName(dg)
                    : a.Value is NameExpr fv && FunctionValueLambda(fv) is string lambda ? lambda
                    : a.Value is NameExpr mg && FunctionRef(mg, out _, out _) ? EmitName(mg) // a method group, for a delegate parameter
                    : EmitExpr(a.Value);
                // a dynamically dispatched call cannot take a lambda as such: give it a delegate type of dynamic values
                if (dynamicCall && a.Value is LambdaExpr dl)
                    v = $"((Func<{string.Join(", ", Enumerable.Repeat("dynamic", dl.Params.Count + 1))}>){v})";
                else if (dynamicCall && v.StartsWith("(__v =>")) v = $"((Func<dynamic, dynamic>){v})";
                // a Python int literal is a long at run time too (xs.index(2) on a late-bound list of longs: 2 is not 2L)
                else if (dynamicCall && !a.Star && (IsIntLiteral(a.Value) || a.Value is UnaryExpr nu && nu.Op == "-" && IsIntLiteral(nu.Operand))) v = EmitValue(a.Value);
                // a list for a .NET array parameter (WebApplication.CreateBuilder(sys.argv[1:])): its items as an array
                if (!a.Star && paramsHere != null && paramsHere.Count > 0 && paramsHere.Any(x => x.IsArray) && !paramsHere.Any(x => x.TakesFunction) &&
                    InferType(a.Value) is string lat && lat.StartsWith("List<"))
                    v = $"{Paren(v)}.ToArray()";
                if (partlyDynamic && !a.Star && !a.DoubleStar && InferType(a.Value) == "dynamic") v = $"(object?){Paren(v)}";
                // *xs into a params array (print(*xs), .NET params methods): spread into an array of the parameter's element type
                if (a.Star && !(InferType(a.Value) is string at && at.EndsWith("[]"))) v = $"[.. {EmitIterable(a.Value)}]";
                parts.Add(a.Name != null ? $"{Esc(a.Name)}: {v}" : v);
            }
            return string.Join(", ", parts);
        }

        static readonly HashSet<string> BuiltinTypeNames = new HashSet<string> { "str", "list", "dict", "set", "int", "float", "tuple", "frozenset", "bytes", "bytearray" };
        /// <summary>Class methods of built-in types (called on the type, not on a value).</summary>
        static readonly HashSet<string> BuiltinClassMethods = new HashSet<string> { "fromkeys", "maketrans", "fromhex", "from_bytes" };

        /// <summary>Overloaded single-argument builtins that are wrapped in a lambda when passed as a function value.</summary>
        static readonly HashSet<string> ValueBuiltins = new HashSet<string> { "str", "repr", "int", "float", "bool", "len", "abs", "ord", "chr", "hex", "bin", "oct", "round", "sorted", "sum", "min", "max", "list", "tuple", "set" };

        static readonly HashSet<string> SpecialBuiltins = new HashSet<string> { "isinstance", "cast", "type", "print", "object", "list", "dict", "set", "super" };

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

        string EmitCall(CallExpr c, bool targetTyped)
        {
            if (c.Func is NameExpr aliasName && AliasTarget(aliasName) is Expr aliasTarget)
                return EmitCall(new CallExpr { Func = aliasTarget, Args = c.Args, Line = c.Line, Col = c.Col }, targetTyped);
            if (CallableObjectCall(c) is CallExpr objectCall) return EmitCall(objectCall, targetTyped);
            if (Obs != null) RecordCall(CallTarget(c, out _), c.Args);
            RecordAdded(c);
            if (EmitFunctionValueCall(c) is string fnValueCall) return fnValueCall;
            if (Obs != null && c.Func is NameExpr lfn && IsLocal(lfn.Id) && _lambdaLocals.TryGetValue(lfn.Id, out var lcalled))
                Obs.AddLambdaCall(_scopeKey + "|λ" + lfn.Id, c.Args.Count == lcalled.Params.Count && c.Args.All(a => a.Name == null && !a.Star)
                    ? c.Args.Select(a => TypeInference.Settled(PyType(InferType(a.Value)) ?? TypeInference.Unknown)).ToArray() : null);
            if (c.Func is NameExpr gfn && gfn.Id == "globals" && c.Args.Count == 0 && !IsShadowed("globals")) return EmitGlobals();
            if (SpecialFormCall(c, targetTyped) is string special) return special;
            if (PythonCall(c, targetTyped) is string python) return python;
            // a call that is none of those: a Typhon function or class, a .NET method or constructor, typed by its arguments
            string func = c.Func is NameExpr cfn ? EmitName(cfn) : StaticThroughInstance(c.Func) ?? EmitExpr(c.Func);
            if (c.Func is LambdaExpr || c.Func is IfExpr) func = Paren(func);
            var typedAt = TypedParamPositions(c);
            if (typedAt == null && c.Func is AttributeExpr pm && RuntimeExtensionMethods.Contains(pm.Name) && IsPythonContainer(InferType(pm.Target)) &&
                !InferType(pm.Target).Contains("dynamic"))
                typedAt = i => true;
            // standard-library functions are generic: an empty [] takes the element type the other arguments give
            if (typedAt == null && StdlibModuleOf(c, out _) != null) typedAt = i => true;
            // a call with a late-bound argument or receiver is dispatched dynamically: lambdas need a delegate type there
            bool dynamicCall = c.Args.Any(a => InferType(a.Value) == "dynamic") || c.Func is AttributeExpr da && InferType(da.Target) == "dynamic";
            var typhonTarget = CallTarget(c, out _);
            bool typedFunctionArgs = typhonTarget != null && TypedCall(typhonTarget, c.Args);
            var overloads = DotNetOverloads(c);
            // which parameters take functions: the .NET overloads, or a builtin's (print(f), str(f) take the function object)
            var shapes = overloads ?? (c.Func is NameExpr bfn && !IsShadowed(bfn.Id) && c.Args.Any(x => x.Value is NameExpr)
                ? _r.GetMembers("Typhon.Runtime.Builtins", bfn.Id).Where(m => m.IsMethod && m.IsStatic).ToList() : null);
            return $"{func}({EmitArgs(c.Args, overloads: overloads, targetTypedAt: typedAt, dynamicCall: dynamicCall, typedKeywords: name => TypedKeyword(c, name), paramTypeAt: (i, name) => ParamTypeOf(c, i, name), typedFunctionArgs: typedFunctionArgs, functionShapes: shapes, typhonCallee: typhonTarget != null)})";
        }

        /// <summary>
        /// Python's special forms: open() in binary mode, sum of tuples, d.get/pop/setdefault with late-bound keys, re/struct/array
        /// calls typed by their literal arguments, frozenset, super().__init__ in two-phase construction, base calls, and
        /// conversions of late-bound values. Null when the call is none of them.
        /// </summary>
        string SpecialFormCall(CallExpr c, bool targetTyped)
        {
            // KeyError(key): Python's str() of a KeyError is the key's repr (KeyError: 'k')
            if (c.Func is NameExpr ke && ke.Id == "KeyError" && !IsShadowed("KeyError") && c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star)
                return $"new global::System.Collections.Generic.KeyNotFoundException({Rt("repr")}((object?){Paren(EmitExpr(c.Args[0].Value))}))";
            // open(path, "rb"): a binary file (reads and writes bytes)
            if (c.Func is NameExpr ofn && ofn.Id == "open" && !IsShadowed("open") && OpenMode(c)?.Contains('b') == true)
                return $"{Rt("open_binary")}({EmitExpr(c.Args[0].Value)}, {CsString(OpenMode(c))})";
            // sum(tuples, ()): concatenation of the tuples
            if (c.Func is NameExpr tsn && tsn.Id == "sum" && !IsShadowed("sum") && TupleSum(c) is string tsum && tsum.StartsWith(PyTuplePrefix))
                return $"{Rt("__tupsum")}<{GenericArg(tsum)}>({EmitIterable(c.Args[0].Value)})";
            // d.get(k, dflt) / d.pop(k) / d.setdefault(k, v) with a late-bound argument: converted to the dict's key/value types
            // (dynamic dispatch never finds the runtime's extension methods)
            if (c.Func is AttributeExpr dg && (dg.Name == "get" || dg.Name == "pop" || dg.Name == "setdefault") && DictTypes(InferType(dg.Target)) is List<string> dkv &&
                c.Args.Count >= 1 && c.Args.Count <= 2 && c.Args.All(x => x.Name == null && !x.Star) &&
                c.Args.Any(x => LateBoundArg(x.Value)) && !dkv.Any(x => x.Contains(TypeInference.Pending)))
            {
                string Slot(int i) { string t = i == 0 ? dkv[0] : dkv[1]; return t == "dynamic" ? "object" : t; }
                var dargs = c.Args.Select((x, i) => LateBoundArg(x.Value)
                    ? $"({Slot(i)}){Paren(EmitExpr(x.Value))}" : EmitExpr(x.Value, targetTyped: i == 1));
                return $"{Paren(EmitExpr(dg.Target))}.{dg.Name}({string.Join(", ", dargs)})";
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
                if (initOwner == null) return "";
                if (Obs != null && _p.TryFindClass(_m, initOwner.Name, out var ic, out var io)) RecordCall(InitTarget(io, ic), c.Args);
            }
            // base.m(...) cannot be dispatched dynamically: late-bound arguments go as object (binding to the dynamic overload)
            if (c.Func is AttributeExpr sm && sm.Target is CallExpr smc && smc.Func is NameExpr smn && smn.Id == "super" && smc.Args.Count == 0 &&
                c.Args.Any(x => InferType(x.Value) == "dynamic") && c.Args.All(x => x.Name == null && !x.Star && !x.DoubleStar))
                return $"base.{Esc(sm.Name)}({string.Join(", ", c.Args.Select(x => InferType(x.Value) == "dynamic" ? $"(object?){Paren(EmitValue(x.Value))}" : EmitValue(x.Value)))})";
            // big-int mode: int(x) parses / truncates to a BigInteger
            if (_big && c.Func is NameExpr bi && bi.Id == "int" && !IsShadowed("int") && c.Args.Count >= 1 && c.Args.All(x => x.Name == null || x.Name == "base"))
                return $"{Rt("__bigint")}({string.Join(", ", c.Args.Select(x => InferType(x.Value) == "dynamic" ? $"(object){Paren(EmitExpr(x.Value))}" : EmitExpr(x.Value)))})";
            // int(x) / float(x) / str(x) / len(x) of a late-bound value: passed as object, the call binds statically and keeps
            // its result type (the runtime converts by the value's run-time type) instead of becoming dynamic itself
            if (c.Func is NameExpr scv && StaticConversions.Contains(scv.Id) && !IsShadowed(scv.Id) && c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star &&
                (InferType(c.Args[0].Value) == "dynamic" || IsDynamicallyDispatched(c.Args[0].Value)))
                return $"{EmitName(scv)}((object){Paren(EmitExpr(c.Args[0].Value))})";
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
                        string v = EmitExpr(c.Args[0].Value);
                        string vt = InferType(c.Args[0].Value);
                        // big-int mode: a value of unknown type that is a Python int may be a BigInteger or a 64-bit count (len())
                        string Is(Expr t)
                        {
                            string mt = MapType(t, c);
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
                        return $"{Paren(EmitExpr(c.Args[0].Value))}.GetType()";
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
                        if (c.Args.Count == 0)
                        {
                            if (targetTyped) return "new()";
                            string what = fn.Id == "list" ? "List<dynamic>" : fn.Id == "dict" ? "OrderedDictionary<dynamic, dynamic>" : "HashSet<dynamic>";
                            ImplicitDynamic(c, $"the element type of '{fn.Id}()'");
                            return $"new {what}()";
                        }
                        break;
                    case "super":
                        if (c.Args.Count == 0) return "base";
                        break;
                }
            }
            return null;
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
                    var positional = new List<string>();
                    if (receiver != null && !isStatic) positional.Add($"(object?){Paren(receiver)}");
                    positional.AddRange(c.Args.Where(x => x.Name == null && !x.DoubleStar)
                        .Select(x => x.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(x.Value))})" : $"(object?){Paren(EmitLooseValue(x.Value))}"));
                    bool keywords = c.Args.Any(x => x.Name != null || x.DoubleStar);
                    return $"{Rt("__invoke_kw")}((object?){type}.__decorated_{a.Name}, [{string.Join(", ", positional)}], {(keywords ? KeywordArgs(c.Args) : "null")})";
                }
                if (k.Bases.Count == 0 || !ResolveBase(k.Bases[0], out var bpy, out _) || !TryTypeExpr(k.Bases[0] is SubscriptExpr bs ? bs.Target : k.Bases[0], out type)) return null;
                k = bpy;
            }
            return null;
        }

        /// <summary>
        /// obj.m(...) / self.m(...) where m is a @staticmethod or @classmethod of obj's Typhon class: C# calls static methods
        /// through the type (the receiver, a name or self, has no side effects to keep). Null for anything else.
        /// </summary>
        string StaticThroughInstance(Expr func)
        {
            if (!(func is AttributeExpr a) || !(a.Target is NameExpr tn)) return null;
            if (TryResolveType(tn.Id, 0, out _, out _) && !IsLocal(tn.Id)) return null; // already through the class
            string t = tn.Id == "self" && _cls != null && HasSelfInScope() ? _cls.CsSelfType : InferType(a.Target);
            ClassDef cd = _cls != null && t == _cls.CsSelfType ? _cls.Def : t != null && ClassKeyOfType(t, out _) is string key ? ClassDefOfKey(key) : null;
            for (var k = cd; k != null; k = k.Bases.Count > 0 && ResolveBase(k.Bases[0], out var bpy, out _) ? bpy : null)
            {
                var m = k.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                if (m == null) continue;
                return HasDecorator(m.Decorators, "staticmethod") || HasDecorator(m.Decorators, "classmethod") ? $"{t}.{Esc(a.Name)}" : null;
            }
            return null;
        }

        /// <summary>The keyword arguments of a late-bound call as a dict: name=value pairs and **mappings.</summary>
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
            if (!c.Args.Any(a => a.Name != null || a.DoubleStar)) return null;
            bool isType = TryTypeExpr(c.Func, out var typeCode);
            var overloads = isType ? DotNetConstructors(c.Func) : DotNetOverloads(c);
            var target = overloads?.FirstOrDefault(o => o.Parameters.Count > 0 && o.Parameters[o.Parameters.Count - 1].Name == "__kwargs");
            if (target == null) return null;
            var known = new HashSet<string>(target.Parameters.Select(p => p.Name));
            var direct = c.Args.Where(a => !a.DoubleStar && (a.Name == null || known.Contains(a.Name))).ToList();
            var extra = c.Args.Where(a => a.DoubleStar || a.Name != null && !known.Contains(a.Name)).ToList();
            if (extra.Count == 0) return null;
            string args = EmitArgs(direct, overloads: overloads);
            string callee = isType ? $"new {typeCode}" : c.Func is NameExpr fn ? EmitName(fn) : EmitExpr(c.Func);
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

        /// <summary>
        /// Python calls that need their own C#: fixed-size conversions, **kwargs, late-bound callables and receivers, containers
        /// started empty, methods called through a built-in type, str.format, generic calls, builtins consuming iterables, and
        /// Python methods of Python values. Null when the call is an ordinary one.
        /// </summary>
        string PythonCall(CallExpr c, bool targetTyped)
        {
            // int32(x), uint8(x), ...: explicit (checked) conversions to .NET's fixed-size numbers, for .NET APIs that need them
            if (c.Func is NameExpr cv && !IsShadowed(cv.Id) && NumericConversions.TryGetValue(cv.Id, out var convType) &&
                c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star && !c.Args[0].DoubleStar)
                return $"checked(({convType})({EmitExpr(c.Args[0].Value)}))";

            // a Typhon function or class with **kwargs: extra keyword arguments go into its kwargs dict
            if (TyphonCallee(c, out int kwImplicit) is FunctionDef kwCallee && kwCallee.Params.Any(x => x.Kind == ParamKind.KwArgs) &&
                KwargsCallArgs(kwCallee, kwImplicit, c.Args) is string kwArgs)
            {
                if (TryTypeExpr(c.Func, out var kwType)) return $"new {kwType}({kwArgs})";
                return $"{(c.Func is NameExpr kfn ? EmitName(kfn) : EmitExpr(c.Func))}({kwArgs})";
            }
            // super().m(...) in a class whose bases have no m (a mixin compiled on its own: the next class in an MRO it is
            // mixed into has it): AttributeError when it runs, as in Python
            if (c.Func is AttributeExpr nsa && nsa.Target is CallExpr nsc && nsc.Func is NameExpr nsn && nsn.Id == "super" && nsc.Args.Count == 0 &&
                nsa.Name != "__init__" && _cls != null && SuperLacks(nsa.Name))
                return $"{Rt("__no_super")}({CsString(nsa.Name)})";
            if (PartialCall(c) is string partialCall) return partialCall;
            if (AsyncioRunCall(c) is string asyncioRun) return asyncioRun;
            if (DataclassReplaceCall(c) is string replaceCall) return replaceCall;
            if (DecoratedMethodCall(c) is string decoratedCall) return decoratedCall;
            if (DotNetKwargsCall(c) is string kwargsCall) return kwargsCall;
            if (ExpandStarArgs(c) is string expanded) return expanded;
            // table[name](x, *rest) / cls(n, n) with cls a late-bound parameter: the callable is known only at run time (a
            // function value, a bound method, a delegate or a class), so the call is made there
            // (also f(*args, **kwargs) / f(x, key=v) on such a callable, and f(*args) on a value of a function type: a
            // decorator's wrapper forwarding its arguments to the function it wraps)
            bool keywords = c.Args.Any(a => a.Name != null || a.DoubleStar);
            ModuleInfo starOwner = null; FunctionDef starDef = null;
            bool fnTypeStar = c.Args.Any(a => a.Star || a.DoubleStar) && FunctionOfType(InferType(c.Func), out starOwner, out starDef);
            if (!(c.Func is AttributeExpr) && !(c.Func is LambdaExpr) &&
                (c.Args.Any(a => a.Star || a.DoubleStar) || c.Func is NameExpr || c.Func is SubscriptExpr || c.Func is CallExpr) &&
                (InferType(c.Func) == "dynamic" || c.Func is NameExpr fnl && IsLocal(fnl.Id) && InferType(c.Func) == null && !IsLocalFunctionName(fnl.Id) ||
                 c.Func is CallExpr && InferType(c.Func) == null && MaybeLateBound(c.Func) || fnTypeStar) &&
                (!keywords || !(c.Func is NameExpr kfn0 && TyphonCallee(c, out _) != null && !IsLocal(kfn0.Id))))
            {
                // the function's own (dynamic) version is what the call reaches
                if (fnTypeStar && Obs != null && TargetOf(starOwner, null, starDef.Name) is InferenceTarget st && st.InferParams)
                    Obs.Calls.Add(new Observations.Call { Callee = st.Key, Types = null, Context = _ctxFallbackKey });
                var spreadArgs = c.Args.Where(a => a.Name == null && !a.DoubleStar)
                    .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
                string positional = $"[{string.Join(", ", spreadArgs)}]";
                string callee = $"(object?){Paren(EmitExpr(c.Func))}";
                if (!keywords) return $"{Rt("__invoke")}({callee}, {positional})";
                return $"{Rt("__invoke_kw")}({callee}, {positional}, {KeywordArgs(c.Args)})";
            }
            // obj.m(x, *rest) on a late-bound obj: the method chosen at run time by the number of arguments
            if (c.Func is AttributeExpr sd && InferType(sd.Target) == "dynamic" && c.Args.Any(a => a.Star) && !c.Args.Any(a => a.Name != null || a.DoubleStar))
            {
                var spread = c.Args.Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitValue(a.Value))}");
                return $"new global::Typhon.Runtime.PyBoundMethod((object){Paren(EmitExpr(sd.Target))}, {CsString(sd.Name)}).__call__([{string.Join(", ", spread)}])";
            }
            if (TryTypeExpr(c.Func, out var typeCode))
                return $"new {typeCode}({EmitArgs(c.Args, overloads: DotNetConstructors(c.Func), targetTypedAt: TypedParamPositions(c), typedKeywords: name => TypedKeyword(c, name), paramTypeAt: (i, name) => ParamTypeOf(c, i, name), typhonCallee: TyphonCallee(c, out _) != null)})";

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

            // itertools.chain.from_iterable(xs): one function in Typhon's itertools
            if (c.Func is AttributeExpr cfi && cfi.Name == "from_iterable" && DottedName(cfi.Target) is string chainName && (chainName == "chain" || chainName.EndsWith(".chain")))
                return $"{(chainName.Contains('.') ? EmitExpr(((AttributeExpr)cfi.Target).Target) + "." : "")}chain_from_iterable({EmitArgs(c.Args)})";

            // methods called through a built-in type: dict.fromkeys(keys) / str.maketrans(a, b) are functions,
            // str.upper(s) / list.append(xs, x) are method calls on the first argument
            if (c.Func is AttributeExpr bt && bt.Target is NameExpr btn && BuiltinTypeNames.Contains(btn.Id) && !IsShadowed(btn.Id))
            {
                if (BuiltinClassMethods.Contains(bt.Name)) return $"{Rt(bt.Name)}({EmitArgs(c.Args)})";
                if (c.Args.Count >= 1 && c.Args[0].Name == null && !c.Args[0].Star)
                {
                    var rest = c.Args.Skip(1).ToList();
                    string recv = EmitExpr(c.Args[0].Value);
                    if (c.Args[0].Value is ConstExpr || c.Args[0].Value is BinOpExpr || c.Args[0].Value is UnaryExpr) recv = Paren(recv);
                    return $"{recv}.{Esc(bt.Name)}({EmitArgs(rest)})";
                }
            }

            // "...".format(a, b): routed to a helper so it does not clash with the builtin format(value, spec)
            if (c.Func is AttributeExpr fa && fa.Name == "format" && (fa.Target is ConstExpr fc && fc.Kind == ConstKind.Str || InferType(fa.Target) == "string"))
                return $"{Rt("str_format")}({EmitExpr(fa.Target)}{(c.Args.Count > 0 ? ", " : "")}{EmitArgs(c.Args)})";

            // generic method call with explicit type arguments: f[int](x)  →  f<int>(x)
            if (c.Func is SubscriptExpr gs && !(gs.Index is SliceExpr))
            {
                var targs = gs.Index is TupleExpr t ? t.Elts : new List<Expr> { gs.Index };
                if (targs.All(IsTypeish) && !(gs.Target is NameExpr gn && IsLocal(gn.Id)))
                    return $"{EmitExpr(gs.Target)}<{string.Join(", ", targs.Select(x => MapType(x, c)))}>({EmitArgs(c.Args)})";
            }

            // builtins that consume iterables: a dict argument must contribute its keys (sorted(d), list(d), ", ".join(d))
            if (c.Func is NameExpr ifn && !IsShadowed(ifn.Id) && IterableArgBuiltins.TryGetValue(ifn.Id, out var spec))
            {
                int positional = c.Args.Count(a => a.Name == null && !a.Star && !a.DoubleStar);
                if (!(spec.OnlyWhenSingle && positional != 1))
                    return $"{EmitName(ifn)}({EmitArgs(c.Args, i => i >= spec.First && (spec.All || i == spec.First))})";
            }
            if (c.Func is AttributeExpr ja && ja.Name == "join" && c.Args.Count == 1 && c.Args[0].Name == null &&
                (ja.Target is ConstExpr jc && jc.Kind == ConstKind.Str || InferType(ja.Target) == "string"))
                return $"{Paren(EmitExpr(ja.Target))}.join({EmitIterable(c.Args[0].Value)})";

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
                em.Name != "get" && em.Name != "pop" && !c.Args.Any(a => a.Star || a.DoubleStar || a.Name != null) &&
                (c.Args.Any(a => !(a.Value is ConstExpr) && !IsEmptyLiteral(a.Value)) || IsBuiltinSubclass(InferType(em.Target))) &&
                _r.GetMembers("Typhon.Runtime.Builtins", em.Name).Count > 0)
            {
                string recv = EmitExpr(em.Target);
                var rest = EmitArgs(c.Args, targetTypedAt: i => true);
                return $"global::Typhon.Runtime.Builtins.{Esc(em.Name)}({recv}{(rest.Length > 0 ? ", " + rest : "")})";
            }
            // m.items() / keys() / values() / get(k, d) of a Mapping[K, V] (IReadOnlyDictionary)
            if (c.Func is AttributeExpr ro && InferType(ro.Target) is string rot && rot.StartsWith("IReadOnlyDictionary<") &&
                (ro.Name == "items" || ro.Name == "keys" || ro.Name == "values" || ro.Name == "get" && c.Args.Count == 2))
                return $"{Rt("__ro_" + ro.Name)}({EmitExpr(ro.Target)}{(c.Args.Count > 0 ? ", " + EmitArgs(c.Args) : "")})";

            // d.strip(), d.split(","), d.items() on a late-bound value: dynamic dispatch never finds extension methods, so a
            // Python value (str, list, dict, set, number) goes to the runtime's static method; anything else (a Typhon
            // object, a .NET object) keeps its own method
            if (c.Func is AttributeExpr dm && InferType(dm.Target) == "dynamic" && !c.Args.Any(a => a.Star || a.DoubleStar) && IsRuntimeMethod(dm.Name, c.Args))
            {
                string r = NewTmp("r");
                string rest = EmitArgs(c.Args, dynamicCall: true);
                string sep = rest.Length > 0 ? ", " : "";
                return $"({Paren(EmitExpr(dm.Target))} is var {r} && {Rt("__is_pyvalue")}({r}) ? global::Typhon.Runtime.Builtins.{Esc(dm.Name)}({r}{sep}{rest}) : {r}.{Esc(dm.Name)}({rest}))";
            }
            return null;
        }

        static readonly HashSet<string> StaticConversions = new HashSet<string> { "int", "float", "str", "repr", "len", "bytes", "bytearray" };

        /// <summary>Is the name a Python method the runtime implements as a static (extension) method (strip, split, items, ...)?</summary>
        bool IsRuntimeMethod(string name)
        {
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
