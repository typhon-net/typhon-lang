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
    // Assignments: to names (declarations, rebinding, widening), to sequences (unpacking), attributes and items.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>Locals that start as None (scope key | name): their later values decide their type.</summary>
        readonly HashSet<string> _noneLocals = new HashSet<string>();

        /// <summary>Optional value-type names known to hold a value where they are read (see NarrowedBy): read as x.Value, typed T.</summary>
        readonly HashSet<string> _narrowed = new HashSet<string>();

        string KnownType(string name)
        {
            if (_narrowed.Contains(name) && KnownTypeCore(name) is string nt && nt.EndsWith("?")) return nt.Substring(0, nt.Length - 1);
            return KnownTypeCore(name);
        }

        string KnownTypeCore(string name)
        {
            // comprehension variables (innermost first) while their comprehension is emitted
            for (int i = _comprehensionTypes.Count - 1; i >= 0; i--)
                if (_comprehensionTypes[i].Types.TryGetValue(name, out var ct)) return ct;
                else if (_comprehensionTypes[i].Params.Contains(name)) return null;
            for (var s = _scope; s != null; s = s.Parent)
                if (s.Types.TryGetValue(name, out var t)) return t;
            return null;
        }

        void SetType(string name, string type)
        {
            if (_scope != null && type != null) _scope.Types[name] = type;
        }

        /// <summary>yield v; in a generator that receives values, then what was sent in (or thrown in) when it resumes.</summary>
        string EmitYieldValue(YieldExpr y, bool wantSent = false)
        {
            string value = YieldedValue(y);
            if (_scope != null && _scope.AsyncForm)
            {
                if (!wantSent) { _w.Line($"await {GenVar}.Yield({value});"); return null; }
                string got = NewTmp("sent");
                _w.Line($"dynamic {got} = await {GenVar}.Yield({value});");
                SetType(got, "dynamic");
                return got;
            }
            _w.Line($"yield return {value};");
            if (_scope == null || !_scope.ReceivesValues) return null;
            string sent = NewTmp("sent");
            _w.Line($"dynamic {sent} = {GenVar}.__resume();");
            SetType(sent, "dynamic");
            return sent;
        }

        string YieldedValue(YieldExpr y) =>
            y.Value == null ? "default!" : _scope?.ReturnType == "dynamic" ? EmitValue(y.Value) : EmitExpr(y.Value, targetTyped: true);

        void EmitAssign(AssignStmt a)
        {
            // x = yield v: the value send() passes in (late-bound: a generator can be sent anything)
            if (a.Value is YieldExpr ay && !ay.From && _scope != null && _scope.ReceivesValues)
            {
                string sent = EmitYieldValue(ay, wantSent: true);
                foreach (var t in a.Targets) EmitAssignTo(t, new NameExpr { Id = sent, Line = a.Line, Col = a.Col }, a);
                return;
            }
            if (a.Value is YieldExpr) { Unsupported(a, "using 'yield' as an expression"); return; }
            if (a.Targets.Count == 1) { EmitAssignTo(a.Targets[0], a.Value, a); return; }
            string tmp = NewTmp();
            _w.Line($"var {tmp} = {EmitValue(a.Value)};");
            // a = b = value: the targets take the value's type (and the inference sees it: xs[i] = node = Node())
            string vt = TypeInference.IsEmptyContainer(a.Value) || NeedsExplicitType(a.Value) ? null : InferType(a.Value);
            if (vt != null && vt != TypeInference.Pending) SetType(tmp, vt); else vt = null;
            foreach (var t in a.Targets) EmitAssignTo(t, new NameExpr { Id = tmp, Line = a.Line, Col = a.Col }, a, rawValue: vt == null ? tmp : null);
        }

        void EmitAssignTo(Expr target, Expr value, Stmt stmt, string rawValue = null)
        {
            switch (target)
            {
                case NameExpr n:
                {
                    // g = f: g keeps f's function type (calls through g are typed calls of f)
                    _fnTypedUse = rawValue == null && value is NameExpr;
                    try { EmitAssignToName(n, value, stmt, rawValue); }
                    finally { _fnTypedUse = false; }
                    return;
                }
                case TupleExpr _:
                case ListExpr _:
                    EmitAssignToSequence(target, value, stmt, rawValue);
                    return;
                default:
                    EmitAssignToOther(target, value, stmt, rawValue);
                    return;
            }
        }

        bool IsHoistedLocal(string name)
        {
            for (var sc = _scope; sc != null; sc = sc.Parent)
            {
                if (sc.Locals == null) continue;
                return sc.Locals.HoistedPlain.Contains(name) && !_renames.ContainsKey(name);
            }
            return false;
        }

        /// <summary>obj.name where obj is a .NET (or runtime) object and name one of its methods, not a property or field.</summary>
        bool IsDotNetMethod(AttributeExpr a)
        {
            string t = InferType(a.Target);
            if (t == null || t == "dynamic" || ClassKeyOfType(t, out _) != null) return false;
            var meta = DotNetMeta(t);
            if (meta == null) return false;
            var members = _r.GetMembers(meta, a.Name);
            return members.Count > 0 && members.All(m => m.IsMethod && !m.IsStatic);
        }

        string EmitBoundMethod(AttributeExpr a) =>
            $"new global::Typhon.Runtime.PyBoundMethod((object){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";

        string BoundMethodValue(Expr value)
        {
            if (value is NameExpr nameAlias && AliasTarget(nameAlias) is AttributeExpr aliased) value = aliased;
            if (!(value is AttributeExpr a)) return null;
            if (DottedName(a.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) &&
                TryResolveType(dotted, 0, out _, out var owner) && owner != null &&
                MethodInHierarchy(owner, a.Name) is FunctionDef definition &&
                !HasDecorator(definition.Decorators, "property") && !IsCachedProperty(definition) &&
                TryTypeExpr(a.Target, out var classType))
                return $"{Rt("getattr")}(typeof({classType}), {CsString(a.Name)})";
            if (InstanceMethodMayBeShadowed(a)) return EmitAttribute(a);
            if (ProjectFunctionAttributeValue(a) is string projectFunction) return projectFunction;
            if (DotNetStaticMeta(a.Target) is string meta)
            {
                var members = _r.GetMembers(meta, a.Name);
                if (members.Any(m => m.IsMethod && m.IsStatic && (meta.StartsWith("Typhon.Stdlib.") || !(m.TypeParams?.Count > 0))) &&
                    members.All(m => m.IsMethod) && TryTypeExpr(a.Target, out var type))
                    return $"new global::Typhon.Runtime.PyBoundMethod(typeof({type}), {CsString(a.Name)})";
                return null;
            }
            if (IsDotNetMethod(a)) return EmitBoundMethod(a);
            if (a.Target is NameExpr name && !IsLocal(name.Id) && TryResolveType(name.Id, 0, out _, out _)) return null;
            var cls = a.Target is NameExpr self && self.Id == "self" && HasSelfInScope() ? _cls?.Def :
                ClassKeyOfType(InferType(a.Target), out _) is string key ? ClassDefOfKey(key) : null;
            if (cls == null || MethodInHierarchy(cls, a.Name) is not FunctionDef method ||
                HasDecorator(method.Decorators, "staticmethod") || HasDecorator(method.Decorators, "classmethod") ||
                HasDecorator(method.Decorators, "property") || IsCachedProperty(method)) return null;
            return $"new global::Typhon.Runtime.PyBoundMethod((object){Paren(EmitExpr(a.Target))}, {CsString(a.Name)}, includeNonPublic: true)";
        }

        /// <summary>A function name being assigned to a variable (its function type is kept).</summary>
        bool _fnTypedUse;

        void EmitAssignToName(NameExpr n, Expr value, Stmt stmt, string rawValue)
        {
            // f = obj.method of a .NET object: a callable bound method (a method group has no type C# can store here)
            bool boundMethod = false;
            if (rawValue == null && BoundMethodValue(value) is string bound)
            {
                rawValue = $"(dynamic){bound}";
                boundMethod = true;
            }
            if (rawValue == null && RegexLiteral(value, out var rxPattern, out var rxFlags)) _regexLocals[n.Id] = (rxPattern, rxFlags);
            else _regexLocals.Remove(n.Id);
            {
                {
                    string name = _renames.TryGetValue(n.Id, out var rn) ? rn : n.Id;
                    if (IsInline(stmt, n.Id))
                    {
                        string declType = null;
                        if (_scope.Locals.Annotations.TryGetValue(n.Id, out var ann) && !TypeInference.IsBareContainer(ann)) declType = MapType(ann, stmt);
                        else if (ConflictType(n.Id) is string wider) declType = wider; // rebound to other types later (in a nested block)
                        string inferred = boundMethod ? "dynamic" : rawValue != null ? null : InferType(value);
                        Declared(n.Id);
                        if (declType != null)
                        {
                            _w.Line($"{declType} {Esc(name)} = {rawValue ?? EmitExpr(value, targetTyped: declType != "dynamic")};");
                            SetType(n.Id, declType);
                        }
                        else if (rawValue == null && TypeInference.IsEmptyContainer(value))
                        {
                            // xs = [] / {} / set(): its element type comes from what the code adds to it later (whole-program inference)
                            char kind = TypeInference.ContainerKind(value);
                            _emptyContainers[n.Id] = kind;
                            string t = EmptyContainerType(_scopeKey + "|" + n.Id, kind, value);
                            if (t == null || t.Contains("dynamic") && "lsd".IndexOf(kind) >= 0)
                            {
                                _w.Line($"var {Esc(name)} = {EmitExpr(value)};");
                                // what that emits: a container of dynamic values ([] / {} / set())
                                SetType(n.Id, InferType(value) ?? (kind == 'l' ? "List<dynamic>" : kind == 'd' ? "OrderedDictionary<dynamic, dynamic>" : kind == 's' ? "HashSet<dynamic>" : null));
                            }
                            else { _w.Line($"{t} {Esc(name)} = {EmitEmptyContainer(value, t, kind)};"); SetType(n.Id, t); }
                            if (t != null && t.Contains(TypeInference.Pending)) SetType(n.Id, t);
                        }
                        else if (rawValue == null && value is LambdaExpr lam)
                        {
                            // f = lambda x: ...: a delegate typed by the calls through f (late-bound until they are known)
                            _lambdaLocals[n.Id] = lam;
                            string ftype = EmitLambdaLocal(lam, _scopeKey + "|λ" + n.Id, out string lambdaCode);
                            _w.Line($"{ftype} {Esc(name)} = {lambdaCode};");
                            SetType(n.Id, ftype);
                        }
                        else if (rawValue == null && NeedsExplicitType(value))
                        {
                            // x = None, then x = Node(...) later: the type of the later values (an object reference can be None)
                            string noneTyped = null;
                            if (IsNone(value))
                            {
                                _noneLocals.Add(_scopeKey + "|" + n.Id);
                                if (Ti != null && Ti.Hoisted.TryGetValue(_scopeKey + "|" + n.Id, out var later) && !TypeInference.IsUnknown(later) &&
                                    !later.Contains(TypeInference.Pending) && !IsValueTypeName(later.TrimEnd('?')) && !later.StartsWith("("))
                                    noneTyped = later;
                            }
                            string t = noneTyped ?? PyType(inferred) ?? ImplicitDynamic(stmt, $"variable '{n.Id}'");
                            _w.Line($"{t} {Esc(name)} = {EmitExpr(value, targetTyped: t != "dynamic")};");
                            SetType(n.Id, t);
                        }
                        else if (rawValue == null && (IsSmallInt(inferred) || _big && IsIntegral(inferred)))
                        {
                            // a .NET int (len of a .NET collection, str.Length, ...) stored in a Python variable is a Python int
                            // (in big-int mode every int variable is a BigInteger, whatever 64-bit value starts it)
                            _w.Line($"{IntT} {Esc(name)} = {EmitExpr(value)};");
                            SetType(n.Id, IntT);
                        }
                        else if (rawValue == null && IsDynamicallyDispatched(value) && PyType(inferred) is string known && !IsUnknownType(known) && known != "object")
                        {
                            // a call with a late-bound argument is dynamic to C#: the variable takes the type Typhon knows
                            _w.Line($"{known} {Esc(name)} = {EmitValue(value)};");
                            SetType(n.Id, known);
                        }
                        else if (inferred == "dynamic")
                        {
                            // a value Typhon binds late though C# knows its class (argparse.Namespace(a=1)): a dynamic variable
                            _w.Line($"dynamic {Esc(name)} = {rawValue ?? EmitValue(value)};");
                            SetType(n.Id, "dynamic");
                        }
                        else
                        {
                            _w.Line($"var {Esc(name)} = {rawValue ?? EmitValue(value)};");
                            // (a call dispatched dynamically whose result Typhon does not know: dynamic, as C# sees it)
                            SetType(n.Id, inferred ?? (rawValue == null && MaybeLateBound(value) ? "dynamic" : null));
                        }
                    }
                    else
                    {
                        string known = KnownType(n.Id) ?? (!IsLocal(n.Id) && _moduleFieldTypes.TryGetValue(n.Id, out var fieldType) ? fieldType : null);
                        string valueType = rawValue == null ? PyType(InferType(value)) : null;
                        if (Obs != null && rawValue == null && (IsHoistedLocal(n.Id) || _noneLocals.Contains(_scopeKey + "|" + n.Id)))
                            Obs.AddHoisted(_scopeKey + "|" + n.Id, IsNone(value) ? TypeInference.NoneType : valueType);
                        // rebinding to a value of another type (names = names.split(",")) in the block that declared the variable:
                        // every later use sees the new value, so it becomes a new C# variable
                        // rebinding elsewhere (a nested block, a hoisted variable): the variable needs a wider type
                        bool sameBlock = _blocks.Count > 0 && _blocks.Peek().Declared.Contains(n.Id);
                        bool incompatibleClass = known != valueType && ClassKeyOfType(known, out _) != null &&
                            ClassKeyOfType(valueType, out _) != null && !DerivesFromType(valueType, known);
                        bool differentFunction = known != valueType && FunctionOfType(known, out _, out _) && FunctionOfType(valueType, out _, out _);
                        bool extractsItem = valueType == "dynamic" && IsPythonContainer(known) &&
                            value is SubscriptExpr { Target: NameExpr source, Index: not SliceExpr } && source.Id == n.Id;
                        if (Obs != null && valueType != null && known != null && !sameBlock &&
                            (Incompatible(known, valueType) || incompatibleClass || differentFunction || extractsItem ||
                             known == "double" && IsIntegral(valueType) && valueType != "bool" && IsIntConversion(value)))
                            Obs.AddConflict(_scopeKey + "|" + n.Id, IsIntegral(known) && valueType == "double" ? "double" : "dynamic");
                        // total = total + x with a late-bound x on a number variable: the sum may be a float, the variable is late-bound
                        else if (Obs != null && valueType == "dynamic" && IsNumeric(known) && value is BinOpExpr vb && ArithmeticOps.Contains(vb.Op))
                            Obs.AddConflict(_scopeKey + "|" + n.Id, "dynamic");
                        // an object variable rebound to a late-bound value (args = _Options() ... args = table[name]): anything
                        else if (Obs != null && valueType == "dynamic" && known != null && ClassKeyOfType(known, out _) != null)
                            Obs.AddConflict(_scopeKey + "|" + n.Id, "dynamic");
                        // (x = int(x) on a float: from here on x is an int, so it gets a new variable too)
                        bool narrows = known == "double" && IsIntegral(valueType) && valueType != "bool";
                        if (valueType != null && (Incompatible(known, valueType) || incompatibleClass || differentFunction || extractsItem || narrows) && sameBlock)
                        {
                            string fresh = NewTmp(n.Id + "_");
                            _w.Line($"var {fresh} = {EmitValue(value)};");
                            _blocks.Peek().Renamed.Add(new KeyValuePair<string, string>(n.Id, _renames.TryGetValue(n.Id, out var prev) ? prev : null));
                            _renames[n.Id] = fresh;
                            SetType(n.Id, valueType);
                            return;
                        }
                        bool typed = known != null && known != "dynamic";
                        // a lambda into a late-bound variable or field: a delegate of dynamic values
                        if (rawValue == null && value is LambdaExpr dl && !typed) { string dtype = EmitLambdaLocal(dl, null, out var dcode); rawValue = $"({dtype}){dcode}"; }
                        _w.Line($"{EmitName(n)} = {rawValue ?? (typed ? EmitExpr(value, targetTyped: true) : EmitValue(value))};");
                    }
                    return;
                }
            }
        }

        void EmitAssignToSequence(Expr target, Expr value, Stmt stmt, string rawValue)
        {
            {
                {
                    var elts = target is TupleExpr te ? te.Elts : ((ListExpr)target).Elts;
                    int starIdx = elts.FindIndex(e => e is StarredExpr);
                    if (starIdx >= 0) { EmitStarredUnpack(elts, starIdx, value, stmt, rawValue); return; }
                    // a late-bound value (C# cannot deconstruct dynamic) or any other iterable (a, b = range(2) / xs / "ab"):
                    // its items by position, checked for the count
                    string unpackedType = rawValue == null && !(value is TupleExpr) ? InferType(value) : null;
                    if (rawValue == null && !(value is TupleExpr) &&
                        (IsDynamicallyDispatched(value) || value is SubscriptExpr sub && (LateBoundArg(sub.Target) || LateBoundArg(sub.Index))))
                        unpackedType = "dynamic";
                    // a call whose type Typhon cannot tell (it may be dynamic to C#): unpacked by position, which works for any
                    if (unpackedType == null && (rawValue != null && value == null ||
                        rawValue == null && value is CallExpr or SubscriptExpr or AttributeExpr))
                        unpackedType = "dynamic";
                    if (unpackedType == null && elts.Count < 2) unpackedType = "dynamic";
                    if (unpackedType != null && (elts.Count < 2 || unpackedType == "dynamic" || unpackedType == "object" ||
                        (TupleElementTypes(unpackedType) == null && !(unpackedType.StartsWith("global::Typhon.Runtime.PyTuple") && elts.Count <= 5) && !HasDeconstruct(unpackedType))))
                    {
                        // a late-bound value (a tuple, a list, a str, a dict at run time): its items by run-time type
                        bool late = IsUnknownType(unpackedType) || unpackedType == "object?";
                        string items = late
                            ? $"{Rt("__iter_dyn")}((object?){Paren(rawValue ?? EmitExpr(value))})"
                            : EmitIterable(value);
                        EmitUnpackedItems(elts, stmt, items, late ? "dynamic" : PyType(ElementTypeOf(unpackedType)));
                        return;
                    }
                    bool allInline = elts.All(e => e is NameExpr ne && IsInline(stmt, ne.Id) && !(_scope.Locals.Annotations.ContainsKey(ne.Id)));
                    // best, cand, d0 = None, None, 1: items C# cannot type in a deconstruction (None, a lambda) are assigned one by
                    // one; when a later item reads an earlier target, through temporaries first (Python evaluates the whole tuple)
                    // (attribute targets too: self.a, self.b = {}, 0 gives each field its typed value and records what objects
                    // get assigned from outside their class)
                    if (rawValue == null && value is TupleExpr nt && nt.Elts.Count == elts.Count && !nt.Elts.Any(x => x is StarredExpr) &&
                        elts.All(e => e is NameExpr || e is AttributeExpr) && (nt.Elts.Any(NeedsExplicitType) || elts.Any(e => e is AttributeExpr)))
                    {
                        var targetKeys = new HashSet<string>(elts.Select(e => DottedName(e)).Where(k => k != null));
                        bool reads = nt.Elts.Any(x => { bool r = false; AstWalker.VisitAll(x, nn => { if (nn is NameExpr rn && targetKeys.Contains(rn.Id) || nn is AttributeExpr ra && DottedName(ra) is string rk && targetKeys.Contains(rk)) r = true; }); return r; });
                        if (!reads) { for (int i = 0; i < elts.Count; i++) EmitAssignTo(elts[i], nt.Elts[i], stmt); return; }
                        if (elts.All(e => e is AttributeExpr))
                        {
                            // self.a, self.b = self.b, self.a: all values first
                            var tmps = nt.Elts.Select(x => { string tn = NewTmp("v"); _w.Line($"var {tn} = {EmitValue(x)};"); return tn; }).ToList();
                            for (int i = 0; i < elts.Count; i++) EmitAssignTo(elts[i], nt.Elts[i], stmt, tmps[i]);
                            return;
                        }
                    }
                    // xs[i], xs[-j] = xs[-j], xs[i]: subscript targets (positions from the end are method calls) take the
                    // items of a temporary, left to right, after the whole value is computed (as in Python)
                    if (elts.Any(e => e is SubscriptExpr) && elts.All(e => e is NameExpr || e is SubscriptExpr || e is AttributeExpr) &&
                        (value is TupleExpr || TupleElementTypes(InferType(value)) != null))
                    {
                        string tmp = NewTmp("t");
                        _w.Line($"var {tmp} = {rawValue ?? EmitExpr(value)};");
                        var vparts = value is TupleExpr vtt && vtt.Elts.Count == elts.Count ? vtt.Elts.Select(x => (Expr)x).ToList() : null;
                        for (int i = 0; i < elts.Count; i++)
                        {
                            EmitAssignTo(elts[i], vparts?[i], stmt, $"{tmp}.Item{i + 1}");
                            if (elts[i] is NameExpr tn && IsInline(stmt, tn.Id) && vparts != null) SetType(tn.Id, InferType(vparts[i]));
                        }
                        return;
                    }
                    string valueCode = rawValue ?? EmitExpr(value);
                    if (allInline) _w.Line($"var ({string.Join(", ", elts.Select(e => Esc(((NameExpr)e).Id)))}) = {valueCode};");
                    else _w.Line($"{DeconstructionTarget(target, stmt)} = {valueCode};");
                    if (value is TupleExpr vt && vt.Elts.Count == elts.Count)
                    {
                        for (int i = 0; i < elts.Count; i++)
                            if (elts[i] is NameExpr ne && IsInline(stmt, ne.Id)) SetType(ne.Id, InferType(vt.Elts[i]));
                    }
                    else if (rawValue == null && UnpackPartTypes(InferType(value), elts.Count) is List<string> parts)
                    {
                        // i, u = i_u where i_u is a (long, List<double>): the names get the tuple's element types
                        for (int i = 0; i < elts.Count; i++)
                            if (elts[i] is NameExpr ne && IsInline(stmt, ne.Id)) SetType(ne.Id, parts[i]);
                    }
                    return;
                }
            }
        }

        void EmitUnpackedItems(List<Expr> targets, Stmt stmt, string items, string itemType)
        {
            string sequence = NewTmp("u");
            _w.Line($"var {sequence} = {Rt("__unpack_items")}({items}, {targets.Count});");
            for (int i = 0; i < targets.Count; i++)
            {
                EmitAssignTo(targets[i], null, stmt, $"{sequence}[{i}]");
                if (targets[i] is NameExpr name && IsInline(stmt, name.Id)) SetType(name.Id, itemType);
            }
        }

        void EmitAssignToOther(Expr target, Expr value, Stmt stmt, string rawValue)
        {
            switch (target)
            {
                case AttributeExpr at:
                {
                    if (at.Target is NameExpr owner && IsClassArgument(owner.Id) || IsInstanceClassDefault(at))
                    {
                        string temporary = NewTmp("value");
                        _w.Line($"object? {temporary} = {rawValue ?? EmitLooseValue(value)};");
                        _w.Line($"{Rt("setattr")}((object?){Paren(EmitExpr(at.Target))}, {CsString(at.Name)}, {temporary});");
                        return;
                    }
                    if (InstanceMethodOwner(at, out var shadowClass) != null)
                    {
                        if (Obs != null && shadowClass != null)
                            Obs.AddExternal(shadowClass + "|" + at.Name, "dynamic");
                        _w.Line($"{Rt("setattr")}((object?){Paren(EmitExpr(at.Target))}, {CsString(at.Name)}, (object?){Paren(rawValue ?? EmitLooseValue(value))});");
                        return;
                    }
                    if (Obs != null && rawValue == null) RecordExternalAttribute(at, value);
                    string ft = null;
                    if (at.Target is NameExpr tn && tn.Id == "self" && _cls != null) ft = InferType(at);
                    if (rawValue == null && ft?.StartsWith("global::Typhon.Runtime.deque<") == true &&
                        value is CallExpr deque && deque.Args.All(a => a.Name == "maxlen" && !a.Star && !a.DoubleStar) &&
                        StdlibModuleOf(deque, out string factory) == "Typhon.Stdlib.collections" && factory == "deque")
                        rawValue = EmitEmptyContainer(value, ft, 'q');
                    if (rawValue == null && value is ComprehensionExpr comp && ft != null && ft != "dynamic"
                        && !TypeInference.IsNonePlaceholder(comp))
                        rawValue = EmitComprehension(comp, ft);
                    string code = rawValue ?? (IsUnknownType(ft) ? BoundMethodValue(value) : null) ??
                        (TypeInference.IsNonePlaceholder(value) && ft != null && ft.StartsWith("List<")
                        ? EmitNonePlaceholder(value, ft) : EmitExpr(value, targetTyped: ft != null && ft != "dynamic"));
                    if (rawValue == null && NarrowingForMember(at, value) is string narrowTo) code = $"checked(({narrowTo})({code}))";
                    _w.Line($"{EmitExpr(at)} = {code};");
                    return;
                }
                case SubscriptExpr sub when sub.Index is SliceExpr sl:
                {
                    // xs[a:b] = ys replaces that part of the list (extended slices need as many values as positions)
                    string tgt = EmitExpr(sub.Target);
                    if (sub.Target is ConstExpr || sub.Target is BinOpExpr || sub.Target is UnaryExpr) tgt = Paren(tgt);
                    string lo = sl.Lower != null ? SliceBound(sl.Lower) : "null", hi = sl.Upper != null ? SliceBound(sl.Upper) : "null";
                    _w.Line($"{Rt("setslice")}({tgt}, {lo}, {hi}, {(sl.Step != null ? SliceBound(sl.Step) : "1")}, {rawValue ?? EmitIterable(value)});");
                    return;
                }
                case SubscriptExpr sub:
                {
                    if (Obs != null && rawValue == null && ElementKey(sub.Target, out char kind) is string ekey)
                        Obs.AddElement(ekey, TypeInference.IsMappingKind(kind)
                            ? (PyType(InferType(sub.Index)) ?? TypeInference.Unknown) + "→" + (kind == 'c' ? "long" : PyType(InferType(value)) ?? TypeInference.Unknown)
                            : PyType(InferType(value)));
                    string et = InferType(sub);
                    // xs[i] = [v] into a list of typed lists: the literal is built with that item type (C# lists are invariant)
                    if (rawValue == null && value is ListExpr sle && sle.Elts.Count > 0 && !sle.Elts.Any(x => x is StarredExpr) && et != null &&
                        et.StartsWith("List<") && !et.Contains("dynamic") && !et.Contains(TypeInference.Pending) && InferType(value) != et)
                        rawValue = $"new {et} {{ {string.Join(", ", sle.Elts.Select(x => EmitElementAs(x, GenericArg(et))))} }}";
                    if (InferType(sub.Index) == "global::Typhon.Runtime.PySlice" ||
                        (InferType(sub.Target) == "dynamic" || InferType(sub.Target) == null && InferType(sub.Index) == "dynamic") &&
                        (!(sub.Index is TupleExpr) || InferType(sub.Target) == "dynamic" && sub.Index is TupleExpr stk && !stk.Elts.Any(x => x is SliceExpr)))
                    {
                        // (a[x, y] = v: the key is the tuple (x, y))
                        string key = sub.Index is TupleExpr kt && !kt.Parenthesized ? EmitExpr(new TupleExpr { Elts = kt.Elts, Parenthesized = true, Line = kt.Line, Col = kt.Col }) : ModuleOrClassValue(sub.Index) ?? EmitExpr(sub.Index);
                        _w.Line($"{Rt("__setitem")}((object?){Paren(EmitExpr(sub.Target))}, (object?){Paren(key)}, (object?){Paren(rawValue ?? EmitValue(value))});");
                        return;
                    }
                    _w.Line($"{EmitExpr(sub)} = {rawValue ?? (et == null || et == "dynamic" ? EmitValue(value) : EmitExpr(value, targetTyped: IsEmptyLiteral(value)))};");
                    return;
                }
                case StarredExpr _:
                    Unsupported(target, "starred assignment targets");
                    return;
                default:
                    Error(target, "cannot assign to this expression");
                    return;
            }
        }

        /// <summary>A call C# binds at run time (a late-bound argument or receiver): its C# type is dynamic.</summary>
        bool IsDynamicallyDispatched(Expr e) =>
            e is CallExpr c && (c.Args.Any(a => InferType(a.Value) == "dynamic") || c.Func is AttributeExpr fa && InferType(fa.Target) == "dynamic");

        /// <summary>Whether a value of this C# type deconstructs into its items (C# tuple syntax); other iterables unpack by position.</summary>
        bool HasDeconstruct(string t) =>
            t.Contains("KeyValuePair<") || t.StartsWith("ValueTuple<") || IsNamedTupleType(t);

        /// <summary>first, *rest, last = xs: the sequence is materialized once, then each target takes its part (Python checks the count).</summary>
        void EmitStarredUnpack(List<Expr> elts, int starIdx, Expr value, Stmt stmt, string rawValue)
        {
            string seq = NewTmp("u");
            _w.Line($"var {seq} = {Rt("list")}({rawValue ?? EmitIterable(value)});");
            int fixedCount = elts.Count - 1;
            _w.Line($"if ({seq}.Count < {fixedCount}) throw new global::System.ArgumentException(\"not enough values to unpack (expected at least {fixedCount}, got \" + {seq}.Count + \")\");");
            for (int i = 0; i < elts.Count; i++)
            {
                if (i == starIdx) EmitAssignTo(((StarredExpr)elts[i]).Value, value, stmt, $"{seq}.GetRange({starIdx}, {seq}.Count - {fixedCount})");
                else EmitAssignTo(elts[i], value, stmt, i < starIdx ? $"{seq}[{i}]" : $"{seq}[{seq}.Count - {elts.Count - i}]");
            }
        }

        /// <summary>'var x = null' / 'var x = []' have no natural type in C#.</summary>
        static bool NeedsExplicitType(Expr value)
        {
            if (value is ConstExpr c && c.Kind == ConstKind.None) return true;
            if (value is LambdaExpr) return true;
            if (value is ListExpr l && l.Elts.Count == 0) return true;
            if (value is DictExpr d && d.Keys.Count == 0) return true;
            return false;
        }

        string DeconstructionTarget(Expr target, Stmt stmt)
        {
            switch (target)
            {
                case NameExpr n:
                    if (IsInline(stmt, n.Id))
                    {
                        if (_scope.Locals.Annotations.TryGetValue(n.Id, out var ann)) { var t = MapType(ann, stmt); SetType(n.Id, t); return t + " " + Esc(n.Id); }
                        return "var " + Esc(n.Id);
                    }
                    return EmitName(n);
                case TupleExpr t: return "(" + string.Join(", ", t.Elts.Select(e => DeconstructionTarget(e, stmt))) + ")";
                case ListExpr l: return "(" + string.Join(", ", l.Elts.Select(e => DeconstructionTarget(e, stmt))) + ")";
                default: return EmitExpr(target);
            }
        }

        static readonly Dictionary<string, string> AugHelpers = new Dictionary<string, string>
        {
            ["/"] = "truediv", ["//"] = "floordiv", ["**"] = "pow", ["%"] = "mod", ["<<"] = "lshift", [">>"] = "rshift",
        };

        void EmitAugAssign(AugAssignStmt a)
        {
            if (a.Target is AttributeExpr member &&
                (member.Target is NameExpr owner && IsClassArgument(owner.Id) || IsInstanceClassDefault(member)))
            {
                string receiver = NewTmp("receiver"), name = CsString(member.Name);
                _w.Line($"object? {receiver} = {EmitExpr(member.Target)};");
                string current = $"{Rt("getattr")}({receiver}, {name})", right = EmitExpr(a.Value);
                string combined = a.Op == "+" ? $"{Rt("__iadd")}({current}, {right})" : a.Op == "*" ? $"{Rt("mul")}({current}, {right})"
                    : AugHelpers.TryGetValue(a.Op, out var operation) ? $"{Rt(operation)}({current}, {right})" : $"({current} {a.Op} {Paren(right)})";
                _w.Line($"{Rt("setattr")}({receiver}, {name}, (object?){Paren(combined)});");
                return;
            }
            // d[k] += v on a late-bound container: read, combine, write back (the container and key evaluated once)
            if (a.Target is SubscriptExpr ds && !(ds.Index is SliceExpr) && !(ds.Index is TupleExpr) && InferType(ds.Target) == "dynamic")
            {
                string c = NewTmp("c"), k = NewTmp("k");
                _w.Line($"object? {c} = {EmitExpr(ds.Target)}, {k} = {ModuleOrClassValue(ds.Index) ?? EmitExpr(ds.Index)};");
                string cur = $"{Rt("__getitem")}({c}, {k})", val = EmitExpr(a.Value);
                string combined = a.Op == "+" ? $"{Rt("add")}({cur}, {val})" : a.Op == "*" ? $"{Rt("mul")}({cur}, {val})"
                    : AugHelpers.TryGetValue(a.Op, out var helper) ? $"{Rt(helper)}({cur}, {val})" : $"({cur} {a.Op} {Paren(val)})";
                _w.Line($"{Rt("__setitem")}({c}, {k}, (object?){Paren(combined)});");
                return;
            }
            string target = EmitExpr(a.Target);
            string value = EmitExpr(a.Value);
            string tt = InferType(a.Target), vt = InferType(a.Value);
            // n += 0.5 / n /= 2 on an int variable: it becomes a float
            if (Obs != null && a.Target is NameExpr an && IsIntegral(tt) && tt != "bool" && (a.Op == "/" || vt == "double" && ArithmeticOps.Contains(a.Op)))
                Obs.AddConflict(_scopeKey + "|" + an.Id, "double");
            // n += x with a late-bound x: the result may be a float (or anything): the variable is late-bound too
            else if (Obs != null && a.Target is NameExpr an2 && IsNumeric(tt) && vt == "dynamic" && ArithmeticOps.Contains(a.Op))
                Obs.AddConflict(_scopeKey + "|" + an2.Id, "dynamic");
            // sets and dicts update in place: s |= t, s -= t, s &= t, s ^= t, d |= e
            if (IsSetType(tt) && (a.Op == "|" || a.Op == "-" || a.Op == "&" || a.Op == "^"))
            {
                string method = a.Op == "|" ? "update" : a.Op == "-" ? "difference_update" : a.Op == "&" ? "intersection_update" : "symmetric_difference_update";
                _w.Line($"{target}.{method}({EmitIterable(a.Value)});");
                return;
            }
            if (IsDictType(tt) && a.Op == "|") { _w.Line($"{target}.update({value});"); return; }
            // xs += [...] on a list that started empty: its element types
            if (Obs != null && a.Op == "+" && ElementKey(a.Target, out char lkind) is string lkey && (lkind == 'l' || lkind == 'q'))
                Obs.AddElement(lkey, a.Value is ConstExpr ls && ls.Kind == ConstKind.Str || vt == "string" ? "string" : PyType(ElementTypeOf(vt)) ?? TypeInference.Unknown);
            // d[k] += x / counts[word] += 1 on a container that started empty: the key (and value) types
            if (Obs != null && a.Target is SubscriptExpr asub && ElementKey(asub.Target, out char akind) is string akey && TypeInference.IsMappingKind(akind))
                Obs.AddElement(akey, (PyType(InferType(asub.Index)) ?? TypeInference.Unknown) + "→" + (akind == 'c' ? "long" : PyType(vt) ?? TypeInference.Unknown));
            if (vt == "bool" && ArithmeticOps.Contains(a.Op)) value = $"({Paren(value)} ? 1L : 0L)";
            if ((a.Op == "//" || a.Op == "%") && PowerOfTwoExponent(a.Value) > 0 && IsIntegral(InferType(a.Target)))
                _w.Line($"{target} = {EmitBinOp(new BinOpExpr { Left = a.Target, Op = a.Op, Right = a.Value, Line = a.Line, Col = a.Col })};");
            else if (AugHelpers.TryGetValue(a.Op, out var helper)) _w.Line($"{target} = {Rt(helper)}({target}, {value});");
            else if (a.Op == "@") _w.Line($"{target} = {target}.__matmul__({value});");
            // list += iterable mutates the list in place (aliases see it), whatever the iterable is
            else if (a.Op == "+" && (IsListy(a.Target) || tt != null && tt.StartsWith("List<")))
            {
                // (items of another type, e.g. List<dynamic> += List<long>: converted one by one)
                string te = ElementTypeOf(tt), ve = a.Value is ConstExpr vs && vs.Kind == ConstKind.Str || vt == "string" ? "string" : ElementTypeOf(vt);
                bool cast = te != null && ve != null && te != ve && ve != TypeInference.Pending && te != TypeInference.Pending;
                // the static call: an extension method cannot be dispatched dynamically (a late-bound value in the list)
                _w.Line(cast ? $"{Rt("__extend_cast")}({target}, {EmitIterable(a.Value)});" : $"global::Typhon.Runtime.Builtins.extend({target}, {EmitIterable(a.Value)});");
            }
            else if (a.Op == "+" && tt == "dynamic" && !IsNumeric(vt)) _w.Line($"{target} = {Rt("__iadd")}({target}, {value});");
            // String repetition returns a new string, including when a late-bound count is rebound to it.
            else if (a.Op == "*" && a.Target is NameExpr &&
                (tt == "dynamic" && vt == "string" || tt == "string" && (IsIntegral(vt) || vt == "dynamic")))
                _w.Line($"{target} = {Rt("mul")}({target}, {value});");
            else _w.Line($"{target} {a.Op}= {value};");
        }

        void EmitAnnAssign(AnnAssignStmt a)
        {
            // x: list = [] / freq: defaultdict = defaultdict(int): a bare container annotation adds nothing, the value decides
            if (a.Target is NameExpr bn && a.Value != null && TypeInference.IsBareContainer(a.Annotation))
            {
                EmitAssignTo(a.Target, a.Value, a);
                return;
            }
            if (a.Target is NameExpr n)
            {
                string type = MapType(a.Annotation, a);
                if (IsInline(a, n.Id))
                {
                    _w.Line($"{type} {Esc(n.Id)} = {(a.Value is ComprehensionExpr ac ? EmitComprehension(ac, type) : a.Value != null ? EmitExpr(a.Value, targetTyped: type != "dynamic") : "default")};");
                    SetType(n.Id, type);
                }
                else if (a.Value != null) _w.Line($"{EmitName(n)} = {(a.Value is ComprehensionExpr ac2 ? EmitComprehension(ac2, type) : EmitExpr(a.Value, targetTyped: type != "dynamic"))};");
                return;
            }
            if (a.Target is AttributeExpr at)
            {
                if (a.Value != null)
                {
                    if (at.Target is NameExpr owner && IsClassArgument(owner.Id) || IsInstanceClassDefault(at)) EmitAssignTo(at, a.Value, a);
                    else _w.Line($"{EmitExpr(at)} = {EmitExpr(a.Value, targetTyped: true)};");
                }
                return;
            }
            Unsupported(a, "this annotated assignment target");
        }
    }
}
