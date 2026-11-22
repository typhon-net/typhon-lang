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
            if (IsTypingUnion(e) && !Receivers.Contains(e)) return $"typeof({RuntimeUnionType})";
            switch (e)
            {
                case null: return "default";
                case ConstExpr c: return EmitConst(c);
                case NameExpr n when _narrowed.Contains(n.Id) && KnownTypeCore(n.Id) is string ot && ot.EndsWith("?"): return EmitName(n) + ".Value";
                case NameExpr ia when ImportAlias(ia) is Expr importTarget: return EmitExpr(importTarget, targetTyped);
                case NameExpr method when ClassBodyMethod(method):
                    return $"new global::Typhon.Runtime.PyBoundMethod(typeof({_cls.CsSelfType}), {CsString(method.Id)}, includeNonPublic: true)";
                case NameExpr fv when FunctionValue(fv, typed: _fnTypedUse) is string fvalue: return fvalue;
                // a nested function as a value where no delegate type is expected (returned as dynamic, stored, a decorator's result)
                case NameExpr lfv when !targetTyped && LocalFunctionValue(lfv) is string closure: return closure;
                // a Typhon class as a value ((C, (), state), a dict of classes): its type object
                case NameExpr cv when !IsLocal(cv.Id) && !_moduleFieldTypes.ContainsKey(cv.Id) && !_renames.ContainsKey(cv.Id) &&
                                      TryResolveType(cv.Id, 0, out var cvMeta, out var cvClass) && cvClass != null && cvClass.TypeParams.Count == 0:
                    return $"typeof({EmitName(cv)})";
                // an exception class as a value (exc_type is KeyError, a tuple of exception classes): its type object
                case NameExpr ev when ExceptionMap.TryGetValue(ev.Id, out var evType) && !IsShadowed(ev.Id) && !IsLocal(ev.Id) && !_renames.ContainsKey(ev.Id):
                    return $"typeof({evType})";
                // one of the runtime's classes imported by name, as a value (type(t) is Template): its type object
                case NameExpr rc when !Receivers.Contains(rc) && RuntimeClassValue(rc) is string rcValue: return rcValue;
                // a builtin type as a value (frozenset({int, float}), issubclass(t, type), d.get(k, object)): its type object
                case NameExpr bt when !Receivers.Contains(bt) && BuiltinTypeValue(bt) is string builtinValue:
                    return builtinValue;
                case NameExpr pm when !Receivers.Contains(pm) && ProjectModuleValue(pm) is string moduleValue: return moduleValue;
                case NameExpr n: return EmitName(n);
                case FStringExpr f: return EmitFString(f);
                // one of the runtime's classes as a value ({weakref.ref: ...}, cls is collections.deque): its type object
                case AttributeExpr ca when !Receivers.Contains(ca) && ModuleOrClassValue(ca) is string classValue: return classValue;
                case AttributeExpr a: return EmitAttribute(a);
                case CallExpr gu when _m.IsGdScript && GdUnsignedCall(gu): return $"((long){EmitCall(gu, targetTyped)})";
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
                case IfExpr i when ConditionalNeedsDynamic(i, targetTyped): return $"({Cond(i.Test)} ? (dynamic?){Paren(EmitLooseValue(i.Body))} : (dynamic?){Paren(EmitLooseValue(i.OrElse))})";
                case IfExpr i: return $"({Cond(i.Test)} ? {EmitExpr(i.Body, targetTyped)} : {EmitExpr(i.OrElse, targetTyped)})";
                case LambdaExpr l when l.RequiresArgumentBinding: return EmitRuntimeLambda(l);
                case LambdaExpr l:
                {
                    var parameters = LambdaParameters(l.Params);
                    _lambdaParams.Push(parameters);
                    try
                    {
                        string body = EmitExpr(l.Body, targetTyped: true);
                        string ps = l.Params.Count == 1 ? parameters[l.Params[0]] :
                            "(" + string.Join(", ", l.Params.Select(p => parameters[p])) + ")";
                        return $"({ps} => {body})";
                    }
                    finally { _lambdaParams.Pop(); }
                }
                case NamedExpr ne: return $"({EmitName(ne.Target)} = {EmitValue(ne.Value)})";
                case TupleExpr t:
                    // (*a, b): a tuple of computed length
                    if (t.Elts.Any(x => x is StarredExpr)) return $"{Rt("tuple")}({EmitSpread(t.Elts, "List", t)})";
                    if (InferType(t) is string tpt && tpt.StartsWith(PyTuplePrefix)) return EmitAsPyTuple(t, tpt);
                    if (t.Elts.Count == 0) return "ValueTuple.Create()";
                    if (t.Elts.Count == 1) return $"ValueTuple.Create({EmitValue(t.Elts[0])})";
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
            if (ModuleOrClassValue(e) is string objectValue) return objectValue;
            switch (e)
            {
                case NameExpr n when ValueBuiltins.Contains(n.Id) && !BuiltinTypeValues.ContainsKey(n.Id) &&
                    !IsShadowed(n.Id) && StdlibOwner(n.Id) == null &&
                    _r.GetMembers("Typhon.Stdlib.builtins", n.Id).Any(m => m.IsMethod && m.IsStatic):
                    return $"(dynamic)new global::Typhon.Runtime.PyBoundMethod(typeof(global::Typhon.Stdlib.builtins), {CsString(n.Id)})";
                case ConstExpr c when c.Kind == ConstKind.Int: return IntLiteral(EmitConst(c));
                case UnaryExpr u when u.Op == "-" && u.Operand is ConstExpr uc && uc.Kind == ConstKind.Int: return "-" + IntLiteral(EmitConst(uc));
                case IfExpr i when ConditionalNeedsDynamic(i, targetTyped): return EmitExpr(i, targetTyped);
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

        const string TemplateType = "global::Typhon.Stdlib.string_.templatelib.Template";

        /// <summary>t"...": a string.templatelib.Template of the literal parts and the interpolations (values not formatted).</summary>
        string EmitTemplate(FStringExpr f)
        {
            var args = new List<string>();
            foreach (var p in f.Parts)
            {
                if (p.Literal != null) { args.Add(CsString(p.Literal)); continue; }
                string spec = p.SpecParts != null ? EmitFString(new FStringExpr { Parts = p.SpecParts, Line = f.Line, Col = f.Col }) : CsString(p.FormatSpec ?? "");
                string conv = p.Conversion == '\0' ? "null" : CsString(p.Conversion.ToString());
                args.Add($"new global::Typhon.Stdlib.string_.templatelib.Interpolation((object?){Paren(EmitValue(p.Expr))}, {CsString(p.ExprText ?? "")}, {conv}, {spec})");
            }
            return $"new {TemplateType}({string.Join(", ", args)})";
        }

        string EmitFString(FStringExpr f)
        {
            if (f.IsTemplate) return EmitTemplate(f);
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
                    // (a spec with replacement fields of its own is built when the string is: f"{x:>{width}}")
                    string spec = p.SpecParts != null ? EmitFString(new FStringExpr { Parts = p.SpecParts, Line = f.Line, Col = f.Col }) : CsString(p.FormatSpec);
                    hole = $"{Rt("format")}({code}, {spec})";
                    if (p.Conversion == 'r') hole = $"{Rt("format")}({Rt("repr")}({code}), {spec})";
                }
                else hole = p.Conversion == 'r' ? $"{Rt("repr")}({code})" : $"{Rt("str")}({code})";
                // A fully qualified helper begins with global::, which an interpolation would parse as a format separator.
                sb.Append("{(").Append(hole).Append(")}");
            }
            return sb.Append('"').ToString();
        }

        /// <summary>The C# class that declares a class variable (a static field) of this name: the class or a Typhon base.</summary>
        /// <summary>Is self.name something the current class, its bases and their attributes do not define?</summary>
        bool SelfMemberUnknown(string name)
        {
            if (name.StartsWith("__") && name.EndsWith("__")) return false;
            if (_cls.Members.Contains(name) || _cls.FieldTypes.ContainsKey(name) || _cls.Def.Body.OfType<ClassDef>().Any(n => n.Name == name)) return false;
            if (CollectImplicitFields(_cls.Def).ContainsKey(name)) return false;
            var bases = _cls.Def.Bases.Where(b => !(DottedName(b is SubscriptExpr bs ? bs.Target : b) is "object" or "Protocol" or "typing.Protocol" or "ABC" or "abc.ABC" or "Generic" or "typing.Generic")).ToList();
            // (a base Typhon cannot see into, a .NET class it cannot resolve: assume it defines it)
            if (bases.Any(b => !ResolveBase(b, out _, out _))) return false;
            return !BaseHasMember(bases, name, requireOverridable: false);
        }

        ClassDef ClassVariableOwner(ClassDef c, string name, int depth = 0)
        {
            if (c == null || depth > 20) return null;
            bool declares = Ti != null && _p.TryFindClass(_m, c.Name, out var sc0, out var so0) && sc0 == c && Ti.ExternalFields.ContainsKey(TypeInference.ClassKey(so0, c.Name) + "|static|" + name) ||
                            c.Body.Any(st => st is AssignStmt sa && sa.Targets.Count == 1 && sa.Targets[0] is NameExpr sn && sn.Id == name ||
                                             st is AnnAssignStmt aa && aa.Target is NameExpr an && an.Id == name && aa.Annotation is SubscriptExpr cv &&
                                             (DottedName(cv.Target) == "ClassVar" || DottedName(cv.Target) == "typing.ClassVar"));
            if (declares) return c;
            if (c.Body.Any(st => st is FunctionDef method && method.Name == name ||
                st is AnnAssignStmt annotation && annotation.Target is NameExpr field && field.Id == name)) return null;
            foreach (var b in c.Bases)
                if (ResolveBase(b, out var bc, out _) && bc != null && ClassVariableOwner(bc, name, depth + 1) is ClassDef owner)
                    return owner;
            return null;
        }

        string StaticOwner(ClassDef c, string name)
        {
            var owner = ClassVariableOwner(c, name);
            if (owner == null) return null;
            if (_cls != null && owner == _cls.Def) return _cls.CsSelfType;
            return owner.TypeParams.Count == 0 ? Esc(owner.Name) : null;
        }

        // An instance can shadow a class variable even though its default is stored in a CLR static field.
        bool IsInstanceClassDefault(AttributeExpr a)
        {
            if (_m.IsGdScript) return false;
            if (a.Target is NameExpr { Id: "self" } && _cls != null && HasSelfInScope())
                return ClassVariableOwner(_cls.Def, a.Name) != null;
            string receiverType = InferType(a.Target);
            var cls = ClassKeyOfType(receiverType, out _) is string key ? ClassDefOfKey(key) : null;
            // A constructor through a project-module alias can yield a non-global qualified name.
            if (cls == null && receiverType != null && receiverType.Contains(".") && !receiverType.StartsWith("global::"))
                TryResolveType(receiverType.Replace("@", ""), 0, out _, out cls);
            return cls != null && ClassVariableOwner(cls, a.Name) != null;
        }

        /// <summary>
        /// A standard module or one of the runtime's classes used as a value (hasattr(os, "sep"), getattr(math, name),
        /// {weakref.ref: ...}): the module object (sys.modules' kind) or the type object; null for anything else. (Where a
        /// module or class is the target of an attribute or a call, its C# name is emitted instead, by the callers.)
        /// </summary>
        /// <summary>The module's expressions that are the target of an attribute or the function of a call: names, not values.</summary>
        HashSet<Expr> Receivers => _receivers ??= CollectReceivers();
        HashSet<Expr> _receivers;
        readonly HashSet<Expr> _attributeStores = new HashSet<Expr>();
        readonly HashSet<Expr> _callCallees = new HashSet<Expr>();

        bool IsAttributeStore(AttributeExpr value)
        {
            _ = Receivers;
            return _attributeStores.Contains(value);
        }

        HashSet<Expr> CollectReceivers()
        {
            var set = new HashSet<Expr>();
            void Store(Expr target)
            {
                if (target is AttributeExpr) _attributeStores.Add(target);
                if (target is TupleExpr tuple) foreach (var item in tuple.Elts) Store(item);
                if (target is ListExpr list) foreach (var item in list.Elts) Store(item);
            }
            if (_m.Ast != null)
                AstWalker.VisitAll(_m.Ast.Body, n =>
                {
                    if (n is AttributeExpr x) set.Add(x.Target);
                    else if (n is CallExpr c) { set.Add(c.Func); _callCallees.Add(c.Func); }
                    // (places, not values: assignment, augmented assignment and del targets)
                    else if (n is AssignStmt a) foreach (var t in a.Targets)
                    {
                        Store(t);
                        AstWalker.VisitAll(t, tn => { if (tn is AttributeExpr || tn is SubscriptExpr) set.Add((Expr)tn); });
                    }
                    else if (n is AugAssignStmt au) { Store(au.Target); set.Add(au.Target); }
                    else if (n is AnnAssignStmt an) { Store(an.Target); set.Add(an.Target); }
                    else if (n is DelStmt d) foreach (var t in d.Targets) { Store(t); set.Add(t); }
                });
            return set;
        }

        /// <summary>A type that may be late-bound in C#: unknown, dynamic, or not settled by the inference (⊥ inside).</summary>
        static bool UnsureType(string t) => t == null || t == "dynamic" || t.Contains(TypeInference.Pending) || t.Contains("<dynamic>");

        /// <summary>Names that are methods of Python's builtin types or protocol methods (not attributes such as __name__ or __dict__).</summary>
        static bool IsMethodName(string n) =>
            n.StartsWith("__") && n.EndsWith("__") ? !NonMethodDunders.Contains(n) : RuntimeExtensionMethods.Contains(n);

        static readonly HashSet<string> NonMethodDunders = new HashSet<string>
        {
            "__name__", "__qualname__", "__module__", "__doc__", "__dict__", "__class__", "__wrapped__", "__self__", "__func__",
            "__annotations__", "__slots__", "__file__", "__all__", "__version__", "__mro__", "__bases__", "__defaults__", "__code__",
        };

        /// <summary>The builtin types as values: the C# type each one's values have.</summary>
        static readonly Dictionary<string, string> BuiltinTypeValues = new Dictionary<string, string>
        {
            ["int"] = "long", ["float"] = "double", ["bool"] = "bool", ["str"] = "string", ["object"] = "object", ["type"] = "global::System.Type",
            ["bytes"] = "global::Typhon.Runtime.PyBytes", ["bytearray"] = "global::Typhon.Runtime.PyByteArray",
            ["property"] = "global::Typhon.Stdlib.builtins.property", ["staticmethod"] = "global::Typhon.Stdlib.builtins.staticmethod",
            ["classmethod"] = "global::Typhon.Stdlib.builtins.classmethod",
            ["super"] = "global::Typhon.Stdlib.builtins.super",
            ["complex"] = "global::System.Numerics.Complex", ["tuple"] = "global::System.Runtime.CompilerServices.ITuple",
            ["list"] = "global::System.Collections.Generic.List<object>", ["set"] = "global::System.Collections.Generic.HashSet<object>",
            ["dict"] = "global::System.Collections.Generic.OrderedDictionary<object, object>",
            ["range"] = "global::Typhon.Runtime.PyRange",
            ["frozendict"] = "global::Typhon.Runtime.frozendict<object, object>",
            ["frozenset"] = "global::Typhon.Runtime.frozenset<object>",
            ["slice"] = "global::Typhon.Runtime.PySlice",
        };

        string BuiltinTypeValue(NameExpr name) =>
            BuiltinTypeValues.TryGetValue(name.Id, out var type) && !IsShadowed(name.Id) && !IsLocal(name.Id) &&
            !_moduleFieldTypes.ContainsKey(name.Id) && !_renames.ContainsKey(name.Id) ? $"typeof({type})" : null;

        bool TypeObjectMember(AttributeExpr member) =>
            InferType(member.Target) is ("global::System.Type" or "System.Type") &&
            !_r.HasMember("System.Type", member.Name);

        /// <summary>Does the class (or a Typhon base) set args itself (args = ... in its body, self.args = ... in a method)?</summary>
        bool AssignsArgs(ClassDef c, int depth = 0)
        {
            if (c == null || depth > 20) return false;
            bool found = false;
            AstWalker.VisitAll(c.Body, n =>
            {
                if (n is AssignStmt a)
                    foreach (var t in a.Targets)
                        if (t is NameExpr tn && tn.Id == "args" || t is AttributeExpr ta && ta.Name == "args" && ta.Target is NameExpr ts && ts.Id == "self") found = true;
            });
            return found || c.Bases.Any(b => ResolveBase(b, out var py, out _) && py != null && py != c && AssignsArgs(py, depth + 1));
        }

        /// <summary>Is the C# type an exception: a .NET exception the builtin names map to, or a Typhon class deriving from one?</summary>
        bool IsExceptionType(string t)
        {
            if (t == null || t == "dynamic") return false;
            string bare = t.Replace("global::", "").TrimEnd('?');
            if (bare == "System.Exception" || ExceptionMetaMap.ContainsValue(bare)) return true;
            if (ClassKeyOfType(t, out _) is string ck && ClassDefOfKey(ck) is ClassDef c)
                for (int depth = 0; c != null && depth < 20; depth++)
                {
                    ClassDef next = null;
                    foreach (var b in c.Bases)
                    {
                        if (!ResolveBase(b, out var py, out var meta)) continue;
                        if (py != null) { next = py; break; }
                        if (meta != null && (meta == "System.Exception" || ExceptionMetaMap.ContainsValue(meta))) return true;
                    }
                    c = next;
                }
            return false;
        }

        /// <summary>
        /// Is the runtime type a class (not a module: modules are static classes, named in lower case, without constructors)?
        /// (The resolver leaves out some constructors, a params one among them: a capitalized name is a class too.)
        /// </summary>
        bool IsRuntimeClassMeta(string meta)
        {
            string last = meta.Split('+', '.').Last().Split('`')[0];
            return last.Length > 0 && char.IsUpper(last[0]) || _r.GetConstructors(meta).Count > 0;
        }

        /// <summary>A name imported from a standard module that is one of the runtime's classes: its type object, or null.</summary>
        string RuntimeClassValue(NameExpr n)
        {
            if (IsLocal(n.Id) || _moduleFieldTypes.ContainsKey(n.Id) || _renames.ContainsKey(n.Id) || BuiltinTypeNames.Contains(n.Id) ||
                ExceptionMap.ContainsKey(n.Id))
                return null;
            // (from string.templatelib import Template: the import made the name an alias of the class)
            string meta = _dotnetAliases.TryGetValue(n.Id, out var aliased) ? aliased
                        : StdlibOwner(n.Id) is string owner ? owner.Replace('+', '.') + "." + n.Id
                        : TryResolveType(n.Id, 0, out var resolved, out var py) && py == null ? resolved : null;
            if (meta == null || !meta.StartsWith("Typhon.Stdlib.") || meta.Contains('`')) return null;
            // (a class nested two deep, string.templatelib.Template, may be recorded with dots: find its metadata name)
            if (!_r.TypeExists(meta))
            {
                // Typhon.Stdlib.a.b.C → Typhon.Stdlib.a+b+C, Typhon.Stdlib.a.b+C, ...: the first that exists
                string dotted = meta.Replace('+', '.'), tail = dotted.Substring("Typhon.Stdlib.".Length);
                var segs = tail.Split('.');
                for (int top = 1; top <= segs.Length; top++)
                {
                    string cand = "Typhon.Stdlib." + string.Join(".", segs.Take(top)) + (top < segs.Length ? "+" + string.Join("+", segs.Skip(top)) : "");
                    if (_r.TypeExists(cand)) { meta = cand; break; }
                }
            }
            // (classes only: a module is a static class, lower-case and without constructors)
            return _r.TypeExists(meta) && IsRuntimeClassMeta(meta) ? $"typeof({CsTypeName(meta)})" : null;
        }

        string ProjectModuleValue(Expr e)
        {
            if (DottedName(e) is not string dotted || _m.Globals.Contains(dotted.Split('.')[0]) ||
                ModuleRef(dotted) is not ModuleInfo module) return null;
            return $"global::Typhon.Runtime.PyModule.Of(typeof({ModuleClassName(module)}), {CsString(module.Name)})";
        }

        // os.path has a CLR namespace for direct calls and the native path module's identity as a Python value.
        string PathModuleValue(Expr e)
        {
            if ((e is AttributeExpr { Name: "path" } || e is NameExpr nPath &&
                (nPath.Id == "path" || _dotnetAliases.TryGetValue(nPath.Id, out var pathAlias) && pathAlias == "Typhon.Stdlib.os_+path" ||
                 _importedTypes.TryGetValue(nPath.Id, out var importedPath) && importedPath == "Typhon.Stdlib.os_+path")) &&
                DotNetStaticMeta(e) == "Typhon.Stdlib.os_+path")
                return "global::Typhon.Stdlib.os_.__path_module__";
            return null;
        }

        string ModuleOrClassValue(Expr e)
        {
            if (ProjectModuleValue(e) is string projectModule) return projectModule;
            if (PathModuleValue(e) is string pathModule) return pathModule;
            if (e is NameExpr cn && RuntimeClassValue(cn) is string imported) return imported;
            if (e is NameExpr n && !IsLocal(n.Id) && !_moduleFieldTypes.ContainsKey(n.Id) && !_renames.ContainsKey(n.Id) &&
                _dotnetAliases.TryGetValue(n.Id, out var meta) && meta.StartsWith("Typhon.Stdlib.") && !meta.Contains('+'))
                return $"global::Typhon.Runtime.PyModule.Of(typeof({EmitName(n)}), {CsString(meta.Substring("Typhon.Stdlib.".Length).TrimEnd('_'))})";
            if (e is AttributeExpr a && DottedName(a) is string dotted && !IsLocal(dotted.Split('.')[0]))
            {
                if (TryResolveType(dotted, 0, out _, out var projectClass) && projectClass is { TypeParams.Count: 0 } &&
                    TryTypeExpr(e, out string projectType))
                    return $"typeof({projectType})";
                // (a generic class such as collections.deque: the type with object for its type arguments, constructible and a dict key)
                for (int arity = 0; arity <= 3; arity++)
                    // (not a module nested in a module, os.path or numpy.linalg: a static class)
                    if (TryResolveType(dotted, arity, out var tmeta, out var py) && py == null && tmeta != null && tmeta.StartsWith("Typhon.Stdlib.") && tmeta.Contains('+') &&
                        IsRuntimeClassMeta(tmeta))
                        return arity == 0 ? $"typeof({CsTypeName(tmeta)})"
                             : $"typeof({CsTypeName(tmeta.Split('`')[0])}<{string.Join(", ", Enumerable.Repeat("object", arity))}>)";
            }
            // a class the runtime defines beside its module, made by a factory function of the module (collections.deque)
            if (e is AttributeExpr fa && fa.Target is NameExpr fm && !IsLocal(fm.Id) && !_moduleFieldTypes.ContainsKey(fm.Id) &&
                _dotnetAliases.TryGetValue(fm.Id, out var fmeta) && fmeta.StartsWith("Typhon.Stdlib.") && !fmeta.Contains('+') &&
                _r.GetMembers(fmeta, fa.Name).All(x => x.IsMethod))
            {
                // (the class the factory returns, named like it: deque<T> deque<T>(...) → typeof(deque<object>))
                string made = _r.GetMembers(fmeta, fa.Name).Where(x => x.IsMethod).Select(x => x.GenericType ?? x.Type)
                    .FirstOrDefault(t => t != null && t.Split('<')[0].EndsWith("." + fa.Name));
                if (made != null)
                {
                    string open = made.Split('<')[0];
                    int depth = 0, arity = made.Contains('<') ? 1 : 0;
                    foreach (char ch in made) { if (ch == '<') depth++; else if (ch == '>') depth--; else if (ch == ',' && depth == 1) arity++; }
                    return arity == 0 ? $"typeof({open})" : $"typeof({open}<{string.Join(", ", Enumerable.Repeat("object", arity))}>)";
                }
            }
            return null;
        }

        bool IsNestedTypeAccess(AttributeExpr attr)
        {
            if (attr.Target is NameExpr self && self.Id == "self" && HasSelfInScope())
                return _cls.Def.Body.OfType<ClassDef>().Any(nested => nested.Name == attr.Name);
            if (DottedName(attr.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) &&
                TryResolveType(dotted, 0, out _, out var declaringClass) && declaringClass != null)
                return declaringClass.Body.OfType<ClassDef>().Any(nested => nested.Name == attr.Name);
            return ClassKeyOfType(InferType(attr.Target), out _) is string key &&
                ClassDefOfKey(key) is ClassDef owner && owner.Body.OfType<ClassDef>().Any(nested => nested.Name == attr.Name);
        }

        string BaseObjectMethod(AttributeExpr attr) =>
            attr is { Target: NameExpr { Id: "int" }, Name: "__new__" } && !IsShadowed("int") ? "__int_new" :
            attr.Target is NameExpr { Id: "object" } && !IsShadowed("object") ? attr.Name switch
            {
                "__new__" => "__object_new",
                "__init__" => "__object_init",
                "__str__" => "__object_str",
                "__getattribute__" => "__object_getattribute",
                "__getstate__" => "__object_getstate",
                "__setattr__" => "__object_setattr",
                "__delattr__" => "__object_delattr",
                _ => null,
            } : null;

        string EmitAttribute(AttributeExpr a, bool allowMethodGroup = false)
        {
            if (IsTypingUnion(a)) return $"typeof({RuntimeUnionType})";
            if (!IsAttributeStore(a) && InferType(a.Target) == LocalDataClassType)
                return $"{Rt("getattr")}((object?){Paren(EmitValue(a.Target))}, {CsString(a.Name)})";
            if (!Receivers.Contains(a) && BaseObjectMethod(a) is string objectMethod)
                return $"global::Typhon.Runtime.Builtins.__object_method({CsString(objectMethod)})";
            if (a.Name is "__mro__" or "__getstate__" && !IsAttributeStore(a))
            {
                string receiver = (a.Target is NameExpr builtin ? BuiltinTypeValue(builtin) : null) ?? EmitValue(a.Target);
                return $"{Rt("getattr")}((object?){Paren(receiver)}, {CsString(a.Name)})";
            }
            if (a.Name == ClassModel.AllocateSingleton && DottedName(a.Target) is string singletonType)
                return string.Join(".", singletonType.Split('.').Select(Esc)) + "." + Esc(a.Name);
            if (IsNestedTypeAccess(a))
            {
                string nestedOwner = TryTypeExpr(a.Target, out var className) ? $"typeof({className})" : EmitExpr(a.Target);
                return $"{Rt("getattr")}((object){Paren(nestedOwner)}, {CsString(a.Name)})";
            }
            if (InstanceMethodMayBeShadowed(a))
                return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
            if (!allowMethodGroup && !Receivers.Contains(a) && ProjectFunctionAttributeValue(a) is string projectFunction)
                return projectFunction;
            if (a.Name == "__traceback__" && IsExceptionType(InferType(a.Target)))
                return $"global::Typhon.Stdlib.types.TracebackType.Of({EmitExpr(a.Target)})";
            if (DottedName(a.Target) is string exportOwner && ModuleRef(exportOwner) is ModuleInfo source &&
                RuntimeReExport(source, a.Name) is var export && export.Module != null && StdlibType(export.Module) is string exportMeta)
            {
                string runtimeOwner = CsTypeName(exportMeta);
                if (!Receivers.Contains(a) && !allowMethodGroup && _r.GetMembers(exportMeta, export.Member) is { Count: > 0 } members &&
                    members.All(m => m.IsMethod))
                    return $"new global::Typhon.Runtime.PyBoundMethod(typeof({runtimeOwner}), {CsString(export.Member)})";
                return runtimeOwner + "." + Esc(export.Member);
            }
            if (DottedName(a.Target) is string prefix && ModuleRef(prefix) is ModuleInfo projectModule &&
                IsProjectPackageModule(projectModule))
            {
                if (a.Name == "__name__") return CsString(projectModule.Name);
                if (a.Name == "__package__") return CsString(projectModule.PackageName);
                if (a.Name == "__file__") return CsString(System.IO.Path.GetFullPath(projectModule.Path));
                if (ProjectPackageMember(prefix + "." + a.Name) is string member) return member;
            }
            if (a.Name is "errno" or "strerror" or "filename" or "filename2" or "winerror" &&
                InferType(a.Target) is ("global::System.IO.IOException" or "System.IO.IOException"))
                return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
            // e.args on an exception whose class does not declare args (a .NET exception or a Typhon subclass of one)
            if (a.Name == "args" && InferType(a.Target) is string ext && IsExceptionType(ext) && !ext.Contains("Typhon.Runtime.") &&
                !(ClassKeyOfType(ext, out _) is string eck && ClassDefOfKey(eck) is ClassDef ecd && AssignsArgs(ecd)))
                return $"global::Typhon.Runtime.PyExc.Args((global::System.Exception){Paren(EmitExpr(a.Target))})";
            // frozenset(kwlist).__contains__ / table.__getitem__ as a value (not called here): a function of the container
            if ((a.Name == "__contains__" || a.Name == "__getitem__") && !Receivers.Contains(a) && InferType(a.Target) is string ct && ct != "dynamic" &&
                (ct.StartsWith("HashSet<") || ct.StartsWith("List<") || ct.StartsWith("OrderedDictionary<") || ct.StartsWith("Dictionary<") ||
                 ct.StartsWith("global::Typhon.Runtime.frozenset<") || ct.StartsWith("frozenset<") || ct == "string"))
            {
                string container = NewTmp("c"), et = ct == "string" ? "string" : ct.StartsWith("OrderedDictionary<") || ct.StartsWith("Dictionary<") ? SplitTypeArgs(GenericArg(ct))[0] : ElementTypeOf(ct) ?? "object";
                string boundTo = EmitExpr(a.Target);
                return a.Name == "__contains__"
                    ? $"((global::System.Func<{ct}, global::System.Func<{et}, bool>>)({container} => __v => {Rt("contains")}({container}, __v)))({boundTo})"
                    : $"((global::System.Func<{ct}, global::System.Func<object?, dynamic>>)({container} => __k => {Rt("__getitem")}((object?){container}, __k)))({boundTo})";
            }
            if (a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super" && sc.Args.Count == 0) return "base." + Esc(a.Name);
            // np.random.random(...): C# forbids a member named like its class, the numpy library exposes it as random_sample
            if (a.Name == "random" && a.Target is AttributeExpr inner && inner.Name == "random" && DottedName(inner.Target) is string mod &&
                (_dotnetAliases.TryGetValue(mod, out var target0) ? target0 : mod) == "Typhon.Stdlib.numpy")
                return EmitExpr(a.Target) + ".random_sample";
            // Function metadata is writable. A nested function's attributes and later reads share one function object.
            if (a.Target is NameExpr fa && (LocalFunctionObject(fa.Id) ?? FunctionValue(fa, typed: false)) is string fav)
                return a.Name is "__name__" or "__doc__" or "__wrapped__" or "__module__" or "__qualname__" ? $"{fav}.{a.Name}" : $"((dynamic){fav}).{Esc(a.Name)}";
            // type(x).__name__, x.__class__, SomeClass.__name__
            if (a.Name == "__class__")
            {
                string receiver = a.Target is NameExpr cls ? BuiltinTypeValue(cls) ?? EmitValue(cls) : EmitValue(a.Target);
                return $"{Rt("__type_of")}((object?){Paren(receiver)})";
            }
            // type(x).__module__ / self.__class__.__module__: the module the class is defined in ('builtins' for the runtime's)
            if (a.Name == "__module__" && (a.Target is CallExpr mtc && mtc.Func is NameExpr mtn && mtn.Id == "type" && !IsShadowed("type") ||
                                           a.Target is AttributeExpr mca && mca.Name == "__class__" || InferType(a.Target) is "global::System.Type" or "System.Type"))
                return $"{Rt("__module_of")}((object?){Paren(EmitExpr(a.Target))})";
            if (a.Name == "__name__" || a.Name == "__qualname__" && (a.Target is CallExpr || a.Target is AttributeExpr qa && qa.Name == "__class__" ||
                                                                    InferType(a.Target) is "global::System.Type" or "System.Type"))
            {
                if (a.Name == "__name__" && PathModuleValue(a.Target) is string pathModuleName)
                    return pathModuleName + ".__name__";
                if (a.Target is CallExpr tc && tc.Func is NameExpr tn && tn.Id == "type" && !IsShadowed("type")) return $"{Rt("__name_of")}({EmitExpr(a.Target)})";
                if (a.Target is AttributeExpr ca && ca.Name == "__class__") return $"{Rt("__name_of")}({EmitExpr(a.Target)})";
                if (DottedName(a.Target) is string tdn && TryTypeExpr(a.Target, out _)) return CsString(tdn.Split('.').Last());
                if (a.Target is NameExpr btn && BuiltinTypeValues.ContainsKey(btn.Id) && !IsShadowed(btn.Id) && !IsLocal(btn.Id)) return CsString(btn.Id); // int.__name__, type.__name__
                // a class or function known only at run time (cls.__name__ in a class decorator, f.__name__ of a callable)
                string nt = InferType(a.Target);
                if (nt == null || nt == "dynamic" || nt == "object" || nt == "object?" || nt == "global::System.Type" || nt == "System.Type")
                    return $"{Rt("__name_of")}((object?){Paren(EmitExpr(a.Target))})";
            }
            if (a.Name is "__func__" or "__doc__" or "__code__" && !IsAttributeStore(a))
                return $"{Rt("getattr")}((object?){Paren(EmitValue(a.Target))}, {CsString(a.Name)})";
            if (a.Name == "__dict__" && !IsAttributeStore(a))
            {
                string value = ModuleOrClassValue(a.Target) ?? (TryTypeExpr(a.Target, out var classType) ? $"typeof({classType})" : EmitExpr(a.Target));
                return $"{Rt("__dict__")}((object){Paren(value)})";
            }
            if (!IsAttributeStore(a) && a.Target is NameExpr classArgument && IsClassArgument(classArgument.Id))
                return $"{Rt("getattr")}((object?){Paren(EmitName(classArgument))}, {CsString(a.Name)})";
            if (TupleFieldOwner(a) is string tupleType)
                return $"((global::Typhon.Runtime.PyTupleGetter){Rt("getattr")}(typeof({tupleType}), {CsString(a.Name)}))";
            if (!IsAttributeStore(a) && (IsInstanceClassDefault(a) || TypeObjectMember(a)))
                return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
            if (!IsAttributeStore(a) && DottedName(a.Target) is string conditionalOwner &&
                TryResolveType(conditionalOwner, 0, out _, out var conditionalType) && conditionalType != null &&
                HasConditionalClassMethod(conditionalType, a.Name) && TryTypeExpr(a.Target, out var conditionalClass))
                return $"{Rt("getattr")}(typeof({conditionalClass}), {CsString(a.Name)})";
            if (!_m.IsGdScript && !IsAttributeStore(a) && DottedName(a.Target) is string descriptorOwner &&
                TryResolveType(descriptorOwner, 0, out _, out var ownerClass) && ownerClass != null &&
                MethodInHierarchy(ownerClass, a.Name) is FunctionDef descriptor && TryTypeExpr(a.Target, out var descriptorType) &&
                (IsCachedProperty(descriptor) || HasDecorator(descriptor.Decorators, "property") ||
                    HasDecorator(descriptor.Decorators, "classmethod") && (!Receivers.Contains(a) || !ownerClass.Body.Contains(descriptor)) ||
                    !allowMethodGroup && !Receivers.Contains(a) && HasDecorator(descriptor.Decorators, "staticmethod")))
                return $"{Rt("getattr")}(typeof({descriptorType}), {CsString(a.Name)})";
            if (!_m.IsGdScript && !IsAttributeStore(a) && ClassKeyOfType(InferType(a.Target), out _) is string instanceClass &&
                ClassDefOfKey(instanceClass) is ClassDef instanceOwner && MethodInHierarchy(instanceOwner, a.Name) is FunctionDef method &&
                (HasDecorator(method.Decorators, "staticmethod") || HasDecorator(method.Decorators, "classmethod")))
                return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
            if (!_m.IsGdScript && !IsAttributeStore(a) && !_callCallees.Contains(a) && InferType(a.Target) == "dynamic" &&
                a.Target is not NameExpr { Id: "self" or "cls" })
                return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
            // it.__next__ / xs.append as a value (not called here) on a late-bound value: a bound method (C#'s dynamic binder reads
            // properties only); found at run time, like getattr
            if (!Receivers.Contains(a) &&
                (InferType(a.Target) == "dynamic" ||
                 IsDynamicallyDispatched(a.Target) && UnsureType(InferType(a.Target)) ||
                 IsMethodName(a.Name) && (UnsureType(InferType(a.Target)) || IsDynamicallyDispatched(a.Target) ||
                     a.Target is CallExpr bmc && bmc.Args.Any(x => UnsureType(InferType(x.Value))))) &&
                !(a.Target is NameExpr bmn && (bmn.Id == "self" || bmn.Id == "cls" || ModuleRef(bmn.Id) != null || _dotnetAliases.ContainsKey(bmn.Id) || TryResolveType(bmn.Id, 0, out _, out _))))
                return $"{Rt("getattr")}((object?){Paren(EmitExpr(a.Target))}, {CsString(a.Name)})";
            // self.x that neither the class nor its bases define (a mixin using an attribute of the classes it is mixed
            // into): late-bound, found on the object's own class at run time, as Python does
            if (a.Target is NameExpr sx && sx.Id == "self" && _cls != null && !_cls.IsInterface && HasSelfInScope() && SelfMemberUnknown(a.Name))
                return !_m.IsGdScript && !IsAttributeStore(a)
                    ? $"{Rt("getattr")}(this, {CsString(a.Name)})"
                    : $"((dynamic)this).{Esc(a.Name)}";
            // self.m1 / self.__class__.m1 / type(self).m1 where m1 is a class variable (a static field): read through the class
            bool selfClass = a.Target is NameExpr selfRef && selfRef.Id == "self" ||
                             a.Target is AttributeExpr sca && sca.Name == "__class__" && sca.Target is NameExpr scs && scs.Id == "self" ||
                             a.Target is CallExpr stc && stc.Func is NameExpr stn && stn.Id == "type" && stc.Args.Count == 1 && stc.Args[0].Value is NameExpr sts && sts.Id == "self";
            if (selfClass && HasSelfInScope() && _cls != null && StaticOwner(_cls.Def, a.Name) is string owner)
                return owner + "." + Esc(a.Name);
            // A runtime module is still a Python namespace. Optional or dynamically added members
            // use its attribute lookup when there is no generated static member to bind.
            if (!IsAttributeStore(a) && a.Target is NameExpr runtimeName && !IsShadowed(runtimeName.Id) &&
                _dotnetAliases.TryGetValue(runtimeName.Id, out var runtimeType) && runtimeType.StartsWith("Typhon.Stdlib.") &&
                !_r.HasMember(runtimeType, a.Name) && ModuleOrClassValue(runtimeName) is string runtimeModule)
                return $"{Rt("getattr")}({runtimeModule}, {CsString(a.Name)})";
            string target = a.Target is ConstExpr ic && ic.Kind == ConstKind.Int ? EmitValue(a.Target) : a.Target is NameExpr tn0 ? EmitName(tn0) : EmitExpr(a.Target);
            if (a.Target is ConstExpr || a.Target is UnaryExpr || a.Target is BinOpExpr || a.Target is AwaitExpr) target = Paren(target);
            if (_m.IsGdScript && GdMemberAccess(a, target) is string gdMember) return gdMember;
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
        bool ConditionalNeedsDynamic(IfExpr i, bool targetTyped = false)
        {
            bool StaticMethod(Expr value) => value is AttributeExpr member && DotNetStaticMeta(member.Target) is string metadata &&
                _r.GetMembers(metadata, member.Name).Any(m => m.IsMethod && m.IsStatic);
            if (StaticMethod(i.Body) || StaticMethod(i.OrElse)) return true;
            if (!targetTyped && ((i.Body is DictExpr { Keys.Count: 0 } && i.OrElse is DictExpr { Keys.Count: > 0 }) ||
                (i.OrElse is DictExpr { Keys.Count: 0 } && i.Body is DictExpr { Keys.Count: > 0 }))) return true;
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
