// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        readonly Dictionary<string, string> _renames = new Dictionary<string, string>();

        string NewTmp(string hint = "t") => "__" + hint + (_tmp++);

        bool IsInline(Node stmt, string name) => _scope?.Locals != null && _scope.Locals.IsInline(stmt, name);

        /// <summary>Per block being emitted: the names it declares, and the renames (rebinding to another type) to undo at its end.</summary>
        sealed class BlockFrame
        {
            public readonly HashSet<string> Declared = new HashSet<string>();
            public readonly List<KeyValuePair<string, string>> Renamed = new List<KeyValuePair<string, string>>();
        }

        readonly Stack<BlockFrame> _blocks = new Stack<BlockFrame>();

        void EmitBlockStatements(List<Stmt> stmts, string nonNegativeUntilAssigned = null, IEnumerable<string> preDeclared = null, string narrowed = null)
        {
            var frame = new BlockFrame();
            if (preDeclared != null) foreach (var p in preDeclared) frame.Declared.Add(p);
            _blocks.Push(frame);
            // a name known non-negative on entry (while i < j: ... j > i >= 0) stays so up to the first statement that rebinds it;
            // likewise an optional value known to be there (if x is not None:)
            bool guarding = nonNegativeUntilAssigned != null && _nonNegative.Add(nonNegativeUntilAssigned);
            bool narrowing = narrowed != null && _narrowed.Add(narrowed);
            foreach (var s in stmts)
            {
                if (guarding && BindsName(s, nonNegativeUntilAssigned)) { _nonNegative.Remove(nonNegativeUntilAssigned); guarding = false; }
                if (narrowing && BindsName(s, narrowed)) { _narrowed.Remove(narrowed); narrowing = false; }
                EmitStmt(s);
            }
            if (guarding) _nonNegative.Remove(nonNegativeUntilAssigned);
            if (narrowing) _narrowed.Remove(narrowed);
            _blocks.Pop();
            for (int i = frame.Renamed.Count - 1; i >= 0; i--)
            {
                var kv = frame.Renamed[i];
                if (kv.Value == null) _renames.Remove(kv.Key); else _renames[kv.Key] = kv.Value;
            }
        }

        void Declared(string name) { if (_blocks.Count > 0) _blocks.Peek().Declared.Add(name); }

        /// <summary>Can a value of type v not be stored in a variable of type t? (only clear cases: Python value types that differ)</summary>
        internal static bool Incompatible(string t, string v)
        {
            if (t == null || v == null || t == v || t == "dynamic" || v == "dynamic" || t == "object" || t == TypeInference.Pending || v == TypeInference.Pending) return false;
            if (IsIntegral(t) && IsIntegral(v) || t == "double" && IsIntegral(v) || t == v + "?") return false;
            bool Plain(string x) => PrimitiveCsTypes.Contains(x) || x.StartsWith("List<") || x.StartsWith("HashSet<") || IsDictType(x) || x.StartsWith("(") ||
                                    x.StartsWith("global::Typhon.Runtime.PyTuple<");
            return Plain(t) && Plain(v);
        }

        void EmitBlock(List<Stmt> stmts, string prologue = null, string nonNegativeUntilAssigned = null, string narrowed = null)
        {
            _w.Open();
            if (prologue != null) _w.Line(prologue);
            EmitBlockStatements(stmts, nonNegativeUntilAssigned, narrowed: narrowed);
            _w.Close();
        }

        void EmitStmt(Stmt s)
        {
            LineDirective(s);
            switch (s)
            {
                case ExprStmt e: EmitExprStmt(e); break;
                case AssignStmt a: EmitAssign(a); break;
                case AugAssignStmt a: EmitAugAssign(a); break;
                case AnnAssignStmt a: EmitAnnAssign(a); break;
                case PassStmt _: break;
                case BreakStmt _:
                    // leaving a loop that has an 'else' marks it as broken out of, so its else block is skipped
                    if (_breakFlags.Count > 0 && _breakFlags.Peek() != null) _w.Line($"{_breakFlags.Peek()} = true;");
                    _w.Line("break;");
                    break;
                case ContinueStmt _: _w.Line("continue;"); break;
                case ReturnStmt r: EmitReturn(r); break;
                case RaiseStmt r: EmitRaise(r); break;
                case GlobalStmt _: break;
                case ImportStmt _: break;
                case ImportFromStmt _: break;
                case AssertStmt a:
                    _w.Line($"if (!{Paren(Cond(a.Test))}) throw new global::Typhon.Runtime.AssertionError({(a.Msg != null ? "str(" + EmitExpr(a.Msg) + ")" : "\"assertion failed\"")});");
                    break;
                case DelStmt d: EmitDel(d); break;
                case IfStmt i: EmitIf(i, isElif: false); break;
                case WhileStmt w:
                {
                    string flag = LoopElseFlag(w.OrElse, w.Body);
                    _w.Line($"while ({Cond(w.Test)})");
                    _breakFlags.Push(flag);
                    EmitBlock(w.Body, nonNegativeUntilAssigned: WhileGuardedName(w.Test), narrowed: NarrowedBy(w.Test, whenTrue: true));
                    _breakFlags.Pop();
                    EmitLoopElse(w.OrElse, flag);
                    break;
                }
                case ForStmt f: EmitFor(f); break;
                case MatchStmt m: EmitMatch(m); break;
                case TryStmt t: EmitTry(t); break;
                case WithStmt w: EmitWith(w); break;
                case FunctionDef f: EmitLocalFunction(f); break;
                case ClassDef c: Unsupported(c, "classes defined inside functions"); break;
                default: Unsupported(s, s.GetType().Name); break;
            }
        }

        void EmitExprStmt(ExprStmt e)
        {
            // d.sort() / d.append(x) on a late-bound value as a statement: the method may return nothing (void), so an if
            // statement, not a conditional expression, chooses between the runtime's method and the object's own
            if (e.Value is CallExpr dc && dc.Func is AttributeExpr dm && InferType(dm.Target) == "dynamic" && !dc.Args.Any(a => a.Star || a.DoubleStar) && IsRuntimeMethod(dm.Name, dc.Args))
            {
                if (Obs != null) RecordCall(CallTarget(dc, out _), dc.Args);
                string r = NewTmp("r");
                string rest = EmitArgs(dc.Args, dynamicCall: true);
                _w.Line($"{{ dynamic {r} = {EmitExpr(dm.Target)}; if ({Rt("__is_pyvalue")}({r})) global::Typhon.Runtime.Builtins.{Esc(dm.Name)}({r}{(rest.Length > 0 ? ", " : "")}{rest}); else {r}.{Esc(dm.Name)}({rest}); }}");
                return;
            }
            switch (e.Value)
            {
                case ConstExpr c when c.Kind == ConstKind.Str || c.Kind == ConstKind.Ellipsis: return; // docstring / ...
                case YieldExpr y:
                    if (y.From)
                    {
                        string t = NewTmp("y");
                        _w.Line($"foreach (var {t} in {EmitExpr(y.Value)}) yield return {t};");
                    }
                    else EmitYieldValue(y);
                    return;
                case CallExpr _:
                case AwaitExpr _:
                    _w.Line(StripParens(EmitExpr(e.Value)) + ";");
                    return;
                default:
                    _w.Line($"_ = {EmitExpr(e.Value)};");
                    return;
            }
        }

        static string StripParens(string code)
        {
            if (code.StartsWith("(await ") && code.EndsWith(")")) return code.Substring(1, code.Length - 2);
            return code;
        }

        // ───────────────────────────── assignment ─────────────────────────────

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
        string EmitYieldValue(YieldExpr y)
        {
            _w.Line($"yield return {(y.Value == null ? "default!" : _scope?.ReturnType == "dynamic" ? EmitValue(y.Value) : EmitExpr(y.Value, targetTyped: true))};");
            if (_scope == null || !_scope.ReceivesValues) return null;
            string sent = NewTmp("sent");
            _w.Line($"dynamic {sent} = {GenVar}.__resume();");
            SetType(sent, "dynamic");
            return sent;
        }

        void EmitAssign(AssignStmt a)
        {
            // x = yield v: the value send() passes in (late-bound: a generator can be sent anything)
            if (a.Value is YieldExpr ay && !ay.From && _scope != null && _scope.ReceivesValues)
            {
                string sent = EmitYieldValue(ay);
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

        /// <summary>A function name being assigned to a variable (its function type is kept).</summary>
        bool _fnTypedUse;

        void EmitAssignToName(NameExpr n, Expr value, Stmt stmt, string rawValue)
        {
            // f = obj.method of a .NET object: a callable bound method (a method group has no type C# can store here)
            if (rawValue == null && value is AttributeExpr bm && IsDotNetMethod(bm)) rawValue = $"(dynamic)new global::Typhon.Runtime.PyBoundMethod((object){Paren(EmitExpr(bm.Target))}, {CsString(bm.Name)})";
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
                        string inferred = rawValue != null ? null : InferType(value);
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
                        else
                        {
                            _w.Line($"var {Esc(name)} = {rawValue ?? EmitValue(value)};");
                            // (a call dispatched dynamically whose result Typhon does not know: dynamic, as C# sees it)
                            SetType(n.Id, inferred ?? (rawValue == null && IsDynamicallyDispatched(value) ? "dynamic" : null));
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
                        if (Obs != null && valueType != null && known != null && !sameBlock &&
                            (Incompatible(known, valueType) || known == "double" && IsIntegral(valueType) && valueType != "bool" && IsIntConversion(value)))
                            Obs.AddConflict(_scopeKey + "|" + n.Id, IsIntegral(known) && valueType == "double" ? "double" : "dynamic");
                        // total = total + x with a late-bound x on a number variable: the sum may be a float, the variable is late-bound
                        else if (Obs != null && valueType == "dynamic" && IsNumeric(known) && value is BinOpExpr vb && ArithmeticOps.Contains(vb.Op))
                            Obs.AddConflict(_scopeKey + "|" + n.Id, "dynamic");
                        // an object variable rebound to a late-bound value (args = _Options() ... args = table[name]): anything
                        else if (Obs != null && valueType == "dynamic" && known != null && ClassKeyOfType(known, out _) != null)
                            Obs.AddConflict(_scopeKey + "|" + n.Id, "dynamic");
                        // (x = int(x) on a float: from here on x is an int, so it gets a new variable too)
                        bool narrows = known == "double" && IsIntegral(valueType) && valueType != "bool";
                        if (valueType != null && (Incompatible(known, valueType) || narrows) && _blocks.Count > 0 && _blocks.Peek().Declared.Contains(n.Id))
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
                    if (rawValue == null && !(value is TupleExpr) && IsDynamicallyDispatched(value)) unpackedType = "dynamic"; // divmod(late_bound, n): C# sees dynamic
                    // a call whose type Typhon cannot tell (it may be dynamic to C#): unpacked by position, which works for any
                    if (rawValue == null && value is CallExpr && unpackedType == null) unpackedType = "dynamic";
                    if (unpackedType != null && (unpackedType == "dynamic" || unpackedType == "object" ||
                        (TupleElementTypes(unpackedType) == null && !(unpackedType.StartsWith("global::Typhon.Runtime.PyTuple") && elts.Count <= 5) && !HasDeconstruct(unpackedType))))
                    {
                        string seq = NewTmp("u");
                        // a late-bound value (a tuple, a list, a str, a dict at run time): its items by run-time type
                        string items = IsUnknownType(unpackedType) || unpackedType == "object?" ? $"{Rt("__iter_dyn")}((object?){Paren(EmitExpr(value))})" : EmitIterable(value);
                        _w.Line($"var {seq} = {Rt("list")}({items});");
                        _w.Line($"if ({seq}.Count != {elts.Count}) throw new global::System.ArgumentException(\"expected {elts.Count} values to unpack, got \" + {seq}.Count);");
                        string itemType = IsUnknownType(unpackedType) || unpackedType == "object?" ? "dynamic" : PyType(ElementTypeOf(unpackedType));
                        for (int i = 0; i < elts.Count; i++)
                        {
                            EmitAssignTo(elts[i], value, stmt, $"{seq}[{i}]");
                            if (elts[i] is NameExpr un && IsInline(stmt, un.Id)) SetType(un.Id, itemType);
                        }
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

        void EmitAssignToOther(Expr target, Expr value, Stmt stmt, string rawValue)
        {
            switch (target)
            {
                case AttributeExpr at:
                {
                    if (Obs != null && rawValue == null) RecordExternalAttribute(at, value);
                    string ft = null;
                    if (at.Target is NameExpr tn && tn.Id == "self" && _cls != null) _cls.FieldTypes.TryGetValue(at.Name, out ft);
                    string code = rawValue ?? (TypeInference.IsNonePlaceholder(value) && ft != null && ft.StartsWith("List<")
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
                    if ((InferType(sub.Target) == "dynamic" || InferType(sub.Target) == null && InferType(sub.Index) == "dynamic") &&
                        (!(sub.Index is TupleExpr) || InferType(sub.Target) == "dynamic" && sub.Index is TupleExpr stk && !stk.Elts.Any(x => x is SliceExpr)))
                    {
                        // (a[x, y] = v: the key is the tuple (x, y))
                        string key = sub.Index is TupleExpr kt && !kt.Parenthesized ? EmitExpr(new TupleExpr { Elts = kt.Elts, Parenthesized = true, Line = kt.Line, Col = kt.Col }) : EmitExpr(sub.Index);
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

        // ───────────────────────────── match (PEP 634) ─────────────────────────────

        /// <summary>
        /// match → an if/else-if chain over a temporary holding the subject. Each case is one condition: the pattern's tests
        /// with its captures bound inside it (__bind returns true), then the guard, so the guard sees the captures.
        /// </summary>
        void EmitMatch(MatchStmt m)
        {
            string subj = NewTmp("m");
            string subjType = InferType(m.Subject);
            _w.Line($"var {subj} = {EmitValue(m.Subject)};");
            bool first = true;
            foreach (var c in m.Cases)
            {
                LineDirective(c);
                string cond = PatternTest(c.Pattern, subj, subjType);
                if (c.Guard != null) cond = Conj(cond, Paren(Cond(c.Guard)));
                _w.Line($"{(first ? "if" : "else if")} ({cond})");
                EmitBlock(c.Body);
                first = false;
            }
        }

        static string Conj(params string[] parts)
        {
            var ps = parts.Where(x => x != "true").ToList();
            if (ps.Contains("false")) return "false";
            return ps.Count == 0 ? "true" : string.Join(" && ", ps.Select(x => x.Contains("||") && !(x.StartsWith("(") && x.EndsWith(")")) ? "(" + x + ")" : x));
        }

        string Bind(string name, string value) => $"{Rt("__bind")}(out {EmitName(new NameExpr { Id = name })}, {value})";

        static readonly HashSet<string> PrimitiveCsTypes = new HashSet<string> { "long", "int", "double", "float", "bool", "string", "char", "decimal" };

        /// <summary>A C# condition that is true when the value v (of C# type vt, null if unknown) matches the pattern; binds its captures.</summary>
        string PatternTest(Pattern p, string v, string vt)
        {
            switch (p)
            {
                case CapturePattern c: return c.Name == null ? "true" : Bind(c.Name, v);
                case AsPattern a: return Conj(PatternTest(a.Inner, v, vt), Bind(a.Name, v));
                case OrPattern o: return "(" + string.Join(" || ", o.Alternatives.Select(alt => Paren(PatternTest(alt, v, vt)))) + ")";
                case ValuePattern val: return ValueTest(val.Value, v, vt);
                case SequencePattern sq: return SequenceTest(sq, v, vt);
                case MappingPattern mp: return MappingTest(mp, v, vt);
                case ClassPattern cp: return ClassTest(cp, v, vt);
            }
            Unsupported(p, "this pattern");
            return "false";
        }

        string ValueTest(Expr value, string v, string vt)
        {
            if (value is ConstExpr none && none.Kind == ConstKind.None) return $"({v} is null)";
            bool untyped = vt == null || vt == "dynamic" || vt == "object";
            if (value is ConstExpr b && b.Kind == ConstKind.Bool) // compared by identity: 1 does not match True
                return vt == "bool" ? (b.Raw == "True" ? v : "!" + v) : untyped ? $"({v} is {(b.Raw == "True" ? "true" : "false")})" : "false";
            string lt = InferType(value);
            bool primitive = vt != null && PrimitiveCsTypes.Contains(vt) && lt != null && PrimitiveCsTypes.Contains(lt);
            if (primitive && (vt == lt || IsIntegral(vt) && IsIntegral(lt) || (vt == "double" && IsIntegral(lt))))
                return $"({v} == {EmitValue(value)})";
            if (primitive) return vt == "string" || lt == "string" ? "false" : $"({v} == {EmitValue(value)})";
            return $"{Rt("eq")}({v}, {EmitValue(value)})";
        }

        string SequenceTest(SequencePattern sq, string v, string vt)
        {
            int n = sq.Items.Count;
            bool star = sq.StarIndex >= 0;
            if (vt == "string" || vt != null && PrimitiveCsTypes.Contains(vt)) return "false"; // a str is not a sequence for patterns
            var tupleTypes = TupleElementTypes(vt);
            if (tupleTypes != null && !star)
                return tupleTypes.Count != n ? "false" : Conj(sq.Items.Select((it, i) => PatternTest(it, $"{v}.Item{i + 1}", tupleTypes[i])).ToArray());
            string seqVar = v, count, et = null;
            var parts = new List<string>();
            if (vt != null && (vt.StartsWith("List<") || vt.StartsWith("IList<") || vt.StartsWith("IReadOnlyList<") || vt.EndsWith("[]")))
            {
                et = ElementTypeOf(vt);
                count = vt.EndsWith("[]") ? $"{v}.Length" : $"{v}.Count";
                parts.Add($"{v} is not null");
            }
            else
            {
                seqVar = NewTmp("seq");
                parts.Add($"{Rt("__match_seq")}({v}, out var {seqVar})");
                count = $"{seqVar}.Count";
            }
            parts.Add(star ? $"{count} >= {n}" : $"{count} == {n}");
            for (int i = 0; i < n; i++)
                parts.Add(PatternTest(sq.Items[i], !star || i < sq.StarIndex ? $"{seqVar}[{i}]" : $"{seqVar}[{count} - {n - i}]", et));
            if (star && sq.StarName != null) parts.Add(Bind(sq.StarName, $"{Rt("slice")}({seqVar}, {sq.StarIndex}, {count} - {n - sq.StarIndex})"));
            return Conj(parts.ToArray());
        }

        string MappingTest(MappingPattern mp, string v, string vt)
        {
            var parts = new List<string>();
            bool typed = IsDictType(vt);
            string map = v, valueType = typed ? SplitTypeArgs(GenericArg(vt))[1] : null;
            if (typed) parts.Add($"{v} is not null");
            else { map = NewTmp("map"); parts.Add($"{Rt("__match_map")}({v}, out var {map})"); }
            for (int i = 0; i < mp.Keys.Count; i++)
            {
                string key = typed ? EmitExpr(mp.Keys[i]) : EmitValue(mp.Keys[i]);
                if (typed) { string tv = NewTmp("mv"); parts.Add($"{map}.TryGetValue({key}, out var {tv})"); parts.Add(PatternTest(mp.Values[i], tv, valueType)); }
                else { parts.Add($"{map}.Contains({key})"); parts.Add(PatternTest(mp.Values[i], $"{map}[{key}]", null)); }
            }
            if (mp.Rest != null) Unsupported(mp, "'**rest' in mapping patterns");
            return Conj(parts.ToArray());
        }

        static readonly Dictionary<string, string> BuiltinPatternTypes = new Dictionary<string, string>
        {
            ["int"] = "long", ["float"] = "double", ["str"] = "string", ["bool"] = "bool",
            ["list"] = "global::System.Collections.IList", ["dict"] = "global::System.Collections.IDictionary",
            ["tuple"] = "global::System.Runtime.CompilerServices.ITuple",
        };

        string ClassTest(ClassPattern cp, string v, string vt)
        {
            string clsName = DottedName(cp.Cls);
            bool builtin = clsName != null && !IsShadowed(clsName) && BuiltinPatternTypes.ContainsKey(clsName);
            string type = builtin ? BuiltinPatternTypes[clsName] : MapType(cp.Cls, cp);
            bool untyped = vt == null || vt == "dynamic" || vt == "object";
            string tmp = NewTmp("c");
            var parts = new List<string>();
            if (!untyped && vt == type) { parts.Add($"{v} is not null"); tmp = v; }
            else if (!untyped && clsName == "int" && IsIntegral(vt)) tmp = $"((long){v})";
            else if (!untyped && (PrimitiveCsTypes.Contains(vt) || PrimitiveCsTypes.Contains(type))) return "false";
            else parts.Add($"{v} is {type} {tmp}");
            if (builtin)
            {
                // int(x), str(s): one positional pattern matches the subject itself
                if (cp.Positional.Count > 1) Error(cp, $"{clsName}() accepts at most one positional sub-pattern");
                if (cp.Positional.Count == 1) parts.Add(PatternTest(cp.Positional[0], tmp, type));
            }
            else
            {
                var names = MatchArgs(cp.Cls);
                for (int i = 0; i < cp.Positional.Count; i++)
                {
                    if (names == null || i >= names.Count) { Error(cp, $"{clsName}() accepts {names?.Count ?? 0} positional sub-patterns (__match_args__)"); break; }
                    parts.Add(PatternTest(cp.Positional[i], $"{tmp}.{Esc(names[i])}", MemberType(type, names[i], method: false)));
                }
            }
            for (int i = 0; i < cp.KeywordNames.Count; i++)
                parts.Add(PatternTest(cp.KeywordPatterns[i], $"{tmp}.{Esc(cp.KeywordNames[i])}", builtin ? null : MemberType(type, cp.KeywordNames[i], method: false)));
            return Conj(parts.ToArray());
        }

        /// <summary>The attribute names positional sub-patterns refer to: __match_args__, or a dataclass's fields in order.</summary>
        List<string> MatchArgs(Expr cls)
        {
            string dotted = DottedName(cls);
            if (dotted == null || !TryResolveType(dotted, 0, out _, out var pyClass) || pyClass == null) return null;
            foreach (var st in pyClass.Body)
                if (st is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr n && n.Id == "__match_args__" && a.Value is TupleExpr t)
                    return t.Elts.OfType<ConstExpr>().Where(c => c.Kind == ConstKind.Str).Select(c => c.Str).ToList();
            if (HasDecorator(pyClass.Decorators, "dataclass"))
                return pyClass.Body.OfType<AnnAssignStmt>().Where(a => a.Target is NameExpr && !(a.Annotation is SubscriptExpr sub && DottedName(sub.Target)?.EndsWith("ClassVar") == true))
                              .Select(a => ((NameExpr)a.Target).Id).ToList();
            return null;
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
            // d[k] += v on a late-bound container: read, combine, write back (the container and key evaluated once)
            if (a.Target is SubscriptExpr ds && !(ds.Index is SliceExpr) && !(ds.Index is TupleExpr) && InferType(ds.Target) == "dynamic")
            {
                string c = NewTmp("c"), k = NewTmp("k");
                _w.Line($"object? {c} = {EmitExpr(ds.Target)}, {k} = {EmitExpr(ds.Index)};");
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
                if (a.Value != null) _w.Line($"{EmitExpr(at)} = {EmitExpr(a.Value, targetTyped: true)};");
                return;
            }
            Unsupported(a, "this annotated assignment target");
        }

        // ───────────────────────────── control flow ─────────────────────────────

        void EmitReturn(ReturnStmt r)
        {
            if (_scope != null && _scope.IsGenerator)
            {
                // return v: the value StopIteration carries (next(), send(), yield from)
                if (r.Value != null && !IsNone(r.Value))
                {
                    if (_scope.ReceivesValues) _w.Line($"{GenVar}.__return = (object?){Paren(EmitValue(r.Value))};");
                    else Unsupported(r, "returning a value from a generator");
                }
                _w.Line("yield break;");
                return;
            }
            if (Obs != null && _retKey != null)
                Obs.AddReturn(_retKey, r.Value == null || (r.Value is ConstExpr rc && rc.Kind == ConstKind.None) ? TypeInference.NoneType : (PyType(InferType(r.Value)) ?? TypeInference.Unknown));
            if (r.Value == null) { _w.Line("return;"); return; }
            string rt = _scope?.ReturnType;
            _w.Line($"return {EmitExpr(r.Value, targetTyped: rt != null && rt != "dynamic" && rt != "void")};");
        }

        void EmitRaise(RaiseStmt r)
        {
            if (r.Exc == null) { _w.Line("throw;"); return; }
            if (r.Exc is NameExpr || r.Exc is AttributeExpr)
            {
                if (TryTypeExpr(r.Exc, out var typeCode)) { _w.Line($"throw new {typeCode}();"); return; }
            }
            // raise "message" (Python 2): a TypeError when it runs, as in Python 3
            string et = InferType(r.Exc);
            if (et == "string" || et != null && (IsIntegral(et) || et == "double" || et == "bool"))
            {
                _w.Line("throw new global::System.InvalidCastException(\"exceptions must derive from BaseException\");");
                return;
            }
            _w.Line($"throw {EmitExpr(r.Exc)};");
        }

        void EmitIf(IfStmt i, bool isElif)
        {
            // if isinstance(x, C): in the block, x is a C (a pattern variable of that type)
            if (IsInstanceNarrowing(i.Test, i.Body, out var nn, out var ncls))
            {
                string pv = NewTmp(nn.Id + "_");
                _w.Line($"{(isElif ? "else if" : "if")} ((object?){Paren(EmitName(nn))} is {ncls} {pv})");
                bool hadRename = _renames.TryGetValue(nn.Id, out var oldRename);
                bool hadType = _scope.Types.TryGetValue(nn.Id, out var oldType);
                _renames[nn.Id] = pv;
                _scope.Types[nn.Id] = ncls;
                try { EmitBlock(i.Body); }
                finally
                {
                    if (hadRename) _renames[nn.Id] = oldRename; else _renames.Remove(nn.Id);
                    if (hadType) _scope.Types[nn.Id] = oldType; else _scope.Types.Remove(nn.Id);
                }
            }
            else
            {
                _w.Line($"{(isElif ? "else if" : "if")} ({Cond(i.Test)})");
                EmitBlock(i.Body, narrowed: NarrowedBy(i.Test, whenTrue: true));
            }
            if (i.OrElse.Count == 1 && i.OrElse[0] is IfStmt elif)
            {
                LineDirective(elif);
                EmitIf(elif, isElif: true);
            }
            else if (i.OrElse.Count > 0)
            {
                _w.Line("else");
                EmitBlock(i.OrElse, narrowed: NarrowedBy(i.Test, whenTrue: false));
            }
        }

        /// <summary>isinstance(name, Cls) with one Typhon or .NET class, on a variable the block does not assign.</summary>
        bool IsInstanceNarrowing(Expr test, List<Stmt> body, out NameExpr name, out string type)
        {
            name = null; type = null;
            if (!(test is CallExpr c && c.Func is NameExpr f && f.Id == "isinstance" && !IsShadowed("isinstance") && c.Args.Count == 2 &&
                  c.Args[0].Value is NameExpr n && c.Args[1].Value is NameExpr cls && IsLocal(n.Id) && !AssignsName(body, n.Id))) return false;
            if (!TryResolveType(cls.Id, 0, out _, out var pyClass) || pyClass == null || pyClass.TypeParams.Count > 0) return false;
            string current = KnownType(n.Id);
            type = MapType(cls, test);
            if (type == null || current == type) return false;
            name = n;
            return true;
        }

        /// <summary>
        /// if x: / if x is not None: / while x: (and the else of if x is None:) on an optional value (int | None → long?):
        /// in that block x holds a value. Returns the name, or null.
        /// </summary>
        string NarrowedBy(Expr test, bool whenTrue)
        {
            if (test is BoolOpExpr b && b.Op == "and" && whenTrue) test = b.Values[0];
            string name = null; bool positive = true;
            if (test is NameExpr n) name = n.Id;
            else if (test is CompareExpr c && c.Ops.Count == 1 && c.Left is NameExpr cn && IsNone(c.Comparators[0]) && (c.Ops[0] == "is not" || c.Ops[0] == "is"))
            { name = cn.Id; positive = c.Ops[0] == "is not"; }
            if (name == null || positive != whenTrue) return null;
            string t = KnownType(name);
            return t != null && t.EndsWith("?") && IsValueTypeName(t.Substring(0, t.Length - 1)) ? name : null;
        }

        bool IsBuiltinRange(Expr iter, out List<Expr> args)
        {
            args = null;
            if (!(iter is CallExpr c) || !(c.Func is NameExpr fn) || fn.Id != "range" || IsShadowed("range")) return false;
            if (c.Args.Count < 1 || c.Args.Count > 3 || c.Args.Any(a => a.Name != null || a.Star || a.DoubleStar)) return false;
            args = c.Args.Select(a => a.Value).ToList();
            return true;
        }

        static bool AssignsName(List<Stmt> body, string name)
        {
            bool found = false;
            void CheckTarget(Expr t)
            {
                if (t is NameExpr ne && ne.Id == name) found = true;
                else if (t is TupleExpr tup) foreach (var e in tup.Elts) CheckTarget(e);
                else if (t is ListExpr list) foreach (var e in list.Elts) CheckTarget(e);
            }
            AstWalker.VisitAll(body, n =>
            {
                if (n is AssignStmt a) foreach (var t in a.Targets) CheckTarget(t);
                if (n is AugAssignStmt aa) CheckTarget(aa.Target);
                if (n is AnnAssignStmt an) CheckTarget(an.Target);
                if (n is ForStmt f) CheckTarget(f.Target);
                if (n is WithStmt w) foreach (var item in w.Items) if (item.Var != null) CheckTarget(item.Var);
            });
            return found;
        }

        /// <summary>Per enclosing loop: the flag a 'break' sets (loops with an 'else' block that a break can skip), or null.</summary>
        readonly Stack<string> _breakFlags = new Stack<string>();

        /// <summary>Declares the "broke out" flag of a loop with an else block, if a break can skip it; returns its name or null.</summary>
        string LoopElseFlag(List<Stmt> orElse, List<Stmt> body)
        {
            if (orElse.Count == 0 || !BreaksOutOf(body)) return null;
            string flag = NewTmp("broke");
            _w.Line($"var {flag} = false;");
            return flag;
        }

        /// <summary>while/for ... else: the else block runs when the loop ends without 'break'.</summary>
        void EmitLoopElse(List<Stmt> orElse, string flag)
        {
            if (orElse.Count == 0) return;
            if (flag != null) _w.Line($"if (!{flag})");
            EmitBlock(orElse);
        }

        /// <summary>Does a 'break' in these statements leave this loop? (Breaks in nested loop bodies leave those; their else blocks count.)</summary>
        static bool BreaksOutOf(List<Stmt> stmts)
        {
            foreach (var st in stmts)
            {
                switch (st)
                {
                    case BreakStmt _: return true;
                    case WhileStmt w: if (BreaksOutOf(w.OrElse)) return true; break;
                    case ForStmt f: if (BreaksOutOf(f.OrElse)) return true; break;
                    case IfStmt i: if (BreaksOutOf(i.Body) || BreaksOutOf(i.OrElse)) return true; break;
                    case TryStmt t:
                        if (BreaksOutOf(t.Body) || BreaksOutOf(t.OrElse) || BreaksOutOf(t.FinalBody) || t.Handlers.Any(h => BreaksOutOf(h.Body))) return true;
                        break;
                    case WithStmt wi: if (BreaksOutOf(wi.Body)) return true; break;
                }
            }
            return false;
        }

        /// <summary>while i &lt; j / j &gt; i / j &gt; 0 (i non-negative): j is non-negative when the body starts; its name, or null.</summary>
        string WhileGuardedName(Expr test)
        {
            if (!(test is CompareExpr c) || c.Ops.Count != 1) return null;
            string op = c.Ops[0];
            if ((op == "<" || op == "<=") && c.Comparators[0] is NameExpr r && IsNonNegative(c.Left) && IsIntegral(InferType(r))) return r.Id;
            if ((op == ">" || op == ">=") && c.Left is NameExpr l && IsNonNegative(c.Comparators[0]) && IsIntegral(InferType(l))) return l.Id;
            return null;
        }

        /// <summary>int(x) / round(x) / math.floor(x): an explicit conversion to int (a float variable assigned one becomes an int).</summary>
        bool IsIntConversion(Expr e) => e is CallExpr c && (c.Func is NameExpr n && (n.Id == "int" || n.Id == "round" && c.Args.Count == 1) && !IsShadowed(n.Id) ||
                                                            c.Func is AttributeExpr a && IsMathModule(a.Target) && (a.Name == "floor" || a.Name == "ceil" || a.Name == "trunc"));

        void EmitFor(ForStmt f)
        {
            string flag = LoopElseFlag(f.OrElse, f.Body);
            _breakFlags.Push(flag);
            EmitForLoop(f);
            _breakFlags.Pop();
            EmitLoopElse(f.OrElse, flag);
        }

        void EmitForLoop(ForStmt f)
        {

            // Fast path: for i in range(...) → a plain counting loop (no enumerator allocation).
            if (!f.IsAsync && f.Target is NameExpr tn && IsBuiltinRange(f.Iter, out var args))
            {
                // big-int mode: the counter stays a long (a range cannot have 2**63 steps); BigInteger bounds are narrowed
                string Bound(Expr e) => _big && InferType(e) == TypeInference.BigInt && !IsIntLiteral(e) ? $"checked((long)({EmitExpr(e)}))" : EmitExpr(e);
                string start = args.Count >= 2 ? Bound(args[0]) : "0";
                string stop = Bound(args.Count >= 2 ? args[1] : args[0]);
                string step = args.Count == 3 ? Bound(args[2]) : "1";
                bool inline = IsInline(f, tn.Id);
                bool direct = inline && !AssignsName(f.Body, tn.Id);
                string iv = direct ? Esc(tn.Id) : NewTmp("i");
                string end = NewTmp("end");
                string cond, incr;
                var stepConst = args.Count == 3 ? ConstIntValue(args[2]) : 1;
                if (stepConst.HasValue)
                {
                    cond = stepConst.Value > 0 ? $"{iv} < {end}" : $"{iv} > {end}";
                    incr = stepConst.Value == 1 ? $"{iv}++" : stepConst.Value == -1 ? $"{iv}--" : $"{iv} += {step}";
                }
                else
                {
                    // unknown step sign: precompute the trip count like Python's range does, keeping the loop test trivial
                    string st = NewTmp("step"), first = NewTmp("start"), k = NewTmp("k");
                    _w.Line($"long {first} = {start}, {st} = {step};");
                    _w.Line($"for (long {k} = 0, {end} = {Rt("range_len")}({first}, {stop}, {st}), {iv} = {first}; {k} < {end}; {k}++, {iv} += {st})");
                    cond = null; incr = null;
                }
                // Python ints are longs: the loop variable is too (indexing narrows it, see EmitPosition)
                if (cond != null) _w.Line($"for (long {iv} = {start}, {end} = {stop}; {cond}; {incr})");
                bool counts = direct && (stepConst ?? 0) > 0 && (args.Count < 2 || IsNonNegative(args[0])) && !_nonNegative.Contains(tn.Id);
                if (counts) _nonNegative.Add(tn.Id);
                if (direct) { SetType(tn.Id, "long"); EmitBlock(f.Body); }
                else
                {
                    string prologue = inline ? $"var {Esc(tn.Id)} = {iv};" : $"{EmitName(tn)} = {iv};";
                    if (inline) SetType(tn.Id, "long");
                    EmitBlock(f.Body, prologue);
                }
                if (counts) _nonNegative.Remove(tn.Id);
                return;
            }

            // a list iterates by position, like Python's list iterator: the body may append to it or assign its items
            string iterType = f.IsAsync ? null : InferType(f.Iter);
            // (items unpacked into names: only items C# can deconstruct, not late-bound ones)
            if (iterType != null && iterType.StartsWith("List<") &&
                (f.Target is NameExpr || f.Target is TupleExpr tt0 && tt0.Elts.All(e => e is NameExpr) && !IsUnknownType(ElementTypeOf(iterType))))
            {
                string list = NewTmp("l"), idx = NewTmp("i");
                _w.Line($"var {list} = {EmitExpr(f.Iter)};");
                _w.Line($"for (int {idx} = 0; {idx} < {list}.Count; {idx}++)");
                string item = $"{list}[{idx}]";
                string prologue;
                if (f.Target is NameExpr ln)
                {
                    if (IsInline(f, ln.Id)) { prologue = $"var {Esc(ln.Id)} = {item};"; SetType(ln.Id, IterElementType(iterType)); }
                    else prologue = $"{EmitName(ln)} = {item};";
                }
                else
                {
                    var tup0 = (TupleExpr)f.Target;
                    bool inl = tup0.Elts.All(e => IsInline(f, ((NameExpr)e).Id));
                    prologue = inl ? $"var {DeconstructionPattern(tup0)} = {item};" : $"{DeconstructionTarget(tup0, f)} = {item};";
                    var parts0 = UnpackPartTypes(ElementTypeOf(iterType), tup0.Elts.Count);
                    if (inl && parts0 != null)
                        for (int i = 0; i < parts0.Count; i++) SetType(((NameExpr)tup0.Elts[i]).Id, parts0[i]);
                }
                EmitBlock(f.Body, prologue);
                return;
            }
            string iter = f.IsAsync ? EmitExpr(f.Iter) : EmitIterable(f.Iter);
            string kw = f.IsAsync ? "await foreach" : "foreach";
            if (f.Target is NameExpr n)
            {
                // the body rebinds the loop variable (case = case.strip(), an inner `for _ in`): a C# foreach variable is
                // read-only, so the item goes into an ordinary variable
                // (an iterable of unknown type is iterated through __iter_dyn: its items are late-bound)
                string loopType = IterElementType(IterableType(f.Iter)) ?? (iter.Contains("__iter_dyn(") ? "dynamic" : null);
                if (IsInline(f, n.Id) && AssignsName(f.Body, n.Id))
                {
                    string item = NewTmp("it");
                    SetType(n.Id, loopType);
                    _w.Line($"{kw} (var {item} in {iter})");
                    EmitBlock(f.Body, $"var {Esc(n.Id)} = {item};");
                }
                else if (IsInline(f, n.Id)) { SetType(n.Id, loopType); _w.Line($"{kw} (var {Esc(n.Id)} in {iter})"); EmitBlock(f.Body); }
                else
                {
                    string t = NewTmp();
                    _w.Line($"{kw} (var {t} in {iter})");
                    EmitBlock(f.Body, $"{EmitName(n)} = {t};");
                }
                return;
            }
            if (f.Target is TupleExpr tup)
            {
                bool allInline = tup.Elts.All(e => e is NameExpr ne && IsInline(f, ne.Id));
                // late-bound items (C# cannot deconstruct dynamic): each item's values by position, checked for the count
                // (also a call dispatched dynamically, enumerate(u) with u late-bound: its C# type is dynamic, whatever it returns)
                string loopElem = IterElementType(IterableType(f.Iter));
                if ((loopElem == "dynamic" || IsDynamicallyDispatched(f.Iter) || IsUnknownType(IterableType(f.Iter)) && !(f.Iter is NameExpr) ||
                     (loopElem == null || loopElem == "object") && f.Iter is CallExpr && UnpackPartTypes(loopElem, tup.Elts.Count) == null) &&
                    !tup.Elts.Any(e => e is StarredExpr))
                {
                    string item = NewTmp("x"), seq = NewTmp("u");
                    _w.Line($"{kw} (var {item} in {iter})");
                    _w.Open();
                    _w.Line($"var {seq} = {Rt("list")}({Rt("__iter_dyn")}((object){item}));");
                    _w.Line($"if ({seq}.Count != {tup.Elts.Count}) throw new global::System.ArgumentException(\"expected {tup.Elts.Count} values to unpack, got \" + {seq}.Count);");
                    for (int i = 0; i < tup.Elts.Count; i++)
                    {
                        EmitAssignTo(tup.Elts[i], null, f, $"{seq}[{i}]");
                        if (tup.Elts[i] is NameExpr un && IsInline(f, un.Id)) SetType(un.Id, "dynamic");
                    }
                    EmitBlockStatements(f.Body);
                    _w.Close();
                    return;
                }
                if (allInline)
                {
                    _w.Line($"{kw} (var {DeconstructionPattern(tup)} in {iter})");
                    // for i, x in enumerate(xs) / for k, v in d.items(): the names get the tuple's element types
                    var parts = UnpackPartTypes(ElementTypeOf(IterableType(f.Iter)), tup.Elts.Count);
                    if (parts != null)
                        for (int i = 0; i < parts.Count; i++) SetType(((NameExpr)tup.Elts[i]).Id, parts[i]);
                    EmitBlock(f.Body);
                }
                else
                {
                    string t = NewTmp();
                    _w.Line($"{kw} (var {t} in {iter})");
                    EmitBlock(f.Body, $"{DeconstructionTarget(tup, f)} = {t};");
                }
                return;
            }
            Unsupported(f.Target, "this loop target");
        }

        /// <summary>The types n unpacked names get from a value of this type: a tuple's items, or n times the item type of a computed-length tuple.</summary>
        List<string> UnpackPartTypes(string t, int n)
        {
            if (TupleElementTypes(t) is List<string> parts) return parts.Count == n ? parts : null;
            if (NamedTupleFieldTypes(t) is List<string> nt) return nt.Count == n ? nt : null;
            if (t != null && t.StartsWith("global::Typhon.Runtime.PyTuple<") && t.EndsWith(">")) return Enumerable.Repeat(GenericArg(t), n).ToList();
            return null;
        }

        /// <summary>"(long, string)" or "(long num1, long num2)" → its element types; null if not a tuple type.</summary>
        static List<string> TupleElementTypes(string t)
        {
            if (t != null && t.StartsWith("ValueTuple<") && t.EndsWith(">") && SplitTypeArgs(GenericArg(t)).Count == 1) return new List<string> { GenericArg(t) };
            if (t == null || !t.StartsWith("(") || !t.EndsWith(")")) return null;
            var parts = SplitTypeArgs(t.Substring(1, t.Length - 2));
            for (int i = 0; i < parts.Count; i++)
            {
                // drop an element name: the last space outside <...> and (...)
                int depth = 0, cut = -1;
                for (int j = 0; j < parts[i].Length; j++)
                {
                    char ch = parts[i][j];
                    if (ch == '<' || ch == '(') depth++;
                    else if (ch == '>' || ch == ')') depth--;
                    else if (ch == ' ' && depth == 0) cut = j;
                }
                if (cut > 0) parts[i] = parts[i].Substring(0, cut);
            }
            return parts;
        }

        string DeconstructionPattern(Expr e)
        {
            if (e is NameExpr n) return Esc(n.Id);
            if (e is TupleExpr t) return "(" + string.Join(", ", t.Elts.Select(DeconstructionPattern)) + ")";
            Unsupported(e, "this loop target");
            return "_";
        }

        static long? ConstIntValue(Expr e)
        {
            if (e is ConstExpr c && c.Kind == ConstKind.Int && long.TryParse(c.Raw.Replace("_", ""), out var v)) return v;
            if (e is UnaryExpr u && u.Op == "-" && ConstIntValue(u.Operand) is long inner) return -inner;
            return null;
        }

        static bool EndsWithJump(List<Stmt> body) =>
            body.Count > 0 && (body[body.Count - 1] is ReturnStmt || body[body.Count - 1] is RaiseStmt || body[body.Count - 1] is BreakStmt || body[body.Count - 1] is ContinueStmt);

        /// <summary>del d[key] / del xs[i]: item deletion. Deleting names or attributes has no CLR equivalent.</summary>
        void EmitDel(DelStmt d)
        {
            foreach (var t in d.Targets)
            {
                if (t is SubscriptExpr s && !(s.Index is SliceExpr)) _w.Line($"{Rt("delitem")}({EmitExpr(s.Target)}, {EmitExpr(s.Index)});");
                else if (t is SubscriptExpr ss && ss.Index is SliceExpr sl)
                    _w.Line($"{Rt("delslice")}({EmitExpr(ss.Target)}, {(sl.Lower != null ? SliceBound(sl.Lower) : "null")}, {(sl.Upper != null ? SliceBound(sl.Upper) : "null")}, {(sl.Step != null ? SliceBound(sl.Step) : "1")});");
                else Unsupported(t, "'del' of a name, attribute or slice");
            }
        }

        void EmitTry(TryStmt t)
        {
            bool hasElse = t.OrElse.Count > 0;
            bool hasFinally = t.FinalBody.Count > 0;
            // try: A except: B else: C finally: D  →  try { try { A } catch { B; goto after; } C; after: ; } finally { D }
            // (C runs only when A completed normally and is outside the handlers; C#'s flow analysis sees every path)
            string after = hasElse ? NewTmp("after") : null;
            bool usedAfter = false;
            if (hasElse && hasFinally) { _w.Line("try"); _w.Open(); }
            _w.Line("try");
            _w.Open();
            EmitBlockStatements(t.Body);
            _w.Close();
            foreach (var h in t.Handlers)
            {
                LineDirective(h);
                string name = h.Name;
                string csName = name;
                bool renamed = false;
                if (name != null && (IsLocal(name) || _renames.ContainsKey(name))) { csName = NewTmp("e"); renamed = true; }
                if (h.Type == null) _w.Line(name == null ? "catch" : $"catch (Exception {Esc(csName)})");
                else
                {
                    var types = CatchTypes(h.Type, h);
                    if (types.Count == 1 && !types[0].StartsWith("(")) _w.Line(name == null ? $"catch ({types[0]})" : $"catch ({types[0]} {Esc(csName)})");
                    else
                    {
                        string v = csName ?? NewTmp("e");
                        _w.Line($"catch (Exception {Esc(v)}) when ({string.Join(" || ", types.Select(x => $"{Esc(v)} is {x}"))})");
                    }
                }
                string saved = null;
                if (renamed) { _renames.TryGetValue(name, out saved); _renames[name] = csName; }
                _w.Open();
                EmitBlockStatements(h.Body);
                if (hasElse && !EndsWithJump(h.Body)) { _w.Line($"goto {after};"); usedAfter = true; }
                _w.Close();
                if (renamed) { if (saved != null) _renames[name] = saved; else _renames.Remove(name); }
            }
            if (hasElse)
            {
                if (t.Handlers.Count == 0) { _w.Line("finally"); _w.Open(); _w.Close(); }
                EmitBlock(t.OrElse);
                if (usedAfter) _w.Line($"{after}: ;");
                if (hasFinally)
                {
                    _w.Close();
                    _w.Line("finally");
                    EmitBlock(t.FinalBody);
                }
                return;
            }
            if (hasFinally)
            {
                _w.Line("finally");
                EmitBlock(t.FinalBody);
            }
            if (t.Handlers.Count == 0 && !hasFinally) { _w.Line("finally"); _w.Open(); _w.Close(); }
        }

        void EmitWith(WithStmt w)
        {
            string kw = w.IsAsync ? "await using" : "using";
            foreach (var item in w.Items)
            {
                string ctx = EmitExpr(item.Context);
                if (item.Var == null) _w.Line($"{kw} ({ctx})");
                else if (item.Var is NameExpr n)
                {
                    if (IsInline(w, n.Id)) { _w.Line($"{kw} (var {Esc(n.Id)} = {ctx})"); SetType(n.Id, InferType(item.Context)); } // with open(p) as f: f is the file
                    else _w.Line($"{kw} ({EmitName(n)} = {ctx})");
                }
                else Unsupported(item.Var, "this 'with ... as' target");
            }
            EmitBlock(w.Body);
        }

        void EmitLocalFunction(FunctionDef f)
        {
            if (f.Decorators.Count > 0) Unsupported(f, "decorators on nested functions");
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            var savedRet = _retKey; _retKey = null; // a nested function's returns are its own
            // definition-time defaults of a nested function are evaluated each time its def runs: locals before it
            foreach (var p in f.Params)
                if (p.Default != null && p.Kind == ParamKind.Normal && !IsConstantDefault(p.Default))
                {
                    string tmp = NewTmp("default");
                    _w.Line($"var {tmp} = {EmitValue(p.Default)};");
                    _defaultSources[p] = tmp;
                }
            var sig = BuildSignature(f, dropFirst: false);
            sig.Scope.HasSelf = false;
            if (!f.IsAsync && !sig.IsGenerator) _funcReturnTypes[f.Name] = sig.ReturnType;
            string asyncKw = f.IsAsync ? "async " : "";
            _w.Line($"{asyncKw}{sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}");
            EmitSignatureBody(f.Body, sig);
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name);
            _retKey = savedRet;
        }
    }
}
