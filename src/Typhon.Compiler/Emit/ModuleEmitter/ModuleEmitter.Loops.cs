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
    // Loops: for (counting loops, lists by position, unpacking targets), while, and loop else.
    internal sealed partial class ModuleEmitter
    {
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
                    string wider = inline ? ConflictType(tn.Id) : null;
                    string prologue = inline ? $"{wider ?? "var"} {Esc(tn.Id)} = {iv};" : $"{EmitName(tn)} = {iv};";
                    if (inline) SetType(tn.Id, wider ?? "long");
                    EmitBlock(f.Body, prologue);
                }
                if (counts) _nonNegative.Remove(tn.Id);
                return;
            }

            // a list iterates by position, like Python's list iterator: the body may append to it or assign its items
            string iterType = f.IsAsync ? null : InferType(f.Iter);
            // (items unpacked into names: only items C# can deconstruct, not late-bound ones)
            if (iterType != null && iterType.StartsWith("List<") &&
                (f.Target is NameExpr || f.Target is TupleExpr tt0 && tt0.Elts.Count >= 2 &&
                 tt0.Elts.All(e => e is NameExpr) && !IsUnknownType(ElementTypeOf(iterType))))
            {
                string list = NewTmp("l"), idx = NewTmp("i");
                _w.Line($"var {list} = {EmitExpr(f.Iter)};");
                _w.Line($"for (int {idx} = 0; {idx} < {list}.Count; {idx}++)");
                string item = $"{list}[{idx}]";
                string prologue;
                if (f.Target is NameExpr ln)
                {
                    if (IsInline(f, ln.Id))
                    {
                        string wider = ConflictType(ln.Id);
                        prologue = $"{wider ?? "var"} {Esc(ln.Id)} = {item};";
                        SetType(ln.Id, wider ?? IterElementType(iterType));
                    }
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
                string loopType = IsDynamicallyDispatched(f.Iter) || iter.StartsWith(Rt("__iter_dyn") + "(")
                    ? "dynamic" : IterElementType(IterableType(f.Iter));
                if (IsInline(f, n.Id) && AssignsName(f.Body, n.Id))
                {
                    string item = NewTmp("it");
                    string wider = ConflictType(n.Id);
                    SetType(n.Id, wider ?? loopType);
                    _w.Line($"{kw} (var {item} in {iter})");
                    EmitBlock(f.Body, $"{wider ?? "var"} {Esc(n.Id)} = {item};");
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
                // (also when the loop iterates late-bound itself, for bit, char in table with table an object: __iter_dyn(table))
                if ((tup.Elts.Count < 2 || loopElem == "dynamic" || IsDynamicallyDispatched(f.Iter) || IsUnknownType(IterableType(f.Iter)) && !(f.Iter is NameExpr) ||
                     iter.StartsWith(Rt("__iter_dyn") + "(") ||
                     (loopElem == null || loopElem == "object") && f.Iter is CallExpr && UnpackPartTypes(loopElem, tup.Elts.Count) == null) &&
                    !tup.Elts.Any(e => e is StarredExpr))
                {
                    string item = NewTmp("x");
                    _w.Line($"{kw} (var {item} in {iter})");
                    _w.Open();
                    EmitUnpackedItems(tup.Elts, f, $"{Rt("__iter_dyn")}((object?){item})", "dynamic");
                    EmitBlockStatements(f.Body);
                    _w.Close();
                    return;
                }
                if (allInline)
                {
                    // for i, x in enumerate(xs) / for k, v in d.items(): the names get the tuple's element types
                    var parts = UnpackPartTypes(ElementTypeOf(IterableType(f.Iter)), tup.Elts.Count);
                    if (parts != null)
                        for (int i = 0; i < parts.Count; i++) SetType(((NameExpr)tup.Elts[i]).Id, parts[i]);
                    string pattern = DeconstructionPattern(tup);
                    if (tup.Elts.Cast<NameExpr>().Any(n => AssignsName(f.Body, n.Id)))
                    {
                        string item = NewTmp("item");
                        _w.Line($"{kw} (var {item} in {iter})");
                        EmitBlock(f.Body, $"var {pattern} = {item};");
                    }
                    else
                    {
                        _w.Line($"{kw} (var {pattern} in {iter})");
                        EmitBlock(f.Body);
                    }
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
    }
}
