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
    // Python protocols bridged to .NET (operators, Equals, ToString, indexers) and namedtuple members.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>A class made by namedtuple(...) / typing.NamedTuple (it deconstructs like a tuple).</summary>
        bool IsNamedTupleType(string t) => NamedTupleOf(t) != null;

        ClassDef NamedTupleOf(string t)
        {
            if (t == null) return null;
            if (_cls != null && t == _cls.CsSelfType && _cls.Def.NamedTupleFields != null) return _cls.Def;
            return ClassKeyOfType(t, out _) is string key && ClassDefOfKey(key) is ClassDef c && c.NamedTupleFields != null ? c : null;
        }

        /// <summary>The field types of a namedtuple type (dynamic where unknown).</summary>
        List<string> NamedTupleFieldTypes(string t)
        {
            var c = NamedTupleOf(t);
            return c?.NamedTupleFields.Select(f => MemberType(t, f, method: false) is string ft && ft != TypeInference.Pending ? ft : "dynamic").ToList();
        }

        /// <summary>The tuple protocol of a namedtuple class, over its fields in order.</summary>
        void EmitNamedTupleMembers(ClassDef c, ClassContext ctx, List<FunctionDef> methods)
        {
            var fs = c.NamedTupleFields;
            string self = ctx.CsSelfType;
            string T(string f) => ctx.FieldTypes.TryGetValue(f, out var t) ? t : "dynamic";
            string F(string f) => "this." + Esc(f);
            bool Has(string name) => methods.Any(m => m.Name == name);
            _w.Line();
            if (fs.Count > 0)
                _w.Line($"public void Deconstruct({string.Join(", ", fs.Select(f => $"out {T(f)} {Esc(f)}"))}) {{ {string.Join(" ", fs.Select(f => $"{Esc(f)} = {F(f)};"))} }}");
            _w.Line($"int global::System.Runtime.CompilerServices.ITuple.Length => {fs.Count};");
            _w.Line($"object? global::System.Runtime.CompilerServices.ITuple.this[int __i] => __i switch {{ {string.Join(" ", fs.Select((f, i) => $"{i} => {F(f)},"))} _ => throw new global::System.IndexOutOfRangeException(\"tuple index out of range\") }};");
            _w.Line($"public object? this[long __i] => ((global::System.Runtime.CompilerServices.ITuple)this)[checked((int)(__i < 0 ? __i + {fs.Count} : __i))];");
            if (!Has("__len__")) _w.Line($"public long __len__() => {fs.Count};");
            if (!Has("__iter__"))
            {
                _w.Line($"public global::System.Collections.Generic.IEnumerator<object?> GetEnumerator() {{ {string.Join(" ", fs.Select(f => $"yield return {F(f)};"))} {(fs.Count == 0 ? "yield break;" : "")} }}");
                _w.Line("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
            }
            string repr = $"$\"{c.Name}({string.Join(", ", fs.Select(f => $"{f}={{(global::Typhon.Runtime.Builtins.repr({F(f)}))}}"))})\"";
            if (!Has("__repr__")) _w.Line($"public string __repr__() => {repr};");
            else _w.Line("string global::Typhon.Runtime.IPyRepr.__repr__() => __repr__();");
            if (!Has("__repr__") && !Has("__str__")) _w.Line("public override string ToString() => __repr__();");
            if (!Has("__eq__"))
            {
                string eq = fs.Count == 0 ? "true" : string.Join(" && ", fs.Select(f => $"global::Typhon.Runtime.Builtins.eq((object?){F(f)}, (object?)__o.{Esc(f)})"));
                _w.Line($"public override bool Equals(object? __obj) => __obj is {self} __o && {eq};");
                _w.Line($"public override int GetHashCode() {{ var __h = new global::System.HashCode(); {string.Join(" ", fs.Select(f => $"__h.Add((object?){F(f)});"))} return __h.ToHashCode(); }}");
                _w.Line($"public static bool operator ==({self}? a, {self}? b) => a is null ? b is null : a.Equals(b);");
                _w.Line($"public static bool operator !=({self}? a, {self}? b) => !(a == b);");
            }
            // tuples order field by field
            _w.Line($"public int CompareTo(object? __obj) {{ if (__obj is not {self} __o) throw new global::System.InvalidCastException(\"'<' not supported between instances of '{c.Name}' and '\" + __obj?.GetType().Name + \"'\"); int __c; {string.Join(" ", fs.Select(f => $"if ((__c = global::Typhon.Runtime.Builtins.compare((object?){F(f)}, (object?)__o.{Esc(f)})) != 0) return __c;"))} return 0; }}");
            if (!Has("__lt__")) _w.Line($"public static bool operator <({self} a, {self} b) => a.CompareTo(b) < 0;");
            if (!Has("__gt__")) _w.Line($"public static bool operator >({self} a, {self} b) => a.CompareTo(b) > 0;");
            if (!Has("__le__")) _w.Line($"public static bool operator <=({self} a, {self} b) => a.CompareTo(b) <= 0;");
            if (!Has("__ge__")) _w.Line($"public static bool operator >=({self} a, {self} b) => a.CompareTo(b) >= 0;");
            // _replace(field=value, ...): a copy with some fields replaced
            if (fs.Count > 0)
                _w.Line($"public {self} _replace({string.Join(", ", fs.Select(f => $"object? {Esc(f)} = null"))}) => new {self}({string.Join(", ", fs.Select(f => $"{Esc(f)} is null ? {F(f)} : ({T(f)})(dynamic){Esc(f)}"))});");
            _w.Line($"public OrderedDictionary<string, object?> _asdict() => new OrderedDictionary<string, object?> {{ {string.Join(", ", fs.Select(f => $"[{CsString(f)}] = {F(f)}"))} }};");
            _w.Line($"internal static readonly global::Typhon.Runtime.PyTuple<string> _fields = new global::Typhon.Runtime.PyTuple<string>(new string[] {{ {string.Join(", ", fs.Select(CsString))} }});");
            if (fs.Count > 0)
                _w.Line($"public static {self} _make(global::System.Collections.IEnumerable __items) {{ var __l = global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Cast<object?>(__items)); if (__l.Count != {fs.Count}) throw new global::System.ArgumentException(\"Expected {fs.Count} arguments, got \" + __l.Count); return new {self}({string.Join(", ", fs.Select((f, i) => $"({T(f)})(dynamic)__l[{i}]!"))}); }}");
        }

        bool LtComparesSelf(ClassDef c, ClassContext ctx)
        {
            var lt = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__lt__");
            return lt != null && lt.Params.Count == 2 && lt.Params[1].Annotation != null && MapType(lt.Params[1].Annotation, lt) == ctx.CsSelfType;
        }

        bool LtComparesAny(ClassDef c)
        {
            var lt = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__lt__");
            return lt != null && lt.Params.Count == 2 && lt.Params[1].Annotation == null;
        }

        static string DunderBridge(string name)
        {
            switch (name)
            {
                case "__str__": return "ToString";
                case "__eq__": return "Equals";
                case "__hash__": return "GetHashCode";
                default: return null;
            }
        }

        static readonly Dictionary<string, string> BinaryDunders = new Dictionary<string, string>
        {
            ["__add__"] = "+", ["__sub__"] = "-", ["__mul__"] = "*", ["__truediv__"] = "/", ["__mod__"] = "%",
            ["__and__"] = "&", ["__or__"] = "|", ["__xor__"] = "^", ["__lt__"] = "<", ["__gt__"] = ">", ["__le__"] = "<=", ["__ge__"] = ">=",
        };
        static readonly Dictionary<string, string> ReflectedDunders = new Dictionary<string, string>
        {
            ["__radd__"] = "+", ["__rsub__"] = "-", ["__rmul__"] = "*", ["__rtruediv__"] = "/",
        };
        static readonly Dictionary<string, string> ComparisonPairs = new Dictionary<string, string> { ["<"] = ">", [">"] = "<", ["<="] = ">=", [">="] = "<=" };

        /// <summary>Python protocol methods (__str__, __add__, __getitem__, ...) are kept as methods and bridged to .NET members.</summary>
        void EmitDunderBridges(List<FunctionDef> methods, ClassContext ctx)
        {
            var self = ctx.CsSelfType;
            string ParamType(FunctionDef f, int i) => f.Params.Count > i && f.Params[i].Annotation != null ? MapType(f.Params[i].Annotation, f) : "dynamic";
            string RetType(FunctionDef f) => f.Returns != null ? MapType(f.Returns, f, isReturn: true) : "dynamic";
            var names = new HashSet<string>(methods.Select(m => m.Name));
            var definedOps = new HashSet<string>();

            foreach (var f in methods)
            {
                switch (f.Name)
                {
                    case "__str__": _w.Line("public override string ToString() => __str__();"); break;
                    case "__repr__": if (!names.Contains("__str__")) _w.Line("public override string ToString() => __repr__();"); break;
                    case "__hash__": _w.Line("public override int GetHashCode() => __hash32(__hash__());"); break;
                    case "__eq__":
                    {
                        string pt = ParamType(f, 1);
                        if (pt == "object" || pt == "dynamic") _w.Line("public override bool Equals(object? obj) => __eq__(obj);");
                        else
                        {
                            _w.Line($"public override bool Equals(object? obj) => obj is {pt} __o && __eq__(__o);");
                            if (pt == self)
                            {
                                _w.Line($"public static bool operator ==({self}? a, {self}? b) => a is null ? b is null : a.Equals(b);");
                                _w.Line($"public static bool operator !=({self}? a, {self}? b) => !(a == b);");
                            }
                        }
                        break;
                    }
                    case "__neg__": _w.Line($"public static {RetType(f)} operator -({self} self) => self.__neg__();"); break;
                    case "__getitem__":
                    {
                        string kt = ParamType(f, 1);
                        var setter = methods.FirstOrDefault(m => m.Name == "__setitem__");
                        string set = setter != null ? " set => __setitem__(key, value);" : "";
                        _w.Line($"public {RetType(f)} this[{kt} key] {{ get => __getitem__(key);{set} }}");
                        break;
                    }
                    case "__len__": if (!ctx.Members.Contains("Count")) _w.Line("public int Count => checked((int)__len__());"); break;
                    case "__iter__":
                        _w.Line($"public IEnumerator<{ctx.IterElementType}> GetEnumerator() => __iter__().GetEnumerator();");
                        _w.Line("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
                        break;
                    default:
                        if (BinaryDunders.TryGetValue(f.Name, out var op))
                        {
                            definedOps.Add(op);
                            _w.Line($"public static {RetType(f)} operator {op}({self} self, {ParamType(f, 1)} other) => self.{f.Name}(other);");
                        }
                        else if (ReflectedDunders.TryGetValue(f.Name, out var rop))
                            _w.Line($"public static {RetType(f)} operator {rop}({ParamType(f, 1)} other, {self} self) => self.{f.Name}(other);");
                        break;
                }
            }
            var ltDef = methods.FirstOrDefault(m => m.Name == "__lt__");
            if (ltDef != null && ltDef.Params.Count == 2 && ParamType(ltDef, 1) == self)
                _w.Line($"public int CompareTo({self}? other) => other is null ? 1 : __lt__(other) ? -1 : other.__lt__(this) ? 1 : 0;");
            else if (ltDef != null && ltDef.Params.Count == 2 && ltDef.Params[1].Annotation == null)
                _w.Line("public int CompareTo(object? other) => other is null ? 1 : global::Typhon.Runtime.Builtins.truthy((object?)__lt__(other)) ? -1 : " +
                        "global::Typhon.Runtime.Builtins.truthy((object?)((dynamic)other).__lt__(this)) ? 1 : 0;");
            // C# requires comparison operators in pairs: derive the missing partner by swapping operands
            foreach (var f in methods)
                if (BinaryDunders.TryGetValue(f.Name, out var op) && ComparisonPairs.TryGetValue(op, out var partner) && !definedOps.Contains(partner))
                {
                    string pt = ParamType(f, 1);
                    if (pt == self) _w.Line($"public static {RetType(f)} operator {partner}({self} a, {self} b) => b.{f.Name}(a);");
                    // other operand of any type (def __lt__(self, other)): Python's reflected comparison, a > b is b < a
                    else if (pt == "dynamic" || pt == "object") _w.Line($"public static {RetType(f)} operator {partner}({self} a, {pt} b) => ((dynamic)b).{f.Name}(a);");
                    else _w.Line($"public static {RetType(f)} operator {partner}({self} a, {pt} b) => throw new global::System.InvalidCastException(\"'{partner}' not supported between instances of '{ctx.Name}' and '{pt}'\");");
                }
        }
    }
}
