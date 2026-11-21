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
    // Whole-program inference hooks: what a dry run observes, and what the inferred results give the emission.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>The function body being emitted (per inferred variant): keys the element types of its containers.</summary>
        string _scopeKey = "";
        /// <summary>Locals (and self fields) that started as an empty container in the current body / class: name → kind.</summary>
        Dictionary<string, char> _emptyContainers = new Dictionary<string, char>();
        Dictionary<string, char> _emptyFields = new Dictionary<string, char>();

        /// <summary>The container type an empty literal declares: from the elements added to it (Elements), else null.</summary>
        string EmptyContainerType(string key, char kind, Expr value = null)
        {
            string ContainerOf(string k, string v)
            {
                switch (kind)
                {
                    case 'd': return $"OrderedDictionary<{k}, {v}>";
                    case 'o': return $"global::Typhon.Runtime.PyOrderedDict<{k}, {v}>";
                    case 'D': return $"global::Typhon.Runtime.defaultdict<{k}, {v}>";
                    case 'c': return $"global::Typhon.Runtime.Counter<{k}>";
                    case 's': return $"HashSet<{k}>";
                    case 'q': return $"global::Typhon.Runtime.deque<{k}>";
                    default: return $"List<{k}>";
                }
            }
            string factoryValue = kind == 'D' ? DefaultFactoryValueType(value) : null;
            if (Ti == null || !Ti.Elements.TryGetValue(key, out var elem))
            {
                if (!_dry) return null;
                string p = TypeInference.Pending; // still being inferred: reading its elements gives no information
                return ContainerOf(p, factoryValue ?? p);
            }
            if (TypeInference.IsMappingKind(kind))
            {
                var kv = elem.Split('→');
                if (kv.Length != 2) return null;
                string v = kind == 'c' ? "long" : kv[1] == "dynamic" && factoryValue != null && !factoryValue.Contains(TypeInference.Pending) ? factoryValue : kv[1];
                return ContainerOf(kv[0], v);
            }
            // [None] * n: its items start as None, so a value type is nullable
            if (value != null && TypeInference.IsNonePlaceholder(value) && IsValueTypeName(elem) && !elem.EndsWith("?")) elem += "?";
            return ContainerOf(elem, null);
        }

        /// <summary>defaultdict(int) → long, (float) → double, (str) → string, (lambda: e) → e's type; list/set/dict: from the uses (null).</summary>
        string DefaultFactoryValueType(Expr value)
        {
            var factory = (value as CallExpr)?.Args.FirstOrDefault(a => a.Name == null)?.Value;
            switch (factory)
            {
                case NameExpr n when n.Id == "int": return IntT;
                case NameExpr n when n.Id == "float": return "double";
                case NameExpr n when n.Id == "str": return "string";
                case NameExpr n when n.Id == "bool": return "bool";
                case LambdaExpr l when l.Params.Count == 0: return PyType(InferType(l.Body));
                default: return null;
            }
        }

        /// <summary>The construction of an empty container of a known type: [] / new() / new Counter&lt;T&gt;() / new defaultdict&lt;K, V&gt;(factory).</summary>
        string EmitEmptyContainer(Expr value, string type, char kind)
        {
            switch (kind)
            {
                case 'c': case 'o': return $"new {type}()";
                case 'q':
                {
                    var maxlen = (value as CallExpr)?.Args.FirstOrDefault(a => a.Name == "maxlen");
                    return maxlen != null ? $"new {type}({EmitExpr(maxlen.Value)})" : $"new {type}()";
                }
                case 'D':
                {
                    var kv = SplitTypeArgs(GenericArg(type));
                    var factory = (value as CallExpr)?.Args.FirstOrDefault(a => a.Name == null)?.Value;
                    string name = factory is NameExpr fn ? fn.Id : "function";
                    string make = factory == null ? "null" : factory is LambdaExpr fl ? $"() => {EmitValue(fl.Body)}"
                        : name == "int" ? (_big ? "() => global::System.Numerics.BigInteger.Zero" : "() => 0L") : name == "float" ? "() => 0.0" : name == "str" ? "() => \"\"" : name == "bool" ? "() => false"
                        : $"() => new {kv[1]}()";
                    return $"new {type}({make}, {CsString(name)})";
                }
                default:
                    if (TypeInference.IsNonePlaceholder(value)) return EmitNonePlaceholder(value, type);
                    return EmitExpr(value, targetTyped: true);
            }
        }

        /// <summary>[None] * n as a List&lt;T&gt; of n nulls.</summary>
        string EmitNonePlaceholder(Expr value, string listType)
        {
            if (value is ListExpr nones) return $"new {listType} {{ {string.Join(", ", nones.Elts.Select(_ => "default!"))} }}";
            if (value is ComprehensionExpr c)
                return $"global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Select({EmitIterable(c.Generators[0].Iter)}, __x => ({GenericArg(listType)})default!))";
            var b = (BinOpExpr)value;
            Expr count = b.Left is ListExpr ? b.Right : b.Left;
            return $"{Rt("__repeat")}(new {listType} {{ default! }}, {EmitExpr(count)})";
        }

        /// <summary>A dry run only records observations for <see cref="TypeInference"/>; its output is discarded.</summary>
        readonly bool _dry;
        TypeInference Ti => _p.Inference;

        /// <summary>The wider type a variable of the current body needs because it is rebound to other types (see Observations.Conflicts), or null.</summary>
        string ConflictType(string name) => Ti != null && Ti.Conflicts.TryGetValue(_scopeKey + "|" + name, out var t) ? t : null;
        Observations Obs => _dry ? Ti?.Obs : null;
        /// <summary>Return-type key of the body being emitted (its returns are recorded), or null.</summary>
        string _retKey;
        /// <summary>Key of the inference target whose dynamic fallback body is being emitted (its calls might never run).</summary>
        string _ctxFallbackKey;

        InferenceTarget TargetOf(ModuleInfo m, ClassDef c, string name) =>
            Ti != null && Ti.Targets.TryGetValue(TypeInference.FuncKey(m, c, name), out var t) ? t : null;

        /// <summary>The __init__ that constructing <paramref name="c"/> runs (its own, or the nearest Typhon base class's).</summary>
        InferenceTarget InitTarget(ModuleInfo m, ClassDef c, int depth = 0)
        {
            if (m == null || c == null || depth > 20) return null;
            if (c.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")) return TargetOf(m, c, "__init__") is InferenceTarget t && t.Class == c ? t : null;
            foreach (var b in c.Bases)
                if ((b is SubscriptExpr s ? s.Target : b) is NameExpr bn && _p.TryFindClass(m, bn.Id, out var bc, out var owner))
                    return InitTarget(owner, bc, depth + 1);
            return null;
        }

        /// <summary>The project function or constructor a call resolves to (by name, imported name or module attribute).</summary>
        InferenceTarget CallTarget(CallExpr call, out bool isConstructor)
        {
            isConstructor = false;
            if (Ti == null) return null;
            ModuleInfo mod = null; string name = null;
            if (call.Func is NameExpr n)
            {
                if (IsLocal(n.Id)) return null;
                if (_m.Functions.Contains(n.Id) || _m.Classes.ContainsKey(n.Id)) { mod = _m; name = n.Id; }
                else if (_importedFromModules.TryGetValue(n.Id, out var imp)) { mod = imp.Key; name = imp.Value; }
            }
            else if (call.Func is AttributeExpr a && DottedName(a.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) && ModuleRef(dotted) is ModuleInfo mref)
            {
                mod = mref; name = a.Name;
            }
            if (mod == null) return null;
            if (mod.Classes.TryGetValue(name, out var cls)) { isConstructor = true; return InitTarget(mod, cls); }
            return mod.Functions.Contains(name) ? TargetOf(mod, null, name) : null;
        }

        /// <summary>Argument types of a call, one per parameter (defaults fill omitted ones); null if the shape is unknown.</summary>
        string[] ArgTypes(InferenceTarget t, List<Arg> args)
        {
            var ps = t.Params;
            var types = new string[ps.Count];
            int pos = 0;
            foreach (var a in args)
            {
                if (a.Star || a.DoubleStar) return null;
                int idx = a.Name == null ? pos++ : ps.FindIndex(p => p.Name == a.Name);
                if (idx < 0 || idx >= ps.Count) return null;
                types[idx] = TypeInference.Settled(PyType(InferType(a.Value)) ?? TypeInference.Unknown);
                if (types[idx] == TypeInference.Pending) types[idx] = TypeInference.Unknown;
            }
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].Annotation != null && !(TypeInference.IsBareContainer(ps[i].Annotation) && types[i] != null && ContainerMatches(ps[i].Annotation, types[i])))
                { types[i] = TypeInference.Declared; continue; }
                if (types[i] == null && IsParamAlias(t.Def, ps[i])) { types[i] = TypeInference.Declared; continue; }
                if (types[i] == null) types[i] = ps[i].Default != null ? (PyType(InferType(ps[i].Default)) ?? TypeInference.Unknown) : null;
                if (types[i] == null) return null;
                // a type written in another module's terms might not resolve there: only portable types cross modules
                if (t.Module != _m && !IsPortableType(types[i])) types[i] = TypeInference.Unknown;
            }
            return types;
        }

        /// <summary>Does the call-site type fit a bare container annotation (list → List&lt;T&gt;, tuple → a tuple, ...)?</summary>
        static bool ContainerMatches(Expr annotation, string t)
        {
            string name = annotation is NameExpr n ? n.Id.ToLowerInvariant() : annotation is AttributeExpr a ? a.Name.ToLowerInvariant() : null;
            switch (name)
            {
                case "list": return t.StartsWith("List<");
                case "dict": return IsDictType(t);
                case "set": case "frozenset": return IsSetType(t);
                case "tuple": return t.StartsWith("(") || t.StartsWith("ValueTuple<") || t.StartsWith("global::Typhon.Runtime.PyTuple<");
                default: return false;
            }
        }

        static readonly HashSet<string> PortableNames = new HashSet<string>
        {
            "int", "long", "double", "float", "bool", "string", "char", "decimal", "List", "Dictionary", "OrderedDictionary", "HashSet", "IEnumerable",
            "IReadOnlyList", "IList", "IDictionary", "IReadOnlyDictionary", "ValueTuple", "global",
        };

        /// <summary>Built from primitives and generic collections only (or fully qualified), so it means the same in every module.</summary>
        static bool IsPortableType(string t)
        {
            if (t.StartsWith("global::")) return true;
            foreach (var part in t.Split(new[] { '<', '>', ',', ' ', '(', ')', '[', ']', '?' }, StringSplitOptions.RemoveEmptyEntries))
                if (!PortableNames.Contains(part)) return false;
            return true;
        }

        void RecordCall(InferenceTarget t, List<Arg> args)
        {
            if (Obs == null || t == null || !t.InferParams) return;
            Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = ArgTypes(t, args), Context = _ctxFallbackKey });
        }

        /// <summary>The inferred return type of a call to an unannotated project function (Pending while still unknown in a dry run).</summary>
        string InferredCallType(CallExpr call)
        {
            var t = CallTarget(call, out bool isCtor);
            return isCtor ? null : InferredCallType(t, call.Args);
        }

        string InferredCallType(InferenceTarget t, List<Arg> args)
        {
            if (t == null || t.Def.Returns != null && !TypeInference.IsBareContainer(t.Def.Returns) || t.Def.IsAsync) return null;
            string sig = "";
            if (t.InferParams)
            {
                var types = ArgTypes(t, args);
                // (a late-bound argument is part of the signature: its parameter is dynamic in the overload for this call)
                if (types == null || types.Any(x => TypeInference.IsUnknown(x) && x != "dynamic") || types.All(x => x == "dynamic" || x == TypeInference.Declared)) return null;
                sig = TypeInference.SigKey(types);
                if (!(Ti.Signatures.TryGetValue(t.Key, out var sigs) && sigs.Any(s => TypeInference.SigKey(s) == sig))) return _dry ? TypeInference.Pending : null;
            }
            if (Ti.Returns.TryGetValue(TypeInference.ReturnKey(t.Key, sig), out var rt))
                return t.Module == _m || IsPortableType(rt) ? rt : null;
            return _dry ? TypeInference.Pending : null;
        }

        /// <summary>Typhon class key for a C# type name visible in this module (same module, or imported by name).</summary>
        ClassDef ClassDefOfKey(string key)
        {
            foreach (var m in _p.Modules.Values)
                foreach (var c in m.Classes.Values)
                    if (TypeInference.ClassKey(m, c.Name) == key) return c;
            return null;
        }

        string ClassKeyOfType(string csType, out bool sameModule)
        {
            sameModule = false;
            if (csType == null) return null;
            if (_m.Classes.ContainsKey(csType)) { sameModule = true; return TypeInference.ClassKey(_m, csType); }
            if (_importedFromModules.TryGetValue(csType, out var imp) && imp.Key.Classes.ContainsKey(imp.Value)) return TypeInference.ClassKey(imp.Key, imp.Value);
            return null;
        }

        /// <summary>The type of member <paramref name="member"/> (field, or method return when <paramref name="method"/>) of a Typhon class.</summary>
        string MemberType(string csType, string member, bool method)
        {
            if (_cls != null && csType == _cls.CsSelfType)
            {
                if (!method && _cls.FieldTypes.TryGetValue(member, out var own)) return own;
                if (method && _cls.MethodReturnTypes.TryGetValue(member, out var ownRet)) return ownRet == "void" ? null : ownRet;
            }
            var key = ClassKeyOfType(csType, out bool same);
            if (key == null || Ti == null) return null;
            var table = method ? Ti.MethodReturns : Ti.Fields;
            if (!table.TryGetValue(key, out var members) || !members.TryGetValue(member, out var t) || t == "void")
                return !method && ClassDefOfKey(key) is ClassDef kc && InheritedFieldType(kc, member) is string inherited && (same || IsPortableType(inherited)) ? inherited : null;
            // a dynamic field is late-bound to C# too (as the class's own fields are: self.cells)
            if (t == "dynamic") return !method ? "dynamic" : null;
            return same || IsPortableType(t) ? t : null;
        }

        /// <summary>The type of a field a Typhon base class declares (self.v1 in a subclass's method), or null.</summary>
        string InheritedFieldType(ClassDef c, string name, int depth = 0)
        {
            if (depth > 20 || Ti == null) return null;
            foreach (var b in TyphonBases(c))
            {
                if (_p.TryFindClass(_m, b.Name, out var bc, out var owner) && bc == b && Ti.Fields.TryGetValue(TypeInference.ClassKey(owner, bc.Name), out var fs) &&
                    fs.TryGetValue(name, out var t) && t != "dynamic" && t != "void")
                    return t;
                if (InheritedFieldType(b, name, depth + 1) is string deeper) return deeper;
            }
            return null;
        }
    }
}
