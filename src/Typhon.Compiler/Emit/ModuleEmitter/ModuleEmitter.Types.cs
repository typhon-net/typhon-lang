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
    // Types of expressions (InferType): what Typhon knows about a value while it emits the code that computes it.
    internal sealed partial class ModuleEmitter
    {
        static string GenericArg(string t)
        {
            int lt = t.IndexOf('<');
            return t.Substring(lt + 1, t.Length - lt - 2);
        }


        const string TaskTypeName = "global::System.Threading.Tasks.Task";

        string AnnotatedCallResult(FunctionDef function)
        {
            string type = MapType(function.Returns, function, isReturn: true);
            return function.IsAsync ? type == "void" ? TaskTypeName : $"{TaskTypeName}<{type}>" : type;
        }

        readonly Dictionary<string, string> _funcReturnTypes = new Dictionary<string, string>();
        readonly Dictionary<string, string> _moduleFieldTypes = new Dictionary<string, string>();

        /// <summary>Element type of iterating a value of C# type <paramref name="t"/> (dict: keys, str: char).</summary>
        static bool IsValueTypeName(string t) => t == "long" || t == "double" || t == "bool" || t == "char" || t == "decimal" || t == "float" ||
            t == TypeInference.BigInt || IsSmallInt(t) || t.StartsWith("(") || t.EndsWith("?");

        /// <summary>The type of the items a for loop or comprehension gets from an iterable of this type (late-bound for a late-bound iterable).</summary>
        static string IterElementType(string t) => t == "dynamic" ? "dynamic" : ElementTypeOf(t);

        static string ElementTypeOf(string t)
        {
            if (t == null || t == TypeInference.Pending) return t;
            if (t == "global::Typhon.Runtime.PyRange") return "long";
            if (t == BigRangeType) return TypeInference.BigInt;
            if (t == "global::Typhon.Runtime.PyBytes" || t == "global::Typhon.Runtime.PyByteArray") return "long";
            if (t.StartsWith("(") && t.EndsWith(")") && TupleElementTypes(t) is List<string> items) return items.Distinct().Count() == 1 ? items[0] : "object";
            if (t == "string") return "string"; // a str's elements are strs (one character each)
            if (t.EndsWith("[]")) return t.Substring(0, t.Length - 2);
            foreach (var g in new[] { "List<", "IList<", "IReadOnlyList<", "IEnumerable<", "HashSet<", "ISet<", "IReadOnlySet<", "IReadOnlyCollection<", "global::Typhon.Runtime.PyIterator<", "global::Typhon.Runtime.PyTuple<", "global::Typhon.Runtime.PyGenerator<", "global::Typhon.Runtime.PyAsyncGenerator<", "IAsyncEnumerable<", "global::System.Collections.Generic.IAsyncEnumerable<" })
                if (t.StartsWith(g) && t.EndsWith(">")) return GenericArg(t);
            if (DictTypes(t) is List<string> kv0) return kv0[0];
            if (t.StartsWith("global::Typhon.Runtime.deque<") && t.EndsWith(">")) return GenericArg(t);
            // the rows of csv.reader / csv.DictReader
            if (t == "global::Typhon.Runtime.CsvReader") return "List<string>";
            if (t == "global::Typhon.Runtime.CsvDictReader") return "OrderedDictionary<string, string>";
            return null;
        }

        /// <summary>Splits "K, List&lt;V&gt;" at top-level commas.</summary>
        static List<string> SplitTypeArgs(string inner)
        {
            var result = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char ch = inner[i];
                if (ch == '<' || ch == '(') depth++;
                else if (ch == '>' || ch == ')') depth--;
                else if (ch == ',' && depth == 0) { result.Add(inner.Substring(start, i - start).Trim()); start = i + 1; }
            }
            result.Add(inner.Substring(start).Trim());
            return result;
        }

        bool SelfInScope()
        {
            for (var sc = _scope; sc != null; sc = sc.Parent) if (sc.HasSelf) return true;
            return false;
        }

        bool IsRuntimeModuleAttribute(AttributeExpr member) =>
            (member.Target is not NameExpr alias || RuntimeModuleValue(alias) == null) &&
            InferType(member.Target) is "global::Typhon.Runtime.PyModule" or "Typhon.Runtime.PyModule" &&
            !_r.HasMember("Typhon.Runtime.PyModule", member.Name);

        bool IsRuntimeObjectAttribute(AttributeExpr member) =>
            !_m.IsGdScript && (InferType(member.Target) is "object" or "object?" ||
                IsTypeParamType(InferType(member.Target))) && !_r.HasMember("System.Object", member.Name);

        string InferType(Expr e)
        {
            switch (e)
            {
                case ConstExpr c:
                    switch (c.Kind)
                    {
                        case ConstKind.Int: { if (_big) return IntT; var code = EmitConst(c); return long.TryParse(code, out _) ? "long" : code.EndsWith("UL") ? "ulong" : null; }
                        case ConstKind.Float: return "double";
                        case ConstKind.Imaginary: return ComplexType;
                        case ConstKind.Str: return "string";
                        case ConstKind.Bytes: return PyBytesType;
                        case ConstKind.Bool: return "bool";
                        default: return null;
                    }
                case FStringExpr tf when tf.IsTemplate: return TemplateType;
                case FStringExpr _: return "string";
                case LambdaExpr l when l.RequiresArgumentBinding || IsGeneratorLambda(l): return "dynamic";
                case YieldExpr _ when _scope?.AsyncForm == true || _lambdaGenerators.Count > 0: return "dynamic";
                case NamedExpr ne: return InferType(ne.Target) ?? InferType(ne.Value);
                case ComprehensionExpr comp: return ComprehensionType(comp);
                case NameExpr { Id: "__debug__" }: return "bool";
                case NameExpr n when n.Id == "__file__" && !IsShadowed(n.Id): return "string";
                case NameExpr late when LateClassSlot(late.Id) != null: return "dynamic";
                case NameExpr ian when ImportAlias(ian) is Expr importTarget: return InferType(importTarget);
                case NameExpr module when !Receivers.Contains(module) && RuntimeModuleValue(module) != null:
                    return "global::Typhon.Runtime.PyModule";
                case NameExpr n:
                {
                    var t = KnownType(n.Id);
                    if (t != null) return t;
                    // self as a value (node = self, [self], f(self)) in an instance method or a function nested in one
                    if (n.Id == "self" && _cls != null && SelfInScope()) return _cls.CsSelfType;
                    if (FunctionRef(n, out var fnOwner, out var fnDef)) return FunctionValueType(fnOwner, fnDef.Name);
                    if (_classFieldInitializer && _cls?.FieldTypes.TryGetValue(n.Id, out string priorClassField) == true)
                        return priorClassField;
                    if (!IsLocal(n.Id) && _moduleFieldTypes.TryGetValue(n.Id, out var mt)) return mt;
                    if (!IsLocal(n.Id) && _runtimeProjectBindings.TryGetValue(n.Id, out var imported))
                        return ConditionalModuleClass(imported.Owner, imported.Name) ? "global::System.Type" : "dynamic";
                    if (!IsLocal(n.Id) && ConditionalModuleClass(_m, n.Id)) return "global::System.Type";
                    return null;
                }
                case AttributeExpr component when UsesComplexComponent(component): return "double";
                case AttributeExpr comparator when UsesBuiltinStringComparison(comparator): return "dynamic";
                case AttributeExpr typeCheck when !_m.IsGdScript && IsTypeCheckSlot(typeCheck.Name) &&
                    BuiltinTypeBase(typeCheck.Target): return "dynamic";
                case AttributeExpr { Name: "__class__" }: return "global::System.Type";
                case AttributeExpr moduleAttribute when IsRuntimeModuleAttribute(moduleAttribute): return "dynamic";
                case AttributeExpr objectAttribute when IsRuntimeObjectAttribute(objectAttribute): return "dynamic";
                case AttributeExpr builtin when ImportedBuiltinTypeValue(builtin) != null: return "global::System.Type";
                case AttributeExpr trace when IsTypedExceptionTraceback(trace): return ExceptionTracebackType;
                case AttributeExpr reflected when TypeObjectMember(reflected):
                    return "dynamic";
                case AttributeExpr tupleField when TupleFieldOwner(tupleField) != null:
                    return "global::Typhon.Runtime.PyTupleGetter";
                case AttributeExpr shadowed when IsInstanceClassDefault(shadowed) || IsNarrowedInstanceAttribute(shadowed): return "dynamic";
                case AttributeExpr sm when InstanceMethodMayBeShadowed(sm): return "dynamic";
                case AttributeExpr at when at.Target is NameExpr self && self.Id == "self" && _cls != null:
                    return _cls.FieldTypes.TryGetValue(at.Name, out var ft) ? ft : InheritedFieldType(_cls.Def, at.Name) ??
                           (_m.IsGdScript ? GdSelfMemberType(at.Name) : null) ??
                           (!_cls.IsInterface && HasSelfInScope() && SelfMemberUnknown(at.Name) ? "dynamic" : null); // (a mixin's: late-bound)
                case AttributeExpr mp when mp.Name.StartsWith(LocalsAnalyzer.MatchPositionPrefix) && mp.Target is CallExpr mc0:
                {
                    var names = MatchArgs(mc0.Func);
                    int pos = int.Parse(mp.Name.Substring(LocalsAnalyzer.MatchPositionPrefix.Length));
                    return names != null && pos < names.Count ? InferType(new AttributeExpr { Target = mp.Target, Name = names[pos] }) : null;
                }
                case AttributeExpr dict0 when dict0.Name == "__dict__": return "dynamic";
                case AttributeExpr exported when DottedName(exported.Target) is string moduleName && ModuleRef(moduleName) is ModuleInfo exportedModule &&
                    UsesRuntimeProjectMember(exportedModule, exported.Name):
                    return ConditionalModuleClass(exportedModule, exported.Name) ? "global::System.Type" : "dynamic";
                case AttributeExpr oa:
                {
                    // Cls.ATTR / self.__class__.ATTR / type(self).ATTR: a class variable's type
                    if (oa.Target is NameExpr ocn && !IsLocal(ocn.Id) && ocn.Id != "self" && TryResolveType(ocn.Id, 0, out _, out var ocls) && ocls != null &&
                        MemberType(_cls != null && _cls.Def == ocls ? _cls.CsSelfType : ocn.Id, oa.Name, method: false) is string staticType)
                        return staticType;
                    if (_cls != null && (oa.Target is AttributeExpr oca && oca.Name == "__class__" && oca.Target is NameExpr ocs && ocs.Id == "self" ||
                                         oa.Target is CallExpr otc && otc.Func is NameExpr otn && otn.Id == "type" && otc.Args.Count == 1 && otc.Args[0].Value is NameExpr ots && ots.Id == "self") &&
                        MemberType(_cls.CsSelfType, oa.Name, method: false) is string selfStatic)
                        return selfStatic;
                    var ot = InferType(oa.Target);
                    if (ot == TypeInference.Pending) return TypeInference.Pending;
                    if (ot == "dynamic") return "dynamic"; // a member of a late-bound value is late-bound
                    if (ot == null) return MathConstant(oa) ?? DotNetResultType(oa.Target, oa.Name, null);
                    return MemberType(ot, oa.Name, method: false) ?? DotNetResultType(oa.Target, oa.Name, null);
                }
                case SubscriptExpr ix when !(ix.Index is SliceExpr):
                {
                    if (BuiltinGenericOrigin(ix.Target) != null || SourceValueSubscription(ix.Target)) return "dynamic";
                    if (InferType(ix.Index) == "global::Typhon.Runtime.PySlice") return "dynamic";
                    var t = InferType(ix.Target);
                    if (UsesDictMissingLookup(t)) return "dynamic";
                    if (t == BigRangeType) return TypeInference.BigInt;
                    if (IsBytesType(t)) return "long"; // b[i] is an int
                    if (NamedTupleOf(t) is ClassDef ntd && ConstIntValue(ix.Index) is long ntk && ntk >= -ntd.NamedTupleFields.Count && ntk < ntd.NamedTupleFields.Count)
                        return MemberType(t, ntd.NamedTupleFields[(int)(ntk < 0 ? ntk + ntd.NamedTupleFields.Count : ntk)], method: false);
                    // (an unknown container with a late-bound key is read through __getitem: a late-bound value)
                    if (t == null && !(ix.Index is TupleExpr) && InferType(ix.Index) == "dynamic") return "dynamic";
                    if (t is "object" or "object?" or "global::System.Type" or "System.Type") return "dynamic";
                    if (t == null || t == TypeInference.Pending || t == "dynamic") return t;
                    if (t == "string") return "string";
                    // a class deriving from list/dict: its items; a class with __getitem__: what that returns
                    if (BuiltinBaseType(t) is string ixBase) t = ixBase;
                    else if (ClassKeyOfType(t, out _) is string gk && ClassDefOfKey(gk) is ClassDef gcd && DefinesInHierarchy(gcd, "__getitem__"))
                        return MemberType(t, "__getitem__", method: true) ?? (_dry ? TypeInference.Pending : "dynamic");
                    var tupleItems = TupleElementTypes(t);
                    if (tupleItems != null)
                    {
                        if (ConstIntValue(ix.Index) is long k && k >= -tupleItems.Count && k < tupleItems.Count) return tupleItems[(int)(k < 0 ? k + tupleItems.Count : k)];
                        return tupleItems.Distinct().Count() == 1 ? tupleItems[0] : null;
                    }
                    if (t.EndsWith("[]")) return t.Substring(0, t.Length - 2);
                    if ((t.StartsWith("List<") || t.StartsWith("IList<") || t.StartsWith("IReadOnlyList<")) && t.EndsWith(">")) return GenericArg(t);
                    if (DictTypes(t) is List<string> dkv) return dkv[1];
                    if (t.StartsWith("global::Typhon.Runtime.deque<") && t.EndsWith(">")) return GenericArg(t);
                    return null;
                }
                case AwaitExpr aw:
                {
                    var t = InferType(aw.Value);
                    if (t == "global::Typhon.Runtime.PyAsyncNext") return "dynamic";
                    return t != null && (t.StartsWith("Task<") || t.StartsWith(TaskTypeName + "<")) &&
                        t.EndsWith(">") ? GenericArg(t) : null;
                }
                case SubscriptExpr sub when sub.Index is SliceExpr:
                {
                    var t = InferType(sub.Target);
                    if (t is "global::Typhon.Runtime.PyRange" or BigRangeType) return t;
                    if (IsBytesType(t)) return t;
                    if (t != null && t.StartsWith(PyTuplePrefix)) return t;
                    return t != null && (t == "string" || t.StartsWith("List<") || t.EndsWith("[]") || t == "dynamic") ? t : null;
                }
                case CallExpr { Func: AttributeExpr sm } when InstanceMethodMayBeShadowed(sm) || IsInstanceClassDefault(sm): return "dynamic";
                case CallExpr { Func: NameExpr { Id: "frozendict" } frozen } keywordCall when !_m.IsGdScript &&
                    !IsShadowed(frozen.Id) && keywordCall.Args.Any(argument => argument.Name != null || argument.DoubleStar):
                    return BuiltinTypeValues[frozen.Id];
                case CallExpr call: return CallType(call);
                case BinOpExpr pf when pf.Op == "%" && (pf.Left is ConstExpr pfc && pfc.Kind == ConstKind.Str || InferType(pf.Left) == "string"): return "string";
                case BinOpExpr lc when lc.Op == "+" && ListConcatMismatch(lc): return "List<dynamic>";
                case BinOpExpr union when !_m.IsGdScript && IsUnionOperation(union): return "dynamic";
                case BinOpExpr b: return BinOpType(b);
                case UnaryExpr u: return u.Op == "not" ? "bool" : InferType(u.Operand) == "bool" ? "long" : InferType(u.Operand);
                case CompareExpr ce:
                {
                    // a comparison is a bool when Python's rules or numbers decide it; other types answer with their own
                    // operator's result (an ndarray compares element-wise and returns an array)
                    Expr left = ce.Left;
                    for (int i = 0; i < ce.Ops.Count; i++)
                    {
                        string op = ce.Ops[i];
                        if (op == "<" || op == "<=" || op == ">" || op == ">=")
                        {
                            string lt = InferType(left), rt = InferType(ce.Comparators[i]);
                            bool decided = (IsNumeric(lt) || lt == "bool" || lt == "char") && (IsNumeric(rt) || rt == "bool" || rt == "char") ||
                                           PythonOrdered(lt) || PythonOrdered(rt);
                            if (!decided) return IsUnknownType(lt) && IsUnknownType(rt) ? "dynamic" : null;
                        }
                        left = ce.Comparators[i];
                    }
                    return "bool";
                }
                case BoolOpExpr b:
                {
                    if (b.Values.All(IsBoolish)) return "bool";
                    var ts = b.Values.Select(InferType).ToList();
                    if (ts.Contains(TypeInference.Pending)) return ts.Where(t => t != TypeInference.Pending).Distinct().Count() == 1 ? ts.First(t => t != TypeInference.Pending) : TypeInference.Pending;
                    if (ts.All(t => t != null && t == ts[0])) return ts[0];
                    // x or None / None or x: the value's type (a reference type holds None as null); x or {}: x's type
                    var known = b.Values.Where(v => !IsNone(v) && !IsEmptyLiteral(v)).Select(InferType).Distinct().ToList();
                    if (known.Count == 1 && known[0] != null && b.Values.Any(IsEmptyLiteral) && !b.Values.Any(IsNone)) return known[0];
                    return known.Count == 1 && known[0] != null && !IsValueTypeName(known[0]) ? known[0] : ts.Contains("dynamic") ? "dynamic" : null;
                }
                case IfExpr i:
                {
                    var a = InferType(i.Body); var o = InferType(i.OrElse);
                    if (a == TypeInference.Pending) return o;
                    if (o == TypeInference.Pending) return a;
                    if (a != null && a == o) return a;
                    if (a == "dynamic" || o == "dynamic") return "dynamic";
                    // None or a list: the list type (a reference can be None); an int or a float: a float
                    if (IsNone(i.Body) && o != null && !IsValueTypeName(o)) return o;
                    if (IsNone(i.OrElse) && a != null && !IsValueTypeName(a)) return a;
                    if (MixedWideIntegerAndReal(a, o)) return "dynamic";
                    if (IsNumeric(a) && IsNumeric(o) && (a == "double" || o == "double")) return "double";
                    // different types: late-bound (both branches are emitted as dynamic)
                    return ConditionalNeedsDynamic(i) ? "dynamic" : null;
                }
                case ListExpr l when l.Elts.Any(x => x is StarredExpr):
                {
                    var et = SpreadElementType(l.Elts);
                    return et == null || et == TypeInference.Pending ? et : $"List<{et}>";
                }
                case TupleExpr te when te.Elts.Any(x => x is StarredExpr):
                {
                    var et = SpreadElementType(te.Elts);
                    return et == null || et == TypeInference.Pending ? et : $"global::Typhon.Runtime.PyTuple<{et}>";
                }
                case SetExpr se when se.Elts.Any(x => x is StarredExpr):
                {
                    var et = SpreadElementType(se.Elts);
                    return et == null || et == TypeInference.Pending ? et : $"HashSet<{et}>";
                }
                case ListExpr l:
                {
                    if (l.Elts.Count == 0) return null;
                    if (TypeAmidEmpties(l.Elts) is string withEmpties) return $"List<{withEmpties}>";
                    if (TupleSequenceAmid(l.Elts) is string tupleSeq) return $"List<{tupleSeq}>";
                    var ts = l.Elts.Select(InferType).Distinct().ToList();
                    if (ts.Count == 1 && ts[0] != null) return $"List<{ts[0]}>";
                    if (ts.All(t => IsIntegral(t) || t == "double")) return ts.Contains("double") ? "List<double>" : "List<long>";
                    return null;
                }
                case SetExpr se:
                {
                    var ts = se.Elts.Select(x => PyType(InferType(x))).Distinct().ToList();
                    if (ts.Contains(TypeInference.Pending)) return TypeInference.Pending;
                    if (ts.Count == 1 && ts[0] != null) return $"HashSet<{ts[0]}>";
                    if (ts.All(t => IsIntegral(t) || t == "double")) return ts.Contains("double") ? "HashSet<double>" : "HashSet<long>";
                    return null;
                }
                case DictExpr d:
                {
                    if (d.Keys.Count == 0 || d.Keys.Any(k => k == null)) return null;
                    var ks = d.Keys.Select(key => IsNone(key) ? "dynamic" : InferType(key)).Distinct().ToList();
                    if (d.Values.Any(x => ClassBodyMethod(x) != null))
                        return $"OrderedDictionary<{(ks.Count == 1 ? PyType(ks[0]) ?? "dynamic" : "dynamic")}, dynamic>";
                    var vs = d.Values.Select(InferType).Distinct().ToList();
                    if (ks.Count == 1 && ks[0] != null && vs.Count == 1 && vs[0] != null) return $"OrderedDictionary<{ks[0]}, {vs[0]}>";
                    if (ks.Count == 1 && ks[0] != null && d.Values.All(IsNone)) return $"OrderedDictionary<{PyType(ks[0])}, dynamic>";
                    if (ks.Count == 1 && ks[0] != null && TypeAmidEmpties(d.Values) is string vWithEmpties) return $"OrderedDictionary<{ks[0]}, {vWithEmpties}>";
                    if (ks.Count == 1 && ks[0] != null && TupleSequenceAmid(d.Values) is string vTupleSeq) return $"OrderedDictionary<{ks[0]}, {vTupleSeq}>";
                    if (d.Keys.Count > 1 && (MixedTypes(d.Keys) || MixedTypes(d.Values)))
                    {
                        string key = MixedTypes(d.Keys) ? "dynamic" : PyType(ks[0]);
                        string value = MixedTypes(d.Values) ? "dynamic" : PyType(vs[0]);
                        if (key != null && value != null && key != TypeInference.Pending && value != TypeInference.Pending)
                            return $"OrderedDictionary<{key}, {value}>";
                    }
                    return null;
                }
                case TupleExpr t:
                {
                    if (_identityTuples.Contains(t))
                    {
                        var items = t.Elts.Select(e => PyType(InferType(e))).Distinct().ToList();
                        string itemType = items.Count == 1 && !UnsureType(items[0]) ? items[0] : "dynamic";
                        return PyTuplePrefix + itemType + ">";
                    }
                    if (t.Elts.Count == 0) return "ValueTuple";
                    string ItemType(Expr value) => AssignedValueType(value) ??
                        (BoundMethodValue(value) != null ? "dynamic" : null);
                    if (t.Elts.Count == 1)
                    {
                        Expr element = t.Elts[0];
                        string one = IsNone(element) ? "object?" : PyType(ItemType(element));
                        return one == null || one == TypeInference.Pending ? one : $"ValueTuple<{one}>";
                    }
                    // a long tuple of one item type is a data sequence (a table of numbers), not a record: PyTuple<T>
                    if (t.Elts.Count >= 8 && !t.Elts.Any(x => x is StarredExpr))
                    {
                        var its = t.Elts.Select(x => PyType(InferType(x))).Distinct().ToList();
                        if (its.Count == 1 && its[0] != null && its[0] != TypeInference.Pending) return PyTuplePrefix + its[0] + ">";
                        if (its.All(x => x == "long" || x == "double")) return PyTuplePrefix + "double>";
                    }
                    var ts = t.Elts.Select(e => ItemType(e) ?? (e is ListExpr { Elts.Count: 0 } ? "List<dynamic>" : null)).ToList();
                    return ts.All(x => x != null) ? "(" + string.Join(", ", ts) + ")" : null;
                }
                default: return null;
            }
        }

        /// <summary>The type of a call: a Typhon function, a class, a builtin, a method of a known type, a standard-library function.</summary>
        string CallType(CallExpr call)
        {
            if (!_m.IsGdScript && (BuiltinTypeBase(call.Func) && call.Args.Count == 3 || IsMetaclassExpression(call.Func)))
                return "dynamic";
            if (CustomAllocatorTarget(call.Func, out _) ||
                call.Func is AttributeExpr allocator && (BaseObjectMethod(allocator) == "__object_new" ||
                    allocator.Name == "__new__" && CustomAllocatorTarget(allocator.Target, out _)))
                return "dynamic";
            if (SuperListAppend(call) != null || ConditionalClassReference(call.Func)) return "dynamic";
            if (call.Func is NameExpr aliasName && AliasTarget(aliasName) is Expr aliasTarget)
                return InferType(new CallExpr { Func = aliasTarget, Args = call.Args, Line = call.Line, Col = call.Col });
            if (CallableObjectCall(call) is CallExpr objectCall) return InferType(objectCall);
            if (FunctionValueCallType(call) is string fnCallType) return fnCallType;
            // an object whose attributes exist only at run time (argparse.Namespace(a=1)): late-bound, as its annotation is
            if (DottedName(call.Func) is string lbt && !IsShadowed(lbt.Split('.')[0]) && IsLateBoundDotNetType(lbt)) return "dynamic";
            if (IsDataclassReplace(call)) { string rt0 = InferType(call.Args[0].Value); return rt0 == TypeInference.Pending ? rt0 : rt0 ?? "dynamic"; }
            if (call.Func is NameExpr gtn && gtn.Id == "globals" && call.Args.Count == 0 && !IsShadowed("globals"))
                return _m.RuntimeCodeMode != null || _lateModuleFields.Count != 0 ? "dynamic" : "OrderedDictionary<string, object?>";
            if (_m.RuntimeCodeMode != null && AtRuntimeModuleScope && call.Func is NameExpr { Id: "locals" } &&
                call.Args.Count == 0 && !IsShadowed("locals")) return "dynamic";
            if (call.Func is NameExpr dfn && IsLocal(dfn.Id) && KnownType(dfn.Id) is string dft && dft.StartsWith("global::System.Func<") && dft.EndsWith(">"))
            {
                string last = SplitTypeArgs(GenericArg(dft)).Last();
                return last == "dynamic" ? "dynamic" : last;
            }
            // f(...) through a late-bound value (a local or module variable holding a bound method, a callable object)
            if (call.Func is NameExpr dvn && (IsLocal(dvn.Id) ? KnownType(dvn.Id) : _moduleFieldTypes.TryGetValue(dvn.Id, out var dvt) ? dvt : null) == "dynamic")
                return "dynamic";
            if (FindallCallForm(call) is string findallForm) return FindallType(findallForm);
            if (ArrayCallForm(call, out var arrElem) != null) return $"List<{arrElem}>";
            if (StructUnpackTypes(call) is List<string> structTypes) return structTypes.Count == 1 ? $"ValueTuple<{structTypes[0]}>" : "(" + string.Join(", ", structTypes) + ")";
            // frozenset(xs): statically a set of xs's elements (the value is a frozenset, which hashes by contents)
            if (call.Func is NameExpr fzn && fzn.Id == "frozenset" && !IsShadowed("frozenset") && call.Args.Count <= 1)
            {
                if (call.Args.Count == 0) return "HashSet<object>";
                // The runtime constructor preserves the iterable's actual CLR element type.
                string fe = ElementTypeOf(InferType(call.Args[0].Value));
                return fe == null || fe == TypeInference.Pending ? fe : $"HashSet<{fe}>";
            }
            if (InferredCallType(call) is string inferredCall) return inferredCall;
            // a module function Typhon does not infer (it has *args / **kwargs) and that returns values: dynamic to C#
            if (call.Func is NameExpr umf && !IsLocal(umf.Id) && _m.Functions.Contains(umf.Id) && CallTarget(call, out _) == null &&
                _m.Ast.Body.OfType<FunctionDef>().FirstOrDefault(fd => fd.Name == umf.Id) is FunctionDef ufd && ufd.Returns == null && !ufd.IsAsync &&
                ufd.TypeParams.Count == 0 && !AstWalker.ContainsOutsideNestedScopes(ufd.Body, n => n is YieldExpr) &&
                AstWalker.ContainsOutsideNestedScopes(ufd.Body, n => n is ReturnStmt ur && ur.Value != null && !IsNone(ur.Value)))
                return "dynamic";
            if (call.Func is NameExpr known && _funcReturnTypes.TryGetValue(known.Id, out var rt) && (IsShadowed(known.Id) || IsLocal(known.Id)))
                return rt == "void" ? null : rt;
            if (call.Func is AttributeExpr mc && !(mc.Target is NameExpr mcs && mcs.Id == "self"))
            {
                if (mc is { Name: "fromhex", Target: NameExpr { Id: "float" } } && !IsShadowed("float"))
                    return "double";
                if (MathCall(mc) is string mathType) return mathType;
                var ot = InferType(mc.Target);
                if (ot == TypeInference.Pending) return TypeInference.Pending;
                if (ot == "dynamic" || TypeObjectMember(mc) || IsRuntimeObjectAttribute(mc)) return "dynamic";
                if (ot != null && MemberType(ot, mc.Name, method: true) is string objRet) return objRet;
                if (DotNetResultType(mc.Target, mc.Name, call) is string netRet) return netRet;
                if (PythonMethodType(ot, mc.Name, call) is string pyRet) return pyRet;
                if (call.Args.Count == 0 && (mc.Name == "items" || mc.Name == "keys" || mc.Name == "values") && DictTypes(ot) is List<string> kv)
                    return mc.Name == "items" ? $"IEnumerable<({kv[0]}, {kv[1]})>" : $"IEnumerable<{(mc.Name == "keys" ? kv[0] : kv[1])}>";
            }
            if (call.Func is AttributeExpr ma && ma.Target is NameExpr ms && ms.Id == "self" && _cls != null && _cls.MethodReturnTypes.TryGetValue(ma.Name, out var mrt))
                return mrt == "void" ? null : mrt;
            if (_m.IsGdScript && GdCallType(call) is string gdRet) return gdRet;
            if (BinaryPowerCall(call) is BinOpExpr power) return BinOpType(power);
            if (StdlibCallType(call) is string stdlibRet) return stdlibRet;
            if (call.Func is NameExpr fn && !IsShadowed(fn.Id))
            {
                if (NumericConversions.TryGetValue(fn.Id, out var conv)) return conv;
                switch (fn.Id)
                {
                    case "str": case "repr": case "input": case "chr": case "hex": case "bin": case "format": return "string";
                    case "int": return IntT;
                    case "len": case "ord": case "hash": return "long";
                    case "float": return "double";
                    case "complex": return "global::System.Numerics.Complex";
                    case "getattr": case "vars": return "dynamic";
                    case "dir": return "List<string>";
                    case "super": return call.Args.Count == 0 && Receivers.Contains(call) && _scope?.HasSelf == true ? null : "dynamic";
                    case "dict" when HasDictKeywords(call): return KeywordDictType(call);
                    case "dict" when call.Args.Any(a => a.Name != null || a.DoubleStar):
                        return DictKeywordSource(call, out _) is string keywordKey ? $"OrderedDictionary<{keywordKey}, dynamic>" : "dynamic";
                    case "bool": case "isinstance": case "any": case "all": return "bool";
                    case "cast": return call.Args.Count == 2 ? MapType(call.Args[0].Value, call) : null;   // typing.cast(T, x) is a C# cast to T
                    case "round": return call.Args.Count == 1 ? "long" : "double";
                    case "abs": return call.Args.Count == 1 ? (_big && IsIntegral(InferType(call.Args[0].Value)) ? IntT : InferType(call.Args[0].Value)) : null;
                    case "range": return _big ? BigRangeType : "global::Typhon.Runtime.PyRange";
                    case "open": return OpenMode(call) switch
                    {
                        null => "dynamic",
                        string mode when mode.Contains('b') => "global::Typhon.Runtime.PyBinaryFile",
                        _ => "global::Typhon.Runtime.PyFile",
                    };
                    case "bytes": return PyBytesType;
                    case "bytearray": return PyByteArrayType;
                    case "type" when call.Args.Count == 1: return "global::System.Type";
                    case "slice": return "global::Typhon.Runtime.PySlice";
                    case "iter":
                    {
                        if (call.Args.Count != 1) return null;
                        string elem = ElementTypeOf(InferType(call.Args[0].Value));
                        return elem == null || elem == TypeInference.Pending ? elem : $"global::Typhon.Runtime.PyIterator<{elem}>";
                    }
                    case "next":
                    {
                        if (call.Args.Count == 0) return null;
                        return ElementTypeOf(InferType(call.Args[0].Value));
                    }
                    case "tuple" when call.Args.Count == 1 && call.Args[0].Name == null && !call.Args[0].Star:
                    {
                        var at = InferType(call.Args[0].Value);
                        string elem = at == "string" ? "string" : ElementTypeOf(at);
                        return elem == null || elem == TypeInference.Pending ? elem : $"global::Typhon.Runtime.PyTuple<{elem}>";
                    }
                    case "list": case "sorted": case "set":
                    {
                        if (call.Args.Count == 0 || call.Args[0].Name != null || call.Args[0].Star) return null;
                        var at = InferType(call.Args[0].Value);
                        if (at == TypeInference.Pending) return at;
                        string elem = fn.Id == "list" && at == "string" ? "string" : ElementTypeOf(at);
                        if (elem == null && fn.Id == "set" && IsUnknownType(at)) return "HashSet<dynamic>";
                        if (elem == null || elem == TypeInference.Pending) return elem;
                        return (fn.Id == "set" ? "HashSet<" : "List<") + elem + ">";
                    }
                    case "enumerate":
                    {
                        if (call.Args.Count == 0 || call.Args[0].Name != null || call.Args[0].Star) return null;
                        string elem = IterElementType(InferType(call.Args[0].Value));
                        return elem == null || elem == TypeInference.Pending ? elem : $"IEnumerable<(long, {elem})>";
                    }
                    case "zip" when call.Args.Count >= 1 && call.Args.All(a => a.Name == null && !a.Star):
                    {
                        // zip(a, b, ...): tuples of the items (a str gives one-character strs)
                        // (a late-bound iterable is iterated as dynamic items)
                        var items = call.Args.Select(a => InferType(a.Value) == "string" ? "string" : PyType(IterElementType(InferType(a.Value)))).ToList();
                        if (items.Contains(TypeInference.Pending)) return TypeInference.Pending;
                        if (items.Any(x => x == null)) return null;
                        return items.Count == 1 ? $"IEnumerable<ValueTuple<{items[0]}>>" : $"IEnumerable<({string.Join(", ", items)})>";
                    }
                    case "dict" when call.Args.Count == 1 && call.Args[0].Name == null && !call.Args[0].Star && !call.Args[0].DoubleStar:
                    {
                        // dict(pairs) / dict(mapping): keys and values of the pairs
                        string at = InferType(call.Args[0].Value);
                        if (at == TypeInference.Pending) return at;
                        if (DictTypes(at) is List<string> dkv0) return $"OrderedDictionary<{dkv0[0]}, {dkv0[1]}>";
                        var pair = TupleElementTypes(ElementTypeOf(at));
                        return pair != null && pair.Count == 2 && !pair.Contains(TypeInference.Pending) ? $"OrderedDictionary<{PyType(pair[0])}, {PyType(pair[1])}>" : null;
                    }
                    case "filter" when call.Args.Count == 2 && call.Args.All(a => a.Name == null && !a.Star && !a.DoubleStar):
                    {
                        string elem = IterElementType(InferType(call.Args[1].Value));
                        return elem == null || elem == TypeInference.Pending ? elem : $"IEnumerable<{elem}>";
                    }
                    case "map" when call.Args.Count == 2 && call.Args.All(a => a.Name == null && !a.Star):
                    {
                        // map(f, xs): what f gives for xs's items (a builtin conversion, a function value, a lambda)
                        string elem = InferType(call.Args[1].Value) == "string" ? "string" : PyType(ElementTypeOf(InferType(call.Args[1].Value)));
                        if (elem == TypeInference.Pending) return elem;
                        string r = MappedType(call.Args[0].Value, elem);
                        return r == null || r == TypeInference.Pending ? r : $"IEnumerable<{r}>";
                    }
                    case "sum" when TupleSum(call) is string tupleSum: return tupleSum;
                    case "sum":
                    {
                        if (call.Args.Count != 1 || call.Args[0].Name != null || call.Args[0].Star) return null;
                        string elem = ElementTypeOf(InferType(call.Args[0].Value));
                        return IsIntegral(elem) ? (elem == TypeInference.BigInt ? elem : "long") : elem == "double" ? "double" : elem == TypeInference.Pending ? elem : null;
                    }
                    case "min": case "max":
                    {
                        // (key= picks an item, it does not change the type)
                        if (call.Args.Any(a => a.Name != null && a.Name != "key" || a.Star)) return null;
                        var pos = call.Args.Where(a => a.Name == null).ToList();
                        if (pos.Count == 0) return null;
                        var ts = pos.Count == 1 ? new List<string> { ElementTypeOf(InferType(pos[0].Value)) } : pos.Select(a => InferType(a.Value)).ToList();
                        if (ts.Contains(TypeInference.Pending)) return TypeInference.Pending;
                        if (ts.Any(t => t is null or "object" or "object?")) return "dynamic";
                        if (_big && ts.All(IsIntegral)) return pos.Count == 1 && ts[0] == "long" ? "long" : IntT;
                        if (ts.All(t => t == ts[0])) return ts[0];
                        if (ts.All(IsIntegral)) return "long";
                        return ts.All(t => IsIntegral(t) || t == "double") ? "double" : null;
                    }
                    case "divmod":
                    {
                        if (call.Args.Count != 2) return null;
                        string x = InferType(call.Args[0].Value), y = InferType(call.Args[1].Value);
                        return IsIntegral(x) && IsIntegral(y) ? $"({IntT}, {IntT})" : (x == "double" || y == "double") && (IsIntegral(x) || x == "double") && (IsIntegral(y) || y == "double") ? "(double, double)" : null;
                    }
                }
            }
            if (TryTypeExpr(call.Func, out var code)) return code;
            return null;
        }

        /// <summary>The type of a binary operation, with Python's rules (int / int is a float, list + list, str * n, ...).</summary>
        static bool ComplexOperand(string type) =>
            IsComplexType(type) || IsIntegral(type) || type is "float" or "double" or "bool";

        string BinOpType(BinOpExpr b)
        {
            string l = InferType(b.Left), r = InferType(b.Right);
            if (ArithmeticOps.Contains(b.Op)) { if (IsIntEnumType(l)) l = "long"; if (IsIntEnumType(r)) r = "long"; }
            // a recursive call whose type is still being inferred contributes no information (optimistic inference)
            if (l == TypeInference.Pending || r == TypeInference.Pending)
            {
                if (b.Op == "/") return "double";
                if (l == TypeInference.Pending && r == TypeInference.Pending) return TypeInference.Pending;
                return l == TypeInference.Pending ? r : l;
            }
            if (_m.IsGdScript && GdBinOpType(b, l, r) is string gdType) return gdType;
            if (!_m.IsGdScript && b.Op is ("+" or "-" or "*" or "/") &&
                (IsComplexType(l) && ComplexOperand(r) || IsComplexType(r) && ComplexOperand(l)))
                return ComplexType;
            // an operator of a Typhon class: what its __sub__ / __add__ / ... returns (not "T - T is T": Point - Point is a Vector)
            if (DunderOperatorType(b.Op, l, r) is string dunderType) return dunderType;
            // a .NET type's own operator for these operand types (date - date is a timedelta, date - timedelta a date)
            if (DotNetOperatorType(b.Op, l, r) is string netOpType) return netOpType;
            if (b.Op == "/" && DivisionOperatorType(l) is string divType) return divType;
            if (b.Op == "/" && (l == "dynamic" || r == "dynamic")) return "dynamic";
            if (b.Op == "/") return (l == "decimal" && r == "decimal") ? "decimal" : (NumericTypes.Contains(l ?? "") && NumericTypes.Contains(r ?? "") ? "double" : null);
            if (l == null || r == null) return null;
            if ((b.Op == "-" || b.Op == "|" || b.Op == "&" || b.Op == "^") && (IsSetType(l) || IsSetType(r))) return IsSetType(l) ? l : r;
            if (b.Op == "+" && (l.StartsWith(PyTuplePrefix) || r.StartsWith(PyTuplePrefix) ||
                                TupleElementTypes(l) is List<string> ll && TupleElementTypes(r) is List<string> rl && ll.Count + rl.Count >= 8 &&
                                HomogeneousTupleItem(l) != null && HomogeneousTupleItem(l) == HomogeneousTupleItem(r)) &&
                TupleItemOrMixed(l) is string hl && TupleItemOrMixed(r) is string hr)
                return PyTuplePrefix + (hl == hr ? hl : (hl == "long" || hl == "double") && (hr == "long" || hr == "double") ? "double" : "dynamic") + ">";
            if (b.Op == "*" && (HomogeneousTupleItem(l) is string rep && IsIntegral(r) || HomogeneousTupleItem(r) is string rep2 && IsIntegral(l)))
                return PyTuplePrefix + (HomogeneousTupleItem(l) ?? HomogeneousTupleItem(r)) + ">";
            if (b.Op == "+" && TupleElementTypes(l) is List<string> tl && TupleElementTypes(r) is List<string> tr)
                return tl.Count == 0 ? r : tr.Count == 0 ? l : "(" + string.Join(", ", tl.Concat(tr)) + ")";
            if (b.Op == "|" && IsDictType(l) && IsDictType(r)) return l;
            if (ArithmeticOps.Contains(b.Op) || IsBitwiseOperator(b.Op) &&
                (l == "bool" && IsIntegral(r) || r == "bool" && IsIntegral(l)))
            {
                if (l == "bool") l = "long";
                if (r == "bool") r = "long";
            }
            // a literal next to a narrower .NET integer stays that type (see Operand)
            if (IsIntLiteral(b.Left) && IsSmallInt(r)) l = r;
            if (IsIntLiteral(b.Right) && IsSmallInt(l)) r = l;
            if (_big && IsIntegral(l) && IsIntegral(r) && (b.Op == "<<" || b.Op == ">>")) return b.Op == "<<" || l == IntT ? IntT : "long";
            if (b.Op == "<<" || b.Op == ">>") return IsIntegral(l) && IsIntegral(r) ? "long" : (l == "dynamic" || r == "dynamic" ? "dynamic" : null);
            if (b.Op == "+" && l == "string" && r == "string") return "string";
            if (b.Op == "*" && (l == "string" || r == "string")) return "string";
            // [0] * n is a list of the same type
            if (b.Op == "*" && l.StartsWith("List<") && IsIntegral(r)) return l;
            if (b.Op == "*" && r.StartsWith("List<") && IsIntegral(l)) return r;
            if (b.Op == "+" && l.StartsWith("List<") && l == r) return l;
            if (l == "dynamic" || r == "dynamic") return "dynamic";
            if (!NumericTypes.Contains(l) || !NumericTypes.Contains(r)) return l == r ? l : null;
            if (b.Op == "**") return IsIntegral(l) && IsIntegral(r) ? IntegerPowerType(b.Right) : "double";
            if (_big && IsIntegral(l) && IsIntegral(r))
                return WideningOps.Contains(b.Op) || l == IntT || r == IntT ? IntT : (IsSmallInt(l) && IsSmallInt(r) && l == r ? l : "long");
            if (_big && (l == IntT || r == IntT) && (l == "double" || r == "double")) return "double";
            if (l == r) return l;
            if (l == "double" || r == "double") return "double";
            if (l == "long" || r == "long") return "long";
            return null;
        }
    }
}
