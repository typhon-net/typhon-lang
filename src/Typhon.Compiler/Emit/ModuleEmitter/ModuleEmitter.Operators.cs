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
    // Operators: arithmetic with Python semantics, comparisons and equality, dunder operators of Typhon classes.
    internal sealed partial class ModuleEmitter
    {
        static readonly Dictionary<string, string> OperatorDunders = new Dictionary<string, string>
        {
            ["+"] = "add", ["-"] = "sub", ["*"] = "mul", ["/"] = "truediv", ["//"] = "floordiv", ["%"] = "mod", ["**"] = "pow",
            ["@"] = "matmul", ["&"] = "and", ["|"] = "or", ["^"] = "xor", ["<<"] = "lshift", [">>"] = "rshift",
        };

        /// <summary>
        /// The result type of l op r when l's class defines __op__ (or r's defines __rop__): that method's return type, dynamic
        /// when it returns different types, Pending while a dry run has not seen it yet. Null when no Typhon class is involved.
        /// </summary>
        string DunderOperatorType(string op, string l, string r)
        {
            if (!OperatorDunders.TryGetValue(op, out var name)) return null;
            string Of(string t, string method)
            {
                if (t == null || ClassKeyOfType(t, out _) is not string key || ClassDefOfKey(key) is not ClassDef cd || !DefinesInHierarchy(cd, method)) return null;
                if (_cls != null && t == _cls.CsSelfType && _cls.MethodReturnTypes.TryGetValue(method, out var own)) return own == "void" ? "dynamic" : own;
                if (Ti != null && Ti.MethodReturns.TryGetValue(key, out var ms) && ms.TryGetValue(method, out var rt)) return rt == "void" ? "dynamic" : rt;
                return _dry ? TypeInference.Pending : "dynamic";
            }
            return Of(l, "__" + name + "__") ?? Of(r, "__r" + name + "__");
        }

        static readonly Dictionary<string, string> OperatorMethods = new Dictionary<string, string>
        {
            ["+"] = "op_Addition", ["-"] = "op_Subtraction", ["*"] = "op_Multiply", ["/"] = "op_Division", ["%"] = "op_Modulus",
        };

        /// <summary>
        /// The result type of a .NET type's own operator for exactly these operand types (date - date is a timedelta, date -
        /// timedelta a date), from either operand's type; // goes to the runtime's typed floordiv overloads. Null if none.
        /// </summary>
        string DotNetOperatorType(string op, string l, string r)
        {
            if (IsUnknownType(l) || IsUnknownType(r) || (IsNumeric(l) || l == "bool" || l == "string") && (IsNumeric(r) || r == "bool" || r == "string")) return null;
            string Typed(string meta, string name) => _r.GetMembers(meta, name)
                .FirstOrDefault(m => m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].GenericType == l && m.Parameters[1].GenericType == r)?.Type;
            if (op == "//") return Typed("Typhon.Runtime.Builtins", "floordiv");
            if (!OperatorMethods.TryGetValue(op, out var method)) return null;
            foreach (var t in new[] { l, r })
                if (!IsNumeric(t) && t != "bool" && t != "string" && DotNetMeta(t) is string meta && Typed(meta, method) is string result) return result;
            return null;
        }

        /// <summary>The result type of a non-numeric .NET type's / operator (pathlib's Path / str), or null.</summary>
        string DivisionOperatorType(string t)
        {
            if (t == null || IsNumeric(t) || t == "bool" || t == "string" || IsUnknownType(t) || t == TypeInference.BigInt) return null;
            var meta = DotNetMeta(t);
            if (meta == null) return null;
            return _r.GetMembers(meta, "op_Division").FirstOrDefault(m => m.IsStatic && m.Parameters.Count == 2)?.Type;
        }

        /// <summary>xs + ys where both are lists but of different item types.</summary>
        bool ListConcatMismatch(BinOpExpr b)
        {
            string lt = InferType(b.Left), rt = InferType(b.Right);
            if (lt == TypeInference.Pending || rt == TypeInference.Pending || lt != null && lt.Contains(TypeInference.Pending) || rt != null && rt.Contains(TypeInference.Pending)) return false;
            // one side a list, the other a list of other items or of a type Typhon cannot tell (C# may see a different list)
            return lt != null && lt.StartsWith("List<") && (rt == null && MaybeLateBound(b.Right) || rt != null && rt.StartsWith("List<") && lt != rt) ||
                   rt != null && rt.StartsWith("List<") && lt == null && MaybeLateBound(b.Left);
        }

        static readonly HashSet<string> ArithmeticOps = new HashSet<string> { "+", "-", "*", "/", "//", "%", "**", "<<", ">>" };

        /// <summary>An operand of arithmetic: an int literal is a long, unless the other side is a narrower .NET integer.</summary>
        string Operand(Expr e, Expr other) => IsIntLiteral(e) && !IsSmallInt(InferType(other)) ? EmitValue(e) : EmitExpr(e);

        /// <summary>k when e is the int literal 2**k (k &gt;= 1), else -1.</summary>
        static int PowerOfTwoExponent(Expr e)
        {
            if (!(e is ConstExpr c) || c.Kind != ConstKind.Int || !long.TryParse(c.Raw.Replace("_", ""), out var v) || v < 2 || (v & (v - 1)) != 0) return -1;
            int k = 0; while ((1L << k) != v) k++;
            return k;
        }

        /// <summary>
        /// Python's value of an integer expression made only of literals (10**6, 50 * 10**6, 1 &lt;&lt; 20), computed at compile time,
        /// or null. Folding makes such values constants (C# default parameter values must be) and saves the pow() call.
        /// </summary>
        static BigInteger? FoldInt(Expr e)
        {
            switch (e)
            {
                case ConstExpr c when c.Kind == ConstKind.Int:
                    return long.TryParse(c.Raw.Replace("_", ""), out var lit) ? lit : (BigInteger?)null;
                case UnaryExpr u when u.Op == "-" || u.Op == "+" || u.Op == "~":
                {
                    var v = FoldInt(u.Operand);
                    if (v == null) return null;
                    return u.Op == "-" ? -v.Value : u.Op == "+" ? v.Value : -v.Value - 1;
                }
                case BinOpExpr b:
                {
                    var l = FoldInt(b.Left); if (l == null) return null;
                    var r = FoldInt(b.Right); if (r == null) return null;
                    BigInteger a = l.Value, d = r.Value;
                    switch (b.Op)
                    {
                        case "+": return a + d;
                        case "-": return a - d;
                        case "*": return a * d;
                        case "//": return d.IsZero ? (BigInteger?)null : FloorDiv(a, d);
                        case "%": return d.IsZero ? (BigInteger?)null : a - d * FloorDiv(a, d);
                        case "**": return d.Sign < 0 || d > 512 ? (BigInteger?)null : BigInteger.Pow(a, (int)d);
                        case "<<": return d.Sign < 0 || d > 512 ? (BigInteger?)null : a << (int)d;
                        case ">>": return d.Sign < 0 ? (BigInteger?)null : d > 512 ? (a.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero) : a >> (int)d;
                        case "&": return a & d;
                        case "|": return a | d;
                        case "^": return a ^ d;
                    }
                    return null;
                }
                default: return null;
            }
        }

        static BigInteger FloorDiv(BigInteger a, BigInteger b)
        {
            var q = BigInteger.DivRem(a, b, out var rem);
            return !rem.IsZero && (rem.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
        }

        string EmitBinOp(BinOpExpr b)
        {
            if (_m.IsGdScript && GdEmitBinOp(b) is string gd) return gd;
            // literal-only integer arithmetic is folded when the result fits 64 bits (otherwise it raises OverflowError at run time)
            if (FoldInt(b) is BigInteger folded && (_big || folded >= long.MinValue && folded <= long.MaxValue))
                return IntConstant(folded);
            string l = Operand(b.Left, b.Right), r = Operand(b.Right, b.Left);
            string lType = InferType(b.Left), rType = InferType(b.Right);
            // an IntEnum member is an int in arithmetic (Level.LOW + 1 == 2)
            if (ArithmeticOps.Contains(b.Op))
            {
                if (IsIntEnumType(lType)) { l = $"((long){Paren(l)})"; lType = "long"; }
                if (IsIntEnumType(rType)) { r = $"((long){Paren(r)})"; rType = "long"; }
            }
            if (_big && ArithmeticOps.Contains(b.Op))
            {
                const string Big = TypeInference.BigInt;
                // big-int mode: 64-bit operands (loop counters, len()) of an operator that can leave 64 bits are computed as
                // BigIntegers; a BigInteger next to a float is a float (C# has no implicit conversion between them)
                if (WideningOps.Contains(b.Op) && IsIntegral(lType) && IsIntegral(rType) && lType != Big && rType != Big && lType != "bool" && rType != "bool")
                    l = $"(({Big}){Paren(l)})";
                else if (lType == Big && (rType == "double" || rType == "float")) l = $"((double){Paren(l)})";
                else if (rType == Big && (lType == "double" || lType == "float")) r = $"((double){Paren(r)})";
            }
            // bool is an int in Python: True + True == 2 (but "%s" % True formats the bool)
            if (ArithmeticOps.Contains(b.Op) && !(b.Op == "%" && lType == "string"))
            {
                if (lType == "bool") { l = $"({Paren(l)} ? 1L : 0L)"; lType = "long"; }
                if (rType == "bool") { r = $"({Paren(r)} ? 1L : 0L)"; rType = "long"; }
            }
            // sets: - | & ^ are set algebra; dicts: | merges
            if ((b.Op == "-" || b.Op == "|" || b.Op == "&" || b.Op == "^") && (IsSetType(lType) || IsSetType(rType)))
            {
                // sets of different item types (ints and late-bound values): the operation on their items as objects
                if (!(IsSetType(lType) && IsSetType(rType) && GenericArg(lType) == GenericArg(rType)))
                    return $"{Rt(b.Op == "-" ? "sub" : b.Op == "|" ? "or_" : b.Op == "&" ? "and_" : "xor_")}((object){Paren(l)}, (object){Paren(r)})";
                return $"{Rt(b.Op == "-" ? "set_sub" : b.Op == "|" ? "set_or" : b.Op == "&" ? "set_and" : "set_xor")}({l}, {r})";
            }
            if (b.Op == "|" && IsDictType(lType) && IsDictType(rType)) return $"{Rt("dict_or")}({l}, {r})";
            // (1, 2) + (3,): tuples of fixed shape concatenate into a tuple of the combined shape
            // tuples as sequences: a PyTuple involved, or a result too long for a record tuple
            if ((b.Op == "+" || b.Op == "*") && InferType(b) is string tupleResult && tupleResult.StartsWith(PyTuplePrefix))
            {
                string te = GenericArg(tupleResult);
                if (b.Op == "+") return $"{Rt("__tupcat")}<{te}>((object?){Paren(l)}, (object?){Paren(r)})";
                bool tupleLeft = HomogeneousTupleItem(lType) != null;
                string count = tupleLeft ? r : l;
                if ((tupleLeft ? rType : lType) == TypeInference.BigInt) count = $"checked((long)({count}))";
                return $"{Rt("__tuprep")}<{te}>((object?){Paren(tupleLeft ? l : r)}, {count})";
            }
            if (b.Op == "+" && TupleElementTypes(lType) is List<string> lItems && TupleElementTypes(rType) is List<string> rItems)
            {
                var items = lItems.Select((_, i) => $"__ta.Item{i + 1}").Concat(rItems.Select((_, i) => $"__tb.Item{i + 1}"));
                return $"(({l}, {r}) switch {{ var (__ta, __tb) => ({string.Join(", ", items)}) }})";
            }
            if ((b.Op == "-" || b.Op == "|" || b.Op == "&" || b.Op == "^") && lType == "dynamic" && rType == "dynamic")
                return $"{Rt(b.Op == "-" ? "sub" : b.Op == "|" ? "or_" : b.Op == "&" ? "and_" : "xor_")}({l}, {r})";
            // x // 2**k and x % 2**k on integers are exactly an arithmetic shift and a mask in Python (floor semantics, any sign)
            if ((b.Op == "//" || b.Op == "%") && PowerOfTwoExponent(b.Right) is int k && k > 0 && IsIntegral(InferType(b.Left)))
                return b.Op == "//" ? $"({Paren(l)} >> {k})" : $"({Paren(l)} & {(1L << k) - 1})";
            switch (b.Op)
            {
                case "<<": return $"{Rt("lshift")}({l}, {r})";
                case ">>": return $"{Rt("rshift")}({l}, {r})";
                // a .NET or runtime type with its own / (Path / "name"): that operator
                case "/" when DivisionOperatorType(lType) != null: return $"({Paren(l)} / {Paren(r)})";
                // a late-bound operand: bound statically to the object overload (the run-time binder trips over the generic ones)
                case "/" when lType == "dynamic" || rType == "dynamic" || lType == null && MaybeLateBound(b.Left) || rType == null && MaybeLateBound(b.Right):
                    return $"{Rt("truediv")}((object){Paren(l)}, (object){Paren(r)})";
                case "/": return $"{Rt("truediv")}({l}, {r})";
                case "//": return $"{Rt("floordiv")}({l}, {r})";
                case "**": return $"{Rt("pow")}({l}, {r})";
                case "@": return $"{Paren(l)}.__matmul__({r})";
                case "*" when IsSequenceLiteral(b.Left) || IsSequenceLiteral(b.Right) || IsSequenceTyped(b.Left) || IsSequenceTyped(b.Right): return $"{Rt("__repeat")}({l}, {r})";
                // late-bound operands may be a str or list at run time: "ab" * 2 must repeat, not fail in the binder
                case "*" when InferType(b.Left) == "dynamic" || InferType(b.Right) == "dynamic": return $"{Rt("mul")}({l}, {r})";
                case "+" when InferType(b.Left) == "dynamic" && InferType(b.Right) == "dynamic": return $"{Rt("add")}({l}, {r})";
                // lists of different item types: a list of dynamic values (C# lists are invariant)
                case "+" when (IsListy(b.Left) || IsListy(b.Right)) && ListConcatMismatch(b): return $"{Rt("__concat_any")}({l}, {r})";
                case "+" when IsListy(b.Left) || IsListy(b.Right): return $"{Rt("concat")}({l}, {r})";
                // "%s: %d" % (name, n), "%(x)s" % d, "%5.2f" % v: printf-style formatting
                case "%" when b.Left is ConstExpr sc && sc.Kind == ConstKind.Str || InferType(b.Left) == "string":
                    if (b.Right is TupleExpr ft && !ft.Elts.Any(x => x is StarredExpr))
                        return $"{Rt("__percent_args")}({l}, new object?[] {{ {string.Join(", ", ft.Elts.Select(x => EmitValue(x)))} }})";
                    return $"{Rt("__percent")}({l}, (object?){Paren(r)})";
                case "%": return $"{Rt("mod")}({l}, {r})"; // Python modulo: result has the sign of the divisor
                default: return $"({l} {b.Op} {r})";
            }
        }

        string EmitCompare(CompareExpr c)
        {
            var parts = new List<string>();
            Expr left = c.Left;
            for (int i = 0; i < c.Ops.Count; i++)
            {
                parts.Add(EmitComparison(left, c.Ops[i], c.Comparators[i]));
                left = c.Comparators[i];
            }
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" && ", parts) + ")";
        }

        static bool IsNone(Expr e) => e is ConstExpr c && c.Kind == ConstKind.None;

        static readonly HashSet<string> NumericOrText = new HashSet<string> { "long", "int", "short", "byte", "sbyte", "ushort", "uint", "ulong", "double", "float", "decimal", "char", "bool", "string", TypeInference.BigInt };

        static bool PythonOrdered(string t) => t != null && (t == "string" || t.StartsWith("List<") || t.StartsWith("IList<") || t.StartsWith("IReadOnlyList<") ||
            IsSetType(t) || t.StartsWith("global::Typhon.Runtime.PyTuple<") || t.StartsWith("(") || t.StartsWith("ValueTuple<"));
        static bool IsUnknownType(string t) => t == null || t == "dynamic" || t == "object" || t == TypeInference.Pending;

        static bool IsNumeric(string t) => t != null && t != "char" && t != "bool" && t != "string" && NumericOrText.Contains(t);

        /// <summary>Do Python's == and C#'s == agree for these operand types? (numbers, str, bool, None, Typhon classes, enums)</summary>
        bool NativeEquality(Expr left, Expr right, string lt, string rt)
        {
            if (IsNone(left) || IsNone(right)) return true;
            if (lt != null && rt != null && NumericOrText.Contains(lt) && NumericOrText.Contains(rt))
                return lt == rt || IsNumeric(lt) && IsNumeric(rt);
            string known = lt ?? rt;
            if (known == null || known == "dynamic" || known == "object" || known == TypeInference.Pending) return false;
            bool isClass = ClassKeyOfType(known, out _) != null || (_cls != null && known == _cls.CsSelfType);
            // a class with __eq__ compares through Equals (C#'s == exists only for an __eq__ typed to the class itself)
            var cdef = ClassKeyOfType(known, out _) is string ek ? ClassDefOfKey(ek) : _cls != null && known == _cls.CsSelfType ? _cls.Def : null;
            if (cdef != null && DefinesInHierarchy(cdef, "__eq__")) return false;
            return isClass && (lt == rt || lt == null || rt == null);
        }

        string EmitComparison(Expr left, string op, Expr right)
        {
            string l = EmitExpr(left), r = EmitExpr(right);
            string lt = InferType(left), rt = InferType(right);
            // big-int mode: an int compared with a float compares as floats (C# does not convert BigInteger implicitly)
            if (lt == TypeInference.BigInt && (rt == "double" || rt == "float")) l = $"((double){Paren(l)})";
            if (rt == TypeInference.BigInt && (lt == "double" || lt == "float")) r = $"((double){Paren(r)})";
            if (op == "==" || op == "!=")
            {
                // Python compares containers and tuples by contents, C# by reference: eq() unless both sides agree
                if (NativeEquality(left, right, lt, rt)) return $"({l} {op} {r})";
                return op == "==" ? $"{Rt("eq")}({l}, {r})" : $"!{Rt("eq")}({l}, {r})";
            }
            if (op == "<" || op == "<=" || op == ">" || op == ">=")
            {
                if (lt == "string" && rt == "string") return $"(string.CompareOrdinal({l}, {r}) {op} 0)";
                // Python's own containers order by Python's rules (sequences and tuples lexicographically, sets by inclusion);
                // values of unknown type decide at run time; every other type keeps its own operators (classes, ndarray, decimal)
                if (PythonOrdered(lt) || PythonOrdered(rt) || IsUnknownType(lt) || IsUnknownType(rt))
                {
                    string helper = op == "<" ? "lt" : op == "<=" ? "le" : op == ">" ? "gt" : "ge";
                    return $"{Rt(helper)}({l}, {r})";
                }
            }
            switch (op)
            {
                case "in": return $"{Rt("contains")}({(right is TupleExpr ? EmitIterable(right) : r)}, {l})";
                case "not in": return $"!{Rt("contains")}({(right is TupleExpr ? EmitIterable(right) : r)}, {l})";
                case "is":
                case "is not":
                {
                    bool negated = op == "is not";
                    string none = negated ? "not null" : "null";
                    if (IsNone(right)) return $"((object?){Paren(l)} is {none})";
                    if (IsNone(left)) return $"((object?){Paren(r)} is {none})";
                    return $"{(negated ? "!" : "")}{Rt("__same_object")}((object?){Paren(l)}, (object?){Paren(r)})";
                }
                default: return $"({l} {op} {r})";
            }
        }

        static readonly HashSet<string> NumericTypes = new HashSet<string> { "int", "long", "double", "float", "decimal", TypeInference.BigInt };

        static bool IsIntegral(string t) => t == "long" || IsSmallInt(t) || t == TypeInference.BigInt;

        /// <summary>Big-int mode: the operators whose result can exceed 64 bits for 64-bit operands (computed as BigInteger).</summary>
        static readonly HashSet<string> WideningOps = new HashSet<string> { "+", "-", "*", "**", "<<" };
    }
}
