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
    // Standard-library calls: result types from literals (re, struct, array), generic signatures, math.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>The Typhon.Stdlib module a call goes to (from itertools import x; itertools.x), or null.</summary>
        string StdlibModuleOf(CallExpr call, out string name)
        {
            name = null;
            if (call.Func is NameExpr n && !IsShadowed(n.Id))
            {
                name = n.Id;
                foreach (var st in _staticTypes) if (st.StartsWith("Typhon.Stdlib.") && _r.GetMembers(st, n.Id).Any(m => m.IsMethod)) return st;
                return null;
            }
            if (call.Func is AttributeExpr a && DotNetStaticMeta(a.Target) is string meta && meta.StartsWith("Typhon.Stdlib.")) { name = a.Name; return meta; }
            return null;
        }

        /// <summary>
        /// Result types of standard-library functions whose C# signatures are generic (the element type follows the argument):
        /// permutations(xs) → tuples of xs's elements, product(a, b) → (A, B) pairs, Counter(xs) → a Counter of xs's elements, ...
        /// </summary>
        string StdlibCallType(CallExpr call)
        {
            string module = StdlibModuleOf(call, out string name);
            if (module == null) return null;
            var pos = call.Args.Where(a => a.Name == null && !a.Star).Select(a => a.Value).ToList();
            string Elem(int i) => i < pos.Count ? PyType(ElementTypeOf(InferType(pos[i]))) : null;
            Expr FirstArg(string keyword) => call.Args.FirstOrDefault(a => a.Name == keyword)?.Value ?? pos.FirstOrDefault();
            string Wrap(string e, string fmt) => e == null || e == TypeInference.Pending ? e : string.Format(fmt, e);
            switch (module + "." + name)
            {
                case "Typhon.Stdlib.os_.fspath":
                {
                    var path = pos.Count > 0 ? InferType(pos[0]) : null;
                    return path == "string" || IsBytesType(path) ? path :
                        path == "global::Typhon.Runtime.Path" ? "string" : "dynamic";
                }
                case "Typhon.Stdlib.itertools.permutations": case "Typhon.Stdlib.itertools.combinations":
                case "Typhon.Stdlib.itertools.combinations_with_replacement": case "Typhon.Stdlib.itertools.batched":
                    return Wrap(Elem(0), "IEnumerable<global::Typhon.Runtime.PyTuple<{0}>>");
                case "Typhon.Stdlib.itertools.product":
                    if (call.Args.Any(a => a.Name == "repeat") || pos.Count > 3) return Wrap(Elem(0), "IEnumerable<global::Typhon.Runtime.PyTuple<{0}>>");
                    var es = Enumerable.Range(0, pos.Count).Select(Elem).ToList();
                    return es.Count >= 2 && es.All(e => e != null && e != TypeInference.Pending) ? $"IEnumerable<({string.Join(", ", es)})>" : es.Contains(TypeInference.Pending) ? TypeInference.Pending : null;
                case "Typhon.Stdlib.itertools.count":
                    return pos.Any(p => InferType(p) == "double") ? "IEnumerable<double>" : $"IEnumerable<{IntT}>";
                case "Typhon.Stdlib.itertools.cycle": case "Typhon.Stdlib.itertools.islice": case "Typhon.Stdlib.itertools.chain": case "Typhon.Stdlib.itertools.accumulate":
                    return Wrap(Elem(0), "IEnumerable<{0}>");
                case "Typhon.Stdlib.itertools.takewhile": case "Typhon.Stdlib.itertools.dropwhile": case "Typhon.Stdlib.itertools.filterfalse":
                    return Wrap(Elem(1), "IEnumerable<{0}>");
                case "Typhon.Stdlib.itertools.compress": return Wrap(Elem(0), "IEnumerable<{0}>");
                case "Typhon.Stdlib.itertools.pairwise": return Wrap(Elem(0), "IEnumerable<({0}, {0})>");
                case "Typhon.Stdlib.itertools.repeat": return pos.Count > 0 ? Wrap(PyType(InferType(pos[0])), "IEnumerable<{0}>") : null;
                case "Typhon.Stdlib.itertools.groupby": return pos.Count == 1 ? Wrap(Elem(0), "IEnumerable<({0}, List<{0}>)>") : null;
                case "Typhon.Stdlib.collections.Counter": return Wrap(pos.Count == 1 && InferType(pos[0]) == "string" ? "string" : Elem(0), "global::Typhon.Runtime.Counter<{0}>");
                case "Typhon.Stdlib.collections.deque": return pos.Count >= 1 ? Wrap(InferType(pos[0]) == "string" ? "string" : Elem(0), "global::Typhon.Runtime.deque<{0}>") : null;
                case "Typhon.Stdlib.functools.reduce":
                    return pos.Count == 3 ? PyType(InferType(pos[2])) : Elem(1);
                case "Typhon.Stdlib.re.compile":
                {
                    var pattern = FirstArg("pattern");
                    if (pattern == null) break;
                    return InferType(pattern) switch
                    {
                        "string" or "global::Typhon.Stdlib.re.Pattern" => "global::Typhon.Stdlib.re.Pattern",
                        PyBytesType or "global::Typhon.Stdlib.re.BytesPattern" => "global::Typhon.Stdlib.re.BytesPattern",
                        _ => null,
                    };
                }
                case "Typhon.Stdlib.os_+path.join":
                    return PathSequenceType(InferType(FirstArg("a")), elements: 1);
                case "Typhon.Stdlib.os_+path.split":
                case "Typhon.Stdlib.os_+path.splitdrive":
                    return PathSequenceType(InferType(FirstArg("p")), elements: 2);
                case "Typhon.Stdlib.os_.scandir":
                    return call.Args.Count == 0 ? "global::Typhon.Stdlib.os_.ScandirIterator" : InferType(FirstArg("path")) switch
                    {
                        "string" => "global::Typhon.Stdlib.os_.ScandirIterator",
                        PyBytesType => "global::Typhon.Stdlib.os_.BytesScandirIterator",
                        _ => null,
                    };
                case "Typhon.Stdlib.fractions.Fraction": return "global::Typhon.Runtime.Fraction";
                case "Typhon.Stdlib.pathlib.Path": return "global::Typhon.Runtime.Path";
            }
            if (GenericStdlibCallType(module, name, call) is string generic) return generic;
            // a function declared to return dynamic (json.loads): late-bound to C# as well
            var plain = _r.GetMembers(module, name).Where(m => m.IsMethod && m.IsStatic).ToList();
            return plain.Count > 0 && plain.All(m => m.ReturnsDynamic) ? "dynamic" : null;
        }

        static string PathSequenceType(string type, int elements) => type is "string" or PyBytesType
            ? elements == 1 ? type : $"({type}, {type})"
            : type == "dynamic" ? "dynamic" : null;

        //
        // Some library results depend on a literal argument: re.findall gives strs or tuples by the pattern's group count,
        // array.array gives floats or ints by its type code. With the literal in the source, the type is static.

        /// <summary>Patterns compiled from a literal and bound to a local name in this function (name → pattern, flags).</summary>
        readonly Dictionary<string, (string Pattern, long Flags)> _regexLocals = new Dictionary<string, (string, long)>();

        /// <summary>The literal pattern (and flags) a regex expression was compiled from: re.compile("...") or a name bound to one.</summary>
        bool RegexLiteral(Expr e, out string pattern, out long flags)
        {
            pattern = null; flags = 0;
            if (e is CallExpr c && c.Args.Count >= 1 && c.Args[0].Name == null && c.Args[0].Value is ConstExpr pc && pc.Kind == ConstKind.Str &&
                StdlibModuleOf(c, out string fname) == "Typhon.Stdlib.re" && fname == "compile")
            {
                pattern = pc.Str;
                flags = c.Args.Count > 1 ? RegexFlags(c.Args[1].Value) : 0;
                return flags >= 0;
            }
            if (e is NameExpr n)
            {
                if (IsLocal(n.Id)) { if (_regexLocals.TryGetValue(n.Id, out var lr)) { (pattern, flags) = lr; return true; } return false; }
                var binds = _m.Ast.Body.OfType<AssignStmt>().Where(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr t && t.Id == n.Id).ToList();
                if (binds.Count == 1 && !(binds[0].Value is NameExpr)) return RegexLiteral(binds[0].Value, out pattern, out flags);
            }
            return false;
        }

        /// <summary>re.I | re.VERBOSE ... as a number (-1 if not a constant expression of re flags).</summary>
        long RegexFlags(Expr e)
        {
            switch (e)
            {
                case ConstExpr c when c.Kind == ConstKind.Int && long.TryParse(c.Raw, out var v): return v;
                case BinOpExpr b when b.Op == "|": { long l = RegexFlags(b.Left), r = RegexFlags(b.Right); return l < 0 || r < 0 ? -1 : l | r; }
                case AttributeExpr a:
                    switch (a.Name) { case "I": case "IGNORECASE": return 2; case "M": case "MULTILINE": return 8; case "S": case "DOTALL": return 16;
                                      case "X": case "VERBOSE": return 64; case "A": case "ASCII": return 256; case "U": case "UNICODE": return 32; case "NOFLAG": return 0; }
                    return -1;
                default: return -1;
            }
        }

        /// <summary>The number of capturing groups of a Python regex (what findall's result shape depends on).</summary>
        static int RegexGroupCount(string p, long flags)
        {
            bool verbose = (flags & 64) != 0 || System.Text.RegularExpressions.Regex.IsMatch(p, @"^\(\?[aiLmsu]*x"), inClass = false;
            int count = 0;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '\\') { i++; continue; }
                if (inClass) { if (c == ']') inClass = false; continue; }
                if (c == '[') { inClass = true; if (i + 1 < p.Length && p[i + 1] == '^') i++; if (i + 1 < p.Length && p[i + 1] == ']') i++; continue; }
                if (verbose && c == '#') { int nl = p.IndexOf('\n', i); i = nl < 0 ? p.Length : nl; continue; }
                if (c == '(' && (i + 1 >= p.Length || p[i + 1] != '?' || i + 3 < p.Length && p[i + 2] == 'P' && p[i + 3] == '<')) count++;
            }
            return count;
        }

        /// <summary>findall's typed form for a literal pattern: __findall_s (strs) or __findall_t (tuples of strs); null if unknown.</summary>
        string FindallForm(string pattern, long flags) => RegexGroupCount(pattern, flags) <= 1 ? "__findall_s" : "__findall_t";
        static string FindallType(string form) => form == "__findall_s" ? "List<string>" : "List<global::Typhon.Runtime.PyTuple<string>>";

        /// <summary>re.findall("lit", s) / pat.findall(s) with a known pattern: which typed form (null if not such a call).</summary>
        string FindallCallForm(CallExpr c)
        {
            if (c.Func is AttributeExpr fa && fa.Name == "findall" && InferType(fa.Target) == "global::Typhon.Stdlib.re.Pattern" && RegexLiteral(fa.Target, out var pp, out var pf))
                return FindallForm(pp, pf);
            if (StdlibModuleOf(c, out string name) == "Typhon.Stdlib.re" && name == "findall" && c.Args.Count >= 2 && c.Args[0].Name == null)
            {
                if (c.Args[0].Value is ConstExpr lc && lc.Kind == ConstKind.Str)
                {
                    long flags = c.Args.Count > 2 ? RegexFlags(c.Args[2].Value) : c.Args.FirstOrDefault(a => a.Name == "flags") is Arg fl ? RegexFlags(fl.Value) : 0;
                    return flags < 0 ? null : FindallForm(lc.Str, flags);
                }
                if (RegexLiteral(c.Args[0].Value, out var p2, out var f2)) return FindallForm(p2, f2);
            }
            return null;
        }

        /// <summary>
        /// The item types struct.unpack gives for a literal format ("<HHd" → long, long, double); null if the format is not a
        /// literal or not valid.
        /// </summary>
        List<string> StructItemTypes(Expr format)
        {
            if (!(format is ConstExpr fc && fc.Kind == ConstKind.Str)) return null;
            string f = fc.Str;
            int i = f.Length > 0 && "@=<>!".IndexOf(f[0]) >= 0 ? 1 : 0;
            var types = new List<string>();
            while (i < f.Length)
            {
                char ch = f[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                int count = 1;
                if (char.IsDigit(ch)) { count = 0; while (i < f.Length && char.IsDigit(f[i])) count = count * 10 + (f[i++] - '0'); if (i >= f.Length) return null; ch = f[i]; }
                i++;
                string t;
                switch (ch)
                {
                    case 'x': continue;
                    case 's': case 'p': types.Add(PyBytesType); continue;
                    case 'c': t = PyBytesType; break;
                    case '?': t = "bool"; break;
                    case 'e': case 'f': case 'd': t = "double"; break;
                    case 'b': case 'B': case 'h': case 'H': case 'i': case 'I': case 'l': case 'L': case 'q': case 'Q': case 'n': case 'N': case 'P': t = IntT; break;
                    default: return null;
                }
                for (int k = 0; k < count; k++) types.Add(t);
            }
            return types;
        }

        /// <summary>struct.unpack("fmt", buf) / unpack_from with a literal format: its item types (null if not such a call).</summary>
        List<string> StructUnpackTypes(CallExpr c)
        {
            if (!(StdlibModuleOf(c, out string name) == "Typhon.Stdlib.struct_" && (name == "unpack" || name == "unpack_from") && c.Args.Count >= 2 && c.Args[0].Name == null)) return null;
            var types = StructItemTypes(c.Args[0].Value);
            return types != null && types.Count >= 1 && types.Count <= 7 ? types : null;
        }

        /// <summary>array.array("d", ...): the typed constructor for a literal type code (null if not such a call).</summary>
        string ArrayCallForm(CallExpr c, out string elem)
        {
            elem = null;
            if (!(StdlibModuleOf(c, out string name) == "Typhon.Stdlib.array_" && name == "array" && c.Args.Count >= 1 && c.Args[0].Value is ConstExpr tc && tc.Kind == ConstKind.Str)) return null;
            switch (tc.Str)
            {
                case "d": case "f": elem = "double"; return "__array_d";
                case "u": elem = "string"; return "__array_u";
                default: elem = IntT == "long" ? "long" : "long"; return "__array_l";
            }
        }


        /// <summary>
        /// The result type of a generic standard-library function, from its C# signature: the type parameters are bound from
        /// the argument types (heappop(List&lt;T&gt;) → T, nlargest(n, IEnumerable&lt;T&gt;) → List&lt;T&gt;), so a module declares its
        /// types once, in C#. Null when they cannot be bound.
        /// </summary>
        string GenericStdlibCallType(string module, string name, CallExpr call)
        {
            if (call.Args.Any(a => a.Star || a.DoubleStar)) return null;
            var positional = call.Args.Where(a => a.Name == null).Select(a => a.Value).ToList();
            var named = call.Args.Where(a => a.Name != null).ToList();
            foreach (var m in _r.GetMembers(module, name))
            {
                if (!m.IsMethod || !m.IsStatic || m.TypeParams == null || m.GenericType == null) continue;
                if (!Applicable(m, positional.Count, named.Select(a => a.Name).ToList())) continue;
                var map = new Dictionary<string, string>();
                bool ok = true, pending = false;
                void Bind(Semantics.ParamSig p, Expr value)
                {
                    if (p?.GenericType == null || value is LambdaExpr) return;
                    string at = PyType(InferType(value));
                    if (at == TypeInference.Pending) { pending = true; return; }
                    if (IsUnknownType(at)) return;
                    if (!BindTypeParams(p.GenericType, at, m.TypeParams, map)) ok = false;
                }
                for (int i = 0; i < positional.Count; i++) Bind(ParamAt(m, i), positional[i]);
                foreach (var a in named) Bind(m.Parameters.FirstOrDefault(x => x.Name == a.Name), a.Value);
                if (!ok) continue;
                if (pending) return TypeInference.Pending;
                if (SubstituteTypeParams(m.GenericType, m.TypeParams, map) is string result) return result;
            }
            return null;
        }

        static readonly string[] SequenceInterfaces = { "IEnumerable<", "IReadOnlyList<", "IList<", "ICollection<", "IReadOnlyCollection<" };
        static readonly string[] MappingTypes = { "IDictionary<", "IReadOnlyDictionary<", "Dictionary<", "OrderedDictionary<" };

        /// <summary>Unifies a parameter type pattern (with type parameters) with an argument type; false on a contradiction.</summary>
        bool BindTypeParams(string pattern, string actual, List<string> typeParams, Dictionary<string, string> map)
        {
            if (pattern.EndsWith("?") && typeParams.Contains(pattern.Substring(0, pattern.Length - 1))) { pattern = pattern.Substring(0, pattern.Length - 1); actual = actual.TrimEnd('?'); }
            if (typeParams.Contains(pattern))
            {
                if (!map.TryGetValue(pattern, out var had)) { map[pattern] = actual; return true; }
                if (had == actual) return true;
                if ((had == "long" || had == "double") && (actual == "long" || actual == "double")) { map[pattern] = "double"; return true; }
                return false;
            }
            if (!pattern.Contains('<') && !pattern.StartsWith("(")) return true; // a concrete type binds nothing
            if (SequenceInterfaces.Any(pattern.StartsWith))
                return ElementTypeOf(actual) is string elem && !IsUnknownType(elem) ? BindTypeParams(GenericArg(pattern), PyType(elem), typeParams, map) : true;
            if (MappingTypes.Any(pattern.StartsWith) || pattern.StartsWith("global::System.Collections.Generic."))
            {
                var pk = SplitTypeArgs(GenericArg(pattern));
                if (pk.Count == 2 && DictTypes(actual) is List<string> kv) return BindTypeParams(pk[0], kv[0], typeParams, map) && BindTypeParams(pk[1], kv[1], typeParams, map);
            }
            if (pattern.StartsWith("(") && TupleElementTypes(pattern) is List<string> pt && TupleElementTypes(actual) is List<string> atu && pt.Count == atu.Count)
            {
                for (int i = 0; i < pt.Count; i++) if (!BindTypeParams(pt[i], atu[i], typeParams, map)) return false;
                return true;
            }
            int lt = pattern.IndexOf('<'), alt = actual.IndexOf('<');
            if (lt > 0 && alt > 0 && pattern.Substring(0, lt) == actual.Substring(0, alt) && pattern.EndsWith(">") && actual.EndsWith(">"))
            {
                var ps = SplitTypeArgs(GenericArg(pattern)); var avs = SplitTypeArgs(GenericArg(actual));
                if (ps.Count == avs.Count) { for (int i = 0; i < ps.Count; i++) if (!BindTypeParams(ps[i], avs[i], typeParams, map)) return false; }
                return true;
            }
            // List<T> from another list-like type (an array, a deque): its element type
            if (pattern.StartsWith("List<") && ElementTypeOf(actual) is string le && !IsUnknownType(le)) return BindTypeParams(GenericArg(pattern), PyType(le), typeParams, map);
            return true;
        }

        /// <summary>The type with its type parameters replaced by their bindings; null if one is unbound.</summary>
        static string SubstituteTypeParams(string type, List<string> typeParams, Dictionary<string, string> map)
        {
            bool complete = true;
            string result = System.Text.RegularExpressions.Regex.Replace(type, @"\b[A-Za-z_]\w*\b", m =>
            {
                if (!typeParams.Contains(m.Value)) return m.Value;
                if (map.TryGetValue(m.Value, out var v)) return v;
                complete = false;
                return m.Value;
            });
            if (!complete) return null;
            // T? of a reference type is the type itself
            if (result.EndsWith("?") && !IsValueTypeName(result.Substring(0, result.Length - 1))) result = result.Substring(0, result.Length - 1);
            return result;
        }

        static readonly HashSet<string> StrToStr = new HashSet<string>
        {
            "lower", "upper", "strip", "lstrip", "rstrip", "replace", "title", "capitalize", "join", "zfill", "center", "ljust", "rjust",
            "swapcase", "casefold", "removeprefix", "removesuffix", "expandtabs", "translate", "format",
        };
        static readonly HashSet<string> StrToBool = new HashSet<string>
        {
            "startswith", "endswith", "isdigit", "isalpha", "isalnum", "isspace", "isupper", "islower", "istitle", "isdecimal", "isnumeric",
            "isidentifier", "isprintable", "isascii",
        };

        /// <summary>The result type of a Python method of str, list, dict or set (the runtime's extension methods).</summary>
        string PythonMethodType(string recv, string name, CallExpr call)
        {
            if (recv == null) return null;
            if (recv == "string")
            {
                if (StrToStr.Contains(name)) return "string";
                if (StrToBool.Contains(name)) return "bool";
                switch (name)
                {
                    case "split": case "rsplit": case "splitlines": return "List<string>";
                    case "find": case "rfind": case "index": case "rindex": case "count": return "long";
                    case "partition": case "rpartition": return "(string, string, string)";
                }
                return null;
            }
            string elem = ElementTypeOf(recv);
            if (recv.StartsWith("List<"))
                switch (name) { case "pop": return elem; case "index": case "count": return "long"; case "copy": return recv; }
            if (IsSetType(recv))
                switch (name)
                {
                    case "union": case "difference": case "intersection": case "symmetric_difference": case "copy": return recv;
                    case "issubset": case "issuperset": case "isdisjoint": return "bool";
                    case "pop": return elem;
                }
            if (recv.StartsWith("global::Typhon.Runtime.deque<"))
                switch (name) { case "pop": case "popleft": return elem; case "index": case "count": return "long"; case "copy": return recv; }
            if (recv.StartsWith("global::Typhon.Runtime.Counter<"))
                switch (name)
                {
                    case "most_common": return $"List<({elem}, long)>";
                    case "elements": return $"IEnumerable<{elem}>";
                    case "total": return "long";
                }
            if (DictTypes(recv) is List<string> kv)
            {
                switch (name)
                {
                    case "get" when call.Args.Count == 2:
                        return kv[1] == TypeInference.Pending ? PyType(InferType(call.Args[1].Value)) ?? TypeInference.Pending : kv[1];
                    // d.get(k) / d.get(k, None) / d.pop(k, None) of value types: an optional value (None when missing)
                    case "get" when call.Args.Count == 1:
                    case "pop" when call.Args.Count == 2 && IsNone(call.Args[1].Value):
                        return IsValueTypeName(kv[1]) && !kv[1].EndsWith("?") ? kv[1] + "?" : kv[1];
                    case "pop": case "setdefault": return kv[1];
                    case "copy": return recv;
                    case "popitem": return $"({kv[0]}, {kv[1]})";
                }
            }
            return null;
        }

        /// <summary>
        /// The type of a comprehension: its generator targets are bound to the element types of their iterables (in a scope of
        /// their own, as in Python), then the element (and value) expression is typed.
        /// </summary>
        string ComprehensionType(ComprehensionExpr comp)
        {
            var saved = _scope;
            var inner = new Scope { Parent = _scope, IsFunctionBoundary = false };
            _scope = inner;
            try
            {
                foreach (var g in comp.Generators) BindTargetTypes(inner, g.Target, IterElementType(InferType(g.Iter)));
                string elt = InferType(comp.Elt) ?? (comp.Elt switch
                {
                    ListExpr { Elts.Count: 0 } => "List<dynamic>",
                    DictExpr { Keys.Count: 0 } => "OrderedDictionary<dynamic, dynamic>",
                    _ => null,
                });
                if (elt == TypeInference.Pending) return elt;
                // late-bound items: what the LINQ query builds ([f(x) for x in d] is a List<dynamic> to C#)
                if ((elt == null || elt == "dynamic") && (comp.Kind == ComprehensionKind.List || comp.Kind == ComprehensionKind.Generator))
                    return comp.Kind == ComprehensionKind.List ? "List<dynamic>" : "IEnumerable<dynamic>";
                if (elt == null || elt == "dynamic") return null;
                switch (comp.Kind)
                {
                    case ComprehensionKind.List: return $"List<{elt}>";
                    case ComprehensionKind.Set: return $"HashSet<{elt}>";
                    case ComprehensionKind.Generator: return $"IEnumerable<{elt}>";
                    default:
                        string v = InferType(comp.ValueElt);
                        return v == null || v == "dynamic" || v == TypeInference.Pending ? (v == TypeInference.Pending ? v : null) : $"OrderedDictionary<{elt}, {v}>";
                }
            }
            finally { _scope = saved; }
        }

        static void BindTargetTypes(Scope s, Expr target, string type)
        {
            if (target is NameExpr n) { s.Params.Add(n.Id); if (type != null && type != TypeInference.Pending) s.Types[n.Id] = type; }
            else if (target is TupleExpr tup)
            {
                var parts = TupleElementTypes(type);
                string element = type is null or "object" or "dynamic" ? "dynamic" : null;
                for (int i = 0; i < tup.Elts.Count; i++)
                    BindTargetTypes(s, tup.Elts[i], parts != null && i < parts.Count ? parts[i] : element);
            }
        }

        /// <summary>Is the expression (statically) a Python list? Used for list + list and list += iterable.</summary>
        bool IsListy(Expr e)
        {
            if (e is ListExpr) return true;
            if (e is CallExpr { Func: NameExpr { Id: "list" } } && !IsShadowed("list")) return true;
            if (e is ComprehensionExpr c) return c.Kind == ComprehensionKind.List;
            if (e is BinOpExpr b && (b.Op == "+" || b.Op == "*")) return IsListy(b.Left) || IsListy(b.Right);
            var t = InferType(e);
            return t != null && t.StartsWith("List<");
        }

        bool IsMathModule(Expr e) =>
            e is NameExpr n && !IsLocal(n.Id) && (n.Id == "math" && !_m.Globals.Contains("math") || _dotnetAliases.TryGetValue(n.Id, out var target) && target == "Typhon.Stdlib.math_");

        static readonly HashSet<string> MathIntFunctions = new HashSet<string> { "floor", "ceil", "trunc", "factorial", "gcd", "lcm", "isqrt", "comb", "perm" };
        static readonly HashSet<string> MathBoolFunctions = new HashSet<string> { "isnan", "isinf", "isfinite", "isclose" };

        /// <summary>Result type of math.f(...) (Typhon's math module mirrors Python's: floor/ceil/... return int).</summary>
        string MathCall(AttributeExpr f)
        {
            if (!IsMathModule(f.Target)) return null;
            if (MathIntFunctions.Contains(f.Name)) return _big && f.Name != "floor" && f.Name != "ceil" && f.Name != "trunc" ? IntT : "long";
            if (MathBoolFunctions.Contains(f.Name)) return "bool";
            return "double";
        }

        string MathConstant(AttributeExpr a) => IsMathModule(a.Target) && (a.Name == "pi" || a.Name == "e" || a.Name == "tau" || a.Name == "inf" || a.Name == "nan") ? "double" : null;
    }
}
