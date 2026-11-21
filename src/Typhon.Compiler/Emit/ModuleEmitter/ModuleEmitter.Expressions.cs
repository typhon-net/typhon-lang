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
    // Expressions: the dispatch, constants, f-strings, attributes and conditions.
    internal sealed partial class ModuleEmitter
    {
        static string Paren(string code) => code.StartsWith("(") && code.EndsWith(")") && BalancedOuter(code) ? code : "(" + code + ")";

        static bool BalancedOuter(string code)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0 && i != code.Length - 1) return false; }
            }
            return true;
        }

        bool IsBoolish(Expr e)
        {
            switch (e)
            {
                case CompareExpr _: return true;
                case BoolOpExpr b: return b.Values.All(IsBoolish);
                case UnaryExpr u when u.Op == "not": return true;
                case ConstExpr c: return c.Kind == ConstKind.Bool;
                case CallExpr c when c.Func is NameExpr fn && !IsShadowed(fn.Id):
                    return fn.Id == "isinstance" || fn.Id == "bool" || fn.Id == "any" || fn.Id == "all" || fn.Id == "callable" || fn.Id == "hasattr";
                case CallExpr c when c.Func is AttributeExpr fa:
                    return fa.Name == "startswith" || fa.Name == "endswith" || fa.Name == "isdigit" || fa.Name == "isalpha" || fa.Name == "isspace" ||
                           fa.Name == "isupper" || fa.Name == "islower" || fa.Name == "isalnum" || fa.Name == "Contains" || fa.Name == "StartsWith" || fa.Name == "EndsWith";
                case NameExpr n: return KnownType(n.Id) == "bool";
                default: return false;
            }
        }

        /// <summary>A condition: bools stay bools, anything else gets Python truthiness.</summary>
        string Cond(Expr e)
        {
            if (e is ConstExpr c && c.Kind == ConstKind.Bool) return c.Raw == "True" ? "true" : "false";
            // while 1: an infinite loop C# sees as one (so code after it is unreachable, not a missing return)
            if (ConstIntValue(e) is long ci) return ci != 0 ? "true" : "false";
            // in a condition, and/or need only the truth of each operand
            if (e is BoolOpExpr bo) return "(" + string.Join(bo.Op == "and" ? " && " : " || ", bo.Values.Select(v => Paren(Cond(v)))) + ")";
            string code = EmitExpr(e);
            return IsBoolish(e) ? code : $"{Rt("truthy")}({code})";
        }

        internal string EmitExpr(Expr e, bool targetTyped = false)
        {
            switch (e)
            {
                case null: return "default";
                case ConstExpr c: return EmitConst(c);
                case NameExpr n when _narrowed.Contains(n.Id) && KnownTypeCore(n.Id) is string ot && ot.EndsWith("?"): return EmitName(n) + ".Value";
                case NameExpr fv when FunctionValue(fv, typed: _fnTypedUse) is string fvalue: return fvalue;
                // a Typhon class as a value ((C, (), state), a dict of classes): its type object
                case NameExpr cv when !IsLocal(cv.Id) && !_moduleFieldTypes.ContainsKey(cv.Id) && !_renames.ContainsKey(cv.Id) &&
                                      TryResolveType(cv.Id, 0, out var cvMeta, out var cvClass) && cvClass != null && cvClass.TypeParams.Count == 0:
                    return $"typeof({EmitName(cv)})";
                case NameExpr n: return EmitName(n);
                case FStringExpr f: return EmitFString(f);
                case AttributeExpr a: return EmitAttribute(a);
                case CallExpr c: return EmitCall(c, targetTyped);
                case SubscriptExpr s: return EmitSubscript(s);
                case BinOpExpr b: return EmitBinOp(b);
                case UnaryExpr u:
                    if (u.Op == "not") return "!" + Paren(Cond(u.Operand));
                    if (u.Op == "-" && u.Operand is ConstExpr nc && (nc.Kind == ConstKind.Int || nc.Kind == ConstKind.Float)) return "-" + EmitConst(nc);
                    if (InferType(u.Operand) == "bool") return u.Op + $"({Paren(EmitExpr(u.Operand))} ? 1L : 0L)";
                    return u.Op + Paren(EmitExpr(u.Operand));
                case BoolOpExpr b:
                {
                    if (b.Values.All(IsBoolish)) return "(" + string.Join(b.Op == "and" ? " && " : " || ", b.Values.Select(v => Paren(Cond(v)))) + ")";
                    // x or y / x and y as values: the operand that decides (previous = previous or set(), name = name or "anon");
                    // operands of different types (0 or None, 2 and "yes") are late-bound
                    string bt = InferType(b);
                    bool late = bt == null || bt == "dynamic";
                    // (an empty {} / [] takes the other operand's type: counts = counts or {})
                    string Operand(Expr x, bool last = false) => late ? $"(dynamic){Paren(EmitExpr(x))}"
                        : IsEmptyLiteral(x) && !last && bt.EndsWith(">") ? $"new {bt}()" // ([] and xs: tested with 'is var', so it needs a type)
                        : EmitExpr(x, targetTyped || IsEmptyLiteral(x));
                    string code = Operand(b.Values[b.Values.Count - 1], last: true);
                    for (int i = b.Values.Count - 2; i >= 0; i--)
                    {
                        string t = NewTmp("v");
                        string test = IsBoolish(b.Values[i]) ? t : $"{Rt("truthy")}({t})";
                        code = b.Op == "or" ? $"({Operand(b.Values[i])} is var {t} && {test} ? {t} : {code})"
                                            : $"({Operand(b.Values[i])} is var {t} && !{test} ? {t} : {code})";
                    }
                    return code;
                }
                case CompareExpr c: return EmitCompare(c);
                case IfExpr i when ConditionalNeedsDynamic(i): return $"({Cond(i.Test)} ? (dynamic?){Paren(EmitExpr(i.Body))} : (dynamic?){Paren(EmitExpr(i.OrElse))})";
                case IfExpr i: return $"({Cond(i.Test)} ? {EmitExpr(i.Body, targetTyped)} : {EmitExpr(i.OrElse, targetTyped)})";
                case LambdaExpr l:
                {
                    _lambdaParams.Push(new HashSet<string>(l.Params));
                    string body = EmitExpr(l.Body, targetTyped: true);
                    _lambdaParams.Pop();
                    string ps = l.Params.Count == 1 ? Esc(l.Params[0]) : "(" + string.Join(", ", l.Params.Select(Esc)) + ")";
                    return $"({ps} => {body})";
                }
                case NamedExpr ne: return $"({EmitName(ne.Target)} = {EmitValue(ne.Value)})";
                case TupleExpr t:
                    // (*a, b): a tuple of computed length
                    if (t.Elts.Any(x => x is StarredExpr)) return $"{Rt("tuple")}({EmitSpread(t.Elts, "List", t)})";
                    if (t.Elts.Count == 0) return "ValueTuple.Create()";
                    if (t.Elts.Count == 1) return $"ValueTuple.Create({EmitValue(t.Elts[0])})";
                    if (InferType(t) is string tpt && tpt.StartsWith(PyTuplePrefix)) return EmitAsPyTuple(t, tpt);
                    // (None, None) where nothing gives the tuple a type: C# needs typed nulls
                    if (!targetTyped && t.Elts.Any(x => x is ConstExpr nc && nc.Kind == ConstKind.None))
                        return "(" + string.Join(", ", t.Elts.Select(x => x is ConstExpr nc2 && nc2.Kind == ConstKind.None ? "(object?)null" : EmitValue(x))) + ")";
                    return "(" + string.Join(", ", t.Elts.Select(x => EmitValue(x))) + ")";
                case ListExpr l: return EmitList(l, targetTyped);
                case SetExpr s:
                    if (s.Elts.Any(x => x is StarredExpr)) return EmitSpread(s.Elts, "HashSet", s);
                    return $"{Rt("__set")}({string.Join(", ", s.Elts.Select(x => EmitValue(x)))})";
                case DictExpr d: return EmitDict(d, targetTyped);
                case ComprehensionExpr c: return EmitComprehension(c);
                case AwaitExpr a: return $"(await {EmitExpr(a.Value)})";
                // (yield v) inside an expression: the async form awaits it; its value is what send() passes in
                case YieldExpr y when _scope != null && _scope.AsyncForm && !y.From: return $"(await {GenVar}.Yield({YieldedValue(y)}))";
                case YieldExpr y: Unsupported(y, "using 'yield' as an expression"); return "default";
                case StarredExpr s: Unsupported(s, "starred expressions here"); return "default";
                case SliceExpr s: Error(s, "slice outside of a subscript"); return "default";
                default: Unsupported(e, e.GetType().Name); return "default";
            }
        }

        static bool IsIntLiteral(Expr e) =>
            e is ConstExpr c && c.Kind == ConstKind.Int || e is UnaryExpr u && u.Op == "-" && u.Operand is ConstExpr uc && uc.Kind == ConstKind.Int;

        /// <summary>A C# integer type narrower than long (from .NET APIs or explicit int32-style annotations).</summary>
        internal static bool IsSmallInt(string t) => t == "int" || t == "short" || t == "byte" || t == "sbyte" || t == "ushort" || t == "uint";

        /// <summary>The type a value has once it is a Python value: .NET's narrower integers widen to Python's int (long).</summary>
        internal string PyType(string t) => IsSmallInt(t) || _big && t == "long" ? IntT : t;

        /// <summary>
        /// Python's int is C#'s long, but a C# integer literal is an int: wherever a literal decides a type ('var', element
        /// types of collection literals, arithmetic) it gets the L suffix. Indexes and .NET arguments keep plain literals,
        /// which convert to int, long or whatever the parameter takes.
        /// </summary>
        string EmitValue(Expr e, bool targetTyped = false)
        {
            switch (e)
            {
                case ConstExpr c when c.Kind == ConstKind.Int: return IntLiteral(EmitConst(c));
                case UnaryExpr u when u.Op == "-" && u.Operand is ConstExpr uc && uc.Kind == ConstKind.Int: return "-" + IntLiteral(EmitConst(uc));
                case IfExpr i when ConditionalNeedsDynamic(i): return EmitExpr(i);
                case IfExpr i: return $"({Cond(i.Test)} ? {EmitValue(i.Body, targetTyped)} : {EmitValue(i.OrElse, targetTyped)})";
                default: return EmitExpr(e, targetTyped);
            }
        }

        /// <summary>An int literal where it decides the type: 5L, or in big-int mode a BigInteger.</summary>
        string IntLiteral(string code) =>
            !_big ? LongLiteral(code) : code.StartsWith("global::") ? code : $"(({TypeInference.BigInt}){code})";

        /// <summary>A compile-time integer as Python's int: a long constant, or in big-int mode a BigInteger of any size.</summary>
        string IntConstant(BigInteger v)
        {
            if (_big)
                return v >= long.MinValue && v <= long.MaxValue ? $"(({TypeInference.BigInt}){(v == long.MinValue ? "long.MinValue" : v + "L")})"
                                                                : $"{TypeInference.BigInt}.Parse(\"{v}\")";
            return v == long.MinValue ? "long.MinValue" : v.Sign < 0 ? $"({v}L)" : $"{v}L";
        }

        static string LongLiteral(string code) => code.Length > 0 && char.IsDigit(code[code.Length - 1]) ? code + "L" : code;

        string EmitConst(ConstExpr c)
        {
            switch (c.Kind)
            {
                case ConstKind.None: return "null";
                case ConstKind.Bool: return c.Raw == "True" ? "true" : "false";
                case ConstKind.Str: return CsString(c.Str);
                case ConstKind.Bytes: return $"global::Typhon.Runtime.PyBytes.Latin1({CsString(c.Str)})";
                case ConstKind.Ellipsis: return "default";
                case ConstKind.Float:
                {
                    string raw = c.Raw.Replace("_", "");
                    if (raw.EndsWith(".")) raw += "0";
                    raw = raw.Replace(".e", ".0e").Replace(".E", ".0E");
                    if (raw.StartsWith(".")) raw = "0" + raw;
                    if (!raw.Contains(".") && !raw.Contains("e") && !raw.Contains("E")) raw += ".0";
                    return raw;
                }
                case ConstKind.Int:
                {
                    string raw = c.Raw.Replace("_", "");
                    BigInteger value;
                    if (raw.StartsWith("0o") || raw.StartsWith("0O"))
                    {
                        value = 0;
                        foreach (char ch in raw.Substring(2)) value = value * 8 + (ch - '0');
                    }
                    else if (raw.StartsWith("0x") || raw.StartsWith("0X")) value = BigInteger.Parse("0" + raw.Substring(2), NumberStyles.HexNumber);
                    else if (raw.StartsWith("0b") || raw.StartsWith("0B")) { value = 0; foreach (char ch in raw.Substring(2)) value = value * 2 + (ch - '0'); }
                    else value = BigInteger.Parse(raw);
                    if (value > ulong.MaxValue) return $"global::System.Numerics.BigInteger.Parse(\"{value}\")";
                    if (value > long.MaxValue) return value + "UL";
                    return value.ToString();
                }
            }
            return "default";
        }

        internal static string CsString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        // C# ends a line (and so a string literal) at U+0085, U+2028 and U+2029 too
                        if (ch < 0x20 || ch == '\u0085' || ch == '\u2028' || ch == '\u2029') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        string EmitFString(FStringExpr f)
        {
            var sb = new StringBuilder("$\"");
            foreach (var p in f.Parts)
            {
                if (p.Literal != null)
                {
                    string lit = CsString(p.Literal);
                    sb.Append(lit.Substring(1, lit.Length - 2).Replace("{", "{{").Replace("}", "}}"));
                    continue;
                }
                string code = EmitExpr(p.Expr);
                string hole;
                if (p.FormatSpec != null)
                {
                    if (p.FormatSpec.Contains("{")) Unsupported(f, "nested replacement fields in f-string format specs");
                    hole = $"{Rt("format")}({code}, {CsString(p.FormatSpec)})";
                    if (p.Conversion == 'r') hole = $"{Rt("format")}({Rt("repr")}({code}), {CsString(p.FormatSpec)})";
                }
                else hole = p.Conversion == 'r' ? $"{Rt("repr")}({code})" : $"{Rt("str")}({code})";
                sb.Append('{').Append(hole).Append('}');
            }
            return sb.Append('"').ToString();
        }

        /// <summary>The C# class that declares a class variable (a static field) of this name: the class or a Typhon base.</summary>
        string StaticOwner(ClassDef c, string name, int depth)
        {
            if (c == null || depth > 20) return null;
            bool declares = Ti != null && _p.TryFindClass(_m, c.Name, out var sc0, out var so0) && sc0 == c && Ti.ExternalFields.ContainsKey(TypeInference.ClassKey(so0, c.Name) + "|static|" + name) ||
                            c.Body.Any(st => st is AssignStmt sa && sa.Targets.Count == 1 && sa.Targets[0] is NameExpr sn && sn.Id == name ||
                                             st is AnnAssignStmt aa && aa.Target is NameExpr an && an.Id == name && aa.Annotation is SubscriptExpr cv &&
                                             (DottedName(cv.Target) == "ClassVar" || DottedName(cv.Target) == "typing.ClassVar"));
            // a method or an instance field of that name in the class wins
            if (!declares) foreach (var b in c.Bases) if (ResolveBase(b, out var bc, out _) && bc != null && StaticOwner(bc, name, depth + 1) is string o) return o;
            if (!declares) return null;
            if (_cls != null && c == _cls.Def) return _cls.CsSelfType;
            return c.TypeParams.Count == 0 ? Esc(c.Name) : null;
        }

        string EmitAttribute(AttributeExpr a)
        {
            if (a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super" && sc.Args.Count == 0) return "base." + Esc(a.Name);
            // np.random.random(...): C# forbids a member named like its class, the numpy library exposes it as random_sample
            if (a.Name == "random" && a.Target is AttributeExpr inner && inner.Name == "random" && DottedName(inner.Target) is string mod &&
                (_dotnetAliases.TryGetValue(mod, out var target0) ? target0 : mod) == "Typhon.Stdlib.numpy")
                return EmitExpr(a.Target) + ".random_sample";
            // type(x).__name__, x.__class__, SomeClass.__name__
            if (a.Name == "__class__") return Paren(EmitExpr(a.Target)) + ".GetType()";
            if (a.Name == "__name__")
            {
                if (a.Target is CallExpr tc && tc.Func is NameExpr tn && tn.Id == "type" && !IsShadowed("type")) return EmitExpr(a.Target) + ".Name";
                if (a.Target is AttributeExpr ca && ca.Name == "__class__") return EmitExpr(a.Target) + ".Name";
                if (DottedName(a.Target) is string tdn && TryTypeExpr(a.Target, out _)) return CsString(tdn.Split('.').Last());
            }
            // obj.__dict__: a live view of its attributes
            if (a.Name == "__dict__" && !(a.Target is NameExpr dn0 && TryResolveType(dn0.Id, 0, out _, out _)))
                return $"{Rt("__dict__")}((object){Paren(EmitExpr(a.Target))})";
            // self.m1 / self.__class__.m1 / type(self).m1 where m1 is a class variable (a static field): read through the class
            bool selfClass = a.Target is NameExpr selfRef && selfRef.Id == "self" ||
                             a.Target is AttributeExpr sca && sca.Name == "__class__" && sca.Target is NameExpr scs && scs.Id == "self" ||
                             a.Target is CallExpr stc && stc.Func is NameExpr stn && stn.Id == "type" && stc.Args.Count == 1 && stc.Args[0].Value is NameExpr sts && sts.Id == "self";
            if (selfClass && HasSelfInScope() && _cls != null && StaticOwner(_cls.Def, a.Name, 0) is string owner)
                return owner + "." + Esc(a.Name);
            string target = a.Target is ConstExpr ic && ic.Kind == ConstKind.Int ? EmitValue(a.Target) : a.Target is NameExpr tn0 ? EmitName(tn0) : EmitExpr(a.Target);
            if (a.Target is ConstExpr || a.Target is UnaryExpr || a.Target is BinOpExpr || a.Target is AwaitExpr) target = Paren(target);
            return target + "." + Esc(a.Name);
        }

        /// <summary>The literal mode of open(path, mode) ("r" when omitted; null when not a literal).</summary>
        static string OpenMode(CallExpr c)
        {
            var mode = c.Args.Count > 1 && c.Args[1].Name == null ? c.Args[1].Value : c.Args.FirstOrDefault(a => a.Name == "mode")?.Value;
            return mode == null ? "r" : mode is ConstExpr mc && mc.Kind == ConstKind.Str ? mc.Str : null;
        }

        static bool IsSimpleOperand(Expr e) => e is NameExpr || e is AttributeExpr a && IsSimpleOperand(a.Target);

        static bool IsEmptyLiteral(Expr e) => e is ListExpr l && l.Elts.Count == 0 || e is DictExpr d && d.Keys.Count == 0 ||
                                              e is CallExpr c && c.Args.Count == 0 && c.Func is NameExpr n && (n.Id == "list" || n.Id == "dict" || n.Id == "set");

        /// <summary>Cheap local type inference, used where C# 'var' cannot be used (hoisted locals, fields).</summary>
        /// <summary>a if c else b with branches of different types C# has no common type for (a range and a list, a str and an int).</summary>
        bool ConditionalNeedsDynamic(IfExpr i)
        {
            if (IsNone(i.Body) || IsNone(i.OrElse)) return false;
            string a = InferType(i.Body), o = InferType(i.OrElse);
            if (a == null || o == null || a == o || a == "dynamic" || o == "dynamic" || a == TypeInference.Pending || o == TypeInference.Pending) return false;
            if (a.Contains(TypeInference.Pending) || o.Contains(TypeInference.Pending)) return false;
            if (IsNumeric(a) && IsNumeric(o)) return false;
            if (a == o + "?" || o == a + "?") return false;
            // a Typhon class and its base: C# converts the subclass to the base
            if (DerivesFromType(a, o) || DerivesFromType(o, a)) return false;
            return true;
        }
    }
}
