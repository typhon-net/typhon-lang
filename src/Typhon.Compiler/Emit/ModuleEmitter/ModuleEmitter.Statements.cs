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
    // Statements: blocks, expression statements, return/raise, if, del, try and with.
    internal sealed partial class ModuleEmitter
    {
        readonly Dictionary<string, string> _renames = new Dictionary<string, string>();

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
            if (t.StartsWith("List<") && v.StartsWith("IEnumerable<")) return true;
            bool Plain(string x) => PrimitiveCsTypes.Contains(x) || x.StartsWith("List<") || x.StartsWith("HashSet<") || IsDictType(x) || x.StartsWith("(") ||
                                    x is "global::Typhon.Runtime.PyAttrDict" or "global::Typhon.Runtime.PyBytes" or "global::Typhon.Runtime.PyByteArray" ||
                                    x is "global::System.Type" or "System.Type" ||
                                    x is "global::Typhon.Stdlib.re.Match" or "global::Typhon.Stdlib.re.BytesMatch" ||
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
                case ImportStmt _:
                case ImportFromStmt _: EmitProjectImports(s); break;
                case AssertStmt a when a.Msg == null && IsTestModule && _scope != null && AssertWithValues(a): break;
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
                case FunctionDef f when _classFieldInitializer && _cls.ClassBodyImplementations.TryGetValue(f, out string implementation):
                    _w.Line($"{Esc(f.Name)} = global::Typhon.Runtime.PyClassDescriptors.ClassBodyFunction(" +
                        $"typeof({_cls.CsSelfType}), {CsString(f.Name)}, {CsString(implementation)});");
                    break;
                case FunctionDef f: EmitLocalFunction(f); break;
                case ClassBodyInitialization initialization:
                    _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof({Esc(initialization.Definition.Name)}).TypeHandle);");
                    break;
                case ClassDef c: EmitLocalDataClass(c); break;
                default: Unsupported(s, s.GetType().Name); break;
            }
        }

        void EmitExprStmt(ExprStmt e)
        {
            if (_scope?.DisplaysExpressionValues == true && e.Value is not CallExpr { Func: NameExpr { Id: "__typhon_export" } })
            {
                _w.Line($"global::Typhon.Runtime.Builtins.__display_runtime_result({EmitExpr(e.Value)});");
                return;
            }
            // d.sort() / d.append(x) on a late-bound value as a statement: the method may return nothing (void), so an if
            // statement, not a conditional expression, chooses between the runtime's method and the object's own
            // (also on an attribute of a value whose type is not known: late-bound in C# then too, pred_info.successors.append(x))
            if (e.Value is CallExpr dc && dc.Func is AttributeExpr dm &&
                LatePythonMethodReceiver(dm.Target, statement: true) &&
                !(dm.Target is NameExpr dmn && (ModuleRef(dmn.Id) != null || _dotnetAliases.ContainsKey(dmn.Id) || TryResolveType(dmn.Id, 0, out _, out _))) &&
                !(dm.Target is CallExpr dsc && dsc.Func is NameExpr dsn && dsn.Id == "super") && !dc.Args.Any(a => a.Star || a.DoubleStar) && IsRuntimeMethod(dm.Name, dc.Args))
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
                        _w.Line(_scope != null && _scope.AsyncForm ? $"foreach (var {t} in {EmitIterable(y.Value)}) await {GenVar}.Yield({t});"
                                                                    : $"foreach (var {t} in {EmitExpr(y.Value)}) yield return {t};");
                    }
                    else EmitYieldValue(y);
                    return;
                // Mapping.register(Context) / _collections_abc.Sequence.register(X): a virtual subclass of an ABC of the typing-only
                // modules; nothing at run time in Typhon
                case CallExpr rc when rc.Func is AttributeExpr ra && ra.Name == "register" && DottedName(ra.Target) is string rdn && !IsLocal(rdn.Split('.')[0]) &&
                                      (_typingOnlyNames.Contains(rdn.Split('.')[0]) ||
                                       // (Complex.register(complex) on a Typhon ABC that defines no register of its own)
                                       TryResolveType(rdn, 0, out _, out var regClass) && regClass != null &&
                                       !HasVirtualSubclasses(regClass) && !DefinesInHierarchy(regClass, "register")):
                    return;
                case CallExpr _:
                case AwaitExpr _:
                    _statementCall = e.Value as CallExpr;
                    _w.Line(StripParens(EmitExpr(e.Value)) + ";");
                    _statementCall = null;
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


        bool KnownNoneResult(CallExpr call)
        {
            if (TryTypeExpr(call.Func, out _) || TyphonCallee(call, out _) is not FunctionDef callee || callee.IsAsync ||
                _p.Modules.Values.Any(module => module.Ast?.MethodDecorators.ContainsKey(callee) == true) ||
                AstWalker.ContainsOutsideNestedScopes(callee.Body, node => node is YieldExpr))
                return false;
            if (call.Func is AttributeExpr attribute)
            {
                if (InstanceMethodMayBeShadowed(attribute)) return false;
                var receiver = attribute.Target is NameExpr { Id: "self" } ? _cls?.Def :
                    ClassKeyOfType(InferType(attribute.Target), out _) is string key ? ClassDefOfKey(key) : null;
                if (receiver != null && FamilyReturnsValue(receiver, callee.Name)) return false;
            }
            return IsNone(callee.Returns) || callee.Returns == null &&
                !AstWalker.ContainsOutsideNestedScopes(callee.Body, node => node is ReturnStmt { Value: not null } result && !IsNone(result.Value)) &&
                (CanFallThrough(callee.Body) || AstWalker.ContainsOutsideNestedScopes(callee.Body, node => node is ReturnStmt));
        }

        void EmitReturn(ReturnStmt r)
        {
            if (_scope != null && _scope.IsGenerator)
            {
                // return v: the value StopIteration carries (next(), send(), yield from)
                if (_scope.AsyncForm)
                {
                    _w.Line(r.Value != null && !IsNone(r.Value) ? $"return (object?){Paren(EmitValue(r.Value))};" : "return null;");
                    return;
                }
                if (r.Value != null && !IsNone(r.Value))
                {
                    if (_scope.ReceivesValues) _w.Line($"{GenVar}.__return = (object?){Paren(EmitValue(r.Value))};");
                    else Unsupported(r, "returning a value from a generator");
                }
                _w.Line("yield break;");
                return;
            }
            string rt = _scope?.ReturnType;
            string bound = rt is null or "dynamic" or "object" ? BoundMethodValue(r.Value) : null;
            var call = r.Value as CallExpr;
            bool returnsNone = call != null && KnownNoneResult(call);
            if (Obs != null && _retKey != null)
                Obs.AddReturn(_retKey, r.Value == null || IsNone(r.Value) || returnsNone ? TypeInference.NoneType :
                    bound != null ? "dynamic" : PyType(InferType(r.Value)) ?? TypeInference.Unknown);
            if (r.Value == null || IsNone(r.Value))
            {
                _w.Line(rt is null or "void" ? "return;" : "return null;");
                return;
            }
            if (bound != null) { _w.Line($"return {ProfileReturn(bound)};"); return; }
            if (returnsNone)
            {
                EmitExprStmt(new ExprStmt { Value = call });
                _w.Line(rt is null or "void" ? "return;" : $"return {ProfileReturn("null")};");
                return;
            }
            _w.Line($"return {ProfileReturn(EmitExpr(r.Value, targetTyped: rt != null && rt != "dynamic" && rt != "void"))};");

            string ProfileReturn(string value) => _scope?.ProfileToken is string profile && rt != "void"
                ? $"global::Typhon.Runtime.PyProfiling.Result<{rt ?? "dynamic"}>({profile}, {value})" : value;
        }

        bool IsTestModule => _staticTypes.Contains("Typhon.Stdlib.pytest") || _dotnetAliases.ContainsValue("Typhon.Stdlib.pytest");

        /// <summary>
        /// assert a == b in a test module: both sides are evaluated once into temporaries, so a failure can report their
        /// values as pytest does (assert 4 == 5). False (nothing emitted) when the operands' types are not known.
        /// </summary>
        bool AssertWithValues(AssertStmt a)
        {
            if (!(a.Test is CompareExpr ce) || ce.Ops.Count != 1) return false;
            string lt = InferType(ce.Left), rt = InferType(ce.Comparators[0]);
            if (lt == null || rt == null || lt == TypeInference.Pending || rt == TypeInference.Pending || lt == "void" || rt == "void") return false;
            string l = NewTmp("left"), r = NewTmp("right");
            _w.Open();
            _w.Line($"{lt} {l} = {EmitExpr(ce.Left, targetTyped: true)};");
            _w.Line($"{rt} {r} = {EmitExpr(ce.Comparators[0], targetTyped: true)};");
            _renames[l] = l; _renames[r] = r;
            SetType(l, lt); SetType(r, rt);
            try
            {
                var cmp = new CompareExpr { Left = new NameExpr { Id = l, Line = a.Line, Col = a.Col }, Ops = ce.Ops, Comparators = { new NameExpr { Id = r, Line = a.Line, Col = a.Col } }, Line = a.Line, Col = a.Col };
                string op = ce.Ops[0];
                LineDirective(a); // (a failure's traceback points at the assert, not at the lines before it)
                _w.Line($"if (!{Paren(Cond(cmp))}) throw new global::Typhon.Runtime.AssertionError(\"assertion failed\") {{ Detail = \"assert \" + repr((object?){l}) + {CsString(" " + op + " ")} + repr((object?){r}) }};");
            }
            finally { _renames.Remove(l); _renames.Remove(r); }
            _w.Close();
            return true;
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

        readonly HashSet<string> _narrowedInstanceVariables = new HashSet<string>();

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
                _narrowedInstanceVariables.Add(pv);
                try { EmitBlock(i.Body); }
                finally
                {
                    _narrowedInstanceVariables.Remove(pv);
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
            if (!IsShadowed(cls.Id) && cls.Id is "str" or "bytes" or "bytearray")
            {
                type = MapType(cls, test);
                if (type == KnownType(n.Id)) return false;
                name = n;
                return true;
            }
            // Open generic containers and the Python numeric hierarchy need their runtime isinstance behavior.
            if (BuiltinTypeNames.Contains(cls.Id) || !TryResolveType(cls.Id, 0, out var clsMeta, out var pyClass) ||
                (pyClass != null ? pyClass.TypeParams.Count > 0 : clsMeta == null || clsMeta.Contains('`'))) return false;
            if (MayHaveVirtualSubclasses(clsMeta, pyClass)) return false;
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

        static bool EndsWithJump(List<Stmt> body) =>
            body.Count > 0 && (body[body.Count - 1] is ReturnStmt || body[body.Count - 1] is RaiseStmt || body[body.Count - 1] is BreakStmt || body[body.Count - 1] is ContinueStmt);

        /// <summary>del d[key] / del xs[i] / del xs[a:b] / del name / del obj.attr.</summary>
        void EmitDel(DelStmt d)
        {
            foreach (var t in d.Targets)
            {
                if (t is SubscriptExpr s && !(s.Index is SliceExpr)) _w.Line($"{Rt("delitem")}({EmitExpr(s.Target)}, {ModuleOrClassValue(s.Index) ?? EmitExpr(s.Index)});");
                else if (t is SubscriptExpr ss && ss.Index is SliceExpr sl)
                    _w.Line($"{Rt("delslice")}({EmitExpr(ss.Target)}, {(sl.Lower != null ? SliceBound(sl.Lower) : "null")}, {(sl.Upper != null ? SliceBound(sl.Upper) : "null")}, {(sl.Step != null ? SliceBound(sl.Step) : "1")});");
                // del name: Python unbinds it (a module's clean-up of helper names, a local no longer needed); the CLR
                // cannot unbind a variable, and reading it later is an error in Python anyway: nothing to do
                else if (t is NameExpr) continue;
                // del obj.attr: an attribute the class declares goes back to its default value; one added at run time is removed
                else if (t is AttributeExpr da)
                {
                    if (IsInstanceClassDefault(da))
                    {
                        _w.Line($"{Rt("delattr")}((object?){Paren(EmitExpr(da.Target))}, {CsString(da.Name)});");
                        continue;
                    }
                    string ot = da.Target is NameExpr dsn && dsn.Id == "self" && _cls != null && HasSelfInScope() ? _cls.CsSelfType : InferType(da.Target);
                    if (InstanceMethodOwner(da, out _) != null ||
                        ClassKeyOfType(ot, out _) is string descriptorClass && ClassDefOfKey(descriptorClass) is ClassDef cachedOwner &&
                        MethodInHierarchy(cachedOwner, da.Name) is FunctionDef cached && IsCachedProperty(cached))
                    {
                        _w.Line($"{Rt("delattr")}((object?){Paren(EmitExpr(da.Target))}, {CsString(da.Name)});");
                        continue;
                    }
                    // (a field of the class: declared in its body, set through self in a method, or created from outside)
                    bool declared = ot != null && ot != "dynamic" &&
                                    (_cls != null && ot == _cls.CsSelfType && _cls.FieldTypes.ContainsKey(da.Name) ||
                                     ClassKeyOfType(ot, out _) is string dck && ClassDefOfKey(dck) is ClassDef dcd &&
                                     (DeclaresMember(dcd, da.Name) || CollectImplicitFields(dcd).ContainsKey(da.Name) || ExternalType(dck + "|" + da.Name) is string et && et != TypeInference.Pending) ||
                                     MemberType(ot, da.Name, method: false) != null);
                    if (declared) _w.Line($"{EmitExpr(da)} = default!;");
                    else _w.Line($"{Rt("delattr")}((object?){Paren(EmitExpr(da.Target))}, {CsString(da.Name)});");
                }
                else if (t is TupleExpr || t is ListExpr) EmitDel(new DelStmt { Targets = (t is TupleExpr te ? te.Elts : ((ListExpr)t).Elts), Line = d.Line, Col = d.Col });
                else Unsupported(t, "this 'del' target");
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
                    var specs = h.Type is TupleExpr ht ? ht.Elts : new List<Expr> { h.Type };
                    // Exception aliases may be Type-valued fields, e.g. socket.timeout.
                    var aliases = specs.Where(x => !TryTypeExpr(x, out _) &&
                        (InferType(x) is "System.Type" or "global::System.Type" || x is NameExpr nameValue && IsLocal(nameValue.Id))).ToList();
                    var types = specs.Except(aliases).SelectMany(x => CatchTypes(x, h)).Distinct().ToList();
                    IEnumerable<string> AliasTests(string caught) => aliases.Select(x =>
                        $"global::Typhon.Stdlib.builtins.isinstance({caught}, (object?){Paren(EmitValue(x))})");
                    // except Exception: not SystemExit, KeyboardInterrupt or GeneratorExit (they derive from BaseException only)
                    bool exceptException = specs.Any(x => x is NameExpr xn && xn.Id == "Exception" && !IsShadowed("Exception"));
                    if (exceptException)
                    {
                        string v = csName ?? NewTmp("e");
                        var others = types.Where(x => x != "Exception" && x != "global::System.Exception" && x != "System.Exception")
                            .Select(x => $"{Esc(v)} is {x}").Concat(AliasTests(Esc(v)));
                        _w.Line($"catch (Exception {Esc(v)}) when ({Esc(v)} is not global::Typhon.Runtime.IPyBaseException{string.Concat(others.Select(o => " || " + o))})");
                    }
                    else if (aliases.Count == 0 && types.Count == 1 && !types[0].StartsWith("("))
                        _w.Line(name == null ? $"catch ({types[0]})" : $"catch ({types[0]} {Esc(csName)})");
                    else
                    {
                        string v = csName ?? NewTmp("e");
                        var tests = types.Select(x => $"{Esc(v)} is {x}").Concat(AliasTests(Esc(v)));
                        _w.Line($"catch (Exception {Esc(v)}) when ({string.Join(" || ", tests)})");
                    }
                }
                string saved = null;
                if (renamed) { _renames.TryGetValue(name, out saved); _renames[name] = csName; }
                // except CycleError as e: in the handler, e is a CycleError (e.args, its attributes)
                bool hadType = false; string oldType = null;
                if (name != null)
                {
                    hadType = _scope.Types.TryGetValue(name, out oldType);
                    _scope.Types[name] = h.Type != null && TryTypeExpr(h.Type, out var caught) && !(h.Type is TupleExpr) ? caught : "global::System.Exception";
                }
                _w.Open();
                EmitBlockStatements(h.Body);
                if (hasElse && !EndsWithJump(h.Body)) { _w.Line($"goto {after};"); usedAfter = true; }
                _w.Close();
                if (name != null) { if (hadType) _scope.Types[name] = oldType; else _scope.Types.Remove(name); }
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
            // context managers with Python's protocol (__enter__ / __exit__): one nested protocol block per item
            if (w.Items.Any(i => UsesEnterProtocol(i.Context, w.IsAsync)))
            {
                EmitWithProtocol(w, 0);
                return;
            }
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

        /// <summary>
        /// A context manager that has __enter__ / __exit__ (a Typhon class, a runtime type such as contextlib's) or whose type
        /// is not known: Python's protocol. A typed .NET IDisposable (an open() file) keeps C#'s using.
        /// </summary>
        bool UsesEnterProtocol(Expr ctx, bool isAsync = false)
        {
            string enter = isAsync ? "__aenter__" : "__enter__";
            string t = InferType(ctx);
            if (t == null || t == "dynamic" || t == "object" || t == "object?") return true;
            if (ClassKeyOfType(t, out _) is string key && ClassDefOfKey(key) is ClassDef cd) return DefinesInHierarchy(cd, enter);
            if (_cls != null && t == _cls.CsSelfType) return DefinesInHierarchy(_cls.Def, enter);
            return DotNetMeta(t) is string meta && _r.GetMembers(meta, enter).Count > 0;
        }

        /// <summary>
        /// with cm as x: body — x = cm.__enter__(); the body runs; an exception calls cm.__exit__(type, exc, None) and is
        /// raised again unless that returns a true value; otherwise cm.__exit__(None, None, None) runs (also after return,
        /// break and continue). Late-bound managers go through the runtime (which also accepts any IDisposable).
        /// </summary>
        void EmitWithProtocol(WithStmt w, int index)
        {
            if (index == w.Items.Count) { EmitBlockStatements(w.Body); return; }
            var item = w.Items[index];
            string t = InferType(item.Context);
            bool isAsync = w.IsAsync;
            bool typed = t != null && t != "dynamic" && !t.StartsWith("object") && UsesEnterProtocol(item.Context, isAsync) && !(DotNetMeta(t) is null && ClassKeyOfType(t, out _) == null && !(_cls != null && t == _cls.CsSelfType));
            bool protocol = UsesEnterProtocol(item.Context, isAsync);
            string cm = NewTmp("cm"), raised = NewTmp("raised"), e = NewTmp("e");
            _w.Open();
            if (!protocol)
            {
                // this item is a plain IDisposable: a using block around the rest
                string kw = isAsync ? "await using" : "using";
                if (item.Var is NameExpr un && IsInline(w, un.Id)) { _w.Line($"{kw} (var {Esc(un.Id)} = {EmitExpr(item.Context)})"); SetType(un.Id, t); }
                else if (item.Var is NameExpr un2) _w.Line($"{kw} ({EmitName(un2)} = {EmitExpr(item.Context)})");
                else _w.Line($"{kw} ({EmitExpr(item.Context)})");
                _w.Open();
                EmitWithProtocol(w, index + 1);
                _w.Close();
                _w.Close();
                return;
            }
            _w.Line($"var {cm} = {EmitExpr(item.Context)};");
            string enter = isAsync ? (typed ? $"(await {cm}.__aenter__())" : $"(await {Rt("__aenter")}((object?){cm}))")
                                   : typed ? $"{cm}.__enter__()" : $"{Rt("__enter")}((object?){cm})";
            string enterType = "dynamic";
            string enterName = isAsync ? "__aenter__" : "__enter__";
            // (a runtime or .NET type's __enter__: its declared result type)
            string dotnetEnter = typed && DotNetMeta(t) is string em ? _r.GetMembers(em, enterName).FirstOrDefault(x => x.IsMethod && x.Parameters.Count == 0)?.Type : null;
            if (typed && (MemberType(t, enterName, method: true) ?? dotnetEnter) is string et0)
                enterType = isAsync ? (et0.StartsWith("global::System.Threading.Tasks.Task<") || et0.StartsWith("Task<") ? GenericArg(et0) : "dynamic") : et0;
            if (item.Var is NameExpr vn)
            {
                if (IsInline(w, vn.Id)) { _w.Line($"var {Esc(vn.Id)} = {enter};"); SetType(vn.Id, enterType); }
                else _w.Line($"{EmitName(vn)} = {enter};");
            }
            else if (item.Var == null) _w.Line($"{(typed && isAsync ? "_ = " : "")}{enter};");
            else Unsupported(item.Var, "this 'with ... as' target");
            string exitNone = isAsync ? (typed ? $"await {cm}.__aexit__(null, null, null)" : $"await {Rt("__aexit")}((object?){cm}, null)")
                                      : typed ? $"{cm}.__exit__(null, null, null)" : $"{Rt("__exit")}((object?){cm}, null)";
            string syncExitType = !isAsync && typed ? MemberType(t, "__exit__", method: true) ??
                (DotNetMeta(t) is string exitMeta ? _r.GetMembers(exitMeta, "__exit__").FirstOrDefault(x => x.IsMethod && x.Parameters.Count == 3)?.Type : null) : null;
            // A Python __exit__ may return None, represented by a CLR void method.
            string exitExc = isAsync ? (typed ? $"(await {cm}.__aexit__((object){e}.GetType(), {e}, null))" : $"(await {Rt("__aexit")}((object?){cm}, {e}))")
                                     : syncExitType is not (null or "void") ? $"{cm}.__exit__((object){e}.GetType(), {e}, null)" : $"{Rt("__exit")}((object?){cm}, {e})";
            _w.Line($"bool {raised} = false;");
            _w.Line("try");
            _w.Open();
            EmitWithProtocol(w, index + 1);
            _w.Close();
            _w.Line($"catch (global::System.Exception {e})");
            _w.Open();
            _w.Line($"{raised} = true;");
            _w.Line($"if (!{Rt("truthy")}((object?)({exitExc}))) throw;");
            _w.Close();
            _w.Line("finally");
            _w.Open();
            _w.Line($"if (!{raised}) {exitNone};");
            _w.Close();
            _w.Close();
        }
    }
}
