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
    // Collections: list, dict, set and tuple literals, comprehensions, iteration, and late-bound containers.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>An expression in a position that iterates it: wrapped in iterable() unless it is certainly not a dict.</summary>
        const string PyTuplePrefix = "global::Typhon.Runtime.PyTuple<";

        /// <summary>The item type of a tuple type whose items all have one type: (long, long, long) or PyTuple&lt;long&gt; → long.</summary>
        static string HomogeneousTupleItem(string t)
        {
            if (t == null) return null;
            if (t.StartsWith(PyTuplePrefix) && t.EndsWith(">")) return GenericArg(t);
            var items = TupleElementTypes(t);
            return items != null && items.Count > 0 && items.Distinct().Count() == 1 && items[0] != TypeInference.Pending ? items[0] : null;
        }

        /// <summary>A tuple type's item type, "dynamic" for a tuple of mixed item types, null if not a tuple type.</summary>
        static string TupleItemOrMixed(string t) => HomogeneousTupleItem(t) ?? (TupleElementTypes(t) != null ? "dynamic" : null);

        /// <summary>
        /// Literal items that are tuples of one item type but not one shape ((N, E, S, W) and an 8-tuple in one dict): as
        /// sequences, PyTuple&lt;T&gt;. Null otherwise.
        /// </summary>
        string TupleSequenceAmid(List<Expr> items)
        {
            if (items.Count < 2) return null;
            var ts = items.Select(x => PyType(InferType(x))).ToList();
            if (ts.Distinct().Count() == 1) return null;
            var elems = ts.Select(HomogeneousTupleItem).Distinct().ToList();
            return elems.Count == 1 && elems[0] != null ? PyTuplePrefix + elems[0] + ">" : null;
        }

        string TupleLiteralValue(Expr value, string targetType)
        {
            if (value is not TupleExpr tuple || targetType == null) return null;
            if (targetType.StartsWith("IReadOnlyList<") || targetType.StartsWith(PyTuplePrefix))
                return EmitAsPyTuple(value, PyTuplePrefix + GenericArg(targetType) + ">");
            if (TupleElementTypes(targetType) is not List<string> types || types.Count < 2 ||
                types.Count != tuple.Elts.Count || tuple.Elts.Any(item => item is StarredExpr))
                return null;
            var converted = tuple.Elts.Select((item, index) => TupleLiteralValue(item, types[index])).ToList();
            if (converted.All(item => item == null)) return null;
            return "(" + string.Join(", ", tuple.Elts.Select((item, index) => converted[index] ?? EmitValue(item, targetTyped: true))) + ")";
        }

        /// <summary>A value for a PyTuple&lt;T&gt; slot: a tuple literal is built as one, another tuple converted.</summary>
        string EmitAsPyTuple(Expr e, string pyTupleType)
        {
            string elem = GenericArg(pyTupleType);
            if (e is TupleExpr te && !te.Elts.Any(x => x is StarredExpr))
                return $"new {pyTupleType}(new {elem}[] {{ {string.Join(", ", te.Elts.Select(x => elem == "double" && IsIntLiteral(x) ? EmitValue(x) + ".0" : EmitValue(x)))} }})";
            string t = PyType(InferType(e));
            if (t == pyTupleType) return EmitExpr(e);
            return $"{Rt("__pytuple")}<{elem}>((object?){Paren(EmitExpr(e))})";
        }

        /// <summary>The type a function value gives for an argument of type <paramref name="arg"/> (map's result items).</summary>
        string MappedType(Expr f, string arg)
        {
            switch (f)
            {
                case NameExpr b when !IsShadowed(b.Id) && !IsLocal(b.Id) && !_m.Functions.Contains(b.Id):
                    switch (b.Id)
                    {
                        case "int": return IntT;
                        case "float": return "double";
                        case "str": case "repr": case "chr": case "hex": case "bin": case "oct": return "string";
                        case "len": case "ord": return "long";
                        case "bool": return "bool";
                        case "abs": return arg;
                        default: return null;
                    }
                case LambdaExpr l when !l.RequiresArgumentBinding && l.Params.Count == 1 && arg != null:
                {
                    var bindings = new Scope();
                    bindings.Types[l.Params[0]] = arg;
                    _comprehensionTypes.Add(bindings);
                    _lambdaParams.Push(LambdaParameters(l.Params));
                    try { return PyType(InferType(l.Body)); }
                    finally { _lambdaParams.Pop(); _comprehensionTypes.Remove(bindings); }
                }
                default: return null;
            }
        }

        /// <summary>sum(tuples, ()): the tuples concatenated, a PyTuple of their item type (null if not that shape).</summary>
        string TupleSum(CallExpr call)
        {
            if (call.Args.Count != 2 || call.Args.Any(a => a.Name != null || a.Star) || !(call.Args[1].Value is TupleExpr empty && empty.Elts.Count == 0)) return null;
            string elem = ElementTypeOf(InferType(call.Args[0].Value));
            if (elem == TypeInference.Pending) return elem;
            return HomogeneousTupleItem(elem) is string item ? PyTuplePrefix + item + ">" : null;
        }

        /// <summary>The type of an expression as something iterated (an Enum class iterates its members).</summary>
        string IterableType(Expr e) => EnumIterated(e, out _) is string et ? et + "[]" : InferType(e);

        /// <summary>The C# type of an Enum class used as an iterable (for kind in Color:), or null.</summary>
        string EnumIterated(Expr e, out ClassDef definition)
        {
            definition = null;
            if (DottedName(e) is not string name || IsLocal(name.Split('.')[0]) ||
                !TryResolveType(name, 0, out _, out var cls) || cls == null ||
                !(IsObjectIntegerEnum(cls) || cls.Bases.Any(basis => DottedName(basis) is "Enum" or "enum.Enum" or "IntEnum" or "enum.IntEnum")))
                return null;
            definition = cls;
            return StdlibTypeCode(null, cls) ?? Esc(cls.Name);
        }

        string EmitIterable(Expr e)
        {
            // for kind in Color: an Enum class iterates its members in definition order
            if (EnumIterated(e, out var enumClass) is string enumType)
                return IsObjectIntegerEnum(enumClass) ? $"{enumType}._value2member_map_.Values" : $"global::System.Enum.GetValues<{enumType}>()";
            string code = EmitExpr(e);
            // iterating a str yields one-character strs
            if (e is ConstExpr sc && sc.Kind == ConstKind.Str || e is FStringExpr) return $"{Rt("chars")}({code})";
            // for x in (1, 2, 3): a tuple literal iterates as a list of its items
            if (e is TupleExpr te && te.Elts.Count > 0 && !te.Elts.Any(x => x is StarredExpr))
            {
                // A slice can bind dynamically even when its result type is known.
                // Give the items an explicit type to keep the following LINQ call static.
                if (te.Elts.Any(x => x is SubscriptExpr { Index: SliceExpr } || LateBoundArg(x)))
                {
                    if (HomogeneousTupleItem(InferType(te)) is string itemType && !IsUnknownType(itemType) && itemType != "object")
                        return $"new {itemType}[] {{ {string.Join(", ", te.Elts.Select(x => $"({itemType}){Paren(EmitValue(x))}"))} }}";
                    return $"{Rt("__iter_dyn")}(new object?[] {{ {string.Join(", ", te.Elts.Select(x => EmitValue(x)))} }})";
                }
                return EmitList(new ListExpr { Elts = te.Elts, Line = te.Line, Col = te.Col }, false);
            }
            if (e is ListExpr || e is TupleExpr || e is SetExpr || e is ComprehensionExpr || e is ConstExpr) return code;
            // a late-bound value: a typed sequence of dynamic items (LINQ lambdas cannot take part in dynamic dispatch); the
            // argument is passed as object so that the call itself is bound statically
            string t = InferType(e);
            if (t == null && e is NameExpr imported && !IsLocal(imported.Id) &&
                ImportedIterableType(imported.Id) is string importedType)
            {
                if (!IsPortableType(importedType)) return $"{Rt("iterable")}({code})";
                t = importedType;
            }
            if (t is "dynamic" or "object" or "object?")
                return $"{Rt("__iter_dyn")}((object){Paren(code)})";
            if (e is CallExpr && IsDynamicallyDispatched(e))
            {
                string late = $"{Rt("__iter_dyn")}((object){Paren(code)})";
                string elem = IterElementType(t);
                return elem == null || elem == "dynamic" || elem == TypeInference.Pending
                    ? late : $"global::System.Linq.Enumerable.Cast<{elem}>({late})";
            }
            // str(n), "".join(...), s.lower(): a str-valued call iterates as one-character strs too
            if (e is CallExpr && InferType(e) == "string") return $"{Rt("chars")}({code})";
            if (e is CallExpr ce && ce.Func is NameExpr cn && !IsShadowed(cn.Id) && NonDictResults.Contains(cn.Id)) return code;
            if (e is CallExpr me && me.Func is AttributeExpr ma && (ma.Name == "items" || ma.Name == "keys" || ma.Name == "values" || ma.Name == "split" || ma.Name == "splitlines")) return code;
            if (t == "string") return $"{Rt("chars")}({code})";
            var fixedTuple = TupleElementTypes(t);
            if (fixedTuple != null)
            {
                if (fixedTuple.Distinct().Count() == 1 && !IsUnknownType(fixedTuple[0]))
                    return $"{Rt("tuple_items")}<{fixedTuple[0]}>({code})";
                return $"{Rt("__iter_dyn")}((object?){Paren(code)})";
            }
            if (t is "global::Typhon.Runtime.PyRange" or BigRangeType || IsBytesType(t)) return code;
            if (IsDictType(t))
            {
                string keys = t.StartsWith("IReadOnlyDictionary<") ? "__ro_keys" : "keys";
                return $"global::Typhon.Runtime.Builtins.{keys}({code})";
            }
            if (t != null && (t.EndsWith("[]") || t.StartsWith("List<") || t.StartsWith("HashSet<") || t.StartsWith("IEnumerable<") ||
                              t.StartsWith(PyGeneratorPrefix) || t.StartsWith(PyTuplePrefix) || t.StartsWith("global::Typhon.Runtime.PyIterator<") ||
                              t.StartsWith("IReadOnlyList<") || t.StartsWith("IList<") || t.StartsWith("(") || t.StartsWith("ValueTuple<")))
                return code;
            if (_p.Resolver is RoslynTypeResolver resolver && DotNetMeta(t) is string metadata && resolver.HasTypedEnumerable(metadata) ||
                t == null && HasTypedSourceIterableMember(e))
                return $"{Rt("iterable")}({code})";
            // An unknown iterable can be dynamic even when the C# expression looks statically callable.
            // Keep the iterator itself statically typed so maps and comprehensions can pass delegates.
            return $"{Rt("__iter_dyn")}((object?){Paren(code)})";
        }

        // An imported class can expose a typed collection whose element name is local to its declaring module.
        // Let the C# iterable overload retain the element type without spelling that name here.
        bool HasTypedSourceIterableMember(Expr expression) =>
            expression is AttributeExpr member && !IsDynamicallyDispatched(member.Target) &&
            ClassKeyOfType(InferType(member.Target), out _) is string owner && Ti != null &&
            Ti.Fields.TryGetValue(owner, out var fields) && fields.TryGetValue(member.Name, out string type) &&
            type != TypeInference.Pending && ElementTypeOf(type) != null;

        /// <summary>An argument that may be late-bound to C#: not a literal, and of dynamic or unknown type.</summary>
        bool LateBoundArg(Expr e) =>
            !(e is ConstExpr || e is ListExpr || e is DictExpr || e is SetExpr || e is TupleExpr || e is LambdaExpr || e is ComprehensionExpr || e is FStringExpr || IsEmptyLiteral(e)) &&
            (MaybeLateBound(e) || InferType(e) == null && (e is NameExpr || e is AttributeExpr || e is SubscriptExpr || e is CallExpr));

        /// <summary>Might C# see the expression as dynamic (whatever Typhon infers for it)?</summary>
        bool MaybeLateBound(Expr e)
        {
            if (e is ConstExpr || e is ListExpr || e is DictExpr || e is TupleExpr || e is SetExpr || e is LambdaExpr || e is ComprehensionExpr) return false;
            string t = InferType(e);
            // EmitExpr boxes a value-producing boolean operation when its operands have different types.
            if (t == "dynamic" || t == null && e is BoolOpExpr || IsDynamicallyDispatched(e)) return true;
            // a call of a Typhon function whose result type is not known yet may bind to its dynamic version
            return e is CallExpr ce && CallTarget(ce, out bool ctor) is InferenceTarget it && !ctor && !it.Def.IsAsync &&
                (it.Def.Returns == null || TypeInference.IsBareContainer(it.Def.Returns)) &&
                IsUnknownType(InferredCallType(it, ce.Args));
        }

        static bool IsPythonContainer(string t) => t != null && (t == "string" || t.StartsWith("List<") || IsSetType(t) || IsDictType(t));

        internal static bool IsSetType(string t) => t != null && (t.StartsWith("HashSet<") || t.StartsWith("ISet<") || t.StartsWith("IReadOnlySet<"));
        /// <summary>Key and value type of a mapping type (a Counter's values are ints); null if not a mapping.</summary>
        internal static List<string> DictTypes(string t)
        {
            if (!IsDictType(t)) return null;
            var args = SplitTypeArgs(GenericArg(t));
            if (t.StartsWith("global::Typhon.Runtime.Counter<") && args.Count == 1) return new List<string> { args[0], "long" };
            return args.Count == 2 ? args : null;
        }

        /// <summary>A mapping type as the inference spells it (Python's dict is OrderedDictionary; .NET dictionaries count too).</summary>
        internal static bool IsDictType(string t) => t != null && t.EndsWith(">") && (t.StartsWith("OrderedDictionary<") || t.StartsWith("Dictionary<") || t.StartsWith("IDictionary<") ||
            t.StartsWith("global::Typhon.Runtime.Counter<") || t.StartsWith("global::Typhon.Runtime.defaultdict<") || t.StartsWith("global::Typhon.Runtime.PyOrderedDict<") ||
            t.StartsWith("IReadOnlyDictionary<") || t.StartsWith("SortedDictionary<") || t.StartsWith("global::System.Collections.Generic.Dictionary<") || t.StartsWith("global::System.Collections.Generic.OrderedDictionary<"));

        /// <summary>
        /// [*a, x, *b] / {*a, x} / (*a, x): a C# collection expression with spreads, given its type by a cast (the element type
        /// comes from the items and the element types of the starred iterables).
        /// </summary>
        string EmitSpread(List<Expr> elts, string collection, Node at)
        {
            string elem = SpreadElementType(elts);
            if (elem == null) elem = ImplicitDynamic(at, "the element type of a collection with starred items");
            var parts = elts.Select(x => x is StarredExpr st ? ".. " + EmitIterable(st.Value) : EmitValue(x));
            return $"(({collection}<{elem}>)[{string.Join(", ", parts)}])";
        }

        string SpreadElementType(List<Expr> elts)
        {
            var ts = elts.Select(x => PyType(x is StarredExpr st ? ElementTypeOf(InferType(st.Value)) : InferType(x))).Distinct().ToList();
            if (ts.Contains(TypeInference.Pending)) return TypeInference.Pending;
            if (ts.Count == 1 && ts[0] != null && ts[0] != "dynamic") return ts[0];
            if (ts.All(t => IsIntegral(t) || t == "double")) return ts.Contains("double") ? "double" : IntT;
            return null;
        }

        string EmitList(ListExpr l, bool targetTyped)
        {
            if (l.Elts.Any(x => x is StarredExpr))
            {
                if (targetTyped) return "[" + string.Join(", ", l.Elts.Select(x => x is StarredExpr st ? ".. " + EmitIterable(st.Value) : EmitExpr(x))) + "]";
                return EmitSpread(l.Elts, "List", l);
            }
            if (targetTyped) return "[" + string.Join(", ", l.Elts.Select(x => EmitExpr(x))) + "]";
            // [None, None]: nothing says of what (a placeholder list whose type no later store gives)
            if (l.Elts.Count > 0 && l.Elts.All(x => x is ConstExpr nc && nc.Kind == ConstKind.None))
                return $"new List<dynamic> {{ {string.Join(", ", l.Elts.Select(_ => "null"))} }}";
            var elts = l.Elts.Select(x => EmitValue(x)).ToList();
            if (elts.Count == 0)
            {
                ImplicitDynamic(l, "the element type of an empty list (annotate the variable, e.g. 'xs: list[int] = []')");
                return "new List<dynamic>()";
            }
            if (TupleSequenceAmid(l.Elts) is string seqType) return $"{Rt("__list")}<{seqType}>({string.Join(", ", l.Elts.Select(x => EmitAsPyTuple(x, seqType)))})";
            // [[1, 2], []]: the empty ones take the others' type
            if (TypeAmidEmpties(l.Elts) is string itemType) return $"{Rt("__list")}<{itemType}>({string.Join(", ", l.Elts.Select(x => EmitItemAs(x, itemType)))})";
            // elements of .NET's narrower integer types become Python ints
            string lt = InferType(l);
            // [late_bound]: a list of dynamic values (C# would take the element type from the value at run time)
            // (late-bound items passed as object: the call itself stays static, so the list is a List<dynamic> to C# too)
            if (lt == "List<dynamic>" || lt == null && l.Elts.Any(x => InferType(x) == "dynamic"))
                return $"{Rt("__list")}<dynamic>({string.Join(", ", l.Elts.Select((x, i) => MaybeLateBound(x) ? $"(object?){Paren(elts[i])}" : elts[i]))})";
            if (lt == "List<long>" && l.Elts.Any(x => IsSmallInt(InferType(x)))) return $"{Rt("__list")}<long>({string.Join(", ", elts)})";
            if (lt == null && l.Elts.Count > 1 && MixedTypes(l.Elts))
                return $"{Rt("__list")}<{ImplicitDynamic(l, "the element type of a list with values of different types")}>({string.Join(", ", elts)})";
            return $"{Rt("__list")}({string.Join(", ", elts)})";
        }

        string EmitDict(DictExpr d, bool targetTyped, string expectedType = null)
        {
            string DictionaryKey(Expr key, bool targetTyped = false) => IsNone(key)
                ? $"{Rt("__key")}<object>(null)" : targetTyped ? EmitExpr(key, targetTyped: true) : EmitValue(key);
            // {**a, 'k': v, **b}: merged left to right
            if (d.Keys.Any(k => k == null))
            {
                var parts = new List<string>();
                var literal = new List<string>();
                void Flush() { if (literal.Count > 0) { parts.Add($"{Rt("__dict")}({string.Join(", ", literal)})"); literal.Clear(); } }
                for (int i = 0; i < d.Keys.Count; i++)
                {
                    if (d.Keys[i] == null) { Flush(); parts.Add(EmitExpr(d.Values[i])); }
                    else literal.Add($"({EmitValue(d.Keys[i])}, {EmitValue(d.Values[i])})");
                }
                Flush();
                if (DictTypes(expectedType) is List<string> expected)
                    return $"{Rt("__dict_merge_as")}<{expected[0]}, {expected[1]}>({string.Join(", ", parts)})";
                // parts of different key/value types: merged into the unified types
                var kts = new List<string>(); var vts = new List<string>();
                for (int i = 0; i < d.Keys.Count; i++)
                {
                    if (d.Keys[i] != null) { kts.Add(PyType(InferType(d.Keys[i]))); vts.Add(PyType(InferType(d.Values[i]))); continue; }
                    var dt = InferType(d.Values[i]);
                    var kv = IsDictType(dt) ? SplitTypeArgs(GenericArg(dt)) : null;
                    kts.Add(kv?[0]); vts.Add(kv?[1]);
                }
                string Unify(List<string> ts, string what)
                {
                    var u = ts.Distinct().ToList();
                    if (u.Count == 1 && u[0] != null) return u[0];
                    if (u.All(t => IsIntegral(t) || t == "double")) return "double";
                    return ImplicitDynamic(d, what);
                }
                if (kts.Distinct().Count() > 1 || vts.Distinct().Count() > 1 || kts.Contains(null) || vts.Contains(null))
                    return $"{Rt("__dict_merge_as")}<{Unify(kts, "the key type of a merged dict")}, {Unify(vts, "the value type of a merged dict")}>({string.Join(", ", parts)})";
                return $"{Rt("__dict_merge")}({string.Join(", ", parts)})";
            }
            var boundMethods = d.Values.Select(value => ClassBodyMethod(value) is string method
                ? $"new global::Typhon.Runtime.PyBoundMethod(typeof({_cls.CsSelfType}), {CsString(method)}, includeNonPublic: true)"
                : targetTyped ? null : BoundMethodValue(value)).ToArray();
            if (boundMethods.Any(method => method != null))
            {
                string key = DictTypes(InferType(d))?[0] ?? "dynamic";
                return $"new OrderedDictionary<{key}, dynamic> {{ {string.Join(", ", d.Keys.Select((k, i) => $"[{DictionaryKey(k)}] = {boundMethods[i] ?? EmitLooseValue(d.Values[i])}"))} }}";
            }
            if (targetTyped && d.Keys.Count > 0 && DictTypes(InferType(d)) is List<string> tdk && tdk[1].StartsWith(PyTuplePrefix))
                return "new() { " + string.Join(", ", d.Keys.Select((k, i) => $"[{DictionaryKey(k, targetTyped: true)}] = {EmitAsPyTuple(d.Values[i], tdk[1])}")) + " }";
            if (targetTyped)
                return d.Keys.Count == 0 ? "new()" : "new() { " + string.Join(", ", d.Keys.Select((k, i) => $"[{DictionaryKey(k, targetTyped: true)}] = {EmitExpr(d.Values[i], targetTyped: true)}")) + " }";
            if (d.Keys.Count == 0)
            {
                ImplicitDynamic(d, "the key/value types of an empty dict (annotate the variable, e.g. 'd: dict[str, int] = {}')");
                return $"{Rt("__newdict")}<dynamic, dynamic>()";
            }
            if (d.Values.All(IsNone))
            {
                string key = DictTypes(InferType(d))?[0] ?? "dynamic";
                return $"{Rt("__dict")}<{key}, dynamic>({string.Join(", ", d.Keys.Select(k => $"({DictionaryKey(k)}, null)"))})";
            }
            string typeArgs = "";
            if (!MixedTypes(d.Keys) && PyType(InferType(d.Keys[0])) is string tk && tk != TypeInference.Pending && TupleSequenceAmid(d.Values) is string tv)
                return $"{Rt("__dict")}<{tk}, {tv}>(" + string.Join(", ", d.Keys.Select((k, i) => $"({DictionaryKey(k)}, {EmitAsPyTuple(d.Values[i], tv)})")) + ")";
            if (!MixedTypes(d.Keys) && PyType(InferType(d.Keys[0])) is string dk && dk != TypeInference.Pending && TypeAmidEmpties(d.Values) is string dv)
                return $"{Rt("__dict")}<{dk}, {dv}>(" + string.Join(", ", d.Keys.Select((k, i) => $"({DictionaryKey(k)}, {EmitItemAs(d.Values[i], dv)})")) + ")";
            if (d.Keys.Count > 1 && (MixedTypes(d.Keys) || MixedTypes(d.Values)))
            {
                // {"name": "Ada", "age": 36}: no common C# type, the differing side is dynamic
                string kt = MixedTypes(d.Keys) ? ImplicitDynamic(d, "the key type of a dict with keys of different types") : PyType(InferType(d.Keys[0]));
                string vt = MixedTypes(d.Values) ? ImplicitDynamic(d, "the value type of a dict with values of different types") : PyType(InferType(d.Values[0]));
                if (kt != null && vt != null && kt != TypeInference.Pending && vt != TypeInference.Pending) typeArgs = $"<{kt}, {vt}>";
            }
            return Rt("__dict") + typeArgs + "(" + string.Join(", ", d.Keys.Select((k, i) => $"({DictionaryKey(k)}, {EmitValue(d.Values[i])})")) + ")";
        }

        /// <summary>
        /// The common type of literal items some of which are empty containers ({"a": [("b", 2)], "c": []}): the others'
        /// type, when each empty one is of that kind. Null when no item is empty or they disagree.
        /// </summary>
        string TypeAmidEmpties(List<Expr> items)
        {
            var full = items.Where(x => !IsEmptyLiteral(x)).ToList();
            if (full.Count == 0 || full.Count == items.Count) return null;
            var ts = full.Select(x => PyType(InferType(x))).Distinct().ToList();
            if (ts.Count != 1 || ts[0] == null || ts[0] == TypeInference.Pending || ts[0] == "dynamic") return null;
            string t = ts[0];
            foreach (var e in items.Where(IsEmptyLiteral))
                if (!(e is ListExpr && t.StartsWith("List<") || e is DictExpr && IsDictType(t) || e is CallExpr && (IsSetType(t) || t.StartsWith("List<") || IsDictType(t)))) return null;
            return t;
        }

        /// <summary>A literal item for a known item type: an empty container is constructed with that type.</summary>
        string EmitItemAs(Expr e, string type) => IsEmptyLiteral(e) && type.EndsWith(">") && !type.StartsWith("I") ? $"new {type}()" : EmitValue(e);

        /// <summary>Types of the variables of the comprehensions being emitted (the lambdas' parameters), innermost last.</summary>
        readonly List<Scope> _comprehensionTypes = new List<Scope>();

        string EmitComprehension(ComprehensionExpr c, string targetType = null)
        {
            var bindings = new Scope();
            _comprehensionTypes.Add(bindings);
            try { return EmitComprehensionCore(c, bindings, targetType); }
            finally { _comprehensionTypes.Remove(bindings); }
        }

        /// <summary>The element of a comprehension for a declared element type: nested comprehensions follow it, an int becomes a float.</summary>
        string EmitElementAs(Expr elt, string targetElem)
        {
            if (elt is AttributeExpr attribute && BoundMethodValue(attribute) is string bound)
            {
                if (targetElem is null or "dynamic") return $"(dynamic){Paren(bound)}";
                if (targetElem is "object" or "object?") return $"(object?){Paren(bound)}";
                if (IsDotNetMethod(attribute) && (targetElem is "Action" or "global::System.Action" ||
                    targetElem.StartsWith("Func<") || targetElem.StartsWith("global::System.Func<") ||
                    targetElem.StartsWith("Action<") || targetElem.StartsWith("global::System.Action<")))
                    return $"({targetElem}){Paren(EmitAttribute(attribute, allowMethodGroup: true))}";
            }
            if (targetElem == null) return EmitValue(elt);
            if (targetElem == "dynamic") return $"(dynamic){Paren(EmitValue(elt))}";
            if (elt is ComprehensionExpr inner) return EmitComprehension(inner, targetElem);
            if (targetElem == "double" && IsIntegral(InferType(elt)) && InferType(elt) != TypeInference.BigInt) return $"(double){Paren(EmitValue(elt))}";
            // [0] for a list[float] element: a list of the declared type (a collection expression has no type in a lambda)
            if (elt is ListExpr le && targetElem.StartsWith("List<") && targetElem.EndsWith(">") && !le.Elts.Any(x => x is StarredExpr))
                return $"new {targetElem} {{ {string.Join(", ", le.Elts.Select(x => EmitElementAs(x, GenericArg(targetElem))))} }}";
            return EmitValue(elt);
        }

        static List<Expr> ComprehensionTargets(Expr expression) => expression switch
        {
            TupleExpr tuple => tuple.Elts,
            ListExpr list => list.Elts,
            _ => null,
        };

        static bool CanUnpackComprehension(Expr expression) =>
            expression is NameExpr || ComprehensionTargets(expression)?.All(CanUnpackComprehension) == true;

        string UnpackComprehension(string item, Expr target, Dictionary<string, string> assignments)
        {
            var statements = new List<string>();
            void Unpack(string value, Expr binding)
            {
                if (binding is NameExpr name)
                {
                    assignments[name.Id] = value;
                    return;
                }
                var targets = ComprehensionTargets(binding);
                string unpacked = NewTmp("unpack");
                statements.Add($"var {unpacked} = {Rt("__unpack_items")}({Rt("__iter_dyn")}((object?){value}), {targets.Count});");
                for (int index = 0; index < targets.Count; index++) Unpack($"{unpacked}[{index}]", targets[index]);
            }
            Unpack(item, target);
            return string.Join(" ", statements);
        }

        string EmitComprehensionCore(ComprehensionExpr c, Scope bindings, string targetType)
        {
            string targetElem = targetType != null && targetType.EndsWith(">") && (targetType.StartsWith("List<") || targetType.StartsWith("HashSet<") ||
                                targetType.StartsWith("IEnumerable<") || targetType.StartsWith("IReadOnlyList<") || targetType.StartsWith("IList<")) &&
                                c.Kind != ComprehensionKind.Dict ? GenericArg(targetType) : null;
            string Gen(int i)
            {
                var g = c.Generators[i];
                // [x async for x in agen()]: one async generator, collected by awaiting (System.Linq's async operators)
                if (g.IsAsync && c.Generators.Count > 1) Unsupported(c, "an async comprehension with several 'for' clauses");
                string iter = EmitIterationSource(g.Iter, g.IsAsync, out bool lateAsync);
                string elemType = lateAsync || !g.IsAsync && iter.StartsWith(Rt("__iter_dyn") + "(")
                    ? "dynamic" : IterElementType(InferType(g.Iter));
                BindTargetTypes(bindings, g.Target, elemType);
                string param; string deconstruct = null;
                string seq = iter;
                var bound = new HashSet<string>();
                AstWalker.VisitAll(g.Target, n => { if (n is NameExpr ne) bound.Add(ne.Id); });
                if (g.Target is NameExpr tn) param = Esc(tn.Id);
                else if ((IsUnknownType(elemType) || elemType == "object?") && CanUnpackComprehension(g.Target))
                {
                    param = NewTmp("c");
                    var values = new Dictionary<string, string>();
                    string unpack = UnpackComprehension(param, g.Target, values);
                    if (g.Ifs.Count == 0)
                        deconstruct = unpack + " " + string.Join(" ", values.Select(binding => $"var {Esc(binding.Key)} = {binding.Value};"));
                    else
                    {
                        seq = $"{Paren(seq)}.Select({param} => {{ {unpack} return new object?[] {{ {string.Join(", ", values.Values)} }}; }})";
                        deconstruct = string.Join(" ", values.Keys.Select((name, index) => $"dynamic {Esc(name)} = {param}[{index}];"));
                    }
                }
                else if (g.Target is TupleExpr tuple && tuple.Elts.All(x => x is NameExpr))
                {
                    param = NewTmp("c");
                    var names = tuple.Elts.Cast<NameExpr>().Select(name => name.Id).ToList();
                    string pattern = string.Join(", ", names.Select((name, index) => index == names.LastIndexOf(name) ? Esc(name) : NewTmp("discard")));
                    deconstruct = $"var ({pattern}) = {param};";
                }
                else { param = NewTmp("c"); deconstruct = $"var {DeconstructionPattern(g.Target)} = {param};"; }
                _lambdaParams.Push(bound.ToDictionary(name => name, Esc));
                string Lambda(string body) => deconstruct == null ? $"{param} => {body}" : $"{param} => {{ {deconstruct} return {body}; }}";
                foreach (var cond in g.Ifs) seq = $"{Paren(seq)}.Where({Lambda(Cond(cond))})";
                string result;
                if (i == c.Generators.Count - 1)
                {
                    string elt = c.Kind == ComprehensionKind.Dict ? $"({EmitValue(c.Elt)}, {EmitValue(c.ValueElt)})" : EmitElementAs(c.Elt, targetElem);
                    result = $"{Paren(seq)}.Select({Lambda(elt)})";
                }
                else result = $"{Paren(seq)}.SelectMany({Lambda(Gen(i + 1))})";
                _lambdaParams.Pop();
                return result;
            }
            string q = Gen(0);
            if (c.Generators.Count == 1 && c.Generators[0].IsAsync)
                switch (c.Kind)
                {
                    case ComprehensionKind.List: return $"(await {Rt("__alist")}({q}))";
                    case ComprehensionKind.Set: return $"(await {Rt("__alist")}({q})).ToHashSet()";
                    case ComprehensionKind.Dict: return $"{Rt("__to_dict")}(await {Rt("__alist")}({q}))";
                    default: return $"{Rt("__async_generator_expression")}({q})";
                }
            switch (c.Kind)
            {
                case ComprehensionKind.List: return q + ".ToList()";
                case ComprehensionKind.Set: return q + ".ToHashSet()";
                case ComprehensionKind.Dict: return $"{Rt("__to_dict")}({q})"; // later keys win, insertion order kept
                default: return $"{Rt("__generator_expression")}({q})";
            }
        }

        /// <summary>Do the values have different (or unknown) types, so C# cannot infer one element type? (ints and floats unify)</summary>
        bool MixedTypes(List<Expr> values)
        {
            var ts = values.Select(x => x is ConstExpr c && c.Kind == ConstKind.None ? "None" : PyType(InferType(x))).Distinct().ToList();
            if (ts.Count <= 1 || ts.Contains(TypeInference.Pending)) return false;
            return !ts.All(t => IsIntegral(t) || t == "double");
        }
    }
}
