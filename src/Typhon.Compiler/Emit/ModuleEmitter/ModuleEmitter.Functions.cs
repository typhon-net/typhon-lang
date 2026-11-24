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
    // Functions: signatures (inferred overloads, defaults, generators), bodies, decorators and visibility.
    internal sealed partial class ModuleEmitter
    {
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
                if (_m.Ast.UnappliedDecorators.Contains(d)) { Unsupported(d, "a decorator written in Python on a method or class"); continue; }
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
        /// typing.Annotated metadata that names a .NET attribute (PEP 593 leaves its meaning to tools) becomes that attribute on
        /// the member declared with it: count: Annotated[int, Parameter()] → [Parameter()] public long count { get; set; }.
        /// Other metadata (strings, Python values) means nothing to the compiler, as it means nothing to CPython at run time.
        /// </summary>
        void EmitAnnotatedAttributes(Expr annotation) => EmitAttributes(AnnotatedAttributes(annotation).ToList());

        IEnumerable<Expr> AnnotatedAttributes(Expr annotation)
        {
            if (annotation is not SubscriptExpr subscription || StripTypingPrefix(DottedName(subscription.Target)) != "Annotated" ||
                subscription.Index is not TupleExpr metadata) yield break;
            foreach (var value in metadata.Elts.Skip(1))
            {
                if (value is not (NameExpr or AttributeExpr or CallExpr) || DecoratorName(value) is not string name) continue;
                if (TryResolveType(name, 0, out var nativeType, out var pythonType) && pythonType == null && nativeType != null ||
                    TryResolveType(name + "Attribute", 0, out nativeType, out pythonType) && pythonType == null && nativeType != null)
                    yield return value;
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
            signatureParts.Where(part => part != null).Select(part => RuntimeParameterAttribute.Replace(part, ""))
                .Any(part => UsesRuntimeSurfaceType(part) ||
                    InternalClassNames is { } internalNames && internalNames.IsMatch(part)) ? "internal" : vis;

        static bool UsesRuntimeSurfaceType(string type) =>
            type.Contains("global::Typhon.Runtime.") || type.Contains("global::Typhon.Stdlib.") ||
            type.Contains("." + FnPrefix) || RuntimeShortName.IsMatch(type);

        // Attributes on a parameter are metadata. They do not restrict the member's surface type.
        static readonly System.Text.RegularExpressions.Regex RuntimeParameterAttribute =
            new System.Text.RegularExpressions.Regex(@"\[global::Typhon\.Runtime\.[A-Za-z_]\w*\][ 	]*");

        /// <summary>A runtime type written by its short name (PyFile, PyTextStream: the runtime's types are named Py...).</summary>
        static readonly System.Text.RegularExpressions.Regex RuntimeShortName = new System.Text.RegularExpressions.Regex(@"(^|[^\w.])(?:Py[A-Z]\w*|(?:global::)?memoryview(?=[>),?\[\s]|$))");

        /// <summary>The project's internal classes (named _Like_this) as one pattern of whole type names, or null.</summary>
        System.Text.RegularExpressions.Regex InternalClassNames => _internalClassNames ??= BuildInternalClassNames();
        System.Text.RegularExpressions.Regex _internalClassNames;
        System.Text.RegularExpressions.Regex BuildInternalClassNames()
        {
            var names = AllClasses().Where(c => Visibility(c.Name, topLevel: true) != "public" ||
                    IsStringEnum(c) || IsObjectIntegerEnum(c) || IsPlainObjectEnum(c) || DerivesFromInternalClass(c))
                .Select(c => c.Name).Distinct().ToList();
            // a type position: after a dot, a bracket, a comma or the start, followed by a non-identifier character or the end
            return names.Count == 0 ? new System.Text.RegularExpressions.Regex("(?!)")
                : new System.Text.RegularExpressions.Regex(@"(^|[.<(,\s])(" + string.Join("|", names.Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?=[>),?\[\s]|$)");
        }

        static string Visibility(string name, bool topLevel)
        {
            if (t_allPublic) return "public";
            if (name.StartsWith("__") && !name.EndsWith("__")) return "private";
            if (name.StartsWith("_") && !name.EndsWith("__")) return topLevel ? "internal" : "protected internal";
            return "public";
        }

        /// <summary>
        /// The .NET or runtime base method a method overrides (as many parameters, not generic, all of them typed), or null:
        /// none, or a Typhon class in between defines the method (its own signature applies then).
        /// </summary>
        internal Semantics.MemberSig DotNetBaseMethod(ClassDef c, FunctionDef f, int depth = 0)
        {
            if (depth > 20 || f.Params.Any(x => x.Kind != ParamKind.Normal)) return null;
            int n = f.Params.Count - 1;
            foreach (var b in c.Bases)
            {
                if (!ClassDeclarationResolver(c).ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (py == c) return null;
                    if (DotNetBaseMethod(py, f, depth + 1) is Semantics.MemberSig inherited) return inherited;
                    continue;
                }
                if (meta == null || !_r.IsOverridable(meta, f.Name)) continue;
                // (of overloads, the first declared that takes as many arguments: the virtual one comes first in the runtime)
                var m = _r.GetMembers(meta, f.Name).FirstOrDefault(x => x.IsMethod && !x.IsStatic && x.TypeParams == null && x.Parameters.Count == n);
                if (m != null && m.Parameters.All(x => x.GenericType != null && !x.IsParams)) return m;
            }
            return null;
        }

        static bool NeedsNullableNativeParameter(Param parameter, string nativeType) =>
            parameter.Annotation == null && IsNone(parameter.Default) && IsValueTypeName(nativeType);

        string TypeParamList(List<TypeParam> tps) => tps.Count == 0 ? "" : "<" + string.Join(", ", tps.Select(t => Esc(t.Name))) + ">";

        static string ClassTypeValue(string name, ClassDef definition)
        {
            if (definition.TypeParams.Count == 0) return $"typeof({name})";
            int genericArguments = name.LastIndexOf('<');
            if (name.EndsWith(">") && genericArguments >= 0)
                name = name.Substring(0, genericArguments);
            return $"typeof({name}<{new string(',', definition.TypeParams.Count - 1)}>)";
        }

        string TypeParamConstraints(List<TypeParam> tps, Node at)
        {
            var sb = new StringBuilder();
            foreach (var parameter in tps)
                if (ClrTypeParamBound(parameter.Bound, at) is string bound)
                    sb.Append($" where {Esc(parameter.Name)} : {bound}");
            return sb.ToString();
        }

        // Python permits alternative constraints and concrete bounds such as str and Callable.
        // Keep a CLR constraint only when the bound is expressible as a CLR base type.
        string ClrTypeParamBound(Expr expression, Node at)
        {
            if (expression == null || expression is TupleExpr or BinOpExpr { Op: "|" } ||
                expression is SubscriptExpr union && IsTypingUnion(union.Target)) return null;
            string bound = MapType(expression, at);
            if (bound is "object" or "dynamic" or "void" or "string" or "Action" ||
                IsValueTypeName(bound) || bound.EndsWith("[]") || bound.StartsWith("(") ||
                bound.StartsWith("Action<") || bound.StartsWith("Func<")) return null;
            if (DotNetMeta(bound) is string metadata && _r is RoslynTypeResolver resolver &&
                !resolver.AllowsTypeParameterConstraint(metadata)) return null;
            if (ClassKeyOfType(bound, out _) is string key && ClassDefOfKey(key) is ClassDef definition &&
                HasDecorator(definition.Decorators, "final")) return null;
            return bound;
        }

        sealed class Signature
        {
            public string ReturnType;      // full C# return type incl. Task/IEnumerable wrapping
            public string ElementType;     // yield element type / awaited type / plain type
            public string Params;
            public string ForwardedConstructorArguments;
            public bool IsGenerator, IsAsync, ReceivesValues, AsyncForm;
            public LocalsResult Locals;
            public Scope Scope;
            /// <summary>Statements at the start of the body (parameters falling back to definition-time defaults).</summary>
            public readonly List<string> Prologue = new List<string>();
            /// <summary>Python names the body refers to by another C# name (the non-null local of such a parameter).</summary>
            public readonly Dictionary<string, string> Renames = new Dictionary<string, string>();
            public readonly Dictionary<string, string> DefaultArguments = new Dictionary<string, string>();
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
                case ConstExpr c: return c.Kind is not (ConstKind.Ellipsis or ConstKind.Bytes or ConstKind.Imaginary) &&
                    !(c.Kind == ConstKind.Int && !long.TryParse(c.Raw.Replace("_", ""), out _));
                case UnaryExpr u when (u.Op == "-" || u.Op == "+") && u.Operand is ConstExpr uc && (uc.Kind == ConstKind.Int || uc.Kind == ConstKind.Float): return true;
                default: return !_big && NativeConstantDefault(e) ||
                    FoldInt(e) is System.Numerics.BigInteger v && v >= long.MinValue && v <= long.MaxValue;
            }
        }

        bool NativeConstantDefault(Expr expression)
        {
            if (expression is NameExpr alias && ImportAlias(alias) is Expr imported) expression = imported;
            string member;
            string owner;
            if (expression is NameExpr name && !IsShadowed(name.Id))
            {
                member = name.Id;
                owner = StdlibOwner(member);
            }
            else if (expression is AttributeExpr property)
            {
                member = property.Name;
                owner = DotNetStaticMeta(property.Target);
            }
            else return false;
            return owner != null && _r.GetMembers(owner, member).Any(candidate => candidate.IsStatic && candidate.IsConstant);
        }

        readonly HashSet<AttributeExpr> _privateClassDefaultReads = new HashSet<AttributeExpr>();
        readonly Dictionary<ClassDef, List<Stmt>> _conditionalClassDefaults = new Dictionary<ClassDef, List<Stmt>>();

        /// <summary>Declares fields for f's definition-time defaults and queues their assignment at the def's position.</summary>
        void RegisterDefaultFields(FunctionDef f, string owner, Dictionary<string, string> fields, List<Stmt> execStmts)
        {
            // Synthetic slots forward missing arguments to the actual override.
            if (f.IsMissingMethodStub) return;
            foreach (var p in f.Params)
            {
                if (p.Default == null || IsConstantDefault(p.Default) || p.Kind != ParamKind.Normal || IsParamAlias(f, p)) continue;
                string field = "__default_" + (owner != null ? owner + "_" : "") + f.Name + "_" + p.Name;
                // a method's default is evaluated in the class body: strategy=HIGHEST_VALUE_STRATEGY means the class variable
                var dflt = p.Default;
                if (owner != null && dflt is NameExpr dn && _m.Classes.TryGetValue(owner, out var oc) &&
                    oc.Body.Any(st => st is AssignStmt ca && ca.Targets.Any(t => t is NameExpr tn && tn.Id == dn.Id) || st is AnnAssignStmt cn && cn.Target is NameExpr cnn && cnn.Id == dn.Id))
                {
                    var member = new AttributeExpr { Target = new NameExpr { Id = owner, Line = dn.Line, Col = dn.Col }, Name = dn.Id, Line = dn.Line, Col = dn.Col };
                    if (!_m.IsGdScript && Visibility(dn.Id, topLevel: false) == "private") _privateClassDefaultReads.Add(member);
                    dflt = member;
                }
                string type = p.Annotation != null ? MapType(p.Annotation, p) : (PyType(InferType(dflt)) ?? "dynamic");
                if (type == TypeInference.Pending) type = "dynamic";
                fields[field] = type;
                _moduleFieldTypes[field] = type;
                _w.Line($"internal static {type} {field};");
                execStmts.Add(new AssignStmt { Targets = { new NameExpr { Id = field, Line = p.Line, Col = p.Col } }, Value = dflt, Line = p.Line, Col = p.Col });
                _defaultSources[p] = field;
            }
        }

        bool UsesMissingDefinitionDefault(string type) => !_m.IsGdScript && type is "dynamic" or "object" or "object?";

        string DefinitionDefaultParameter(string type, string name, out string defaultCode)
        {
            bool useMissing = UsesMissingDefinitionDefault(type);
            if (useMissing)
            {
                defaultCode = "";
                return $"[global::System.Runtime.InteropServices.Optional] {type} {name}";
            }
            string nullableType = Nullable(type);
            defaultCode = TypedParameterDefault(nullableType, "null");
            return $"{nullableType} {name}";
        }

        /// <summary>Emits a function-like body with its signature's prologue and renames in effect.</summary>
        void EmitSignatureBody(List<Stmt> body, Signature sig)
        {
            var enclosingRenames = _renames.Count == 0 ? null : new Dictionary<string, string>(_renames);
            if (enclosingRenames != null)
            {
                foreach (var name in sig.Scope.Params) _renames.Remove(name);
                foreach (var name in sig.Locals.AllLocals) _renames.Remove(name);
            }
            foreach (var kv in sig.Renames) _renames[kv.Key] = kv.Value;
            if (sig.IsGenerator)
            {
                // A Python generator has one cursor, including when its CLR signature only promises an enumerable.
                _w.Open();
                string generatorType = sig.IsAsync ? "global::Typhon.Runtime.PyAsyncGenerator<" : PyGeneratorPrefix;
                _w.Line($"var {GenVar} = new {generatorType}{sig.ElementType}>();");
                // (the async form: an async method that awaits each yield, so yields may stand in try/except/finally and
                // inside expressions)
                _w.Line(sig.AsyncForm ? "async global::System.Threading.Tasks.Task<object?> __body()" : $"IEnumerable<{sig.ElementType}> __body()");
                EmitFunctionBody(body, sig.Scope, null, sig.Locals, extraPrologue: () => { foreach (var line in sig.Prologue) _w.Line(line); });
                _w.Line(sig.AsyncForm ? $"{GenVar}.__start_async(__body);" : $"{GenVar}.__start(__body(), plain: {(!sig.ReceivesValues ? "true" : "false")});");
                _w.Line($"return {GenVar};");
                _w.Close();
            }
            else EmitFunctionBody(body, sig.Scope, null, sig.Locals, extraPrologue: () => { foreach (var line in sig.Prologue) _w.Line(line); });
            foreach (var kv in sig.Renames) _renames.Remove(kv.Key);
            if (enclosingRenames != null)
                foreach (var kv in enclosingRenames) _renames[kv.Key] = kv.Value;
        }

        const string PyGeneratorPrefix = "global::Typhon.Runtime.PyGenerator<", GenVar = "__gen";

        /// <summary>
        /// A generator that uses what is sent in (x = yield v) or returns a value: it needs the generator protocol (send,
        /// throw, close, StopIteration.value), not just an iterator.
        /// </summary>
        /// <summary>
        /// Does the generator yield where a C# iterator cannot: in a try block that has except clauses, in an except or finally
        /// block, or inside a larger expression (print((yield)), y = f((yield x)))? Then its body takes the async form.
        /// </summary>
        static bool GeneratorNeedsAsyncForm(List<Stmt> body, bool tracksFinally)
        {
            bool found = false;
            void InExpr(Expr e) { if (e != null && !found) AstWalker.VisitAll(e, n => { if (n is YieldExpr) found = true; }); }
            void Stmts(List<Stmt> ss, bool restricted) { foreach (var st in ss) { if (found) return; Statement(st, restricted); } }
            void Statement(Stmt st, bool restricted)
            {
                switch (st)
                {
                    case FunctionDef _: case ClassDef _: return;
                    case TryStmt t:
                        bool catches = restricted || t.Handlers.Count > 0 || tracksFinally && t.FinalBody.Count > 0;
                        Stmts(t.Body, catches);
                        foreach (var h in t.Handlers) { InExpr(h.Type); Stmts(h.Body, true); }
                        Stmts(t.OrElse, catches);
                        Stmts(t.FinalBody, true);
                        return;
                    case WithStmt w:
                        foreach (var item in w.Items) InExpr(item.Context);
                        Stmts(w.Body, true);
                        return;
                    case ExprStmt es when es.Value is YieldExpr y: if (restricted || y.From) found = true; else InExpr(y.Value); return;
                    case AssignStmt a when a.Value is YieldExpr y && a.Targets.Count == 1: if (restricted || y.From) found = true; else InExpr(y.Value); return;
                    case AnnAssignStmt an when an.Value is YieldExpr y: if (restricted || y.From) found = true; else InExpr(y.Value); return;
                }
                foreach (var child in AstWalker.Children(st))
                {
                    if (child is List<Stmt> list) Stmts(list, restricted);
                    else if (child is Expr ex) InExpr(ex);
                    else if (child is System.Collections.IEnumerable items && !(child is string))
                        foreach (var item in items) { if (item is Stmt cs) Statement(cs, restricted); else if (item is Expr ce) InExpr(ce); }
                }
            }
            Stmts(body, false);
            return found;
        }

        static bool GeneratorReceivesValues(List<Stmt> body) => AstWalker.ContainsOutsideNestedScopes(body, n =>
            n is AssignStmt a && a.Value is YieldExpr ay && !ay.From || n is AnnAssignStmt an && an.Value is YieldExpr ny && !ny.From ||
            n is ReturnStmt r && r.Value != null && !(r.Value is ConstExpr rc && rc.Kind == ConstKind.None));

        static bool NeedsPackedVarArgs(FunctionDef f)
        {
            int index = f.Params.FindIndex(p => p.Kind == ParamKind.VarArgs);
            return index >= 0 && index < f.Params.Count - 1;
        }

        static void ApplyParameterDefaults(List<string> parameters, IReadOnlyList<string> defaults)
        {
            // C# requires syntactically optional parameters to follow metadata-only optional and required ones.
            bool useAttributes = false;
            for (int i = parameters.Count - 1; i >= 0; i--)
            {
                var value = defaults[i];
                if (string.IsNullOrEmpty(value))
                    useAttributes = true;
                else if (useAttributes)
                    parameters[i] = WithOptionalDefault(parameters[i], value);
                else
                    parameters[i] += " = " + value;
            }
        }

        static string TypedParameterDefault(string type, string value) =>
            value == "null" && type.EndsWith("?") && IsValueTypeName(type.TrimEnd('?'))
                ? $"default({type})" : value;

        static string WithOptionalDefault(string parameter, string value) =>
            // CLR metadata has no DefaultParameterValue constant for a nullable value type.
            value.StartsWith("default(", StringComparison.Ordinal) && value.EndsWith("?)", StringComparison.Ordinal)
                ? $"[global::System.Runtime.InteropServices.Optional] {parameter}"
                : $"[global::System.Runtime.InteropServices.Optional, global::System.Runtime.InteropServices.DefaultParameterValue({value})] {parameter}";

        // Overrides must agree on their CLR return type. A method that only raises may supply
        // an abstract-by-convention declaration whose return type is not known yet.
        bool ImplicitMethodResultIsDynamic(FunctionDef method, ClassDef owner = null)
        {
            owner ??= _cls?.Def;
            return owner != null && (FamilyReturnsValue(owner, method.Name) ||
                method.Body.Count > 0 && !CanFallThrough(method.Body) &&
                !AstWalker.ContainsOutsideNestedScopes(method.Body, node => node is ReturnStmt) &&
                !FamilyDefinesElsewhere(owner, method.Name));
        }

        Signature BuildSignature(FunctionDef f, bool dropFirst, string selfName = null, string[] paramTypes = null,
            string retKey = null, bool keepSpecializedDefaults = true, bool forOverrideSlot = false, ClassDef declaringClass = null)
        {
            var sig = new Signature();
            var locals = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(p => p.Name), null);
            sig.Locals = locals;
            sig.IsGenerator = locals.HasYield;
            sig.IsAsync = f.IsAsync;
            sig.AsyncForm = sig.IsGenerator && (f.IsAsync || GeneratorNeedsAsyncForm(f.Body, tracksFinally: !_m.IsGdScript));
            sig.ReceivesValues = sig.IsGenerator && !f.IsAsync && (sig.AsyncForm || GeneratorReceivesValues(f.Body));
            var scope = new Scope { Locals = locals, IsGenerator = sig.IsGenerator, IsAsync = f.IsAsync, Parent = _scope, ReceivesValues = sig.ReceivesValues, AsyncForm = sig.AsyncForm };
            string qualifier = _scope?.FunctionQualName is string enclosing ? enclosing + ".<locals>." :
                _cls != null ? (_cls.Def.PyName ?? _cls.Name) + "." : "";
            scope.FunctionQualName = qualifier + (f.PyName ?? f.Name);
            scope.ProfileName = f.PyName ?? f.Name;
            scope.ProfileLine = f.Line;
            if (_cls != null && f.Name == "__new__" && IsStringEnum(_cls.Def) && f.Params.Count > 0)
                scope.ClassArgument = f.Params[0].Name;
            sig.Scope = scope;
            if (!_m.IsGdScript && dropFirst && f.Params.Count > 0 && locals.AssignedParameters.Contains(f.Params[0].Name))
            {
                string name = f.Params[0].Name;
                scope.ReboundReceiver = NewTmp("receiver");
                scope.Types[name] = "dynamic";
                sig.Renames[name] = scope.ReboundReceiver;
                sig.Prologue.Add($"dynamic {scope.ReboundReceiver} = this;");
            }

            var ps = new List<string>();
            var defaults = new List<string>();
            bool packedVarArgs = NeedsPackedVarArgs(f);
            var forwarded = dropFirst && f.Name == "__init__" ? new List<string>() : null;
            int pi = 0;
            // an override of a .NET or runtime class's method: unannotated parameters take the base method's types
            ClassDef methodOwner = declaringClass ?? _cls?.Def;
            var baseMethod = dropFirst && _cls?.IsInterface != true && methodOwner?.Body.Contains(f) == true
                ? DotNetBaseMethod(methodOwner, f) : null;
            string[] baseTypes = baseMethod?.Parameters.Select(x => x.GenericType).ToArray();
            bool inferredMethod = dropFirst && f.Name != "__init__" && _cls?.Def.Body.Contains(f) == true &&
                (paramTypes != null || _ctxFallbackKey != null);
            var classBodyDefinition = _cls?.ClassBodyImplementations.FirstOrDefault(binding => binding.Value == f.Name).Key;
            for (int i = 0; i < f.Params.Count; i++)
            {
                var p = f.Params[i];
                if (i == 0 && dropFirst) continue;
                scope.Params.Add(p.Name);
                Expr definitionDefault = p.Default ?? classBodyDefinition?.Params[i].Default;
                if (!_m.IsGdScript && definitionDefault is NameExpr or AttributeExpr &&
                    (IsRuntimeTypeValue(definitionDefault) || TryTypeExpr(definitionDefault, out _) || IsConditionalModuleBinding(definitionDefault)))
                    (scope.ClassDefaultParameters ??= new HashSet<string>()).Add(p.Name);
                if (p.Kind == ParamKind.KwArgs)
                {
                    // **kwargs: the extra keyword arguments as a dict (call sites pass it by name; omitted, it is empty)
                    ps.Add($"[global::Typhon.Runtime.PyKeywordArguments] OrderedDictionary<string, dynamic>? {Esc(p.Name)}");
                    defaults.Add("null");
                    forwarded?.Add(Esc(p.Name));
                    scope.Types[p.Name] = "OrderedDictionary<string, dynamic>";
                    sig.Prologue.Add($"{Esc(p.Name)} ??= new OrderedDictionary<string, dynamic>();");
                    pi++;
                    continue;
                }
                string inferred = paramTypes != null && pi < paramTypes.Length ? paramTypes[pi] : null;
                if (inferred == null && p.Annotation == null && baseTypes != null && pi < baseTypes.Length)
                    inferred = baseTypes[pi] is "object" or "object?" or "global::System.Object" ||
                        !_m.IsGdScript && NeedsNullableNativeParameter(p, baseTypes[pi]) ? "dynamic" : baseTypes[pi];
                pi++;
                // def f(..., bisect=bisect) that callers never pass: the name stands for the function (calls go to it directly)
                if (IsParamAlias(f, p))
                {
                    // (the C# parameter gets another name: the body's calls go to the function, which it must not hide)
                    ps.Add($"object? __alias_{p.Name}");
                    forwarded?.Add($"__alias_{p.Name}");
                    defaults.Add("null");
                    scope.Params.Remove(p.Name);
                    scope.Aliases[p.Name] = p.Default;
                    _aliasTargetNodes.Add(p.Default);
                    continue;
                }
                string type = p.Annotation != null && !(TypeInference.IsBareContainer(p.Annotation) && inferred != null && inferred != TypeInference.Declared) ? MapType(p.Annotation, p)
                    : inferred != null && inferred != TypeInference.Declared ? inferred
                    : ImplicitDynamic(p, $"parameter '{p.Name}'");
                if (type == "void") type = "object";
                if (paramTypes != null && p.Annotation == null && !TypeInference.IsUnknown(type))
                    (scope.InferredParameters ??= new HashSet<string>()).Add(p.Name);
                scope.Types[p.Name] = type;
                if (p.Kind == ParamKind.VarArgs)
                {
                    if (p.Annotation == null) type = "object";
                    string argument = Esc(p.Name);
                    forwarded?.Add(argument);
                    ps.Add(packedVarArgs ? $"[global::Typhon.Runtime.PyPackedArguments] {type}[]? {argument}" : $"params {type}[] {argument}");
                    defaults.Add(packedVarArgs ? "null" : null);
                    var tupleType = $"{PyTuplePrefix}{(type == "object" ? "dynamic" : type)}>";
                    string localType = ConflictType(p.Name) ?? tupleType;
                    string local = p.Name + "__tuple";
                    string array = packedVarArgs ? $"{argument} ?? global::System.Array.Empty<{type}>()" : argument;
                    if (f.Name == "__init__" && packedVarArgs) sig.DefaultArguments[p.Name] = Paren(array);
                    sig.Prologue.Add($"{localType} {Esc(local)} = new {tupleType}({array});");
                    sig.Renames[p.Name] = local;
                    scope.Types[p.Name] = localType;
                    continue;
                }
                string decl = $"{type} {Esc(p.Name)}";
                // Select the initializer statically even when its Python parameters
                // are late-bound and the object defines __getattr__.
                forwarded?.Add(type == "dynamic" ? $"(object?){Esc(p.Name)}" : Esc(p.Name));
                // The fallback method keeps the complete Python signature. Specializations are
                // available when all arguments are supplied and cannot compete on omitted arguments.
                bool keepDefault = p.Default != null && (inferredMethod ? paramTypes == null :
                    (!_quietDynamic || p.Annotation == null && IsNone(p.Default)) &&
                    (paramTypes == null || keepSpecializedDefaults && (p.Annotation != null || InferType(p.Default) == type)));
                string defaultCode = null;
                // a parameter the body rebinds to values of another type (in a nested block): the body uses a wider local
                string wider = p.Kind == ParamKind.Normal ? ConflictType(p.Name) : null;
                if (keepDefault && (forOverrideSlot || f.IsMissingMethodStub) && !IsConstantDefault(p.Default))
                    decl = DefinitionDefaultParameter(type, Esc(p.Name), out defaultCode);
                else if (keepDefault && p.Kind == ParamKind.Normal && !IsConstantDefault(p.Default) && _defaultSources.TryGetValue(p, out var source))
                {
                    // An omitted object argument uses the CLR sentinel so an explicit Python None stays null.
                    string local = p.Name + "__arg";
                    string parameterName = Esc(p.Name);
                    decl = DefinitionDefaultParameter(type, parameterName, out defaultCode);
                    string argument = UsesMissingDefinitionDefault(type)
                        ? $"global::System.Object.ReferenceEquals({parameterName}, global::System.Type.Missing) ? {source} : {parameterName}"
                        : $"{parameterName} ?? {source}";
                    sig.DefaultArguments[p.Name] = Paren(argument);
                    sig.Prologue.Add($"{wider ?? type} {Esc(local)} = {argument};");
                    sig.Renames[p.Name] = local;
                    if (wider != null) scope.Types[p.Name] = wider;
                }
                else
                {
                    if (keepDefault)
                    {
                        defaultCode = TypedParameterDefault(type, EmitExpr(p.Default, targetTyped: true));
                        // C# allows only null for a dynamic or object parameter in source, but honors boxed metadata constants.
                        if (type is "dynamic" or "object" && !IsNone(p.Default))
                        {
                            string constant = !_big && FoldInt(p.Default) is System.Numerics.BigInteger integer &&
                                integer >= long.MinValue && integer <= long.MaxValue ? integer.ToString(CultureInfo.InvariantCulture) + "L" : defaultCode;
                            decl = WithOptionalDefault(decl, constant);
                            defaultCode = "";
                        }
                    }
                    if (wider != null)
                    {
                        string local = p.Name + "__v";
                        sig.Prologue.Add($"{wider} {Esc(local)} = {Esc(p.Name)};");
                        sig.Renames[p.Name] = local;
                        scope.Types[p.Name] = wider;
                    }
                }
                if (!_m.IsGdScript && p.PositionalOnly) decl = "[global::Typhon.Runtime.PyPositionalOnly] " + decl;
                if (!_m.IsGdScript && p.KeywordOnly) decl = "[global::Typhon.Runtime.PyKeywordOnly] " + decl;
                ps.Add(decl);
                defaults.Add(defaultCode);
            }
            ApplyParameterDefaults(ps, defaults);
            sig.Params = string.Join(", ", ps);
            if (forwarded != null) sig.ForwardedConstructorArguments = string.Join(", ", forwarded);

            string elem;
            // -> list (no type arguments): the element type the returned values have
            string inferredBare = f.Returns != null && TypeInference.IsBareContainer(f.Returns) && !sig.IsGenerator && retKey != null && Ti != null &&
                                  Ti.Returns.TryGetValue(retKey, out var br) && br != "dynamic" && ContainerMatches(f.Returns, br) ? br : null;
            if (inferredBare != null) elem = inferredBare;
            else if (f.Returns != null)
            {
                elem = MapType(f.Returns, f, isReturn: true);
                // Python functions created by exec can return values independently of their annotations.
                if (_m.RuntimeCodeMode != null && methodOwner == null && elem == "void" &&
                    !sig.IsGenerator && locals.HasValueReturn)
                    elem = "dynamic";
                // -> int with a 'return None' somewhere: the function returns an optional int
                if (!sig.IsGenerator && IsValueTypeName(elem) && !elem.EndsWith("?") && ReturnsNone(f.Body)) elem += "?";
                if (sig.IsGenerator)
                {
                    // Iterator[T] → IEnumerable<T>: the element type is T
                    if (elem.StartsWith("IEnumerable<") || elem.StartsWith("IAsyncEnumerable<")) { sig.ReturnType = elem; elem = GenericArg(elem); }
                    else sig.ReturnType = "IEnumerable<" + elem + ">";
                }
            }
            else if (sig.IsGenerator && !f.IsAsync && baseMethod?.Type is string inheritedIterator &&
                (inheritedIterator.StartsWith("IEnumerable<") || inheritedIterator.StartsWith("global::System.Collections.Generic.IEnumerable<")))
            {
                sig.ReturnType = inheritedIterator;
                elem = GenericArg(inheritedIterator);
            }
            else if (sig.IsGenerator) elem = ImplicitDynamic(f, $"the values yielded by '{f.PyName ?? f.Name}'");
            // (an override of a .NET or runtime method: the base method's return type)
            else if (!f.IsAsync && baseMethod?.ReturnsVoid == true) elem = "void";
            else if (!f.IsAsync && baseMethod?.ReturnsDynamic == true) elem = "dynamic";
            else if (locals.HasValueReturn && !f.IsAsync && baseMethod?.Type != null) elem = baseMethod.Type;
            else if (locals.HasValueReturn)
                elem = retKey != null && Ti != null && Ti.Returns.TryGetValue(retKey, out var inferredReturn) && inferredReturn != "dynamic" ? inferredReturn
                     : ImplicitDynamic(f, $"the return type of '{f.PyName ?? f.Name}'");
            else if (dropFirst && ImplicitMethodResultIsDynamic(f, methodOwner)) elem = "dynamic";
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

        /// <summary>Does the body (outside nested functions) return None explicitly or with a bare return?</summary>
        static bool ReturnsNone(List<Stmt> body) => AstWalker.ContainsOutsideNestedScopes(body,
            n => n is ReturnStmt { Value: null or ConstExpr { Kind: ConstKind.None } });

        void EmitFunction(FunctionDef f, bool isModuleLevel)
        {
            if (!_m.IsGdScript) EmitSignatureProvider(f);
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
            string functionKey = TypeInference.FuncKey(_m, null, f.Name);
            _scopeKey = functionKey + "#" + TypeInference.SigKey(paramTypes);
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            // When the original signature is emitted as well, it alone supplies omitted arguments.
            var sig = BuildSignature(f, dropFirst: false, paramTypes: paramTypes, retKey: retKey,
                keepSpecializedDefaults: Ti?.Fallback.Contains(functionKey) != true);
            EmitDocComment(f.Body);
            LineDirective(f);
            if (!_m.IsGdScript)
            {
                string doc = f.Body.FirstOrDefault() is ExprStmt statement && statement.Value is ConstExpr { Kind: ConstKind.Str } text
                    ? CsString(text.Str) : "null";
                string kind = f.IsAsync ? (sig.IsGenerator ? "AsyncGenerator" : "Coroutine") : sig.IsGenerator ? "Generator" : "Ordinary";
                _w.Line($"[global::Typhon.Runtime.PySource({CsString(_path)}, {CsString(PythonModuleName)}, global::Typhon.Runtime.PyCallableKind.{kind}, doc: {doc}, line: {f.FirstSourceLine})]");
                EmitSignatureAttribute(f);
            }
            EmitAttributes(f.Decorators);
            string vis = SurfaceVisibility(Visibility(f.Name, topLevel: true), sig.ReturnType, sig.Params);
            string asyncKw = f.IsAsync && !sig.IsGenerator ? "async " : "";
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
            _w.Line(CheckedKeyword);
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
        void EmitFunctionBody(List<Stmt> body, Scope scope, IEnumerable<string> excluded, LocalsResult locals = null, string prologue = null, Action extraPrologue = null, string epilogue = null)
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
            if (_scopeKey == RuntimeCodeScopeKey && saved == null)
                scope.Locals.LocalFunctions.ExceptWith(_m.RuntimeBindings);
            if (scope.Locals.LocalFunctions.Count > 0)
            {
                var callees = new HashSet<Expr>();
                AstWalker.VisitAll(body, node =>
                {
                    if (node is CallExpr call) callees.Add(call.Func);
                    if (node is NameExpr name && scope.Locals.LocalFunctions.Contains(name.Id) && !callees.Contains(name))
                        scope.LocalFunctionObjects[name.Id] = null;
                });
            }
            _scope = scope;
            // locals that only ever hold non-negative ints index without the count-from-the-end check; names this body binds
            // shadow the enclosing function's
            var savedNonNegative = new HashSet<string>(_nonNegative);
            var own = AlwaysNonNegative(body, scope.Params);
            _nonNegative.RemoveWhere(n => scope.Params.Contains(n) || BindsAnywhere(body, n));
            _nonNegative.UnionWith(own);
            _w.Open();
            // Python ints never wrap around: arithmetic is checked (beyond 64 bits it raises OverflowError)
            _w.Line(CheckedKeyword);
            _w.Open();
            scope.ProfileToken = null;
            scope.TraceToken = null;
            if (!_dry && _p.EmitsTraceEvents && (scope.ModuleTraceScope || scope.ProfileName != null) &&
                !scope.IsGenerator && !scope.IsAsync)
            {
                scope.TraceToken = NewTmp("trace");
                var lines = new SortedSet<int>();
                AstWalker.ContainsOutsideNestedScopes(body, node =>
                {
                    if (node is Stmt and not GlobalStmt && node.Line > 0) lines.Add(node.Line);
                    return false;
                });
                string name = scope.ProfileName ?? "<module>";
                _w.Line($"using var {scope.TraceToken} = global::Typhon.Runtime.PyTracing.Enter(" +
                    $"typeof({ModuleClassName(_m)}).Assembly, {CsString(name)}, {CsString(scope.FunctionQualName ?? name)}, {CsString(_m.Path ?? "")}, " +
                    $"{(scope.ModuleTraceScope ? 1 : scope.ProfileLine)}, {CsString(PythonModuleName)}, [{string.Join(", ", lines)}]);");
            }
            if (!_dry && _p.EmitsProfileEvents && scope.ProfileName != null && !scope.IsGenerator && !scope.IsAsync)
            {
                scope.ProfileToken = NewTmp("profile");
                _w.Line($"using var {scope.ProfileToken} = global::Typhon.Runtime.PyProfiling.Enter(" +
                    $"{CsString(scope.ProfileName)}, {CsString(scope.FunctionQualName)}, {CsString(_m.Path ?? "")}, {scope.ProfileLine}, {CsString(_m.Name)});");
            }
            if (scope.TraceToken != null || scope.ProfileToken != null)
            {
                _w.Line("try");
                _w.Open();
            }
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
                    else if (Ti != null && Ti.Hoisted.TryGetValue(_scopeKey + "|" + h.Key, out var hoistedType) &&
                             scope.Locals.HoistedPlain.Contains(h.Key))
                    { type = hoistedType; observed = true; }
                    else type = PyType(fv != null ? InferType(fv) : LoopVariableType(scope.Locals, h.Key));
                    // a variable assigned values of different types (in different blocks) is late-bound
                    if (type != null && !observed && scope.Locals.HoistedValues.TryGetValue(h.Key, out var values) &&
                        values.Any(value => value == null || PyType(InferType(value)) is string assigned &&
                            assigned != TypeInference.Pending && Incompatible(type, assigned)))
                        type = null;
                    // a capture bound from a value of unknown type (case [x, y] on an object) is not known to agree with the others
                    if (type != null && scope.Locals.HoistedCaptures.TryGetValue(h.Key, out var captures) &&
                        captures.Any(value => value == null || IsUnknownType(InferType(value))))
                        type = null;
                    type = type ?? ImplicitDynamic(body.FirstOrDefault(), $"local variable '{h.Key}' (assigned in a nested block)");
                }
                if (ConflictType(h.Key) is string wider) type = wider;
                scope.Types[h.Key] = type;
                _w.Line($"{type} {Esc(h.Key)} = default;");
            }
            // parameters count as declared by the body's top block: rebinding one there to another type gives a new variable
            EmitBlockStatements(body, preDeclared: scope.Params);
            if (epilogue != null) _w.Line(epilogue);
            // falling off the end returns None (C# warns about unreachable code where the end cannot be reached; that is off)
            if (scope.AsyncForm) _w.Line("return null;"); // the async form's body: falling off the end returns None
            if (scope.ReturnType != null && scope.ReturnType != "void" && !scope.IsGenerator)
            {
                if (Obs != null && _retKey != null && CanFallThrough(body)) Obs.AddReturn(_retKey, TypeInference.NoneType);
                _w.Line("return default!;");
            }
            if (scope.TraceToken != null || scope.ProfileToken != null)
            {
                _w.Close();
                string error = scope.TraceToken != null ? NewTmp("traceError") : null;
                _w.Line(error == null ? "catch" : $"catch (global::System.Exception {error})");
                _w.Open();
                if (scope.ProfileToken != null) _w.Line($"if ({scope.ProfileToken} != null) {scope.ProfileToken}.Result = null;");
                if (error != null) _w.Line($"{scope.TraceToken}.Unwind({error});");
                _w.Line("throw;");
                _w.Close();
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

        /// <summary>The type of a hoisted variable that only for-loops bind: the integral mode or the iterables' element type.</summary>
        string LoopVariableType(LocalsResult locals, string name)
        {
            if (!locals.HoistedLoops.TryGetValue(name, out var loops) || loops.Count == 0) return null;
            var types = loops.Select(f => f.IsAsync ? null : IsBuiltinRange(f.Iter, out _) ? IntT : ElementTypeOf(InferType(f.Iter))).Distinct().ToList();
            return types.Count == 1 && types[0] != null && types[0] != TypeInference.Pending && types[0] != "dynamic" ? types[0] : null;
        }

        /// <summary>An initializer outside a method body (fields): checked like code in methods. Constants stay as they are.</summary>
        static string Checked(string code) => code.Length > 0 && (char.IsDigit(code[0]) || code[0] == '"' || code == "null" || code == "true" || code == "false") ? code : $"checked({code})";

        void CaptureLocalDefaults(FunctionDef f)
        {
            // definition-time defaults of a nested function are evaluated each time its def runs: locals before it
            foreach (var p in f.Params)
                if (p.Default != null && p.Kind == ParamKind.Normal && !IsConstantDefault(p.Default))
                {
                    string tmp = NewTmp("default");
                    _w.Line($"var {tmp} = {EmitValue(p.Default)};");
                    _defaultSources[p] = tmp;
                }
        }

        void EmitLocalFunction(FunctionDef f)
        {
            if (f.Decorators.Count > 0) Unsupported(f, "decorators on nested functions");
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            var savedRet = _retKey; _retKey = null; // a nested function's returns are its own
            CaptureLocalDefaults(f);
            var sig = BuildSignature(f, dropFirst: false);
            sig.Scope.HasSelf = false;
            bool shadowsParameter = _scope != null && _scope.Params.Contains(f.Name) && IsUnknownType(KnownTypeCore(f.Name));
            bool conditionalModuleBinding = _lateModuleFields.Contains(f.Name) &&
                (_scopeKey == TypeInference.FuncKey(_m, null, "<main>") || _scopeKey == TypeInference.FuncKey(_m, null, "<module>"));
            bool bindsLocal = conditionalModuleBinding || shadowsParameter || _scope?.Locals?.AllLocals.Contains(f.Name) == true;
            bool exports = AtRuntimeModuleScope;
            string name = bindsLocal || exports ? NewTmp("fn") : Esc(f.Name);
            if (!bindsLocal && !exports)
            {
                if (!f.IsAsync && !sig.IsGenerator) _funcReturnTypes[f.Name] = sig.ReturnType;
                _localFunctionSigs[f.Name] = (f, sig);
                if (f.TypeParams.Count == 0 && _scope != null && _scope.LocalFunctionObjects.ContainsKey(f.Name))
                {
                    string value = NewTmp("closure");
                    _scope.LocalFunctionObjects[f.Name] = value;
                    // The generated function body can pass this very closure as a value.
                    _w.Line($"global::Typhon.Runtime.PyClosure {value} = null!;");
                    _w.Line($"{value} = {ClosureCode(f, sig, name, null)};");
                }
            }
            string asyncKw = f.IsAsync && !sig.IsGenerator ? "async " : "";
            _w.Line($"{asyncKw}{sig.ReturnType} {name}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}");
            EmitSignatureBody(f.Body, sig);
            if (exports)
                _w.Line($"__typhon_execution.Export({CsString(f.Name)}, {ClosureCode(f, sig, name, null)});");
            else if (bindsLocal)
                _w.Line($"{EmitName(new NameExpr { Id = f.Name })} = {ClosureCode(f, sig, name, null)};");
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name);
            _retKey = savedRet;
        }
    }
}
