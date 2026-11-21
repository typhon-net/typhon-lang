// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed class CodeWriter
    {
        readonly StringBuilder _sb = new StringBuilder();
        int _indent;
        public void Line(string s = "") { if (s.Length > 0) _sb.Append(' ', _indent * 4); _sb.Append(s).Append('\n'); }
        public void Open() { Line("{"); _indent++; }
        public void Close(string suffix = "") { _indent--; Line("}" + suffix); }
        public void Raw(string s) { _sb.Append(s).Append('\n'); }
        public int Indent { get => _indent; set => _indent = value; }
        public override string ToString() => _sb.ToString();
    }

    /// <summary>Per-function emission state.</summary>
    internal sealed class Scope
    {
        public Scope Parent;
        public LocalsResult Locals;
        public readonly HashSet<string> Params = new HashSet<string>();
        public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
        public string ReturnType;        // element type for generators, awaited type for async; "void" for none
        public bool IsGenerator;
        /// <summary>A generator that receives values (x = yield v) or returns one: a PyGenerator whose body reads them.</summary>
        public bool ReceivesValues;
        public bool IsAsync;
        public bool HasSelf;             // instance method: 'self' → 'this'
        public string ClsName;           // classmethod: 'cls' → class name
        public bool IsFunctionBoundary = true;
        /// <summary>Parameters that stand for a function (def f(x, bisect=bisect) never passed): name → that function.</summary>
        public readonly Dictionary<string, Expr> Aliases = new Dictionary<string, Expr>();
    }

    internal sealed class ClassContext
    {
        public ClassDef Def;
        public string Name;
        public string CsSelfType;        // Name<T,...>
        public bool IsInterface;
        public bool IsSealed;
        public readonly HashSet<string> Members = new HashSet<string>();
        public readonly Dictionary<string, string> FieldTypes = new Dictionary<string, string>();
        public readonly HashSet<string> TypeParams = new HashSet<string>();
        public string IterElementType;
        public readonly Dictionary<string, string> MethodReturnTypes = new Dictionary<string, string>();
        public bool NoInitDone;
    }

    internal sealed partial class ModuleEmitter
    {
        readonly ModuleInfo _m;
        readonly ProjectContext _p;
        readonly List<TyDiagnostic> _diags;
        readonly ITypeResolver _r;
        readonly CodeWriter _w = new CodeWriter();

        readonly List<string> _usingLines = new List<string>();
        readonly List<string> _usingNamespaces = new List<string> { "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks" };
        readonly List<string> _staticTypes = new List<string>();
        readonly Dictionary<string, ModuleInfo> _moduleAliases = new Dictionary<string, ModuleInfo>();
        readonly Dictionary<string, KeyValuePair<ModuleInfo, string>> _importedFromModules = new Dictionary<string, KeyValuePair<ModuleInfo, string>>();
        readonly List<ModuleInfo> _wildcardModules = new List<ModuleInfo>();
        readonly Dictionary<string, string> _dotnetAliases = new Dictionary<string, string>();
        readonly Dictionary<string, string> _importedTypes = new Dictionary<string, string>();

        Scope _scope;
        ClassContext _cls;
        readonly Stack<HashSet<string>> _lambdaParams = new Stack<HashSet<string>>();
        int _tmp;
        string _path;

        /// <summary>Big-int mode: Python's int is BigInteger (see ProjectContext.BigInts); else a checked long.</summary>
        bool _big => _p.BigInts;
        /// <summary>The C# type of Python's int.</summary>
        string IntT => _p.BigInts ? TypeInference.BigInt : "long";

        public ModuleEmitter(ModuleInfo module, ProjectContext project, List<TyDiagnostic> diags, bool dryRun = false)
        {
            _m = module; _p = project; _diags = diags; _r = project.Resolver; _path = module.Path; _dry = dryRun;
        }

        // ───────────────────────────── whole-program inference hooks ─────────────────────────────

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

        // ───────────────────────────── diagnostics ─────────────────────────────

        void Report(Node at, string id, Severity sev, string msg)
        {
            _diags.Add(new TyDiagnostic { Id = id, Severity = sev, Message = msg, Path = _path, Line = at?.Line ?? 1, Col = at?.Col ?? 1 });
        }

        void Error(Node at, string msg) => Report(at, "TY0002", Severity.Error, msg);
        void Unsupported(Node at, string what) => Report(at, "TY0003", Severity.Error, what + " is not supported in Typhon yet");

        /// <summary>Every implicit fallback to 'dynamic' goes through here (TypeScript-style noImplicitAny).</summary>
        string ImplicitDynamic(Node at, string what)
        {
            if (_p.TypeMode == TypeMode.Strict)
                Report(at, "TY1001", Severity.Error, $"cannot infer the type of {what}; add a type annotation (or annotate it as 'Any' to opt into dynamic typing)");
            else if (_p.TypeMode == TypeMode.Warn)
                Report(at, "TY1001", Severity.Warning, $"{what} is implicitly dynamic (late-bound, slower); add a type annotation");
            return "dynamic";
        }

        void LineDirective(Node n)
        {
            if (n != null && n.Line > 0) _w.Raw("#line " + n.Line + " \"" + _path + "\"");
        }

        // ───────────────────────────── module ─────────────────────────────

        public string Emit()
        {
            foreach (var n in AllNodes(_m.Ast.Body))
            {
                if (n is ImportStmt i) ProcessImport(i);
                else if (n is ImportFromStmt f) ProcessImportFrom(f);
            }

            _w.Raw("// <auto-generated>");
            _w.Raw("//   Generated by the Typhon compiler from " + System.IO.Path.GetFileName(_path) + ". Do not edit.");
            _w.Raw("// </auto-generated>");
            _w.Raw("#pragma warning disable CS1718, CS0183, CS0184, CS0162, CS0164, CS0168, CS0219, CS8321, CS1998, CS0105, CS0659, CS0660, CS0661, CS8981, CS1591");
            _w.Raw("#nullable enable annotations");
            _w.Line("using System;");
            _w.Line("using System.Collections.Generic;");
            _w.Line("using System.Linq;");
            _w.Line("using System.Threading.Tasks;");
            _w.Line("using static global::Typhon.Runtime.Builtins;");
            _w.Line("using static global::Typhon.Runtime.DictGetStruct;");
            _w.Line("using static global::Typhon.Runtime.DictGetClass;");
            foreach (var u in _usingLines.Distinct()) _w.Line(u);
            _w.Line();

            // return types of module-level functions feed the local type inference
            foreach (var f in _m.Ast.Body.OfType<FunctionDef>())
                if (f.Returns != null && !TypeInference.IsBareContainer(f.Returns) && !f.IsAsync && f.TypeParams.Count == 0 && !AstWalker.ContainsOutsideNestedScopes(f.Body, n => n is YieldExpr))
                    _funcReturnTypes[f.Name] = MapType(f.Returns, f, isReturn: true);

            bool hasNs = !string.IsNullOrEmpty(_m.Namespace);
            if (hasNs) { _w.Line("namespace " + _m.Namespace); _w.Open(); }

            var body = _m.Ast.Body;
            EmitDocComment(body);
            LineDirective(body.FirstOrDefault());
            _w.Line("public static partial class " + Esc(_m.ClassName));
            _w.Open();
            EmitModuleBody(body);
            _w.Close();

            if (hasNs) _w.Close();
            _w.Raw("#line default");
            return _w.ToString();
        }

        static IEnumerable<Node> AllNodes(object root)
        {
            var list = new List<Node>();
            AstWalker.VisitAll(root, list.Add);
            return list;
        }

        static bool IsMainGuard(Stmt s)
        {
            if (!(s is IfStmt i) || !(i.Test is CompareExpr c) || c.Ops.Count != 1 || c.Ops[0] != "==") return false;
            bool IsName(Expr e) => e is NameExpr n && n.Id == "__name__";
            bool IsMain(Expr e) => e is ConstExpr k && k.Kind == ConstKind.Str && k.Str == "__main__";
            return (IsName(c.Left) && IsMain(c.Comparators[0])) || (IsMain(c.Left) && IsName(c.Comparators[0]));
        }

        void EmitModuleBody(List<Stmt> body)
        {
            bool isMainModule = _m.IsMainModule;

            // free variables of functions/classes in this module → must be static fields, not Main locals
            var usedInDefs = new HashSet<string>();
            foreach (var s in body.Where(s => s is FunctionDef || s is ClassDef)) CollectFreeNames(s, usedInDefs);
            // globals() reads module names by string: every module-level name is a field then
            bool usesGlobals = false;
            AstWalker.VisitAll(body, n => { if (n is CallExpr gc && gc.Func is NameExpr gn && gn.Id == "globals" && gc.Args.Count == 0) usesGlobals = true; });
            if (usesGlobals)
                foreach (var s in body)
                    if (s is AssignStmt ga) foreach (var t in ga.Targets) foreach (var nm in NamesIn(t)) usedInDefs.Add(nm);
                    else if (s is AnnAssignStmt gaa && gaa.Target is NameExpr gan) usedInDefs.Add(gan.Id);

            FindFunctionAliases(body);
            var fields = new Dictionary<string, string>();      // name → C# type
            var execStmts = new List<Stmt>();                     // static ctor (regular) / Main (main module)
            var mainBlocks = new List<Stmt>();

            foreach (var s in body)
            {
                // defaults Python evaluates when the def / class statement runs: fields assigned at this position
                if (s is FunctionDef fdef) RegisterDefaultFields(fdef, null, fields, execStmts);
                if (s is ClassDef cdef) foreach (var m in cdef.Body.OfType<FunctionDef>()) RegisterDefaultFields(m, cdef.Name, fields, execStmts);
                if (s is FunctionDef || s is ClassDef || s is ImportStmt || s is ImportFromStmt || s is PassStmt) continue;
                if (s is AssignStmt aliasStmt && aliasStmt.Targets.Count == 1 && aliasStmt.Targets[0] is NameExpr aliasName && _m.TypeAliases.ContainsKey(aliasName.Id)) continue;
                if (s is AssignStmt fnAlias && fnAlias.Targets.Count == 1 && fnAlias.Targets[0] is NameExpr fnAliasName && _functionAliases.ContainsKey(fnAliasName.Id)) continue;
                if (s is AnnAssignStmt aliasAnn && aliasAnn.Target is NameExpr aliasAnnName && _m.TypeAliases.ContainsKey(aliasAnnName.Id)) continue;
                if (s is ExprStmt es && es.Value is ConstExpr ce && (ce.Kind == ConstKind.Str || ce.Kind == ConstKind.Ellipsis)) continue;
                if (IsMainGuard(s)) { mainBlocks.AddRange(((IfStmt)s).Body); continue; }

                string fieldName = null; Expr ann = null, value = null;
                if (s is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr an) { fieldName = an.Id; value = a.Value; }
                else if (s is AnnAssignStmt aa && aa.Target is NameExpr aan) { fieldName = aan.Id; ann = aa.Annotation; value = aa.Value; }

                // remember the type of every module-level name (field or Main local) for later inference
                if (fieldName != null && !_moduleFieldTypes.ContainsKey(fieldName))
                {
                    string t = ann != null ? MapType(ann, s) : PyType(InferType(value));
                    if (t != null) _moduleFieldTypes[fieldName] = t;
                }

                bool makeField = fieldName != null && !fields.ContainsKey(fieldName) && (!isMainModule || usedInDefs.Contains(fieldName));
                if (makeField)
                {
                    // NAMES = [] filled later (appends anywhere in the module): typed by what is added, like a local list
                    string emptyType = null;
                    if (ann == null && value != null && TypeInference.IsEmptyContainer(value))
                    {
                        char ekind = TypeInference.ContainerKind(value);
                        _emptyModuleFields[fieldName] = ekind;
                        emptyType = EmptyContainerType(ModuleFieldKey(fieldName), ekind, value);
                        if (emptyType != null && (emptyType.Contains("dynamic") || emptyType.Contains(TypeInference.Pending) && !_dry)) emptyType = null;
                    }
                    string type = ann != null ? MapType(ann, s) : emptyType ?? (PyType(InferType(value)) ?? ImplicitDynamic(s, $"module variable '{fieldName}'"));
                    fields[fieldName] = type;
                    _moduleFieldTypes[fieldName] = type;
                    string vis = SurfaceVisibility(fieldName.StartsWith("_") ? "internal" : "public", type);
                    LineDirective(s);
                    // a field initializer runs before the static constructor: only while no earlier module statement had to go
                    // there, else this assignment joins them, so the module runs top to bottom like Python's
                    if (!isMainModule && value != null && execStmts.Count == 0)
                        _w.Line($"{vis} static {type} {Esc(fieldName)} = {Checked(type != "dynamic" ? EmitExpr(value, targetTyped: true) : EmitValue(value))};");
                    else
                    {
                        _w.Line($"{vis} static {type} {Esc(fieldName)};");
                        if (value != null) execStmts.Add(s);
                    }
                    continue;
                }
                // N, E, S, W = -10, 1, 10, -1 / a = b = 0: every name bound here that a def reads (or another module may
                // import) is a field too; the statement then assigns the fields
                if (s is AssignStmt multi && (multi.Targets.Count > 1 || multi.Targets[0] is TupleExpr || multi.Targets[0] is ListExpr))
                {
                    var bound = new Dictionary<string, string>();
                    foreach (var target in multi.Targets) UnpackedTypes(target, multi.Value, PyType(InferType(multi.Value)), bound);
                    bool declared = false;
                    foreach (var kv in bound)
                    {
                        if (kv.Value != null && !_moduleFieldTypes.ContainsKey(kv.Key)) _moduleFieldTypes[kv.Key] = kv.Value;
                        if (fields.ContainsKey(kv.Key) || (isMainModule && !usedInDefs.Contains(kv.Key))) continue;
                        string type = kv.Value ?? ImplicitDynamic(s, $"module variable '{kv.Key}'");
                        fields[kv.Key] = type;
                        _moduleFieldTypes[kv.Key] = type;
                        LineDirective(s);
                        _w.Line($"{SurfaceVisibility(kv.Key.StartsWith("_") ? "internal" : "public", type)} static {type} {Esc(kv.Key)};");
                        declared = true;
                    }
                    if (declared || bound.Keys.Any(fields.ContainsKey)) { execStmts.Add(s); continue; }
                }
                execStmts.Add(s);
            }
            if (fields.Count > 0) _w.Line();
            _moduleFieldNames.UnionWith(fields.Keys);

            foreach (var s in body)
            {
                if (s is FunctionDef f) { EmitFunction(f, isModuleLevel: true); _w.Line(); }
                else if (s is ClassDef c) { EmitClass(c); _w.Line(); }
            }
            foreach (var f in body.OfType<FunctionDef>())
                if (FunctionValueKeys.Contains(TypeInference.FuncKey(_m, null, f.Name))) { EmitFunctionValueClass(f); _w.Line(); }

            if (isMainModule)
            {
                var all = new List<Stmt>(execStmts);
                all.AddRange(mainBlocks);
                EmitMain(all, fields.Keys, body.FirstOrDefault());
            }
            else
            {
                if (execStmts.Count > 0)
                {
                    _scopeKey = TypeInference.FuncKey(_m, null, "<module>");
                    _w.Line("static " + Esc(_m.ClassName) + "()");
                    EmitFunctionBody(execStmts, new Scope { ReturnType = "void" }, fields.Keys);
                    _w.Line();
                }
                if (mainBlocks.Count > 0 && (_p.MainModule == null || _p.MainModule == _m.Name)) EmitMain(mainBlocks, fields.Keys, body.First(IsMainGuard));
            }
        }

        // ───────────────────────────── functions as values ─────────────────────────────
        //
        // A module function used as a value (passed, assigned, stored, returned) has its own type: a generated sealed
        // subclass of PyFunction with a single instance. Inference sees that type like any other, so a parameter that
        // receives the function is specialised per function (one overload each, like a template argument), and a call
        // through a value of that type is a direct, statically typed call of the function, recorded for its signatures.

        const string FnPrefix = "__fn_";

        /// <summary>Module fields that start as an empty container (their element types come from what the module adds).</summary>
        readonly Dictionary<string, char> _emptyModuleFields = new Dictionary<string, char>();
        string ModuleFieldKey(string name) => _m.Name + "|module." + name;

        /// <summary>The module's static fields (module-level variables functions can see).</summary>
        readonly HashSet<string> _moduleFieldNames = new HashSet<string>();

        /// <summary>globals(): the module's names and values (fields, classes, functions used as values), read when called.</summary>
        string EmitGlobals()
        {
            var entries = new List<string> { $"[\"__name__\"] = {CsString(_m.Name)}" };
            foreach (var f in _moduleFieldNames.OrderBy(x => x, StringComparer.Ordinal)) entries.Add($"[{CsString(f)}] = {Esc(f)}");
            foreach (var c in _m.Classes.Keys.OrderBy(x => x, StringComparer.Ordinal)) entries.Add($"[{CsString(c)}] = typeof({Esc(c)})");
            foreach (var fn in _m.Ast.Body.OfType<FunctionDef>())
                if (FunctionValueKeys.Contains(TypeInference.FuncKey(_m, null, fn.Name))) entries.Add($"[{CsString(fn.Name)}] = {FunctionValueType(_m, fn.Name)}.Instance");
            return $"new OrderedDictionary<string, object?> {{ {string.Join(", ", entries)} }}";
        }

        HashSet<string> FunctionValueKeys => _p.FunctionValueKeys ??= ComputeFunctionValueKeys(_p);

        internal static bool IsFunctionValueCandidate(FunctionDef f) => f.TypeParams.Count == 0 && f.Decorators.Count == 0 && f.Params.All(p => p.Kind == ParamKind.Normal);

        static HashSet<string> ComputeFunctionValueKeys(ProjectContext p)
        {
            var names = new HashSet<string>();
            foreach (var m in p.Modules.Values)
            {
                if (m.Ast == null || m.ParseDiagnostics.Count > 0) continue;
                var callees = new HashSet<Node>();
                var aliases = new List<ImportAlias>();
                AstWalker.VisitAll(m.Ast.Body, n =>
                {
                    if (n is CallExpr c) callees.Add(c.Func);
                    if (n is ImportFromStmt imp) aliases.AddRange(imp.Names.Where(a => a.AsName != null));
                });
                AstWalker.VisitAll(m.Ast.Body, n => { if (n is NameExpr ne && !callees.Contains(ne)) names.Add(ne.Id); });
                foreach (var a in aliases) if (names.Contains(a.AsName)) names.Add(a.Name);
            }
            var keys = new HashSet<string>();
            foreach (var m in p.Modules.Values)
                if (m.Ast != null && m.ParseDiagnostics.Count == 0)
                    foreach (var f in m.Ast.Body.OfType<FunctionDef>())
                        if (names.Contains(f.Name) && IsFunctionValueCandidate(f)) keys.Add(TypeInference.FuncKey(m, null, f.Name));
            return keys;
        }

        static string ModuleClassName(ModuleInfo m) => "global::" + (string.IsNullOrEmpty(m.Namespace) ? "" : m.Namespace + ".") + Esc(m.ClassName);

        /// <summary>The C# type of a module function used as a value.</summary>
        static string FunctionValueType(ModuleInfo m, string name) => ModuleClassName(m) + "." + FnPrefix + name;

        /// <summary>Does the name denote a module function that has a function type (and which)?</summary>
        bool FunctionRef(NameExpr n, out ModuleInfo owner, out FunctionDef def)
        {
            owner = null; def = null;
            if (IsLocal(n.Id) || _renames.ContainsKey(n.Id) || _moduleFieldTypes.ContainsKey(n.Id)) return false;
            if (_m.Functions.Contains(n.Id)) owner = _m;
            else if (_importedFromModules.TryGetValue(n.Id, out var imp) && imp.Key.Functions.Contains(imp.Value)) owner = imp.Key;
            if (owner == null) return false;
            string name = owner == _m ? n.Id : _importedFromModules[n.Id].Value;
            if (!FunctionValueKeys.Contains(TypeInference.FuncKey(owner, null, name))) return false;
            def = owner.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name);
            return def != null;
        }

        /// <summary>The function a function type denotes (null if the type is not one).</summary>
        bool FunctionOfType(string t, out ModuleInfo owner, out FunctionDef def)
        {
            owner = null; def = null;
            if (t == null || !t.StartsWith("global::") || !t.Contains("." + FnPrefix)) return false;
            foreach (var m in _p.Modules.Values)
            {
                string prefix = ModuleClassName(m) + "." + FnPrefix;
                if (!t.StartsWith(prefix) || m.Ast == null) continue;
                string name = t.Substring(prefix.Length);
                def = m.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name);
                if (def != null) { owner = m; return true; }
            }
            return false;
        }

        /// <summary>
        /// A module function as a value: its function type's instance. Where the value goes somewhere its type is not
        /// kept (<paramref name="typed"/> false: a list, a field, a .NET call, a return value), it may later be called with
        /// any argument types, so the function keeps its dynamic version.
        /// </summary>
        string FunctionValue(NameExpr n, bool typed)
        {
            if (!FunctionRef(n, out var owner, out var def)) return null;
            if (!typed && Obs != null && TargetOf(owner, null, def.Name) is InferenceTarget t && t.InferParams)
                Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = null, Context = _ctxFallbackKey });
            return FunctionValueType(owner, def.Name) + ".Instance";
        }

        /// <summary>A call through a value of a function type: a direct call of the function (null if the callee is not one).</summary>
        string EmitFunctionValueCall(CallExpr c)
        {
            if (c.Func is NameExpr fn && !IsLocal(fn.Id) && !_moduleFieldTypes.ContainsKey(fn.Id)) return null; // a plain call by name
            if (!FunctionOfType(InferType(c.Func), out var owner, out var def)) return null;
            var target = TargetOf(owner, null, def.Name);
            if (Obs != null && target != null) RecordCall(target, c.Args);
            return $"{ModuleClassName(owner)}.{Esc(def.Name)}({EmitArgs(c.Args, typedFunctionArgs: target != null && TypedCall(target, c.Args), typhonCallee: true, paramTypeAt: (i, name) => AnnotatedParamType(def, i, name))})";
        }

        string AnnotatedParamType(FunctionDef def, int pos, string name)
        {
            var p = name != null ? def.Params.FirstOrDefault(x => x.Name == name) : pos >= 0 && pos < def.Params.Count ? def.Params[pos] : null;
            return p?.Annotation != null && p.Kind == ParamKind.Normal ? MapType(p.Annotation, p) : null;
        }

        /// <summary>
        /// Does a call reach a typed overload of an inferred function? Then a function passed to it keeps its function type;
        /// a call with an argument of unknown type runs the dynamic version, which may call the function with anything.
        /// </summary>
        bool TypedCall(InferenceTarget t, List<Arg> args) =>
            t.InferParams && ArgTypes(t, args) is string[] types && !types.Any(x => TypeInference.IsUnknown(x));

        /// <summary>The type of a call through a value of a function type (null if the callee is not one).</summary>
        string FunctionValueCallType(CallExpr c)
        {
            if (c.Func is NameExpr fn && !IsLocal(fn.Id) && !_moduleFieldTypes.ContainsKey(fn.Id)) return null;
            if (!FunctionOfType(InferType(c.Func), out var owner, out var def)) return null;
            if (TargetOf(owner, null, def.Name) is InferenceTarget t && InferredCallType(t, c.Args) is string inferred) return inferred;
            if (def.Returns != null && !TypeInference.IsBareContainer(def.Returns))
            {
                string rt = MapType(def.Returns, def, isReturn: true);
                return rt == "void" ? null : rt;
            }
            return null;
        }

        /// <summary>The class of a module function's function type: its instance, a call with any arguments, conversions to delegates.</summary>
        void EmitFunctionValueClass(FunctionDef f)
        {
            string cls = FnPrefix + f.Name;
            var ps = f.Params;
            int required = ps.Count(p => p.Default == null);
            var locals = LocalsAnalyzer.Analyze(f.Body, ps.Select(p => p.Name), null);
            bool returnsValue = f.IsAsync || locals.HasYield || locals.HasValueReturn ||
                                f.Returns != null && !(f.Returns is ConstExpr rc && rc.Kind == ConstKind.None);
            _w.Line($"internal sealed class {cls} : global::Typhon.Runtime.PyFunction");
            _w.Open();
            _w.Line($"public static readonly {cls} Instance = new {cls}();");
            _w.Line($"{cls}() : base({CsString(f.Name)}) {{ }}");
            _w.Line("public override object? __call__(object?[] a)");
            _w.Open();
            for (int n = required; n <= ps.Count; n++)
            {
                string call = $"{Esc(f.Name)}({string.Join(", ", Enumerable.Range(0, n).Select(i => $"(dynamic?)a[{i}]"))})";
                _w.Line(returnsValue ? $"if (a.Length == {n}) return {call};" : $"if (a.Length == {n}) {{ {call}; return null; }}");
            }
            _w.Line($"throw ArgCount({CsString(f.Name)}, {required}, {ps.Count}, a.Length);");
            _w.Close();
            // conversions to the delegate types of its known signatures (a Callable[[int], int] parameter, Func<long, long>)
            var delegates = new HashSet<string>();
            // (a generator's C# type depends on how it is emitted: no conversion; an async function returns a Task)
            bool isGenerator = AstWalker.ContainsOutsideNestedScopes(f.Body, n => n is YieldExpr);
            void Conversion(List<string> types, string ret)
            {
                if (isGenerator || types.Count > 15 || types.Concat(new[] { ret }).Any(x => x == null || x.Contains("dynamic") || x.Contains(TypeInference.Pending) || x.Contains(TypeInference.Unknown))) return;
                if (f.IsAsync) ret = ret == "void" ? "global::System.Threading.Tasks.Task" : $"global::System.Threading.Tasks.Task<{ret}>";
                string d = ret == "void" ? (types.Count == 0 ? "global::System.Action" : $"global::System.Action<{string.Join(", ", types)}>")
                                         : $"global::System.Func<{string.Join(", ", types.Concat(new[] { ret }))}>";
                if (delegates.Add(d)) _w.Line($"public static implicit operator {d}({cls} _) => {Esc(f.Name)};");
            }
            if (ps.All(p => p.Annotation != null) && (f.Returns != null || !returnsValue))
                Conversion(ps.Select(p => MapType(p.Annotation, p)).ToList(), f.Returns != null ? MapType(f.Returns, f, isReturn: true) : "void");
            if (TargetOf(_m, null, f.Name) is InferenceTarget t && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
                foreach (var sig in sigs)
                {
                    var types = sig.Select((x, i) => x == TypeInference.Declared ? MapType(ps[i].Annotation, ps[i]) : x).ToList();
                    string ret = !returnsValue ? "void" : f.Returns != null ? MapType(f.Returns, f, isReturn: true)
                        : Ti.Returns.TryGetValue(TypeInference.ReturnKey(t.Key, TypeInference.SigKey(sig)), out var r) ? r : null;
                    Conversion(types, ret);
                }
            _w.Close();
        }

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

        /// <summary>The type each name in an assignment target gets (null: unknown): a, (b, c) = 1, (2.0, "x"); x, y = point; i, j = range(2).</summary>
        void UnpackedTypes(Expr target, Expr value, string valueType, Dictionary<string, string> into)
        {
            switch (target)
            {
                case NameExpr n:
                    into[n.Id] = TypeInference.IsUnknown(valueType) || valueType == TypeInference.Pending ? null : valueType;
                    return;
                case TupleExpr _:
                case ListExpr _:
                {
                    var elts = target is TupleExpr te ? te.Elts : ((ListExpr)target).Elts;
                    var values = value is TupleExpr vt && vt.Elts.Count == elts.Count ? vt.Elts : value is ListExpr vl && vl.Elts.Count == elts.Count ? vl.Elts : null;
                    var parts = values?.Select(v => PyType(InferType(v))).ToList() ?? TupleElementTypes(valueType);
                    for (int i = 0; i < elts.Count; i++)
                    {
                        if (elts[i] is StarredExpr st) { foreach (var name in NamesIn(st.Value)) into[name] = null; continue; }
                        string part = parts != null && parts.Count == elts.Count ? parts[i] : valueType != null ? ElementTypeOf(valueType) : null;
                        UnpackedTypes(elts[i], values?[i], part, into);
                    }
                    return;
                }
                default: return; // attributes / subscripts bind no names
            }
        }

        static void CollectFreeNames(Stmt s, HashSet<string> into)
        {
            if (s is FunctionDef f)
            {
                var loc = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(p => p.Name), null);
                var bound = new HashSet<string>(loc.AllLocals);
                bound.UnionWith(f.Params.Select(p => p.Name));
                bound.UnionWith(loc.LocalFunctions);
                foreach (var n in AllNodes(f.Body))
                    if (n is NameExpr ne && (!bound.Contains(ne.Id) || loc.Globals.Contains(ne.Id))) into.Add(ne.Id);
                foreach (var p in f.Params) AstWalker.VisitAll(p.Default, n => { if (n is NameExpr ne) into.Add(ne.Id); });
            }
            else if (s is ClassDef c)
            {
                foreach (var st in c.Body)
                {
                    if (st is FunctionDef || st is ClassDef) CollectFreeNames(st, into);
                    else AstWalker.VisitAll(st, n => { if (n is NameExpr ne) into.Add(ne.Id); });
                }
            }
        }

        void EmitMain(List<Stmt> stmts, IEnumerable<string> moduleFields, Node at)
        {
            var scope = new Scope { ReturnType = "void" };
            var locals = LocalsAnalyzer.Analyze(stmts, Enumerable.Empty<string>(), moduleFields);
            bool isAsync = locals.HasAwait;
            LineDirective(at);
            _scopeKey = TypeInference.FuncKey(_m, null, "<main>");
            _w.Line(isAsync ? "public static async Task Main(string[] __argv)" : "public static void Main(string[] __argv)");
            scope.IsAsync = isAsync;
            EmitFunctionBody(stmts, scope, moduleFields, locals, prologue: "global::Typhon.Stdlib.sys_.__init__(__argv);");
        }

        // ───────────────────────────── imports ─────────────────────────────

        ModuleInfo ResolvePsModule(string dotted)
        {
            var m = _p.FindModule(dotted);
            if (m != null) return m;
            // relative to this module's package
            int lastDot = _m.Name.LastIndexOf('.');
            if (lastDot > 0) return _p.FindModule(_m.Name.Substring(0, lastDot) + "." + dotted);
            return null;
        }

        string ResolveRelative(string module, int level)
        {
            if (level == 0) return module;
            var parts = _m.Name.Split('.').ToList();
            parts.RemoveAt(parts.Count - 1);
            for (int i = 1; i < level && parts.Count > 0; i++) parts.RemoveAt(parts.Count - 1);
            if (!string.IsNullOrEmpty(module)) parts.Add(module);
            return string.Join(".", parts);
        }

        static readonly HashSet<string> IgnoredModules = new HashSet<string> { "typing", "abc", "dataclasses", "enum", "__future__", "collections.abc", "typing_extensions", "clr" };

        void ProcessImport(ImportStmt s)
        {
            foreach (var alias in s.Names)
            {
                string name = alias.Name, local = alias.AsName;
                if (IgnoredModules.Contains(name)) continue;
                var mod = ResolvePsModule(name);
                if (mod != null)
                {
                    _moduleAliases[local ?? name] = mod;
                    if (local != null) _usingLines.Add($"using {Esc(local)} = {mod.QualifiedClassName};");
                    continue;
                }
                string stdlib = StdlibType(name);
                if (stdlib != null)
                {
                    string l = local ?? name.Split('.')[0];
                    if (local == null && name.Contains(".")) { Unsupported(s, "'import a.b' of a Typhon standard module (use 'from a import b')"); continue; }
                    _usingLines.Add($"using {Esc(l)} = global::{string.Join(".", stdlib.Replace('+', '.').Split('.').Select(Esc))};");
                    _dotnetAliases[l] = stdlib;
                    continue;
                }
                if (_r.TypeExists(name))
                {
                    if (local != null) { _usingLines.Add($"using {Esc(local)} = global::{name};"); _dotnetAliases[local] = name; }
                    continue;
                }
                if (_r.NamespaceExists(name))
                {
                    if (local != null) { _usingLines.Add($"using {Esc(local)} = global::{name};"); _dotnetAliases[local] = name; }
                    else { _usingLines.Add($"using {name};"); _usingNamespaces.Add(name); }
                    continue;
                }
                Report(s, "TY0101", Severity.Error, $"No module named '{name}' (not a Typhon module in this project, a Typhon standard module, or a .NET namespace/type in the referenced assemblies)");
            }
        }

        void ProcessImportFrom(ImportFromStmt s)
        {
            string modName = ResolveRelative(s.Module, s.Level);
            if (IgnoredModules.Contains(modName)) return;

            var mod = ResolvePsModule(modName);
            if (mod != null)
            {
                _usingLines.Add($"using static {mod.QualifiedClassName};");
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _wildcardModules.Add(mod); continue; }
                    if (!mod.Classes.ContainsKey(a.Name) && !mod.Functions.Contains(a.Name) && !mod.Globals.Contains(a.Name))
                    { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}'"); continue; }
                    _importedFromModules[a.AsName ?? a.Name] = new KeyValuePair<ModuleInfo, string>(mod, a.Name);
                    if (a.AsName != null)
                    {
                        if (mod.Classes.ContainsKey(a.Name)) _usingLines.Add($"using {Esc(a.AsName)} = {mod.QualifiedClassName}.{Esc(a.Name)};");
                        else Unsupported(s, $"renaming the function or variable '{a.Name}' on import");
                    }
                }
                return;
            }

            string stdlib = StdlibType(modName);
            if (stdlib != null || _r.TypeExists(modName))
            {
                string meta = stdlib ?? modName;
                string csName = string.Join(".", meta.Replace('+', '.').Split('.').Select(Esc));
                _usingLines.Add($"using static global::{csName};");
                _staticTypes.Add(meta);
                if (meta.StartsWith("Typhon.Stdlib."))
                    foreach (var a in s.Names)
                        if (a.Name == "*") _stdlibStar.Add(meta);
                        else if (a.AsName == null) _stdlibImported[a.Name] = meta;
                foreach (var a in s.Names)
                    if (a.AsName != null)
                    {
                        if (_r.TypeExists(meta + "+" + a.Name)) _usingLines.Add($"using {Esc(a.AsName)} = global::{csName}.{a.Name};");
                        else Unsupported(s, $"renaming the member '{a.Name}' on import");
                    }
                return;
            }

            if (_r.NamespaceExists(modName))
            {
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _usingLines.Add($"using {modName};"); _usingNamespaces.Add(modName); continue; }
                    string full = modName + "." + a.Name;
                    if (_r.NamespaceExists(full))
                    {
                        string l = a.AsName ?? a.Name;
                        _usingLines.Add($"using {Esc(l)} = global::{full};");
                        _dotnetAliases[l] = full;
                        continue;
                    }
                    if (!AnyArity(full, out _)) { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}' (no such .NET type or namespace)"); continue; }
                    if (a.AsName != null && _r.TypeExists(full))
                    {
                        _usingLines.Add($"using {Esc(a.AsName)} = global::{full};");
                        _importedTypes[a.AsName] = full;
                    }
                    else
                    {
                        _usingLines.Add($"using {modName};");
                        if (!_usingNamespaces.Contains(modName)) _usingNamespaces.Add(modName);
                        _importedTypes[a.Name] = full;
                    }
                }
                return;
            }
            Report(s, "TY0101", Severity.Error, $"No module named '{modName}'");
        }

        /// <summary>
        /// Typhon standard modules are .NET types in namespace Typhon.Stdlib: "math" → Typhon.Stdlib.math_ (built in),
        /// "numpy" → Typhon.Stdlib.numpy (Typhon.NumPy package). The trailing '_' avoids C#'s rule that a member
        /// cannot share its class's name (time.time, random.random). Submodules are nested types (os.path → os_+path).
        /// </summary>
        string StdlibType(string dotted)
        {
            var parts = dotted.Split('.');
            foreach (var head in new[] { parts[0], parts[0] + "_" })
            {
                string meta = "Typhon.Stdlib." + head + (parts.Length > 1 ? "+" + string.Join("+", parts.Skip(1)) : "");
                if (_r.TypeExists(meta)) return meta;
            }
            return null;
        }

        bool AnyArity(string meta, out int arity)
        {
            for (arity = 0; arity <= 8; arity++)
                if (_r.TypeExists(arity == 0 ? meta : meta + "`" + arity)) return true;
            return false;
        }

        // ───────────────────────────── functions ─────────────────────────────

        static string DecoratorName(Expr d)
        {
            if (d is CallExpr c) d = c.Func;
            if (d is NameExpr n) return n.Id;
            if (d is AttributeExpr a) return DottedName(a);
            return null;
        }

        static bool HasDecorator(List<Expr> decorators, string name) => decorators.Any(d => DecoratorName(d) == name || DecoratorName(d) == "typing." + name || DecoratorName(d) == "abc." + name ||
                                                                                           DecoratorName(d) == "dataclasses." + name || DecoratorName(d) == "functools." + name);

        static readonly HashSet<string> KnownDecorators = new HashSet<string>
        {
            "staticmethod", "classmethod", "property", "abstractmethod", "override", "final", "dataclass", "overload",
            "cache", "lru_cache", "functools.cache", "functools.lru_cache",
            "typing.override", "typing.final", "typing.overload", "abc.abstractmethod", "dataclasses.dataclass",
        };

        void EmitAttributes(List<Expr> decorators, Func<string, bool> isHandled = null)
        {
            foreach (var d in decorators)
            {
                string name = DecoratorName(d);
                if (name == null) { Unsupported(d, "this decorator form"); continue; }
                if (KnownDecorators.Contains(name) || name.EndsWith(".setter") || name.EndsWith(".getter") || (isHandled != null && isHandled(name))) continue;
                // any other decorator is treated as a .NET attribute: @Obsolete("x") → [Obsolete("x")]
                if (d is CallExpr c)
                {
                    var args = c.Args.Select(a => a.Name != null ? $"{a.Name} = {EmitExpr(a.Value)}" : EmitExpr(a.Value));
                    _w.Line($"[{name}({string.Join(", ", args)})]");
                }
                else _w.Line($"[{name}]");
            }
        }

        /// <summary>
        /// Typhon's runtime types (PyTuple, Counter, deque, Path, ...) are internal to each assembly (no runtime dependency), so a
        /// member whose signature uses one cannot be public: it becomes internal (C# callers see everything else).
        /// </summary>
        /// <summary>
        /// A public member whose signature uses an internal type (the runtime's, a function type, a Typhon class named _Options)
        /// is internal too: C# requires a signature to be at least as accessible as the member.
        /// </summary>
        string SurfaceVisibility(string vis, params string[] signatureParts) =>
            (vis == "public" || vis == "protected internal" || vis == "protected") &&
            signatureParts.Any(p => p != null && (p.Contains("global::Typhon.Runtime.") || p.Contains("global::Typhon.Stdlib.") || p.Contains("." + FnPrefix) ||
                                                  InternalClassNames is { } ic && ic.IsMatch(p))) ? "internal" : vis;

        /// <summary>The project's internal classes (named _Like_this) as one pattern of whole type names, or null.</summary>
        System.Text.RegularExpressions.Regex InternalClassNames => _internalClassNames ??= BuildInternalClassNames();
        System.Text.RegularExpressions.Regex _internalClassNames;
        System.Text.RegularExpressions.Regex BuildInternalClassNames()
        {
            var names = AllClasses().Select(c => c.Name).Where(n => Visibility(n, topLevel: true) != "public").Distinct().ToList();
            // a type position: after a dot, a bracket, a comma or the start, followed by a non-identifier character or the end
            return names.Count == 0 ? new System.Text.RegularExpressions.Regex("(?!)")
                : new System.Text.RegularExpressions.Regex(@"(^|[.<(,\s])(" + string.Join("|", names.Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?=[>),?\[\s]|$)");
        }

        static string Visibility(string name, bool topLevel)
        {
            if (name.StartsWith("__") && !name.EndsWith("__")) return "private";
            if (name.StartsWith("_") && !name.EndsWith("__")) return topLevel ? "internal" : "protected internal";
            return "public";
        }

        string TypeParamList(List<TypeParam> tps) => tps.Count == 0 ? "" : "<" + string.Join(", ", tps.Select(t => Esc(t.Name))) + ">";

        string TypeParamConstraints(List<TypeParam> tps, Node at)
        {
            var sb = new StringBuilder();
            foreach (var t in tps.Where(t => t.Bound != null))
            {
                var bounds = t.Bound is TupleExpr tup ? tup.Elts : new List<Expr> { t.Bound };
                sb.Append($" where {Esc(t.Name)} : {string.Join(", ", bounds.Select(b => MapType(b, at)))}");
            }
            return sb.ToString();
        }

        sealed class Signature
        {
            public string ReturnType;      // full C# return type incl. Task/IEnumerable wrapping
            public string ElementType;     // yield element type / awaited type / plain type
            public string Params;
            public bool IsGenerator, IsAsync, ReceivesValues;
            public LocalsResult Locals;
            public Scope Scope;
            /// <summary>Statements at the start of the body (parameters falling back to definition-time defaults).</summary>
            public readonly List<string> Prologue = new List<string>();
            /// <summary>Python names the body refers to by another C# name (the non-null local of such a parameter).</summary>
            public readonly Dictionary<string, string> Renames = new Dictionary<string, string>();
        }

        /// <summary>
        /// Parameter defaults Python evaluates once, when the def runs (a module constant, a list, a call). C# only takes
        /// constants, so such a value lives in a static field (module functions and methods; assigned at the def's position in
        /// module initialization) or a local (nested functions), and the parameter defaults to null and falls back to it.
        /// </summary>
        readonly Dictionary<Param, string> _defaultSources = new Dictionary<Param, string>();

        /// <summary>A default C# accepts as a constant: literals and literal-only integer arithmetic that fits 64 bits.</summary>
        bool IsConstantDefault(Expr e)
        {
            // big-int mode: a BigInteger parameter cannot have a constant default (no user-defined conversions there)
            if (_big && FoldInt(e) != null) return false;
            switch (e)
            {
                case ConstExpr c: return c.Kind != ConstKind.Ellipsis && !(c.Kind == ConstKind.Int && !long.TryParse(c.Raw.Replace("_", ""), out _));
                case UnaryExpr u when (u.Op == "-" || u.Op == "+") && u.Operand is ConstExpr uc && (uc.Kind == ConstKind.Int || uc.Kind == ConstKind.Float): return true;
                default: return FoldInt(e) is System.Numerics.BigInteger v && v >= long.MinValue && v <= long.MaxValue;
            }
        }

        // ───────────────────────────── names that stand for a function ─────────────────────────────

        /// <summary>Module names bound once to a function (icount = itertools.count) and only ever called: calls go to it.</summary>
        readonly Dictionary<string, Expr> _functionAliases = new Dictionary<string, Expr>();

        /// <summary>Is the expression a reference to a function: a standard-library or .NET static method, or a module function?</summary>
        bool IsFunctionReference(Expr e)
        {
            switch (e)
            {
                case AttributeExpr fa:
                    if (ModuleRef(DottedName(fa.Target) ?? "") is ModuleInfo mi) return mi.Functions.Contains(fa.Name);
                    if (DotNetStaticMeta(fa.Target) is string sm)
                    {
                        var members = _r.GetMembers(sm, fa.Name);
                        return members.Count > 0 && members.All(m => m.IsMethod && m.IsStatic);
                    }
                    return false;
                case NameExpr n:
                    if (_m.Functions.Contains(n.Id)) return true;
                    if (_m.Classes.ContainsKey(n.Id) || _moduleFieldTypes.ContainsKey(n.Id)) return false;
                    return _staticTypes.Any(st => st.StartsWith("Typhon.Stdlib.") && _r.GetMembers(st, n.Id) is var ms && ms.Count > 0 && ms.All(m => m.IsMethod && m.IsStatic));
                default:
                    return false;
            }
        }

        /// <summary>Is every use of the name in these statements a call (name(...)), apart from the given binding?</summary>
        static bool OnlyCalled(IEnumerable<Node> roots, string name, Node except)
        {
            var callees = new HashSet<Node>();
            int loads = 0, called = 0, binds = 0;
            foreach (var root in roots)
                AstWalker.VisitAll(root, n =>
                {
                    if (n is CallExpr c) callees.Add(c.Func);
                    if (n is AssignStmt a && a != except) foreach (var t in a.Targets) AstWalker.VisitAll(t, x => { if (x is NameExpr tn && tn.Id == name) binds++; });
                    if (n is AugAssignStmt au && au.Target is NameExpr an && an.Id == name) binds++;
                    if (n is GlobalStmt g && g.Names.Contains(name)) binds++;
                });
            foreach (var root in roots)
                AstWalker.VisitAll(root, n => { if (n is NameExpr ne && ne.Id == name) { loads++; if (callees.Contains(ne)) called++; } });
            // the binding's own target counts as a load in the walk: subtract it
            int ownTargets = except is AssignStmt ea ? ea.Targets.Count(t => t is NameExpr tn && tn.Id == name) : 0;
            return binds == 0 && loads - ownTargets == called && called > 0;
        }

        void FindFunctionAliases(List<Stmt> body)
        {
            _functionAliases.Clear();
            foreach (var s in body)
                if (s is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr n && !_m.Functions.Contains(n.Id) && !_m.Classes.ContainsKey(n.Id) &&
                    IsFunctionReference(a.Value) && OnlyCalled(body, n.Id, a))
                {
                    _functionAliases[n.Id] = a.Value;
                    _aliasTargetNodes.Add(a.Value);
                }
        }

        readonly Dictionary<Param, bool> _paramAliases = new Dictionary<Param, bool>();

        /// <summary>
        /// def solve(..., bisect=bisect): a parameter whose default is a function, which the body only calls and no call in the
        /// program passes. A common idiom (a global made a fast local); the name then stands for the function.
        /// </summary>
        bool IsParamAlias(FunctionDef f, Param p)
        {
            if (f == null || p.Default == null || p.Annotation != null || p.Kind != ParamKind.Normal) return false;
            if (_paramAliases.TryGetValue(p, out var known)) return known;
            bool result = IsFunctionReference(p.Default) && OnlyCalled(f.Body, p.Name, null) && !PassedAnywhere(f, p);
            _paramAliases[p] = result;
            return result;
        }

        /// <summary>Does any call of a function with this name pass the parameter (by position or by name)?</summary>
        bool PassedAnywhere(FunctionDef f, Param p)
        {
            int index = f.Params.IndexOf(p);
            bool method = _p.Modules.Values.Any(m => m.Ast != null && m.Classes.Values.Any(c => c.Body.Contains(f)));
            int positional = method ? index - 1 : index;
            bool passed = false;
            foreach (var m in _p.Modules.Values.Where(m => m.Ast != null))
                AstWalker.VisitAll(m.Ast.Body, n =>
                {
                    if (passed || !(n is CallExpr c)) return;
                    string callee = c.Func is NameExpr cn ? cn.Id : c.Func is AttributeExpr ca ? ca.Name : null;
                    if (callee != f.Name) return;
                    if (c.Args.Any(a => a.Star || a.DoubleStar || a.Name == p.Name) || c.Args.Count(a => a.Name == null) > positional) passed = true;
                });
            return passed;
        }

        /// <summary>The expressions aliases stand for (bisect in bisect=bisect): they name the function itself, never the alias.</summary>
        readonly HashSet<Node> _aliasTargetNodes = new HashSet<Node>();

        /// <summary>The function a called name stands for (a parameter alias in scope, or a module alias), or null.</summary>
        Expr AliasTarget(NameExpr n)
        {
            if (_aliasTargetNodes.Contains(n)) return null;
            for (var sc = _scope; sc != null; sc = sc.Parent)
            {
                if (sc.Aliases.TryGetValue(n.Id, out var t)) return t;
                if (sc.Params.Contains(n.Id) || sc.Types.ContainsKey(n.Id)) return null;
            }
            return !IsLocal(n.Id) && _functionAliases.TryGetValue(n.Id, out var m) ? m : null;
        }

        /// <summary>Declares fields for f's definition-time defaults and queues their assignment at the def's position.</summary>
        void RegisterDefaultFields(FunctionDef f, string owner, Dictionary<string, string> fields, List<Stmt> execStmts)
        {
            foreach (var p in f.Params)
            {
                if (p.Default == null || IsConstantDefault(p.Default) || p.Kind != ParamKind.Normal || IsParamAlias(f, p)) continue;
                string field = "__default_" + (owner != null ? owner + "_" : "") + f.Name + "_" + p.Name;
                // a method's default is evaluated in the class body: strategy=HIGHEST_VALUE_STRATEGY means the class variable
                var dflt = p.Default;
                if (owner != null && dflt is NameExpr dn && _m.Classes.TryGetValue(owner, out var oc) &&
                    oc.Body.Any(st => st is AssignStmt ca && ca.Targets.Any(t => t is NameExpr tn && tn.Id == dn.Id) || st is AnnAssignStmt cn && cn.Target is NameExpr cnn && cnn.Id == dn.Id))
                    dflt = new AttributeExpr { Target = new NameExpr { Id = owner, Line = dn.Line, Col = dn.Col }, Name = dn.Id, Line = dn.Line, Col = dn.Col };
                string type = p.Annotation != null ? MapType(p.Annotation, p) : (PyType(InferType(dflt)) ?? "dynamic");
                if (type == TypeInference.Pending) type = "dynamic";
                fields[field] = type;
                _moduleFieldTypes[field] = type;
                _w.Line($"internal static {type} {field};");
                execStmts.Add(new AssignStmt { Targets = { new NameExpr { Id = field, Line = p.Line, Col = p.Col } }, Value = dflt, Line = p.Line, Col = p.Col });
                _defaultSources[p] = field;
            }
        }

        static string NullableOf(string t) => t == "dynamic" || t == "object" || t.EndsWith("?") ? t : t + "?";

        /// <summary>Emits a function-like body with its signature's prologue and renames in effect.</summary>
        void EmitSignatureBody(List<Stmt> body, Signature sig)
        {
            foreach (var kv in sig.Renames) _renames[kv.Key] = kv.Value;
            if (sig.ReceivesValues)
            {
                // the generator object, then its body as a local iterator that reads what send() / throw() pass in
                _w.Open();
                _w.Line($"var {GenVar} = new {sig.ReturnType}();");
                _w.Line($"IEnumerable<{sig.ElementType}> __body()");
                EmitFunctionBody(body, sig.Scope, null, sig.Locals, extraPrologue: () => { foreach (var line in sig.Prologue) _w.Line(line); });
                _w.Line($"{GenVar}.__start(__body());");
                _w.Line($"return {GenVar};");
                _w.Close();
            }
            else EmitFunctionBody(body, sig.Scope, null, sig.Locals, extraPrologue: () => { foreach (var line in sig.Prologue) _w.Line(line); });
            foreach (var kv in sig.Renames) _renames.Remove(kv.Key);
        }

        const string PyGeneratorPrefix = "global::Typhon.Runtime.PyGenerator<", GenVar = "__gen";

        /// <summary>
        /// A generator that uses what is sent in (x = yield v) or returns a value: it needs the generator protocol (send,
        /// throw, close, StopIteration.value), not just an iterator.
        /// </summary>
        static bool GeneratorReceivesValues(List<Stmt> body) => AstWalker.ContainsOutsideNestedScopes(body, n =>
            n is AssignStmt a && a.Value is YieldExpr ay && !ay.From || n is AnnAssignStmt an && an.Value is YieldExpr ny && !ny.From ||
            n is ReturnStmt r && r.Value != null && !(r.Value is ConstExpr rc && rc.Kind == ConstKind.None));

        Signature BuildSignature(FunctionDef f, bool dropFirst, string selfName = null, string[] paramTypes = null, string retKey = null)
        {
            var sig = new Signature();
            var locals = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(p => p.Name), null);
            sig.Locals = locals;
            sig.IsGenerator = locals.HasYield;
            sig.IsAsync = f.IsAsync;
            sig.ReceivesValues = sig.IsGenerator && !f.IsAsync && GeneratorReceivesValues(f.Body);
            var scope = new Scope { Locals = locals, IsGenerator = sig.IsGenerator, IsAsync = f.IsAsync, Parent = _scope, ReceivesValues = sig.ReceivesValues };
            sig.Scope = scope;

            var ps = new List<string>();
            var defaults = new List<string>();
            int pi = 0;
            for (int i = 0; i < f.Params.Count; i++)
            {
                var p = f.Params[i];
                if (i == 0 && dropFirst) continue;
                scope.Params.Add(p.Name);
                if (p.Kind == ParamKind.KwArgs)
                {
                    // **kwargs: the extra keyword arguments as a dict (call sites pass it by name; omitted, it is empty)
                    ps.Add($"OrderedDictionary<string, dynamic>? {Esc(p.Name)}");
                    defaults.Add(" = null");
                    scope.Types[p.Name] = "OrderedDictionary<string, dynamic>";
                    sig.Prologue.Add($"{Esc(p.Name)} ??= new OrderedDictionary<string, dynamic>();");
                    pi++;
                    continue;
                }
                string inferred = paramTypes != null && pi < paramTypes.Length ? paramTypes[pi] : null;
                pi++;
                // def f(..., bisect=bisect) that callers never pass: the name stands for the function (calls go to it directly)
                if (IsParamAlias(f, p))
                {
                    // (the C# parameter gets another name: the body's calls go to the function, which it must not hide)
                    ps.Add($"object? __alias_{p.Name}");
                    defaults.Add(" = null");
                    scope.Params.Remove(p.Name);
                    scope.Aliases[p.Name] = p.Default;
                    _aliasTargetNodes.Add(p.Default);
                    continue;
                }
                string type = p.Annotation != null && !(TypeInference.IsBareContainer(p.Annotation) && inferred != null && inferred != TypeInference.Declared) ? MapType(p.Annotation, p)
                    : inferred != null && inferred != TypeInference.Declared ? inferred
                    : ImplicitDynamic(p, $"parameter '{p.Name}'");
                if (type == "void") type = "object";
                scope.Types[p.Name] = type;
                string decl;
                if (p.Kind == ParamKind.VarArgs)
                {
                    if (p.Annotation == null) type = "object";
                    decl = f.Params.Any(x => x.Kind == ParamKind.KwArgs) ? $"{type}[]? {Esc(p.Name)}" : $"params {type}[] {Esc(p.Name)}";
                    if (f.Params.Any(x => x.Kind == ParamKind.KwArgs)) { sig.Prologue.Add($"{Esc(p.Name)} ??= global::System.Array.Empty<{type}>();"); defaults.Add(" = null"); ps.Add(decl); scope.Types[p.Name] = type + "[]"; continue; }
                    scope.Types[p.Name] = type + "[]";
                }
                else decl = $"{type} {Esc(p.Name)}";
                // an inferred overload keeps a default only if the default has the overload's parameter type
                bool keepDefault = p.Default != null && (paramTypes == null || p.Annotation != null || InferType(p.Default) == type);
                string defaultCode = null;
                // a parameter the body rebinds to values of another type (in a nested block): the body uses a wider local
                string wider = p.Kind == ParamKind.Normal ? ConflictType(p.Name) : null;
                if (keepDefault && p.Kind == ParamKind.Normal && !IsConstantDefault(p.Default) && _defaultSources.TryGetValue(p, out var source))
                {
                    // a definition-time default: optional (null) parameter, the body uses a non-null local that falls back to it
                    string local = p.Name + "__arg";
                    decl = $"{NullableOf(type)} {Esc(p.Name)}";
                    defaultCode = " = null";
                    sig.Prologue.Add($"{wider ?? type} {Esc(local)} = {Esc(p.Name)} ?? {source};");
                    sig.Renames[p.Name] = local;
                    if (wider != null) scope.Types[p.Name] = wider;
                }
                else
                {
                    // a late-bound parameter with a number/bool default (def f(x=0)): C# allows only null as the default of a
                    // dynamic parameter, but honours [Optional, DefaultParameterValue(...)]
                    if (keepDefault && (type == "dynamic" || type == "object") && p.Default is ConstExpr dc && (dc.Kind == ConstKind.Int || dc.Kind == ConstKind.Float || dc.Kind == ConstKind.Bool || dc.Kind == ConstKind.Str) ||
                        keepDefault && (type == "dynamic" || type == "object") && p.Default is UnaryExpr du && du.Operand is ConstExpr duc && (duc.Kind == ConstKind.Int || duc.Kind == ConstKind.Float))
                    {
                        string constant = p.Default is ConstExpr ci && ci.Kind == ConstKind.Int || p.Default is UnaryExpr ui && ((ConstExpr)ui.Operand).Kind == ConstKind.Int
                            ? (ConstIntValue(p.Default) is long lv ? lv + "L" : null) : EmitExpr(p.Default);
                        if (constant != null)
                        {
                            decl = $"[global::System.Runtime.InteropServices.Optional, global::System.Runtime.InteropServices.DefaultParameterValue({constant})] {decl}";
                            defaultCode = "";
                        }
                    }
                    else if (keepDefault) defaultCode = " = " + EmitExpr(p.Default, targetTyped: true);
                    if (wider != null)
                    {
                        string local = p.Name + "__v";
                        sig.Prologue.Add($"{wider} {Esc(local)} = {Esc(p.Name)};");
                        sig.Renames[p.Name] = local;
                        scope.Types[p.Name] = wider;
                    }
                }
                ps.Add(decl);
                defaults.Add(defaultCode);
            }
            // C#: optional parameters must come last
            bool attributeOptional = false;
            for (int i = ps.Count - 1, required = 0; i >= 0; i--)
            {
                if (defaults[i] == null) required = 1;
                else if (required == 0)
                {
                    // before a parameter made optional by attributes (a dynamic one with a str/number default), C# wants the
                    // attribute form too: `= value` would make a later parameter "required"
                    if (attributeOptional && defaults[i].StartsWith(" = "))
                        ps[i] = $"[global::System.Runtime.InteropServices.Optional, global::System.Runtime.InteropServices.DefaultParameterValue({defaults[i].Substring(3)})] {ps[i]}";
                    else ps[i] += defaults[i];
                    if (defaults[i] == "") attributeOptional = true;
                }
            }
            sig.Params = string.Join(", ", ps);

            string elem;
            // -> list (no type arguments): the element type the returned values have
            string inferredBare = f.Returns != null && TypeInference.IsBareContainer(f.Returns) && !sig.IsGenerator && retKey != null && Ti != null &&
                                  Ti.Returns.TryGetValue(retKey, out var br) && br != "dynamic" && ContainerMatches(f.Returns, br) ? br : null;
            if (inferredBare != null) elem = inferredBare;
            else if (f.Returns != null)
            {
                elem = MapType(f.Returns, f, isReturn: true);
                // -> int with a 'return None' somewhere: the function returns an optional int
                if (!sig.IsGenerator && IsValueTypeName(elem) && !elem.EndsWith("?") && ReturnsNone(f.Body)) elem += "?";
                if (sig.IsGenerator)
                {
                    // Iterator[T] → IEnumerable<T>: the element type is T
                    if (elem.StartsWith("IEnumerable<") || elem.StartsWith("IAsyncEnumerable<")) { sig.ReturnType = elem; elem = GenericArg(elem); }
                    else sig.ReturnType = "IEnumerable<" + elem + ">";
                }
            }
            else if (sig.IsGenerator) elem = ImplicitDynamic(f, $"the values yielded by '{f.Name}'");
            else if (locals.HasValueReturn)
                elem = retKey != null && Ti != null && Ti.Returns.TryGetValue(retKey, out var inferredReturn) && inferredReturn != "dynamic" ? inferredReturn
                     : ImplicitDynamic(f, $"the return type of '{f.Name}'");
            // a method whose overrides (or the method it overrides) return values: C# needs one return type for all of them
            // (def fn(self): raise NotImplementedError in the base, values in the subclasses)
            else if (dropFirst && _cls != null && FamilyReturnsValue(_cls.Def, f.Name)) elem = "dynamic";
            else elem = "void";

            sig.ElementType = elem;
            if (sig.ReceivesValues) sig.ReturnType = $"{PyGeneratorPrefix}{elem}>";
            else if (sig.IsGenerator)
            {
                if (sig.ReturnType == null) sig.ReturnType = (f.IsAsync ? "IAsyncEnumerable<" : "IEnumerable<") + elem + ">";
            }
            else if (f.IsAsync) sig.ReturnType = elem == "void" ? "Task" : "Task<" + elem + ">";
            else sig.ReturnType = elem;
            scope.ReturnType = elem;
            return sig;
        }

        // ───────────────────────────── attributes created from outside a class ─────────────────────────────

        /// <summary>Does the class (or a Typhon base) declare the member: a method, class variable, annotation or self.x field?</summary>
        bool DeclaresMember(ClassDef c, string name, int depth = 0)
        {
            if (c == null || depth > 20) return false;
            if (c.Body.Any(st => st is FunctionDef f && f.Name == name || st is AssignStmt a && a.Targets.Any(t => t is NameExpr tn && tn.Id == name) ||
                                 st is AnnAssignStmt aa && aa.Target is NameExpr an && an.Id == name || st is ClassDef nc && nc.Name == name)) return true;
            if (CollectImplicitFields(c).ContainsKey(name)) return true;
            foreach (var b in c.Bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py == null) return true; // a .NET base: it may well have it
                if (DeclaresMember(py, name, depth + 1)) return true;
            }
            return false;
        }

        /// <summary>obj.attr = v / Cls.ATTR = v where the class does not declare it: the attribute becomes a field (dry run).</summary>
        void RecordExternalAttribute(AttributeExpr at, Expr value)
        {
            string vt = value is ConstExpr nc && nc.Kind == ConstKind.None ? TypeInference.NoneType : PyType(InferType(value)) ?? TypeInference.Unknown;
            if (at.Target is NameExpr cn && !IsLocal(cn.Id) && cn.Id != "self" && TryResolveType(cn.Id, 0, out _, out var pyClass) && pyClass != null &&
                _p.TryFindClass(_m, pyClass.Name, out var cls, out var owner) && cls == pyClass)
            {
                bool declaredNone = cls.Body.Any(st => st is AssignStmt sa && sa.Targets.Count == 1 && sa.Targets[0] is NameExpr sn && sn.Id == at.Name &&
                                                       sa.Value is ConstExpr sc && sc.Kind == ConstKind.None);
                if (declaredNone || !DeclaresMember(cls, at.Name)) Obs.AddExternal(TypeInference.ClassKey(owner, cls.Name) + "|static|" + at.Name, vt);
                return;
            }
            if (at.Target is NameExpr self && self.Id == "self") return;
            if (ClassKeyOfType(InferType(at.Target), out _) is string ck && ClassDefOfKey(ck) is ClassDef cd &&
                (!DeclaresMember(cd, at.Name) || CollectImplicitFields(cd).ContainsKey(at.Name)))
                Obs.AddExternal(ck + "|" + at.Name, vt);
        }

        string ExternalType(string key) =>
            Ti != null && Ti.ExternalFields.TryGetValue(key, out var t) ? t : _dry ? TypeInference.Pending : null;

        // ───────────────────────────── class hierarchies ─────────────────────────────

        IEnumerable<ClassDef> AllClasses() => _p.Modules.Values.Where(m => m.Ast != null).SelectMany(m => m.Classes.Values);

        /// <summary>The Typhon base classes of a class (resolved by name across the project).</summary>
        IEnumerable<ClassDef> TyphonBases(ClassDef c)
        {
            foreach (var b in c.Bases)
                if ((b is SubscriptExpr s ? s.Target : b) is NameExpr bn && _p.TryFindClass(_m, bn.Id, out var bc, out _) && bc != c) yield return bc;
                else if (b is AttributeExpr ba && _p.TryFindClass(_m, ba.Name, out var bc2, out _) && bc2 != c) yield return bc2;
        }

        bool DerivesFrom(ClassDef c, ClassDef ancestor, int depth = 0) =>
            depth < 20 && TyphonBases(c).Any(b => b == ancestor || DerivesFrom(b, ancestor, depth + 1));

        static bool Defines(ClassDef c, string method) => c.Body.OfType<FunctionDef>().Any(f => f.Name == method);

        /// <summary>Does any definition of the method in the class's hierarchy (its bases and subclasses) return a value?</summary>
        bool FamilyReturnsValue(ClassDef c, string method)
        {
            foreach (var k in AllClasses())
            {
                if (!(k == c || DerivesFrom(k, c) || DerivesFrom(c, k))) continue;
                foreach (var f in k.Body.OfType<FunctionDef>())
                {
                    if (f.Name != method) continue;
                    if (f.Returns != null && !(f.Returns is ConstExpr rc && rc.Kind == ConstKind.None)) return true;
                    var loc = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(x => x.Name), null);
                    if (loc.HasValueReturn || loc.HasYield) return true;
                }
            }
            return false;
        }

        /// <summary>Can control reach the end of the statements (so a function returns None there)?</summary>
        internal static bool CanFallThrough(List<Stmt> body)
        {
            foreach (var s in body) if (!CanComplete(s)) return false;
            return true;
        }

        static bool CanComplete(Stmt s)
        {
            switch (s)
            {
                case ReturnStmt _: case RaiseStmt _: case BreakStmt _: case ContinueStmt _: return false;
                case IfStmt i: return CanFallThrough(i.Body) || CanFallThrough(i.OrElse);
                case WhileStmt w:
                {
                    bool forever = w.Test is ConstExpr c && (c.Kind == ConstKind.Bool && c.Raw == "True" || c.Kind == ConstKind.Int && c.Raw != "0");
                    return HasBreak(w.Body) || !forever && CanFallThrough(w.OrElse);
                }
                case ForStmt f: return HasBreak(f.Body) || CanFallThrough(f.OrElse);
                case TryStmt t:
                    if (t.FinalBody.Count > 0 && !CanFallThrough(t.FinalBody)) return false;
                    return CanFallThrough(t.Body) && CanFallThrough(t.OrElse) || t.Handlers.Any(h => CanFallThrough(h.Body));
                case WithStmt w: return CanFallThrough(w.Body);
                case MatchStmt m:
                    // without an irrefutable case, no case may match
                    if (!m.Cases.Any(mc => mc.Guard == null && (mc.Pattern is CapturePattern || mc.Pattern is AsPattern ap && ap.Inner is CapturePattern))) return true;
                    return m.Cases.Any(mc => CanFallThrough(mc.Body));
                default: return true;
            }
        }

        /// <summary>Does the loop body contain a 'break' for this loop (not one of a nested loop or function)?</summary>
        static bool HasBreak(List<Stmt> body)
        {
            foreach (var s in body)
            {
                switch (s)
                {
                    case BreakStmt _: return true;
                    case IfStmt i: if (HasBreak(i.Body) || HasBreak(i.OrElse)) return true; break;
                    case TryStmt t: if (HasBreak(t.Body) || HasBreak(t.OrElse) || HasBreak(t.FinalBody) || t.Handlers.Any(h => HasBreak(h.Body))) return true; break;
                    case WithStmt w: if (HasBreak(w.Body)) return true; break;
                    case MatchStmt m: if (m.Cases.Any(mc => HasBreak(mc.Body))) return true; break;
                    case WhileStmt w: if (HasBreak(w.OrElse)) return true; break;   // a break in a loop's else leaves the outer loop
                    case ForStmt f: if (HasBreak(f.OrElse)) return true; break;
                }
            }
            return false;
        }

        /// <summary>Does the body (outside nested functions) contain 'return None'?</summary>
        static bool ReturnsNone(List<Stmt> body) => AstWalker.ContainsOutsideNestedScopes(body, n => n is ReturnStmt r && r.Value is ConstExpr c && c.Kind == ConstKind.None);

        static string GenericArg(string t)
        {
            int lt = t.IndexOf('<');
            return t.Substring(lt + 1, t.Length - lt - 2);
        }

        void EmitFunction(FunctionDef f, bool isModuleLevel)
        {
            var t = isModuleLevel ? TargetOf(_m, null, f.Name) : null;
            if (t != null && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
            {
                for (int i = 0; i < sigs.Count; i++)
                {
                    EmitFunctionVariant(f, sigs[i], TypeInference.ReturnKey(t.Key, TypeInference.SigKey(sigs[i])), null);
                    if (i < sigs.Count - 1 || Ti.Fallback.Contains(t.Key)) _w.Line();
                }
                if (Ti.Fallback.Contains(t.Key)) EmitFunctionVariant(f, null, null, t.Key);
                return;
            }
            EmitFunctionVariant(f, null, t != null && !t.InferParams ? TypeInference.ReturnKey(t.Key, "") : null, t != null && t.InferParams ? t.Key : null);
        }

        /// <summary>One C# method for a Typhon function: typed by an inferred signature, or as written (dynamic fallback).</summary>
        void EmitFunctionVariant(FunctionDef f, string[] paramTypes, string retKey, string fallbackKey)
        {
            var savedRet = _retKey; var savedCtx = _ctxFallbackKey;
            _retKey = retKey; _ctxFallbackKey = fallbackKey;
            if (Obs != null) Obs.InFallbackBody = fallbackKey != null;
            var savedScopeKey = _scopeKey;
            _scopeKey = TypeInference.FuncKey(_m, null, f.Name) + "#" + TypeInference.SigKey(paramTypes);
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            var sig = BuildSignature(f, dropFirst: false, paramTypes: paramTypes, retKey: retKey);
            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            string vis = SurfaceVisibility(Visibility(f.Name, topLevel: true), sig.ReturnType, sig.Params);
            string asyncKw = f.IsAsync && !sig.IsGenerator ? "async " : (f.IsAsync ? "async " : "");
            _w.Line($"{vis} static {asyncKw}{sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}");
            bool memoize = f.Decorators.Any(TypeInference.IsCacheDecorator) && !sig.IsGenerator && !f.IsAsync && sig.ReturnType != "void" &&
                           f.TypeParams.Count == 0 && f.Params.Count > 0 && f.Params.All(p => p.Kind == ParamKind.Normal);
            if (memoize) EmitMemoized(f, sig);
            else EmitSignatureBody(f.Body, sig);
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name);
            _retKey = savedRet; _ctxFallbackKey = savedCtx; _scopeKey = savedScopeKey;
            if (Obs != null) Obs.InFallbackBody = savedCtx != null;
        }

        /// <summary>
        /// @cache / @lru_cache: the function looks its arguments up in a static dictionary and otherwise runs the original body
        /// (a private method), so recursive calls through the function are memoized too. (lru_cache's size limit only bounds
        /// memory; results are the same.)
        /// </summary>
        void EmitMemoized(FunctionDef f, Signature sig)
        {
            string id = NewTmp("memo_" + f.Name);
            var names = f.Params.Select(p => sig.Renames.TryGetValue(p.Name, out var r) ? r : p.Name).ToList();
            var types = f.Params.Select(p => sig.Scope.Types.TryGetValue(p.Name, out var t) && t != "dynamic" ? t : "object").ToList();
            string keyType = types.Count == 1 ? types[0] : "(" + string.Join(", ", types) + ")";
            string key = names.Count == 1 ? Esc(names[0]) : "(" + string.Join(", ", names.Select(Esc)) + ")";
            _w.Open();
            _w.Line("checked");
            _w.Open();
            foreach (var line in sig.Prologue) _w.Line(line);
            _w.Line($"if ({id}.TryGetValue({key}, out var __hit)) return __hit;");
            _w.Line($"var __result = {id}_impl({string.Join(", ", names.Select(Esc))});");
            _w.Line($"{id}[{key}] = __result;");
            _w.Line("return __result;");
            _w.Close();
            _w.Close();
            _w.Line($"static readonly Dictionary<{keyType}, {sig.ReturnType}> {id} = new Dictionary<{keyType}, {sig.ReturnType}>();");
            var impl = new Signature { ReturnType = sig.ReturnType, Locals = sig.Locals, Scope = sig.Scope };
            string ps = string.Join(", ", f.Params.Select((p, i) => $"{(sig.Scope.Types.TryGetValue(p.Name, out var t) ? t : "dynamic")} {Esc(p.Name)}"));
            _w.Line($"static {sig.ReturnType} {id}_impl({ps})");
            EmitSignatureBody(f.Body, impl);
        }

        /// <summary>Emits "{ ... }" for a function-like body with Python scoping rules.</summary>
        void EmitFunctionBody(List<Stmt> body, Scope scope, IEnumerable<string> excluded, LocalsResult locals = null, string prologue = null, Action extraPrologue = null)
        {
            var saved = _scope;
            var savedEmpty = _emptyContainers;
            _emptyContainers = new Dictionary<string, char>();
            var savedRegex = new Dictionary<string, (string, long)>(_regexLocals);
            _regexLocals.Clear();
            var savedLambdas = new Dictionary<string, LambdaExpr>(_lambdaLocals);
            _lambdaLocals.Clear();
            scope.Parent = saved;
            scope.Locals = locals ?? LocalsAnalyzer.Analyze(body, scope.Params, excluded);
            _scope = scope;
            // locals that only ever hold non-negative ints index without the count-from-the-end check; names this body binds
            // shadow the enclosing function's
            var savedNonNegative = new HashSet<string>(_nonNegative);
            var own = AlwaysNonNegative(body, scope.Params);
            _nonNegative.RemoveWhere(n => scope.Params.Contains(n) || BindsAnywhere(body, n));
            _nonNegative.UnionWith(own);
            _w.Open();
            // Python ints never wrap around: arithmetic is checked (beyond 64 bits it raises OverflowError)
            _w.Line("checked");
            _w.Open();
            if (prologue != null) _w.Line(prologue);
            extraPrologue?.Invoke();
            foreach (var h in scope.Locals.Hoisted)
            {
                string type;
                if (h.Value != null) type = MapType(h.Value, body.FirstOrDefault());
                else
                {
                    scope.Locals.HoistedFirstValue.TryGetValue(h.Key, out var fv);
                    bool observed = false;
                    if (fv != null && TypeInference.IsEmptyContainer(fv))
                    {
                        char kind = TypeInference.ContainerKind(fv);
                        _emptyContainers[h.Key] = kind;
                        type = EmptyContainerType(_scopeKey + "|" + h.Key, kind, fv);
                    }
                    // what the assignments in the body give it (whole-program inference: the names they use exist there)
                    else if (Ti != null && Ti.Hoisted.TryGetValue(_scopeKey + "|" + h.Key, out var hoistedType) && hoistedType != "dynamic" &&
                             scope.Locals.HoistedPlain.Contains(h.Key))
                    { type = hoistedType; observed = true; }
                    else type = PyType(fv != null ? InferType(fv) : LoopVariableType(scope.Locals, h.Key));
                    // a variable assigned values of different types (in different blocks) is late-bound
                    if (type != null && !observed && scope.Locals.HoistedValues.TryGetValue(h.Key, out var values) &&
                        values.Select(v => PyType(InferType(v))).Where(t => t != null && t != TypeInference.Pending).Any(t => Incompatible(type, t)))
                        type = null;
                    // a capture bound from a value of unknown type (case [x, y] on an object) is not known to agree with the others
                    if (type != null && scope.Locals.HoistedCaptures.TryGetValue(h.Key, out var captures) && captures.Any(v => v == null || InferType(v) == null))
                        type = null;
                    type = type ?? ImplicitDynamic(body.FirstOrDefault(), $"local variable '{h.Key}' (assigned in a nested block)");
                }
                if (ConflictType(h.Key) is string wider) type = wider;
                scope.Types[h.Key] = type;
                _w.Line($"{type} {Esc(h.Key)} = default;");
            }
            // parameters count as declared by the body's top block: rebinding one there to another type gives a new variable
            EmitBlockStatements(body, preDeclared: scope.Params);
            // falling off the end returns None (C# warns about unreachable code where the end cannot be reached; that is off)
            if (scope.ReturnType != null && scope.ReturnType != "void" && !scope.IsGenerator)
            {
                if (Obs != null && _retKey != null && CanFallThrough(body)) Obs.AddReturn(_retKey, TypeInference.NoneType);
                _w.Line("return default!;");
            }
            _w.Close();
            _w.Close();
            _scope = saved;
            _emptyContainers = savedEmpty;
            _regexLocals.Clear(); foreach (var kv in savedRegex) _regexLocals[kv.Key] = kv.Value;
            _lambdaLocals.Clear(); foreach (var kv in savedLambdas) _lambdaLocals[kv.Key] = kv.Value;
            _nonNegative.Clear(); _nonNegative.UnionWith(savedNonNegative);
        }

        static bool BindsAnywhere(List<Stmt> body, string name) => body.Any(s => BindsName(s, name));

        /// <summary>The type of a hoisted variable that only for-loops bind: long for range loops, else the iterables' element type.</summary>
        string LoopVariableType(LocalsResult locals, string name)
        {
            if (!locals.HoistedLoops.TryGetValue(name, out var loops) || loops.Count == 0) return null;
            var types = loops.Select(f => f.IsAsync ? null : IsBuiltinRange(f.Iter, out _) ? "long" : ElementTypeOf(InferType(f.Iter))).Distinct().ToList();
            return types.Count == 1 && types[0] != null && types[0] != TypeInference.Pending && types[0] != "dynamic" ? types[0] : null;
        }

        /// <summary>An initializer outside a method body (fields): checked like code in methods. Constants stay as they are.</summary>
        static string Checked(string code) => code.Length > 0 && (char.IsDigit(code[0]) || code[0] == '"' || code == "null" || code == "true" || code == "false") ? code : $"checked({code})";

        // ───────────────────────────── classes ─────────────────────────────

        static string DottedName(Expr e)
        {
            switch (e)
            {
                case NameExpr n: return n.Id;
                case AttributeExpr a: var t = DottedName(a.Target); return t == null ? null : t + "." + a.Name;
                default: return null;
            }
        }

        void EmitClass(ClassDef c)
        {
            var bases = new List<Expr>();
            bool isInterface = false, isEnum = false, isAbstract = false;
            foreach (var b in c.Bases)
            {
                string bn = DottedName(b is SubscriptExpr bs ? bs.Target : b);
                if (bn == "Protocol" || bn == "typing.Protocol") { isInterface = true; continue; }
                if (bn == "Enum" || bn == "IntEnum" || bn == "enum.Enum" || bn == "enum.IntEnum") { isEnum = true; continue; }
                if (bn == "ABC" || bn == "abc.ABC") { isAbstract = true; continue; }
                if (bn == "object" || bn == "Generic" || bn == "typing.Generic") continue;
                bases.Add(b);
            }
            if (isEnum) { EmitEnum(c); return; }

            var ctx = new ClassContext { Def = c, Name = c.Name, IsInterface = isInterface, IsSealed = HasDecorator(c.Decorators, "final") };
            foreach (var tp in c.TypeParams) ctx.TypeParams.Add(tp.Name);
            ctx.CsSelfType = Esc(c.Name) + TypeParamList(c.TypeParams);
            bool hasAbstractMethods = c.Body.OfType<FunctionDef>().Any(f => HasDecorator(f.Decorators, "abstractmethod"));
            if (hasAbstractMethods) isAbstract = true;

            var savedCls = _cls; var savedScope = _scope; var savedEmptyFields = _emptyFields;
            _cls = ctx; _scope = null; _emptyFields = new Dictionary<string, char>();

            EmitDocComment(c.Body);
            LineDirective(c);
            EmitAttributes(c.Decorators);
            string vis = Visibility(c.Name, topLevel: true);
            string kind = isInterface ? "partial interface" : (isAbstract ? "abstract partial class" : (ctx.IsSealed ? "sealed partial class" : "partial class"));
            var baseTypes = bases.Select(b => MapType(b, c)).ToList();
            if (!isInterface) baseTypes.AddRange(StructuralProtocols(c, bases));
            // def __iter__(self) -> Iterator[T]  ⇒  the class implements IEnumerable<T> (foreach, LINQ, comprehensions)
            var iter = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__iter__");
            string iterElem = null;
            if (iter != null && !isInterface)
            {
                string rt = iter.Returns != null ? MapType(iter.Returns, iter, isReturn: true) : null;
                iterElem = rt != null && rt.StartsWith("IEnumerable<") ? GenericArg(rt) : "object";
                baseTypes.Add($"global::System.Collections.Generic.IEnumerable<{iterElem}>");
            }
            ctx.IterElementType = iterElem;
            // def __lt__(self, other: Self) -> bool  ⇒  IComparable<Self>, so sorted()/min()/max()/List.Sort order the objects
            if (!isInterface && LtComparesSelf(c, ctx)) baseTypes.Add($"global::System.IComparable<{ctx.CsSelfType}>");
            // def __len__(self) -> int  ⇒  IPyLen, so len(obj) works without reflection
            if (!isInterface && c.Body.OfType<FunctionDef>().Any(f => f.Name == "__len__" && f.Params.Count == 1 && f.Returns is NameExpr rn && rn.Id == "int"))
                baseTypes.Add("global::Typhon.Runtime.IPyLen");
            // a namedtuple: a tuple of its fields (unpacking, indexing, iteration, len, comparison, repr)
            if (c.NamedTupleFields != null && !isInterface)
            {
                baseTypes.Add("global::System.Runtime.CompilerServices.ITuple");
                baseTypes.Add("global::Typhon.Runtime.IPyRepr");
                baseTypes.Add("global::System.IComparable");
                if (!baseTypes.Contains("global::Typhon.Runtime.IPyLen")) baseTypes.Add("global::Typhon.Runtime.IPyLen");
                if (iter == null) { baseTypes.Add("global::System.Collections.Generic.IEnumerable<object?>"); ctx.IterElementType = "object"; }
            }
            string baseList = baseTypes.Count > 0 ? " : " + string.Join(", ", baseTypes) : "";
            _w.Line($"{vis} {kind} {Esc(c.Name)}{TypeParamList(c.TypeParams)}{baseList}{TypeParamConstraints(c.TypeParams, c)}");
            _w.Open();

            if (isInterface) EmitInterfaceBody(c);
            else EmitClassBody(c, ctx, bases, isAbstract);

            _w.Close();
            _cls = savedCls; _scope = savedScope; _emptyFields = savedEmptyFields;
        }

        bool LtComparesSelf(ClassDef c, ClassContext ctx)
        {
            var lt = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__lt__");
            return lt != null && lt.Params.Count == 2 && lt.Params[1].Annotation != null && MapType(lt.Params[1].Annotation, lt) == ctx.CsSelfType;
        }

        static bool IsProtocol(ClassDef c) => c.Bases.Any(b => { var n = DottedName(b is SubscriptExpr s ? s.Target : b); return n == "Protocol" || n == "typing.Protocol"; });

        /// <summary>
        /// Python Protocols are structural, CLR interfaces are nominal. Bridge the gap inside the project: a Typhon class
        /// that has every member of a Typhon Protocol (same names, same arity) implements that interface automatically.
        /// </summary>
        IEnumerable<string> StructuralProtocols(ClassDef c, List<Expr> explicitBases)
        {
            var explicitNames = new HashSet<string>(explicitBases.Select(b => DottedName(b)?.Split('.').Last()).Where(n => n != null));
            var members = new Dictionary<string, int>();
            foreach (var s in c.Body)
            {
                if (s is FunctionDef f) members[f.Name] = f.Params.Count;
                else if (s is AnnAssignStmt a && a.Target is NameExpr n) members[n.Id] = -1;
            }
            foreach (var kv in CollectImplicitFields(c)) if (!members.ContainsKey(kv.Key)) members[kv.Key] = -1;

            foreach (var mod in _p.Modules.Values)
                foreach (var proto in mod.Classes.Values.Where(IsProtocol))
                {
                    if (proto.Name == c.Name || explicitNames.Contains(proto.Name) || proto.TypeParams.Count > 0) continue;
                    bool matches = true;
                    foreach (var s in proto.Body)
                    {
                        if (s is FunctionDef pf) matches &= members.TryGetValue(pf.Name, out var arity) && (arity == pf.Params.Count || HasDecorator(pf.Decorators, "property"));
                        else if (s is AnnAssignStmt pa && pa.Target is NameExpr pn) matches &= members.ContainsKey(pn.Id);
                        if (!matches) break;
                    }
                    if (matches && proto.Body.Any(s => s is FunctionDef || s is AnnAssignStmt))
                        yield return mod == _m ? Esc(proto.Name) : $"{mod.QualifiedClassName}.{Esc(proto.Name)}";
                }
        }

        void EmitEnum(ClassDef c)
        {
            EmitDocComment(c.Body);
            LineDirective(c);
            _w.Line($"{Visibility(c.Name, true)} enum {Esc(c.Name)}");
            _w.Open();
            foreach (var s in c.Body)
            {
                if (s is AssignStmt a && a.Targets[0] is NameExpr n)
                {
                    bool auto = a.Value is CallExpr call && DottedName(call.Func)?.EndsWith("auto") == true;
                    _w.Line(auto ? Esc(n.Id) + "," : $"{Esc(n.Id)} = {EmitExpr(a.Value)},");
                }
                else if (!(s is PassStmt) && !(s is ExprStmt)) Unsupported(s, "methods or annotations inside an Enum");
            }
            _w.Close();
        }

        void EmitInterfaceBody(ClassDef c)
        {
            foreach (var s in c.Body)
            {
                switch (s)
                {
                    case AnnAssignStmt a when a.Target is NameExpr n:
                        LineDirective(s);
                        _w.Line($"{MapType(a.Annotation, s)} {Esc(n.Id)} {{ get; set; }}");
                        break;
                    case FunctionDef f:
                        var sig = BuildSignature(f, dropFirst: !HasDecorator(f.Decorators, "staticmethod"));
                        EmitDocComment(f.Body);
                        LineDirective(f);
                        if (HasDecorator(f.Decorators, "property")) _w.Line($"{sig.ReturnType} {Esc(f.Name)} {{ get; }}");
                        else _w.Line($"{sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params});");
                        break;
                    case ExprStmt _:
                    case PassStmt _:
                        break;
                    default: Unsupported(s, "this statement inside a Protocol"); break;
                }
            }
        }

        /// <summary>Resolves a base class expression to a Typhon class, or a .NET metadata name.</summary>
        bool ResolveBase(Expr b, out ClassDef pyClass, out string meta)
        {
            pyClass = null; meta = null;
            int arity = 0;
            var target = b;
            if (b is SubscriptExpr s) { target = s.Target; arity = s.Index is TupleExpr t ? t.Elts.Count : 1; }
            string dotted = DottedName(target);
            if (dotted == null) return false;
            return TryResolveType(dotted, arity, out meta, out pyClass);
        }

        bool BaseHasMember(List<Expr> bases, string member, bool requireOverridable, int depth = 0)
        {
            if (depth > 20) return false;
            foreach (var b in bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (py.Body.Any(s => (s is FunctionDef f && (f.Name == member || DunderBridge(f.Name) == member)) ||
                                         (!requireOverridable && s is AnnAssignStmt a && a.Target is NameExpr n && n.Id == member)))
                        return true;
                    if (!requireOverridable && CollectImplicitFields(py).ContainsKey(member)) return true;
                    if (BaseHasMember(py.Bases, member, requireOverridable, depth + 1)) return true;
                }
                else if (meta != null)
                {
                    if (requireOverridable ? _r.IsOverridable(meta, member) : _r.HasMember(meta, member)) return true;
                }
            }
            return false;
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

        /// <summary>self.x = ... assignments inside methods declare instance attributes (Python style).</summary>
        static Dictionary<string, KeyValuePair<Expr, KeyValuePair<FunctionDef, Expr>>> CollectImplicitFields(ClassDef c)
        {
            var result = new Dictionary<string, KeyValuePair<Expr, KeyValuePair<FunctionDef, Expr>>>(); // name → (annotation, (method, value))
            foreach (var f in c.Body.OfType<FunctionDef>())
            {
                if (f.Params.Count == 0 || HasDecorator(f.Decorators, "staticmethod")) continue;
                string self = f.Params[0].Name;
                AstWalker.VisitAll(f.Body, n =>
                {
                    Expr target = null, ann = null, value = null;
                    if (n is AssignStmt a) foreach (var t in a.Targets) { target = t; value = a.Targets.Count == 1 ? a.Value : null; Check(); }
                    else if (n is AnnAssignStmt aa) { target = aa.Target; ann = aa.Annotation; value = aa.Value; Check(); }
                    else if (n is AugAssignStmt ag) { target = ag.Target; Check(); }

                    void Check()
                    {
                        if (target is AttributeExpr at && at.Target is NameExpr tn && tn.Id == self)
                        {
                            if (!result.TryGetValue(at.Name, out var existing) || (existing.Key == null && ann != null) || (existing.Value.Value == null && value != null))
                                result[at.Name] = new KeyValuePair<Expr, KeyValuePair<FunctionDef, Expr>>(ann ?? existing.Key, new KeyValuePair<FunctionDef, Expr>(f, value ?? existing.Value.Value));
                        }
                        if (target is TupleExpr tup)
                        {
                            // self.a, self.b = x, y: each field takes its own item
                            var items = value is TupleExpr vt && vt.Elts.Count == tup.Elts.Count ? vt.Elts : null;
                            for (int i = 0; i < tup.Elts.Count; i++) { target = tup.Elts[i]; value = items?[i]; Check(); }
                        }
                    }
                });
            }
            return result;
        }

        void EmitClassBody(ClassDef c, ClassContext ctx, List<Expr> bases, bool isAbstract)
        {
            var methods = c.Body.OfType<FunctionDef>().ToList();
            var declared = new HashSet<string>();
            foreach (var m in methods)
                if (m.Returns != null && !m.IsAsync && m.TypeParams.Count == 0 && !AstWalker.ContainsOutsideNestedScopes(m.Body, n => n is YieldExpr))
                    ctx.MethodReturnTypes[m.Name] = MapType(m.Returns, m, isReturn: true);
            foreach (var m in methods)
                if (m.Returns == null && TargetOf(_m, c, m.Name) is InferenceTarget mt && mt.Class == c && !mt.InferParams &&
                    Ti.Returns.TryGetValue(TypeInference.ReturnKey(mt.Key, ""), out var mrt) && mrt != "dynamic")
                    ctx.MethodReturnTypes[m.Name] = mrt;
            if (Obs != null && _m.Classes.TryGetValue(c.Name, out var topCls) && topCls == c)
                Obs.MethodReturns[TypeInference.ClassKey(_m, c.Name)] = new Dictionary<string, string>(ctx.MethodReturnTypes);
            bool isDataclass = HasDecorator(c.Decorators, "dataclass");
            var dataFields = new List<AnnAssignStmt>();

            // 1. class-level declarations
            foreach (var s in c.Body)
            {
                switch (s)
                {
                    case AnnAssignStmt a when a.Target is NameExpr n:
                    {
                        bool isClassVar = a.Annotation is SubscriptExpr sub && (DottedName(sub.Target) == "ClassVar" || DottedName(sub.Target) == "typing.ClassVar");
                        string type = MapType(isClassVar ? ((SubscriptExpr)a.Annotation).Index : a.Annotation, s);
                        declared.Add(n.Id);
                        ctx.Members.Add(n.Id);
                        ctx.FieldTypes[n.Id] = type;
                        LineDirective(s);
                        string init = a.Value != null ? " = " + Checked(EmitExpr(a.Value, targetTyped: true)) + ";" : "";
                        if (isClassVar) _w.Line($"{Visibility(n.Id, false)} static {type} {Esc(n.Id)}{(init.Length > 0 ? init : ";")}");
                        else
                        {
                            _w.Line($"{Visibility(n.Id, false)} {type} {Esc(n.Id)} {{ get; set; }}{init}");
                            if (isDataclass) dataFields.Add(a);
                        }
                        break;
                    }
                    case AssignStmt a when a.Targets.Count == 1 && a.Targets[0] is NameExpr n:
                    {
                        // NAME = None, assigned a value later from outside (Cls.NAME = Cls(...)): the type of those values
                        string later = a.Value is ConstExpr cn0 && cn0.Kind == ConstKind.None ? ExternalType(TypeInference.ClassKey(_m, c.Name) + "|static|" + n.Id) : null;
                        if (later == TypeInference.Pending || later == "dynamic") later = null;
                        if (later != null && IsValueTypeName(later) && !later.EndsWith("?")) later += "?";
                        string type = later ?? PyType(InferType(a.Value)) ?? ImplicitDynamic(s, $"class variable '{n.Id}'");
                        declared.Add(n.Id);
                        ctx.Members.Add(n.Id);
                        ctx.FieldTypes[n.Id] = type; // read as self.name too (through the class)
                        LineDirective(s);
                        _w.Line($"{Visibility(n.Id, false)} static {type} {Esc(n.Id)} = {Checked(type != "dynamic" ? EmitExpr(a.Value, targetTyped: true) : EmitValue(a.Value))};");
                        break;
                    }
                    case FunctionDef f: ctx.Members.Add(f.Name); break;
                    case ClassDef _: break;
                    case ExprStmt e when e.Value is ConstExpr: break; // docstring / ...
                    case PassStmt _: break;
                    default: Unsupported(s, "this statement in a class body"); break;
                }
            }

            // 2. attributes created via self.x = ... in methods
            foreach (var kv in CollectImplicitFields(c))
            {
                string name = kv.Key;
                if (declared.Contains(name) || ctx.Members.Contains(name)) continue;
                if (BaseHasMember(bases, name, requireOverridable: false)) continue;
                var ann = kv.Value.Key; var method = kv.Value.Value.Key; var value = kv.Value.Value.Value;
                string type;
                if (ann != null) type = MapType(ann, method);
                else if (value != null && TypeInference.IsEmptyContainer(value))
                {
                    // self.items = [] / {}: typed by what the methods add to it
                    char kind = TypeInference.ContainerKind(value);
                    _emptyFields[name] = kind;
                    type = EmptyContainerType(TypeInference.ClassKey(_m, c.Name) + "|self." + name, kind, value) ?? ImplicitDynamic(method, $"attribute 'self.{name}'");
                }
                else
                {
                    // infer with the method's parameter types in scope: annotations, or every inferred constructor signature
                    List<string[]> variants = null;
                    if (method.Name == "__init__" && TargetOf(_m, c, "__init__") is InferenceTarget it && it.Class == c && it.InferParams &&
                        Ti.Signatures.TryGetValue(it.Key, out var initSigs) && !Ti.Fallback.Contains(it.Key))
                        variants = initSigs;
                    var candidates = new HashSet<string>();
                    foreach (var variant in (IEnumerable<string[]>)variants ?? new string[][] { null })
                    {
                        // (an instance method: self.reference = self is a reference to this class)
                        var tmpScope = new Scope { HasSelf = !method.Decorators.Any(d => d is NameExpr dn && (dn.Id == "staticmethod" || dn.Id == "classmethod")) };
                        int vi = 0;
                        foreach (var p in method.Params.Skip(1))
                        {
                            string pt = p.Annotation != null ? MapType(p.Annotation, p) : variant != null && variant[vi] != TypeInference.Declared ? variant[vi] : null;
                            vi++;
                            if (pt != null) { tmpScope.Params.Add(p.Name); tmpScope.Types[p.Name] = pt; }
                        }
                        var saved = _scope; _scope = tmpScope;
                        candidates.Add(value != null ? PyType(InferType(value)) : null);
                        _scope = saved;
                    }
                    type = candidates.Count == 1 ? candidates.First() : null;
                    if (TypeInference.IsUnknown(type) || type == TypeInference.Pending) type = null;
                    type = type ?? ImplicitDynamic(method, $"attribute 'self.{name}'");
                    // assignments from outside the class (obj.attr = v) count too: an int field that gets floats is a float
                    if (type != "dynamic" && Ti != null && _m.Classes.TryGetValue(c.Name, out var wcls) && wcls == c &&
                        Ti.ExternalFields.TryGetValue(TypeInference.ClassKey(_m, c.Name) + "|" + name, out var outside) && outside != type)
                        type = (type == "long" || type == "double") && (outside == "long" || outside == "double") ? "double" : ImplicitDynamic(method, $"attribute 'self.{name}'");
                }
                declared.Add(name);
                ctx.Members.Add(name);
                ctx.FieldTypes[name] = type;
                LineDirective(method);
                _w.Line($"{SurfaceVisibility(Visibility(name, false), type)} {type} {Esc(name)} {{ get; set; }}");
            }
            // 2b. attributes other code creates (obj.attr = v, Cls.ATTR = v)
            if (Ti != null && _m.Classes.TryGetValue(c.Name, out var extCls) && extCls == c)
            {
                string prefix = TypeInference.ClassKey(_m, c.Name) + "|";
                foreach (var kv in Ti.ExternalFields.Where(kv => kv.Key.StartsWith(prefix)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    bool isStatic = kv.Key.Substring(prefix.Length).StartsWith("static|");
                    string name = kv.Key.Substring(prefix.Length + (isStatic ? "static|".Length : 0));
                    if (declared.Contains(name) || ctx.Members.Contains(name) || BaseHasMember(bases, name, requireOverridable: false)) continue;
                    string type = kv.Value == TypeInference.Pending ? "dynamic" : kv.Value;
                    declared.Add(name);
                    ctx.Members.Add(name);
                    ctx.FieldTypes[name] = type;
                    LineDirective(c);
                    _w.Line(isStatic ? $"{SurfaceVisibility("public", type)} static {type} {Esc(name)};" : $"{SurfaceVisibility("public", type)} {type} {Esc(name)} {{ get; set; }}");
                }
            }
            if (declared.Count > 0) _w.Line();
            if (Obs != null && _m.Classes.TryGetValue(c.Name, out var topLevel) && topLevel == c)
                Obs.Fields[TypeInference.ClassKey(_m, c.Name)] = new Dictionary<string, string>(ctx.FieldTypes);

            // 3. dataclass constructor / repr / equality
            if (isDataclass && !methods.Any(m => m.Name == "__init__"))
            {
                var ps = dataFields.Select(a => $"{ctx.FieldTypes[((NameExpr)a.Target).Id]} {Esc(((NameExpr)a.Target).Id)}" + (a.Value != null ? " = " + EmitExpr(a.Value, targetTyped: true) : ""));
                _w.Line($"public {Esc(c.Name)}({string.Join(", ", ps)})");
                _w.Open();
                foreach (var a in dataFields) { var n = Esc(((NameExpr)a.Target).Id); _w.Line($"this.{n} = {n};"); }
                _w.Close();
                if (dataFields.Count > 0) _w.Line($"public {Esc(c.Name)}() {{ }}");
                if (!methods.Any(m => m.Name == "__repr__" || m.Name == "__str__"))
                {
                    var parts = dataFields.Select(a => { var n = ((NameExpr)a.Target).Id; return $"{n}={{repr(this.{Esc(n)})}}"; });
                    _w.Line($"public override string ToString() => $\"{c.Name}({string.Join(", ", parts)})\";");
                }
                if (!methods.Any(m => m.Name == "__eq__"))
                {
                    var cmp = dataFields.Count == 0 ? "true" : string.Join(" && ", dataFields.Select(a => { var n = Esc(((NameExpr)a.Target).Id); return $"Equals(this.{n}, __o.{n})"; }));
                    _w.Line($"public override bool Equals(object? obj) => obj is {ctx.CsSelfType} __o && {cmp};");
                    _w.Line($"public static bool operator ==({ctx.CsSelfType}? a, {ctx.CsSelfType}? b) => a is null ? b is null : a.Equals(b);");
                    _w.Line($"public static bool operator !=({ctx.CsSelfType}? a, {ctx.CsSelfType}? b) => !(a == b);");
                    var hashFields = dataFields.Take(8).Select(a => "this." + Esc(((NameExpr)a.Target).Id));
                    _w.Line(dataFields.Count == 0 ? "public override int GetHashCode() => 0;" : $"public override int GetHashCode() => HashCode.Combine({string.Join(", ", hashFields)});");
                }
                _w.Line();
            }

            // 4. methods (properties are grouped by name)
            var propertyDone = new HashSet<string>();
            foreach (var f in methods)
            {
                if (f.Name == "__init__") { EmitConstructor(f, ctx, bases); _w.Line(); continue; }
                bool isProp = HasDecorator(f.Decorators, "property") || f.Decorators.Any(d => DecoratorName(d)?.EndsWith(".setter") == true);
                if (isProp)
                {
                    if (propertyDone.Add(f.Name)) { EmitProperty(methods.Where(m => m.Name == f.Name).ToList(), ctx, bases); _w.Line(); }
                    continue;
                }
                EmitMethod(f, ctx, bases);
                _w.Line();
            }
            if (!isDataclass && !methods.Any(m => m.Name == "__init__")) EmitInheritedConstructors(ctx, bases);
            EmitDunderBridges(methods, ctx);
            if (c.NamedTupleFields != null) EmitNamedTupleMembers(c, ctx, methods);

            // 5. nested classes
            foreach (var nested in c.Body.OfType<ClassDef>()) EmitClass(nested);
        }

        string MethodModifiers(FunctionDef f, ClassContext ctx, List<Expr> bases, out bool isStatic, out bool isAbstract)
        {
            isStatic = HasDecorator(f.Decorators, "staticmethod") || HasDecorator(f.Decorators, "classmethod");
            isAbstract = HasDecorator(f.Decorators, "abstractmethod");
            string vis = Visibility(f.Name, false);
            if (f.Name.StartsWith("__") && f.Name.EndsWith("__")) vis = "public";
            var mods = new List<string> { vis };
            if (isStatic) mods.Add("static");
            else if (isAbstract) mods.Add("abstract");
            else if (vis != "private")
            {
                bool explicitOverride = HasDecorator(f.Decorators, "override");
                if (explicitOverride || BaseHasMember(bases, f.Name, requireOverridable: true)) mods.Add("override");
                else if (!ctx.IsSealed && !HasDecorator(f.Decorators, "final")) mods.Add("virtual");
                if (HasDecorator(f.Decorators, "final") && mods.Contains("override")) mods.Add("sealed");
            }
            if (f.IsAsync) mods.Add("async");
            return string.Join(" ", mods);
        }

        void EmitMethod(FunctionDef f, ClassContext ctx, List<Expr> bases)
        {
            bool isStaticDecl = HasDecorator(f.Decorators, "staticmethod");
            bool isClassMethod = HasDecorator(f.Decorators, "classmethod");
            if (!isStaticDecl && f.Params.Count == 0) { Error(f, $"method '{f.Name}' needs a 'self' parameter (or use @staticmethod)"); return; }
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            try { EmitMethodCore(f, ctx, bases, isStaticDecl, isClassMethod); }
            finally { foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name); }
        }

        void EmitMethodCore(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod)
        {
            var target = TargetOf(_m, ctx.Def, f.Name);
            string retKey = target != null && target.Class == ctx.Def && !target.InferParams && !BaseHasMember(bases, f.Name, requireOverridable: false)
                ? TypeInference.ReturnKey(target.Key, "") : null;
            var savedRet = _retKey; _retKey = retKey;
            var savedScopeKey = _scopeKey; _scopeKey = TypeInference.FuncKey(_m, ctx.Def, f.Name);
            try { EmitMethodTyped(f, ctx, bases, isStaticDecl, isClassMethod, retKey); }
            finally { _retKey = savedRet; _scopeKey = savedScopeKey; }
        }

        void EmitMethodTyped(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod, string retKey)
        {
            var sig = BuildSignature(f, dropFirst: !isStaticDecl, retKey: retKey);
            string mods = MethodModifiers(f, ctx, bases, out bool isStatic, out bool isAbstract);
            if (!isStatic) { sig.Scope.HasSelf = true; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (isClassMethod) { sig.Scope.ClsName = ctx.CsSelfType; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (f.IsAsync && sig.IsGenerator) mods = mods.Replace("async", "async");

            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            if (mods.StartsWith("public ") && SurfaceVisibility("public", sig.ReturnType, sig.Params) == "internal") mods = "internal " + mods.Substring("public ".Length);
            string header = $"{mods} {sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}";
            if (isAbstract) { _w.Line(header + ";"); return; }
            _w.Line(header);
            EmitSignatureBody(f.Body, sig);
        }

        /// <summary>
        /// Python inherits __init__, C# does not inherit constructors: a class without __init__ gets one constructor per
        /// constructor of its base (the nearest Typhon __init__, or the .NET base's public/protected constructors).
        /// </summary>
        void EmitInheritedConstructors(ClassContext ctx, List<Expr> bases, int depth = 0)
        {
            // a subclass of a two-phase class allocates through it too
            if (depth == 0 && NearestInitAncestor(ctx.Def) is ClassDef twoPhaseOwner && TwoPhaseClasses.Contains(twoPhaseOwner) && !ctx.NoInitDone)
            {
                _w.Line($"private protected {Esc(ctx.Name)}({NoInitType} _) : base(_) {{ }}");
                ctx.NoInitDone = true;
            }
            foreach (var b in bases)
            {
                if (depth > 20 || !ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (IsProtocol(py)) continue;
                    var init = py.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
                    if (init == null)
                    {
                        if (HasDecorator(py.Decorators, "dataclass")) return;
                        EmitInheritedConstructors(ctx, py.Bases, depth + 1);
                        return;
                    }
                    if (_p.TryFindClass(_m, py.Name, out var initCls, out var initOwner) && InitTarget(initOwner, initCls) is InferenceTarget it &&
                        it.InferParams && Ti.Signatures.TryGetValue(it.Key, out var baseSigs))
                    {
                        foreach (var types in baseSigs)
                        {
                            var tps = it.Params.Select((p, i) => $"{(types[i] == TypeInference.Declared ? MapType(p.Annotation, init) : types[i])} {Esc(p.Name)}");
                            // (a dynamic parameter goes as object: a constructor initializer cannot be dispatched dynamically)
                            var baseArgs = it.Params.Select((p, i) => types[i] == "dynamic" ? $"(object?){Esc(p.Name)}" : Esc(p.Name));
                            _w.Line($"{SurfaceVisibility("public", string.Join(", ", tps))} {Esc(ctx.Name)}({string.Join(", ", tps)}) : base({string.Join(", ", baseArgs)}) {{ }}");
                        }
                        _w.Line();
                        if (!Ti.Fallback.Contains(it.Key)) return;
                    }
                    var ps = new List<string>(); var names = new List<string>();
                    foreach (var p in init.Params.Skip(1))
                    {
                        if (p.Kind != ParamKind.Normal) return; // *args/**kwargs: no faithful C# mirror, write an __init__
                        string type = p.Annotation != null ? MapType(p.Annotation, init) : "dynamic";
                        ps.Add($"{type} {Esc(p.Name)}" + (p.Default != null ? " = " + EmitExpr(p.Default, targetTyped: true) : ""));
                        // a constructor initializer cannot be dispatched dynamically: late-bound values go as object
                        names.Add(type == "dynamic" ? $"(object?){Esc(p.Name)}" : Esc(p.Name));
                    }
                    _w.Line($"{SurfaceVisibility("public", string.Join(", ", ps))} {Esc(ctx.Name)}({string.Join(", ", ps)}) : base({string.Join(", ", names)}) {{ }}");
                    _w.Line();
                    return;
                }
                if (meta != null)
                {
                    var ctors = _r.GetConstructors(meta);
                    foreach (var c in ctors)
                    {
                        var ps = c.Parameters.Select(kv => $"{kv.Key} {Esc(kv.Value)}");
                        _w.Line($"{(c.IsProtected ? "protected" : "public")} {Esc(ctx.Name)}({string.Join(", ", ps)}) : base({string.Join(", ", c.Parameters.Select(kv => Esc(kv.Value)))}) {{ }}");
                    }
                    if (ctors.Count > 0) _w.Line();
                    return;
                }
            }
        }

        void EmitConstructor(FunctionDef f, ClassContext ctx, List<Expr> bases)
        {
            var t = TargetOf(_m, ctx.Def, "__init__");
            if (t != null && t.Class != ctx.Def) t = null;
            if (t != null && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
            {
                foreach (var types in sigs) { EmitConstructorVariant(f, ctx, bases, types, null); _w.Line(); }
                if (Ti.Fallback.Contains(t.Key)) EmitConstructorVariant(f, ctx, bases, null, t.Key);
                return;
            }
            EmitConstructorVariant(f, ctx, bases, null, t != null && t.InferParams ? t.Key : null);
        }

        void EmitConstructorVariant(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes, string fallbackKey)
        {
            var savedCtx = _ctxFallbackKey; _ctxFallbackKey = fallbackKey;
            if (Obs != null) Obs.InFallbackBody = fallbackKey != null;
            var savedScopeKey = _scopeKey; _scopeKey = TypeInference.FuncKey(_m, ctx.Def, "__init__") + "#" + TypeInference.SigKey(paramTypes);
            try { EmitConstructorCore(f, ctx, bases, paramTypes); }
            finally { _ctxFallbackKey = savedCtx; _scopeKey = savedScopeKey; if (Obs != null) Obs.InFallbackBody = savedCtx != null; }
        }

        // ───────────────────────────── two-phase construction ─────────────────────────────

        const string NoInitType = "global::Typhon.Runtime.NoInit";

        static bool IsSuperInit(Stmt st) => st is ExprStmt es && es.Value is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" &&
                                            a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super";

        /// <summary>Does __init__ run its base class's __init__ somewhere other than as its first statement?</summary>
        /// <summary>An __init__ without super().__init__(...) (Base.__init__(self, ...) is normalized to it).</summary>
        static bool NeverCallsBaseInit(FunctionDef init)
        {
            bool found = false;
            AstWalker.VisitAll(init.Body, n =>
            {
                if (n is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" && a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super") found = true;
            });
            return !found;
        }

        static bool DefersBaseInit(FunctionDef init)
        {
            int first = init.Body.Count > 0 && init.Body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? 1 : 0;
            bool found = false, deferred = false;
            AstWalker.VisitAll(init.Body, n =>
            {
                if (n is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" && a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super")
                {
                    found = true;
                    if (!(init.Body.Count > first && init.Body[first] is ExprStmt es && es.Value == c)) deferred = true;
                }
            });
            return found && deferred;
        }

        /// <summary>The nearest Typhon ancestor (along first bases) that defines __init__; null if none (or a .NET base comes first).</summary>
        ClassDef NearestInitAncestor(ClassDef c)
        {
            var k = c;
            for (int depth = 0; depth < 50; depth++)
            {
                if (k.Bases.Count == 0) return null;
                if (!ResolveBase(k.Bases[0], out var py, out _) || py == null) return null;
                if (py.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")) return py;
                k = py;
            }
            return null;
        }

        HashSet<ClassDef> TwoPhaseClasses
        {
            get
            {
                if (_p.TwoPhaseClasses != null) return _p.TwoPhaseClasses;
                var set = new HashSet<ClassDef>();
                foreach (var c in AllClasses())
                {
                    var init = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
                    // (an __init__ that never calls its base's: Python allows it, the base is then only allocated)
                    if (init == null || !(DefersBaseInit(init) || NeverCallsBaseInit(init) && NearestInitAncestor(c) != null)) continue;
                    for (var b = NearestInitAncestor(c); b != null && set.Add(b); b = NearestInitAncestor(b)) { }
                }
                return _p.TwoPhaseClasses = set;
            }
        }

        void EmitConstructorCore(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes)
        {
            var sig = BuildSignature(f, dropFirst: true, paramTypes: paramTypes);
            sig.Scope.HasSelf = true;
            sig.Scope.Params.Remove(f.Params[0].Name);
            bool twoPhase = TwoPhaseClasses.Contains(ctx.Def) && f.Params.All(x => x.Kind == ParamKind.Normal);
            if (twoPhase || DefersBaseInit(f) || NeverCallsBaseInit(f) && NearestInitAncestor(ctx.Def) != null)
            {
                // allocate with the base's allocation-only constructor; super().__init__(...) runs where the body has it
                string alloc = NearestInitAncestor(ctx.Def) != null ? $" : base(default({NoInitType}))" : "";
                EmitDocComment(f.Body);
                LineDirective(f);
                EmitAttributes(f.Decorators);
                if (twoPhase)
                {
                    if (!ctx.NoInitDone) { _w.Line($"private protected {Esc(ctx.Name)}({NoInitType} _){alloc} {{ }}"); ctx.NoInitDone = true; }
                    _w.Line($"{SurfaceVisibility("public", sig.Params)} {Esc(ctx.Name)}({sig.Params}){alloc} {{ __init__({string.Join(", ", f.Params.Skip(1).Select(x => Esc(x.Name)))}); }}");
                    LineDirective(f);
                    _w.Line($"protected {(NearestInitAncestor(ctx.Def) != null ? "new " : "")}void __init__({sig.Params})");
                }
                else _w.Line($"{SurfaceVisibility("public", sig.Params)} {Esc(ctx.Name)}({sig.Params}){alloc}");
                EmitSignatureBody(f.Body, sig);
                return;
            }
            var body = f.Body;
            string baseCall = "";
            int first = body.Count > 0 && body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? 1 : 0;
            // super().__init__(args), or the older Base.__init__(self, args): the base constructor
            List<Arg> superArgs = null;
            if (body.Count > first && body[first] is ExprStmt es && es.Value is CallExpr call && call.Func is AttributeExpr at && at.Name == "__init__")
            {
                if (at.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super") superArgs = call.Args;
                else if (DottedName(at.Target) is string bname && bases.Any(b => DottedName(b is SubscriptExpr bs ? bs.Target : b) == bname) &&
                         call.Args.Count >= 1 && call.Args[0].Value is NameExpr selfArg && selfArg.Id == f.Params[0].Name && call.Args[0].Name == null)
                    superArgs = call.Args.Skip(1).ToList();
            }
            if (superArgs != null)
            {
                var saved = _scope; _scope = sig.Scope;
                // a constructor initializer cannot be dispatched dynamically: late-bound arguments go as object, which binds
                // statically to the base's dynamic constructor
                bool lateBound = superArgs.Any(x => InferType(x.Value) == "dynamic") && superArgs.All(x => x.Name == null && !x.Star && !x.DoubleStar);
                var baseInit = NearestInitAncestor(ctx.Def)?.Body.OfType<FunctionDef>().FirstOrDefault(fd => fd.Name == "__init__");
                string kwBase = superArgs.Any(x => x.Name != null || x.DoubleStar) ? KwargsCallArgs(baseInit, 1, superArgs) : null;
                // (only the late-bound ones: a typed argument keeps its type, for a base constructor typed in that parameter)
                baseCall = " : base(" + (kwBase ?? (lateBound ? string.Join(", ", superArgs.Select(x => InferType(x.Value) == "dynamic" ? $"(object?){Paren(EmitValue(x.Value))}" : EmitValue(x.Value))) : EmitArgs(superArgs))) + ")";
                if (Obs != null && bases.Count > 0 && ResolveBase(bases[0], out var basePy, out _) && basePy != null &&
                    _p.TryFindClass(_m, basePy.Name, out var bcls, out var bowner))
                    RecordCall(InitTarget(bowner, bcls), superArgs);
                _scope = saved;
                body = body.Where((s, i) => i != first).ToList();
                sig.Locals = LocalsAnalyzer.Analyze(body, f.Params.Select(p => p.Name), null);
            }
            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            _w.Line($"{SurfaceVisibility(Visibility(f.Name, false).Replace("private", "public"), sig.Params)} {Esc(ctx.Name)}({sig.Params}){baseCall}");
            EmitSignatureBody(body, sig);
        }

        void EmitProperty(List<FunctionDef> defs, ClassContext ctx, List<Expr> bases)
        {
            var getter = defs.FirstOrDefault(d => HasDecorator(d.Decorators, "property"));
            var setter = defs.FirstOrDefault(d => d.Decorators.Any(x => DecoratorName(x)?.EndsWith(".setter") == true));
            if (getter == null) { Error(defs[0], "a property setter needs a @property getter"); return; }
            var gsig = BuildSignature(getter, dropFirst: true);
            gsig.Scope.HasSelf = true;
            gsig.Scope.Params.Remove(getter.Params[0].Name);
            string mods = MethodModifiers(getter, ctx, bases, out _, out bool isAbstract);
            EmitDocComment(getter.Body);
            LineDirective(getter);
            EmitAttributes(getter.Decorators);
            _w.Line($"{mods} {gsig.ReturnType} {Esc(getter.Name)}");
            _w.Open();
            if (isAbstract) { _w.Line(setter != null ? "get; set;" : "get;"); _w.Close(); return; }
            _w.Line("get");
            EmitFunctionBody(getter.Body, gsig.Scope, null, gsig.Locals);
            if (setter != null)
            {
                var ssig = BuildSignature(setter, dropFirst: true);
                ssig.Scope.HasSelf = true;
                ssig.Scope.Params.Remove(setter.Params[0].Name);
                string valueName = setter.Params.Count > 1 ? setter.Params[1].Name : "value";
                LineDirective(setter);
                _w.Line("set");
                EmitFunctionBody(setter.Body, ssig.Scope, null, ssig.Locals,
                    prologue: valueName != "value" ? $"var {Esc(valueName)} = value;" : null);
            }
            _w.Close();
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
            // C# requires comparison operators in pairs: derive the missing partner by swapping operands
            foreach (var f in methods)
                if (BinaryDunders.TryGetValue(f.Name, out var op) && ComparisonPairs.TryGetValue(op, out var partner) && !definedOps.Contains(partner))
                {
                    string pt = ParamType(f, 1);
                    if (pt == self) _w.Line($"public static {RetType(f)} operator {partner}({self} a, {self} b) => b.{f.Name}(a);");
                }
        }

        // ───────────────────────────── doc comments ─────────────────────────────

        void EmitDocComment(List<Stmt> body)
        {
            if (body.Count == 0 || !(body[0] is ExprStmt es) || !(es.Value is ConstExpr c) || c.Kind != ConstKind.Str) return;
            var lines = c.Str.Replace("\r", "").Trim('\n').Split('\n');
            int indent = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            _w.Line("/// <summary>");
            for (int i = 0; i < lines.Length; i++)
            {
                string l = i == 0 ? lines[i].Trim() : (lines[i].Length >= indent ? lines[i].Substring(indent) : lines[i].Trim());
                _w.Line("/// " + System.Security.SecurityElement.Escape(l));
            }
            _w.Line("/// </summary>");
        }
    }
}
