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
    // .NET members: overloads, constructors, result types, and Python ints (long) where .NET takes int.
    internal sealed partial class ModuleEmitter
    {
        static readonly Dictionary<string, string> NumericConversions = new Dictionary<string, string>
        {
            ["int8"] = "sbyte", ["int16"] = "short", ["int32"] = "int", ["int64"] = "long",
            ["uint8"] = "byte", ["uint16"] = "ushort", ["uint32"] = "uint", ["uint64"] = "ulong", ["float32"] = "float", ["float64"] = "double",
        };

        /// <summary>Metadata name of the .NET type behind a C# type as the inference spells it; null for Typhon classes and unknown types.</summary>
        string DotNetMeta(string cs)
        {
            if (cs == null || cs == "dynamic" || cs == "object" || cs == TypeInference.Pending || cs.StartsWith("(")) return null;
            if (cs.EndsWith("?")) cs = cs.Substring(0, cs.Length - 1);
            if (cs.EndsWith("[]")) return "System.Array";
            switch (cs)
            {
                case "string": return "System.String";
                case "long": return "System.Int64";
                case "int": return "System.Int32";
                case "double": return "System.Double";
                case "bool": return "System.Boolean";
                case "char": return "System.Char";
                case "decimal": return "System.Decimal";
                case "float": return "System.Single";
            }
            if (cs.StartsWith("global::")) cs = cs.Substring("global::".Length);
            string name = cs; int arity = 0;
            int lt = cs.IndexOf('<');
            if (lt >= 0) { name = cs.Substring(0, lt); arity = SplitTypeArgs(GenericArg(cs)).Count; }
            // @struct.Struct: a C# keyword escaped in the emitted name
            return TryResolveType(name.Replace("@", ""), arity, out var meta, out _) ? meta : null;
        }

        /// <summary>The .NET type a dotted name denotes when used as the receiver of a static member (Math, np, DateTime, ...).</summary>
        string DotNetStaticMeta(Expr target)
        {
            string dotted = DottedName(target);
            if (dotted == null || IsLocal(dotted.Split('.')[0]) || dotted == "self") return null;
            if (_moduleAliases.ContainsKey(dotted) || ModuleRef(dotted) != null) return null;
            return TryResolveType(dotted, 0, out var meta, out _) ? meta : null;
        }

        IReadOnlyList<Semantics.MemberSig> DotNetConstructors(Expr func)
        {
            Expr target = func is SubscriptExpr s ? s.Target : func;
            int arity = func is SubscriptExpr gs ? (gs.Index is TupleExpr t ? t.Elts.Count : 1) : 0;
            string dotted = DottedName(target);
            if (dotted == null || !TryResolveType(dotted, arity, out var meta, out _) || meta == null) return null;
            return _r.GetMembers(meta, ".ctor");
        }

        IReadOnlyList<Semantics.MemberSig> DotNetOverloads(CallExpr c)
        {
            if (c.Func is AttributeExpr fa)
            {
                if (DotNetStaticMeta(fa.Target) is string sm) return _r.GetMembers(sm, fa.Name).Where(m => m.IsMethod && m.IsStatic).ToList();
                if (fa.Target is NameExpr sn && sn.Id == "self") return GdSelfMembers(fa.Name).Where(m => m.IsMethod).ToList() is { Count: > 0 } gs ? gs : null;
                var rt = InferType(fa.Target);
                if (ClassKeyOfType(rt, out _) != null) return null;
                if (DotNetMeta(rt) is string im)
                {
                    var instance = _r.GetMembers(im, fa.Name).Where(m => m.IsMethod && !m.IsStatic).ToList();
                    // app.MapGet(...): an extension method of an imported namespace (when the type has no such method itself)
                    return instance.Count > 0 ? instance : _r.GetExtensionMethods(im, fa.Name, _usingNamespaces) is { Count: > 0 } ext ? ext.ToList() : instance;
                }
                return null;
            }
            if (c.Func is NameExpr fn && !IsShadowed(fn.Id))
            {
                foreach (var st in _staticTypes)
                {
                    var ms = _r.GetMembers(st, fn.Id).Where(m => m.IsMethod && m.IsStatic).ToList();
                    if (ms.Count > 0) return ms;
                }
                // big-int mode: Python's builtins too (range(n) narrows a BigInteger, abs/pow/max keep it exact)
                if (_big)
                {
                    var bs = _r.GetMembers("Typhon.Runtime.Builtins", fn.Id).Where(m => m.IsMethod && m.IsStatic).ToList();
                    if (bs.Count > 0) return bs;
                }
            }
            return null;
        }

        /// <summary>Can the overload be called with these positional and named arguments (by count and names only)?</summary>
        static bool Applicable(Semantics.MemberSig m, int positional, List<string> named)
        {
            var ps = m.Parameters;
            bool hasParams = ps.Count > 0 && ps[ps.Count - 1].IsParams;
            int fixedCount = hasParams ? ps.Count - 1 : ps.Count;
            if (positional > fixedCount && !hasParams) return false;
            foreach (var n in named) if (!ps.Any(p => p.Name == n)) return false;
            for (int i = positional; i < fixedCount; i++) if (!ps[i].IsOptional && !named.Contains(ps[i].Name)) return false;
            return true;
        }

        static Semantics.ParamSig ParamAt(Semantics.MemberSig m, int i)
        {
            var ps = m.Parameters;
            if (i < 0) return null; // a starred argument (f(*xs)) has no single parameter
            if (i < ps.Count && !ps[i].IsParams) return ps[i];
            return ps.Count > 0 && ps[ps.Count - 1].IsParams && i >= ps.Count - 1 ? ps[ps.Count - 1] : null;
        }

        /// <summary>
        /// A Python int (C# long) argument passed to .NET: when no overload takes a long at that position but some take an
        /// int (or another narrower integer), the argument needs a checked narrowing conversion. Returns its type, or null.
        /// </summary>
        string NarrowingFor(IReadOnlyList<Semantics.MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            if (_m.IsGdScript && GdFloatNarrowing(overloads, args, a, argPos) is string gdFloat) return gdFloat;
            if (_big) return BigNarrowingFor(overloads, args, a, argPos);
            if (IsIntLiteral(a.Value) || InferType(a.Value) != "long") return null;
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            string narrowTo = null;
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p == null) continue;
                if (p.Fit == Semantics.LongFit.Accepts) return null;
                if (p.Fit == Semantics.LongFit.Narrow && (narrowTo == null || p.NarrowTo == "int")) narrowTo = p.NarrowTo;
            }
            return narrowTo;
        }

        /// <summary>
        /// Big-int mode: a Python int argument (a BigInteger, or a 64-bit count from len()/a loop) passed to .NET or the runtime.
        /// No conversion when an overload takes a BigInteger (or object, a type parameter, ...); else an explicit, checked
        /// conversion to the numeric type the parameter has (long before int before the rest).
        /// </summary>
        string BigNarrowingFor(IReadOnlyList<Semantics.MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            if (IsIntLiteral(a.Value)) return null;
            string at = InferType(a.Value);
            if (at != TypeInference.BigInt && at != "long") return null;
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            string narrowTo = null;
            int Rank(string t) => t == "long" ? 0 : t == "int" ? 1 : t == "double" ? 2 : 3;
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p == null) continue;
                // (a 64-bit count is converted like a BigInteger: a no-op for long, the usual checked narrowing for int)
                if (p.BigAccepts) return null;
                if (p.BigNarrowTo != null && (narrowTo == null || Rank(p.BigNarrowTo) < Rank(narrowTo))) narrowTo = p.BigNarrowTo;
            }
            return narrowTo;
        }

        /// <summary>Big-int mode: does some applicable overload take exactly a BigInteger at this argument's position?</summary>
        bool BigOverloadAt(IReadOnlyList<Semantics.MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p != null && p.IsBigInteger) return true;
            }
            return false;
        }

        /// <summary>The member of a .NET receiver (static: a type name; instance: a value of a .NET type) named <paramref name="name"/>.</summary>
        IEnumerable<Semantics.MemberSig> DotNetMembers(Expr receiver, string name)
        {
            if (DotNetStaticMeta(receiver) is string sm) return _r.GetMembers(sm, name).Where(m => m.IsStatic);
            if (receiver is NameExpr sn && sn.Id == "self") return GdSelfMembers(name);
            var rt = InferType(receiver);
            if (rt == null || ClassKeyOfType(rt, out _) != null) return Enumerable.Empty<Semantics.MemberSig>();
            return DotNetMeta(rt) is string im ? _r.GetMembers(im, name).Where(m => !m.IsStatic) : Enumerable.Empty<Semantics.MemberSig>();
        }

        /// <summary>Result type of a .NET property, field or method call when it is a special type all candidates agree on.</summary>
        string DotNetResultType(Expr receiver, string name, CallExpr call)
        {
            var ms = DotNetMembers(receiver, name).Where(m => m.IsMethod == (call != null)).ToList();
            if (call != null)
            {
                int positional = call.Args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
                var named = call.Args.Where(x => x.Name != null).Select(x => x.Name).ToList();
                ms = ms.Where(m => Applicable(m, positional, named)).ToList();
            }
            if (ms.Count == 0) return null;
            // a method declared to return dynamic (argparse's parse_args): late-bound, like such module functions (json.loads)
            if (call != null && ms.All(m => m.ReturnsDynamic)) return "dynamic";
            string t = ms[0].Type;
            if (_m.IsGdScript && call != null && !ms.All(m => m.Type == t)) return GdOverloadType(ms, call);
            return t != null && ms.All(m => m.Type == t) ? t : null;
        }

        /// <summary>A Python int assigned to a .NET property or field of a narrower integer type: the narrowing type, or null.</summary>
        string NarrowingForMember(AttributeExpr at, Expr value)
        {
            if (_m.IsGdScript && GdFloatNarrowingForMember(at, value) is string gdFloat) return gdFloat;
            if (IsIntLiteral(value)) return null;
            string vt = InferType(value);
            if (_big && vt == TypeInference.BigInt)
                return DotNetMembers(at.Target, at.Name).FirstOrDefault(x => !x.IsMethod)?.ValueBigNarrowTo;
            if (vt != "long") return null;
            var m = DotNetMembers(at.Target, at.Name).FirstOrDefault(x => !x.IsMethod);
            return m != null && m.ValueFit == Semantics.LongFit.Narrow ? m.ValueNarrowTo : null;
        }
    }
}
