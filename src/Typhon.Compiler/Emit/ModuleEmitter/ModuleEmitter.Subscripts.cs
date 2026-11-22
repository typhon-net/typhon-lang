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
    // Indexing and slicing: positions that may count from the end, slices, and the non-negative analysis.
    internal sealed partial class ModuleEmitter
    {
        string EmitIndex(Expr e)
        {
            if (e is UnaryExpr u && u.Op == "-" && u.Operand is ConstExpr c && c.Kind == ConstKind.Int) return "^" + EmitConst(c);
            // "wb"[flag]: a bool is the int 0 or 1
            if (InferType(e) == "bool") return $"({Paren(EmitExpr(e))} ? 1 : 0)";
            return ModuleOrClassValue(e) ?? EmitExpr(e);
        }

        string EmitSubscript(SubscriptExpr s)
        {
            // (a key of unknown or late-bound type says nothing about the key type: max(c, key=lambda x: c[x]) before x is typed)
            if (Obs != null && !(s.Index is SliceExpr) && ElementKey(s.Target, out char rk) is string rkey && (rk == 'D' || rk == 'c') &&
                InferType(s.Index) is string rit && rit != "dynamic")
                Obs.AddElement(rkey, (PyType(InferType(s.Index)) ?? TypeInference.Unknown) + "→" + (rk == 'c' ? "long" : TypeInference.Pending));
            // a generic type used as a value, e.g. Stack[str].of(...) or Dictionary[str, int] passed around
            if (!(s.Index is SliceExpr) && TryTypeExpr(s, out var genericType)) return genericType;
            string target = EmitExpr(s.Target);
            if (s.Target is ConstExpr || s.Target is BinOpExpr || s.Target is UnaryExpr || s.Target is AwaitExpr) target = Paren(target);
            if (s.Index is SliceExpr sl)
            {
                // Python slicing semantics (copies, clamps out-of-range bounds, negative indices, steps)
                string step = sl.Step != null ? ", " + SliceBound(sl.Step) : "";
                return $"{Rt("slice")}({target}, {(sl.Lower != null ? SliceBound(sl.Lower) : "null")}, {(sl.Upper != null ? SliceBound(sl.Upper) : "null")}{step})";
            }
            string targetType = InferType(s.Target);
            // a class deriving from list: positions like a list's (a Python int, maybe negative)
            if (BuiltinBaseType(targetType) is string subBase && subBase.StartsWith("List<")) targetType = subBase;
            // move[0] of a namedtuple: its first field (typed)
            if (NamedTupleOf(targetType) is ClassDef ntc && ConstIntValue(s.Index) is long nti && nti >= -ntc.NamedTupleFields.Count && nti < ntc.NamedTupleFields.Count)
                return $"{target}.{Esc(ntc.NamedTupleFields[(int)(nti < 0 ? nti + ntc.NamedTupleFields.Count : nti)])}";
            // a late-bound container: a position (a Python int, maybe negative) or a key, resolved at run time
            // (also a late-bound key on a container of a type Typhon does not know: C# would bind its indexer at run time to an int)
            if ((targetType == "dynamic" || targetType == null && InferType(s.Index) == "dynamic") && !(s.Index is TupleExpr))
                return $"{Rt("__getitem")}((object?){target}, (object?){Paren(ModuleOrClassValue(s.Index) ?? EmitExpr(s.Index))})";
            // a[x, y] on a late-bound a: the key is the tuple (x, y) (a Typhon class's __getitem__ takes one key)
            if (targetType == "dynamic" && s.Index is TupleExpr dtk && !dtk.Parenthesized && !dtk.Elts.Any(x => x is SliceExpr))
                return $"{Rt("__getitem")}((object?){target}, (object?){EmitExpr(new TupleExpr { Elts = dtk.Elts, Parenthesized = true, Line = dtk.Line, Col = dtk.Col })})";
            // s[i] is a one-character str
            if (targetType == "string" && !(s.Index is TupleExpr)) return $"{Rt("str_at")}({target}, {EmitPosition(s.Index, targetType)})";
            // t[i] of a tuple of fixed shape: ItemN for a constant index, else the ITuple indexer (items of one type)
            var tupleItems = TupleElementTypes(targetType);
            if (tupleItems != null && !(s.Index is SliceExpr) && !(s.Index is TupleExpr))
            {
                if (ConstIntValue(s.Index) is long k && k >= -tupleItems.Count && k < tupleItems.Count) return $"{target}.Item{(k < 0 ? k + tupleItems.Count : k) + 1}";
                string common = tupleItems.Distinct().Count() == 1 ? tupleItems[0] : "object";
                string indexType = InferType(s.Index);
                string index = indexType != null && IsIntegral(indexType) && !IsDynamicallyDispatched(s.Index)
                    ? indexType == "bool" ? EmitIndex(s.Index) : SliceBound(s.Index)
                    : $"(object?){Paren(EmitValue(s.Index))}";
                return $"{Rt("tuple_at")}<{common}>({target}, {index})";
            }
            if (s.Index is TupleExpr dt && IsDictType(targetType)) return $"{target}[{EmitExpr(dt)}]"; // d[a, b]: the key is the tuple (a, b)
            // d[k] with a late-bound k on a typed dict: the key converted, so the lookup (and its result) stays static
            // (also a number of another type: d[3.0] finds the key 3, as in Python)
            if (IsDictType(targetType) && DictTypes(targetType) is List<string> skv && skv[0] != "dynamic" && !skv[0].Contains(TypeInference.Pending) &&
                !(s.Index is TupleExpr) && (InferType(s.Index) is "dynamic" or "object" or "object?" || IsNumeric(skv[0]) && InferType(s.Index) is string kit && IsNumeric(kit) && kit != skv[0] && !(skv[0] == "double" && IsIntegral(kit))))
                return $"{target}[{Rt("__key")}<{skv[0]}>((object?){Paren(ModuleOrClassValue(s.Index) ?? EmitExpr(s.Index))})]";
            // self[x, y] on a Typhon class: its __getitem__ takes one key, the tuple (x, y)
            if (s.Index is TupleExpr tk && !tk.Parenthesized && (ClassKeyOfType(targetType, out _) != null || _cls != null && targetType == _cls.CsSelfType || s.Target is NameExpr sn0 && sn0.Id == "self"))
                return $"{target}[{EmitExpr(new TupleExpr { Elts = tk.Elts, Parenthesized = true, Line = tk.Line, Col = tk.Col })}]";
            if (s.Index is TupleExpr t && !t.Parenthesized)
                return $"{target}[{string.Join(", ", t.Elts.Select(x => x is SliceExpr xs ? EmitRange(xs, targetType) : EmitPosition(x, targetType)))}]";
            // xs[i] with a late-bound i: a Python int converted, the position checked like any other (an int indexer does not
            // take a boxed long at run time)
            if (targetType != null && (targetType.StartsWith("List<") || targetType.EndsWith("[]")) && InferType(s.Index) == "dynamic")
                return $"{Rt("__at")}({target}, (long){Paren(ModuleOrClassValue(s.Index) ?? EmitExpr(s.Index))})";
            // a list or array position that may be negative counts from the end (Python's xs[-k], xs[i - 1] at i == 0)
            if (!IsIntLiteral(s.Index) && !IsNonNegative(s.Index) && targetType != null && !(s.Index is TupleExpr) && IsIntegral(InferType(s.Index)) &&
                (targetType.StartsWith("List<") || targetType.EndsWith("[]") || targetType.StartsWith("IList<") || targetType.StartsWith("IReadOnlyList<") || targetType.StartsWith(PyTuplePrefix)))
            {
                string count = targetType.EndsWith("[]") ? "Length" : "Count";
                bool simple = s.Target is NameExpr || s.Target is AttributeExpr sa && (sa.Target is NameExpr || sa.Target is AttributeExpr sa2 && sa2.Target is NameExpr);
                if (targetType.StartsWith("List<") || targetType.EndsWith("[]")) return $"{Rt("__at")}({target}, {EmitIndex(s.Index)})";
                if (simple) return $"{target}[{Rt("__pos")}({EmitIndex(s.Index)}, {target}.{count})]";
            }
            return $"{target}[{EmitPosition(s.Index, targetType)}]";
        }

        /// <summary>
        /// Names whose value cannot be negative where they are read: counters of range loops from a non-negative start with a
        /// positive step that the body does not assign. Their positions need no count-from-the-end check.
        /// </summary>
        readonly HashSet<string> _nonNegative = new HashSet<string>();

        /// <summary>
        /// Locals of a function body that only ever hold non-negative ints: every assignment stores a non-negative value
        /// (literals, len(), sums and products of such locals, += of one, range counters from a non-negative start). Computed
        /// as a greatest fixpoint: assume all, drop those with an assignment that is not shown non-negative, repeat.
        /// </summary>
        HashSet<string> AlwaysNonNegative(List<Stmt> body, IEnumerable<string> parameters)
        {
            var assigns = new List<(string Name, Expr Value, string Op)>();
            var bad = new HashSet<string>(parameters);
            var all = new HashSet<string>();
            void Bind(Expr t, Expr v, string op)
            {
                switch (t)
                {
                    case NameExpr n: all.Add(n.Id); if (v == null) bad.Add(n.Id); else assigns.Add((n.Id, v, op)); break;
                    case TupleExpr te when op == "=" && v is TupleExpr vt && vt.Elts.Count == te.Elts.Count && !te.Elts.Any(x => x is StarredExpr):
                        for (int i = 0; i < te.Elts.Count; i++) Bind(te.Elts[i], vt.Elts[i], "="); break;
                    case TupleExpr te: foreach (var x in te.Elts) Bind(x, null, op); break;
                    case ListExpr le: foreach (var x in le.Elts) Bind(x, null, op); break;
                    case StarredExpr st: Bind(st.Value, null, op); break;
                }
            }
            var nonNegativeMarker = new ConstExpr { Kind = ConstKind.Int, Raw = "0" };
            void Visit(object o)
            {
                if (o == null) return;
                if (o is System.Collections.IEnumerable list && !(o is string)) { foreach (var i in list) Visit(i); return; }
                if (!(o is Node node)) return;
                switch (node)
                {
                    case FunctionDef fd: bad.Add(fd.Name); foreach (var g in NamesBoundIn(fd.Body, nonlocalOnly: true)) bad.Add(g); return;
                    case ClassDef cd: bad.Add(cd.Name); return;
                    case LambdaExpr _: return;
                    case ComprehensionExpr ce: foreach (var g in ce.Generators) Visit(g.Iter); return; // its targets are its own
                    case AssignStmt a: foreach (var t in a.Targets) Bind(t, a.Value, "="); break;
                    case AnnAssignStmt an: if (an.Value != null) Bind(an.Target, an.Value, "="); break;
                    case AugAssignStmt au: Bind(au.Target, au.Value, au.Op); break;
                    case NamedExpr ne: Bind(ne.Target, ne.Value, "="); break;
                    case ForStmt f:
                    {
                        // for i in range(a, b[, k > 0]): i >= a, so i is non-negative when a is
                        List<Expr> ra = null;
                        bool counter = f.Target is NameExpr && !f.IsAsync && IsBuiltinRange(f.Iter, out ra) && (ra.Count < 3 || ConstIntValue(ra[2]) is long st && st > 0);
                        Bind(f.Target, counter ? (ra.Count < 2 ? nonNegativeMarker : ra[0]) : null, "=");
                        break;
                    }
                    default:
                        foreach (var name in NamesBoundIn(new List<Stmt>(), node)) { all.Add(name); bad.Add(name); }
                        break;
                }
                foreach (var c in AstWalker.Children(node)) Visit(c);
            }
            Visit(body);
            var result = new HashSet<string>(all.Where(x => !bad.Contains(x)));
            bool changed = true;
            var saved = new HashSet<string>(_nonNegative);
            while (changed)
            {
                changed = false;
                _nonNegative.Clear(); _nonNegative.UnionWith(result);
                foreach (var (name, value, op) in assigns)
                {
                    if (!result.Contains(name)) continue;
                    // op: "=" for assignments, the operator of augmented ones (x += v is "+")
                    bool ok = op == "=" || op == "+" || op == "*" || op == "//" || op == ">>" ? IsNonNegative(value)
                            : op == "%" && value is ConstExpr mc && mc.Kind == ConstKind.Int && mc.Raw != "0";
                    if (!ok) { result.Remove(name); changed = true; }
                }
            }
            _nonNegative.Clear(); _nonNegative.UnionWith(saved);
            return result;
        }

        /// <summary>Names a statement binds other than by assignment (with/except/import/def/class/global/del/match), or nonlocal/global names of a nested body.</summary>
        static IEnumerable<string> NamesBoundIn(List<Stmt> body, Node node = null, bool nonlocalOnly = false)
        {
            if (nonlocalOnly)
            {
                var names = new List<string>();
                AstWalker.VisitAll(body, n => { if (n is GlobalStmt g) names.AddRange(g.Names); });
                return names;
            }
            switch (node)
            {
                case WithStmt w: return w.Items.Where(i => i.Var != null).SelectMany(i => NamesIn(i.Var));
                case ExceptHandler h when h.Name != null: return new[] { h.Name };
                case ImportStmt im: return im.Names.Select(a => a.AsName ?? a.Name.Split('.')[0]);
                case ImportFromStmt imf: return imf.Names.Select(a => a.AsName ?? a.Name);
                case GlobalStmt g: return g.Names;
                case DelStmt d: return d.Targets.SelectMany(NamesIn);
                case MatchStmt m: return m.Cases.SelectMany(c => LocalsAnalyzer.PatternCaptures(c.Pattern, null).Select(kv => kv.Key));
                default: return Enumerable.Empty<string>();
            }
        }

        static IEnumerable<string> NamesIn(Expr t)
        {
            switch (t)
            {
                case NameExpr n: yield return n.Id; break;
                case TupleExpr te: foreach (var x in te.Elts) foreach (var y in NamesIn(x)) yield return y; break;
                case ListExpr le: foreach (var x in le.Elts) foreach (var y in NamesIn(x)) yield return y; break;
                case StarredExpr s: foreach (var y in NamesIn(s.Value)) yield return y; break;
            }
        }

        /// <summary>Does the statement (anywhere inside, nested scopes included) bind the name?</summary>
        static bool BindsName(Stmt s, string name)
        {
            bool found = false;
            AstWalker.VisitAll(s, n =>
            {
                if (found) return;
                switch (n)
                {
                    case AssignStmt a: found = a.Targets.Any(t => NamesIn(t).Contains(name)); break;
                    case AugAssignStmt au: found = NamesIn(au.Target).Contains(name); break;
                    case AnnAssignStmt an: found = NamesIn(an.Target).Contains(name); break;
                    case ForStmt f: found = NamesIn(f.Target).Contains(name); break;
                    case NamedExpr ne: found = ne.Target.Id == name; break;
                    case FunctionDef fd: found = fd.Name == name; break;
                    case ClassDef cd: found = cd.Name == name; break;
                    default: found = NamesBoundIn(null, n).Contains(name); break;
                }
            });
            return found;
        }

        bool IsNonNegative(Expr e)
        {
            switch (e)
            {
                case ConstExpr c: return c.Kind == ConstKind.Int;
                case NameExpr n: return _nonNegative.Contains(n.Id);
                case BinOpExpr b when b.Op == "+" || b.Op == "*" || b.Op == "//": return IsNonNegative(b.Left) && IsNonNegative(b.Right);
                case BinOpExpr b when b.Op == "%": return IsNonNegative(b.Right) && b.Right is ConstExpr;
                case CallExpr c when c.Func is NameExpr f && f.Id == "len" && !IsShadowed("len"): return true;
                default: return false;
            }
        }

        /// <summary>A slice bound (or other 64-bit position argument of the runtime); in big-int mode a BigInteger is narrowed, checked.</summary>
        string SliceBound(Expr e) => _big && !IsIntLiteral(e) && InferType(e) == TypeInference.BigInt ? $"checked((long)({EmitExpr(e)}))"
            // a late-bound bound: converted, so the slice binds statically and keeps its type
            : InferType(e) == "dynamic" ? $"((long?)({EmitExpr(e)}))" : EmitExpr(e);

        string EmitRange(SliceExpr sl, string targetType)
        {
            if (sl.Step != null) Unsupported(sl, "slice steps inside multi-dimensional subscripts");
            return (sl.Lower != null ? EmitPosition(sl.Lower, targetType) : "") + ".." + (sl.Upper != null ? EmitPosition(sl.Upper, targetType) : "");
        }

        /// <summary>
        /// An index into a value of type <paramref name="targetType"/>. Lists, arrays, strings (and .NET indexers in general)
        /// take an int position: a Python int (long) is narrowed, checked. Dicts and Typhon classes take Python values as is.
        /// </summary>
        string EmitPosition(Expr index, string targetType)
        {
            string code = EmitIndex(index);
            if (IsIntLiteral(index) || !IsPositional(targetType)) return code;
            string it = InferType(index);
            bool sequence = targetType != null && (targetType == "string" || targetType.EndsWith("[]") || targetType.StartsWith("List<") ||
                                                    targetType.StartsWith("IList<") || targetType.StartsWith("IReadOnlyList<") || targetType.StartsWith(PyTuplePrefix));
            // a list/array/str position is an int: narrow anything that is not already one (long, dynamic, unknown)
            bool narrow = sequence ? !IsSmallInt(it) : it == "long" || it == "ulong" || it == TypeInference.BigInt;
            return narrow ? $"checked((int)({code}))" : code;
        }

        bool IsPositional(string t)
        {
            if (t == null || t == "dynamic") return true;
            if (IsDictType(t)) return false;
            if (_cls != null && t == _cls.CsSelfType) return false;
            return ClassKeyOfType(t, out _) == null;
        }

        bool IsSequenceTyped(Expr e) { var t = InferType(e); return t == "string" || (t != null && t.StartsWith("List<")); }

        static bool IsSequenceLiteral(Expr e) => e is ConstExpr c && c.Kind == ConstKind.Str || e is FStringExpr || e is ListExpr;
    }
}
