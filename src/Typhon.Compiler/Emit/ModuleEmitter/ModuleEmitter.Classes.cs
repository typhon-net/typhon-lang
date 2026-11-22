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
    // Classes: the class body (fields, properties, methods), enums, protocols and bases.
    internal sealed partial class ModuleEmitter
    {
        static string DottedName(Expr e)
        {
            switch (e)
            {
                case NameExpr n: return n.Id;
                case AttributeExpr a: var t = DottedName(a.Target); return t == null ? null : t + "." + a.Name;
                default: return null;
            }
        }

        bool HasVirtualSubclasses(ClassDef c, int depth = 0) =>
            depth < 20 && (DottedName(c.Metaclass) is "ABCMeta" or "abc.ABCMeta" ||
                c.Bases.Any(b => DottedName(b) is "ABC" or "abc.ABC" || IsMappingBase(b)) ||
                TyphonBases(c).Any(b => HasVirtualSubclasses(b, depth + 1)) ||
                _p.MixedInto.Any(ancestor => ancestor.Value.Any(copy => copy.Class == c) &&
                    HasVirtualSubclasses(ancestor.Key, depth + 1)));

        bool MayHaveVirtualSubclasses(string meta, ClassDef c) =>
            c != null ? HasVirtualSubclasses(c) :
                meta is MappingMeta or "Typhon.Stdlib.os_+PathLike" or
                    "Typhon.Stdlib.collections+abc+Generator" or "Typhon.Stdlib.collections+abc+Coroutine" or
                    "Typhon.Stdlib.collections+abc+MappingView" or "Typhon.Stdlib.collections+abc+KeysView" or
                    "Typhon.Stdlib.collections+abc+ValuesView" or "Typhon.Stdlib.collections+abc+ItemsView" ||
                meta?.StartsWith("Typhon.Stdlib.numbers+", StringComparison.Ordinal) == true;

        const string MappingMeta = "Typhon.Stdlib.collections+abc+Mapping";

        bool IsMappingBase(Expr value) =>
            DottedName(value is SubscriptExpr sub ? sub.Target : value) is string dotted &&
            TryResolveType(dotted, 0, out var meta, out _) && meta == MappingMeta;

        HashSet<string> AbstractMethods(ClassDef c, int depth = 0)
        {
            var unresolved = new HashSet<string>();
            if (depth > 20) return unresolved;
            if (c.Bases.Any(IsMappingBase)) unresolved.UnionWith(new[] { "__getitem__", "__iter__", "__len__" });
            foreach (var parent in TyphonBases(c))
                unresolved.UnionWith(AbstractMethods(parent, depth + 1));
            foreach (var methods in c.Body.OfType<FunctionDef>().GroupBy(f => f.Name))
            {
                if (methods.Any(f => HasDecorator(f.Decorators, "abstractmethod"))) unresolved.Add(methods.Key);
                else unresolved.Remove(methods.Key);
            }
            return unresolved;
        }

        static bool DataclassReprEnabled(ClassDef c) =>
            c.Decorators.OfType<CallExpr>().Where(d => DecoratorName(d) is "dataclass" or "dataclasses.dataclass")
                .SelectMany(d => d.Args).FirstOrDefault(a => a.Name == "repr")?.Value is not ConstExpr { Kind: ConstKind.Bool, Raw: "False" };

        bool GeneratesDataclassRepr(ClassDef c) => !_m.IsGdScript && HasDecorator(c.Decorators, "dataclass") &&
            DataclassReprEnabled(c) && !c.Body.OfType<FunctionDef>().Any(method => method.Name == "__repr__");

        static string ClassActivation(ClassDef definition) => "__typhon_class_active_" + definition.Name;

        void EmitConditionalModuleClassAttribute(ClassDef definition)
        {
            if (definition.ConditionalModuleDefinition)
                _w.Line($"[global::Typhon.Runtime.PyConditionalModuleClass({CsString(ClassActivation(definition))})]");
        }

        void EmitClass(ClassDef c)
        {
            if (!_m.IsGdScript && IsStringEnum(c)) { EmitStringEnum(c); return; }
            var bases = new List<Expr>();
            bool isInterface = false, isEnum = false, isAbstract = false;
            foreach (var b in c.Bases)
            {
                string bn = DottedName(b is SubscriptExpr bs ? bs.Target : b);
                if (bn == "Protocol" || bn == "typing.Protocol") { isInterface = true; continue; }
                if (bn == "Enum" || bn == "IntEnum" || bn == "enum.Enum" || bn == "enum.IntEnum") { isEnum = true; continue; }
                if (bn == "ABC" || bn == "abc.ABC") { isAbstract = true; continue; }
                if (bn == "object" || bn == "Generic" || bn == "typing.Generic" || !_m.IsGdScript && BuiltinTypeBase(b)) continue;
                bases.Add(b);
            }
            if (isEnum) { EmitEnum(c); return; }

            var ctx = new ClassContext { Def = c, Name = c.Name, IsInterface = isInterface, IsSealed = HasDecorator(c.Decorators, "final") };
            foreach (var tp in c.TypeParams) ctx.TypeParams.Add(tp.Name);
            ctx.CsSelfType = Esc(c.Name) + TypeParamList(c.TypeParams);
            if (!isAbstract) isAbstract = AbstractMethods(c).Count != 0;

            var savedCls = _cls; var savedScope = _scope; var savedEmptyFields = _emptyFields;
            _cls = ctx; _scope = null; _emptyFields = new Dictionary<string, char>();

            EmitDocComment(c.Body);
            LineDirective(c);
            if (!isInterface) EmitClassSource(c.Body, c.Line);
            EmitConditionalModuleClassAttribute(c);
            if (!_m.IsGdScript && IsUserMetaclass(c))
                _w.Line("[global::Typhon.Runtime.PyMetaclassDefinition]");
            EmitAttributes(c.Decorators);
            if (!isInterface && UsesCustomAllocation(c))
                _w.Line("[global::Typhon.Runtime.PyCustomAllocation]");
            string vis = Visibility(c.Name, topLevel: true);
            if (vis == "public" && DerivesFromInternalClass(c)) vis = "internal";
            string kind = isInterface ? "partial interface" : (isAbstract ? "abstract partial class" : (ctx.IsSealed ? "sealed partial class" : "partial class"));
            var baseTypes = bases.Select(b => IsMappingBase(b) ? CsTypeName(MappingMeta) : MapType(b, c)).ToList();
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
            // def __lt__(self, other) with other of any type: IComparable, so sorting compares through __lt__ too
            else if (!isInterface && LtComparesAny(c)) baseTypes.Add("global::System.IComparable");
            // def __len__(self) -> int  ⇒  IPyLen, so len(obj) works without reflection
            if (!isInterface && c.Body.OfType<FunctionDef>().Any(f => f.Name == "__len__" && f.Params.Count == 1 && f.Returns is NameExpr rn && rn.Id == "int"))
                baseTypes.Add("global::Typhon.Runtime.IPyLen");
            // a namedtuple: a tuple of its fields (unpacking, indexing, iteration, len, comparison, repr)
            if (!isInterface && (c.NamedTupleFields != null || c.Body.OfType<FunctionDef>().Any(f => f.Name == "__repr__") ||
                GeneratesDataclassRepr(c)))
                baseTypes.Add("global::Typhon.Runtime.IPyRepr");
            if (c.NamedTupleFields != null && !isInterface)
            {
                baseTypes.Add("global::System.Runtime.CompilerServices.ITuple");
                baseTypes.Add("global::System.IComparable");
                if (!baseTypes.Contains("global::Typhon.Runtime.IPyLen")) baseTypes.Add("global::Typhon.Runtime.IPyLen");
                if (iter == null) { baseTypes.Add("global::System.Collections.Generic.IEnumerable<object?>"); ctx.IterElementType = "object"; }
            }
            string baseList = baseTypes.Count > 0 ? " : " + string.Join(", ", baseTypes) : "";
            _w.Line($"{vis} {kind} {Esc(c.Name)}{TypeParamList(c.TypeParams)}{baseList}{TypeParamConstraints(c.TypeParams, c)}");
            _w.Open();

            if (isInterface) EmitInterfaceBody(c);
            else
            {
                EmitClassBody(c, ctx, bases, isAbstract);
                if (c.UninitializedSingleton)
                    _w.Line($"internal static dynamic {ClassModel.AllocateSingleton}() => global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof({ctx.CsSelfType}));");
            }

            _w.Close();
            _cls = savedCls; _scope = savedScope; _emptyFields = savedEmptyFields;
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
            EmitConditionalModuleClassAttribute(c);
            // an IntEnum is long-based: the runtime prints its members as ints (str(Level.HIGH) == "2"), arithmetic treats them as ints
            bool intEnum = IsIntEnum(c);
            _w.Line($"{Visibility(c.Name, true)} enum {Esc(c.Name)}{(intEnum ? " : long" : "")}");
            _w.Open();
            string previous = null;
            foreach (var s in c.Body)
            {
                if (s is AssignStmt a && a.Targets[0] is NameExpr n)
                {
                    // auto(): the previous member's value + 1, starting at 1 (Python's default _generate_next_value_)
                    bool auto = a.Value is CallExpr call && DottedName(call.Func)?.EndsWith("auto") == true;
                    _w.Line(auto ? $"{Esc(n.Id)} = {(previous == null ? "1" : Esc(previous) + " + 1")}," : $"{Esc(n.Id)} = {EmitExpr(a.Value)},");
                    previous = n.Id;
                }
                else if (!(s is PassStmt) && !(s is ExprStmt)) Unsupported(s, "methods or annotations inside an Enum");
            }
            _w.Close();
        }

        sealed class FieldSpec { public Expr Default, Factory; public bool? Init, Repr, Compare; }

        /// <summary>A dataclass field's dataclasses.field(...) options, or null when its value is not a field() call.</summary>
        FieldSpec DataclassField(AnnAssignStmt a)
        {
            if (!(a.Value is CallExpr fc) || !(DottedName(fc.Func) is "field" or "dataclasses.field")) return null;
            var spec = new FieldSpec();
            bool? Flag(Expr e) => e is ConstExpr b && b.Kind == ConstKind.Bool ? b.Raw == "True" : null;
            foreach (var arg in fc.Args)
                switch (arg.Name)
                {
                    case "default": spec.Default = arg.Value; break;
                    case "default_factory": spec.Factory = arg.Value; break;
                    case "init": spec.Init = Flag(arg.Value); break;
                    case "repr": spec.Repr = Flag(arg.Value); break;
                    case "compare": spec.Compare = Flag(arg.Value); break;
                }
            return spec;
        }

        /// <summary>A default_factory's call for a field of the given type: list/dict/set make an empty container of that type.</summary>
        string FactoryCall(Expr factory, string type)
        {
            if (factory is NameExpr fn && !IsShadowed(fn.Id) && type != "dynamic" && type != "object")
            {
                if (fn.Id is "list" or "dict" or "set") return $"new {type.TrimEnd('?')}()";
                if (fn.Id == "int") return "0L";
                if (fn.Id == "float") return "0.0";
                if (fn.Id == "str") return "\"\"";
            }
            if (factory is LambdaExpr { RequiresArgumentBinding: false } lf && lf.Params.Count == 0) return Checked(EmitExpr(lf.Body, targetTyped: true));
            return Checked(EmitExpr(new CallExpr { Func = factory, Line = factory.Line, Col = factory.Col }, targetTyped: true));
        }

        static bool IsIntEnum(ClassDef c) => c.Bases.Any(b => DottedName(b) is "IntEnum" or "enum.IntEnum");

        /// <summary>Is the C# type a Typhon IntEnum (whose members are ints in arithmetic)?</summary>
        bool IsIntEnumType(string t) => t != null && ClassKeyOfType(t, out _) is string key && ClassDefOfKey(key) is ClassDef cd && IsIntEnum(cd);

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
        static bool IsRuntimeType(string meta) =>
            meta != null && (meta.StartsWith("Typhon.Runtime.") || meta.StartsWith("Typhon.Stdlib.") || meta == "memoryview");

        /// <summary>Does the class directly or indirectly inherit a type with restricted CLR visibility?</summary>
        bool DerivesFromInternalClass(ClassDef c, int depth = 0)
        {
            if (depth > 20) return false;
            foreach (var b in c.Bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py == null ? IsRuntimeType(meta) :
                    py != c && (Visibility(py.Name, topLevel: true) != "public" || DerivesFromInternalClass(py, depth + 1))) return true;
            }
            return false;
        }

        /// <summary>Is the member a runtime base class declares (and no Typhon class between them)?</summary>
        bool OverridesRuntimeMember(ClassDef c, string name, int depth = 0)
        {
            if (depth > 20) return false;
            foreach (var b in c.Bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (py.Body.OfType<FunctionDef>().Any(f => f.Name == name)) return false;
                    if (py != c && OverridesRuntimeMember(py, name, depth + 1)) return true;
                }
                else if (IsRuntimeType(meta)) return true;
            }
            return false;
        }

        bool ResolveBase(Expr b, out ClassDef pyClass, out string meta)
        {
            pyClass = null; meta = null;
            int arity = 0;
            var target = b;
            if (b is SubscriptExpr s) { target = s.Target; arity = s.Index is TupleExpr t ? t.Elts.Count : 1; }
            string dotted = DottedName(target);
            if (dotted == null) return false;
            if (arity > 0 && IsMappingBase(b)) { meta = MappingMeta; return true; }
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
                    if (ClassDeclarationResolver(py).BaseHasMember(py.Bases, member, requireOverridable, depth + 1)) return true;
                }
                else if (meta != null)
                {
                    if (requireOverridable ? _r.IsOverridable(meta, member) : _r.HasMember(meta, member)) return true;
                }
            }
            return false;
        }

        /// <summary>self.x = ... assignments inside methods declare instance attributes (Python style).</summary>
        const string LocalDataClassType = "global::Typhon.Runtime.PyLocalDataClass";

        void EmitLocalDataClass(ClassDef c)
        {
            if (c.Bases.Count > 1 || c.Decorators.Count > 0 || c.TypeParams.Count > 0 || c.Metaclass != null ||
                c.Body.Any(s => s is not (PassStmt or ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) &&
                    s is not AssignStmt { Targets.Count: 1 } || s is AssignStmt assignment && assignment.Targets[0] is not NameExpr))
            {
                Unsupported(c, "this local class body");
                return;
            }
            string parent = c.Bases.Count == 1 ? EmitValue(c.Bases[0]) : "typeof(object)";
            string name = Esc(c.Name);
            if (IsInline(c, c.Name)) { _w.Line($"{LocalDataClassType} {name};"); Declared(c.Name); }
            string value = NewTmp("cls");
            string qualified = _scope.FunctionQualName + ".<locals>." + c.Name;
            _w.Line($"var {value} = new {LocalDataClassType}({CsString(c.Name)}, {CsString(PythonModuleName)}, {CsString(qualified)}, (global::System.Type){Paren(parent)});");
            if (c.Body.FirstOrDefault() is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } doc })
                _w.Line($"{value}.Members[\"__doc__\"] = {EmitValue(doc)};");
            foreach (var member in c.Body.OfType<AssignStmt>())
                _w.Line($"{value}.Members[{CsString(((NameExpr)member.Targets[0]).Id)}] = {EmitValue(member.Value)};");
            _w.Line($"{name} = {value};");
            SetType(c.Name, LocalDataClassType);
        }

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
                    if (n is AssignStmt a) foreach (var t in a.Targets) { target = t; value = a.Value; Check(); }
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

        static IEnumerable<FunctionDef> ClassBodyMethods(Stmt statement) => statement switch
        {
            FunctionDef method => new[] { method },
            IfStmt branch => branch.Body.Concat(branch.OrElse).SelectMany(ClassBodyMethods),
            WhileStmt loop => loop.Body.Concat(loop.OrElse).SelectMany(ClassBodyMethods),
            _ => Enumerable.Empty<FunctionDef>(),
        };

        bool HasConditionalClassMethod(ClassDef definition, string name, int depth = 0) =>
            depth < 20 && (ExecutableClassStatements(definition).SelectMany(ClassBodyMethods).Any(method => method.Name == name) ||
                TyphonBases(definition).Any(parent => HasConditionalClassMethod(parent, name, depth + 1)));

        bool IsConditionalClassMethodReference(AttributeExpr member)
        {
            if (DottedName(member.Target) is string dotted && TryResolveType(dotted, 0, out _, out var declared) && declared != null)
                return HasConditionalClassMethod(declared, member.Name);
            var owner = member.Target is NameExpr { Id: "self" } && HasSelfInScope() ? _cls?.Def :
                ClassKeyOfType(InferType(member.Target), out _) is string key ? ClassDefOfKey(key) : null;
            return owner != null && HasConditionalClassMethod(owner, member.Name);
        }

        FunctionDef ClassBodyImplementation(FunctionDef definition, string name) => new FunctionDef
        {
            Name = name, PyName = definition.Name, Line = definition.Line, Col = definition.Col,
            Params = definition.Params, Returns = definition.Returns, Body = definition.Body, IsAsync = definition.IsAsync,
            Decorators = definition.UsedInClassBody ? new List<Expr> { new NameExpr { Id = "staticmethod" } } : new List<Expr>(),
        };

        static bool IsExecutableClassStatement(Stmt statement, HashSet<string> reserved, bool nested = false) => statement switch
        {
            AssignStmt assignment => (nested || assignment.Targets.Count != 1 || assignment.Targets[0] is not NameExpr) &&
                !assignment.Targets.SelectMany(NamesIn).Any(reserved.Contains),
            AugAssignStmt augmented => !NamesIn(augmented.Target).Any(reserved.Contains),
            ExprStmt expression => nested || expression.Value is not ConstExpr,
            AssertStmt or RaiseStmt => true,
            FunctionDef method => method.UsedInClassBody ||
                nested && !reserved.Contains(method.Name) && ClassModel.CanBindClassBodyFunction(method),
            PassStmt or BreakStmt or ContinueStmt => nested,
            IfStmt branch => branch.Body.Concat(branch.OrElse).All(s => IsExecutableClassStatement(s, reserved, nested: true)),
            WhileStmt loop => loop.Body.Concat(loop.OrElse).All(s => IsExecutableClassStatement(s, reserved, nested: true)),
            _ => false,
        };

        static IEnumerable<string> NamesInClassStatement(Stmt statement) => statement switch
        {
            AssignStmt assignment => assignment.Targets.SelectMany(NamesIn),
            AugAssignStmt augmented => NamesIn(augmented.Target),
            FunctionDef method => new[] { method.Name },
            IfStmt branch => branch.Body.Concat(branch.OrElse).SelectMany(NamesInClassStatement),
            WhileStmt loop => loop.Body.Concat(loop.OrElse).SelectMany(NamesInClassStatement),
            _ => Enumerable.Empty<string>(),
        };

        Dictionary<ClassDef, HashSet<Stmt>> _executableClassStatements = new Dictionary<ClassDef, HashSet<Stmt>>();

        HashSet<Stmt> ExecutableClassStatements(ClassDef definition)
        {
            if (_executableClassStatements.TryGetValue(definition, out var statements)) return statements;
            var reserved = new HashSet<string>(definition.Body.SelectMany(statement => statement switch
            {
                FunctionDef method => new[] { method.Name },
                ClassDef nested => new[] { nested.Name },
                AnnAssignStmt annotation => NamesIn(annotation.Target),
                _ => Enumerable.Empty<string>(),
            }));
            statements = new HashSet<Stmt>(definition.Body.Where(s => IsExecutableClassStatement(s, reserved)));
            _executableClassStatements[definition] = statements;
            return statements;
        }

        void EmitLateClassSlots(ClassDef definition, ClassContext context, HashSet<Stmt> executable, HashSet<string> declared)
        {
            var assigned = new HashSet<string>();
            foreach (var statement in definition.Body)
            {
                if (statement is AssignStmt) assigned.UnionWith(NamesInClassStatement(statement));
                else if (executable.Contains(statement))
                    foreach (var name in NamesInClassStatement(statement).Where(assigned.Add))
                    {
                        string storage = "__typhon_" + NewTmp("class_slot");
                        string attribute = Esc(name);
                        const string descriptors = "global::Typhon.Runtime.PyClassDescriptors";
                        context.LateClassSlots.Add(name, storage);
                        context.Members.Add(name);
                        context.FieldTypes.Add(name, "dynamic");
                        declared.Add(name);
                        _w.Line("[global::System.Runtime.CompilerServices.CompilerGenerated]");
                        _w.Line($"private static object? {storage} = {descriptors}.Unassigned;");
                        _w.Line($"[global::Typhon.Runtime.PyClassSlot({CsString(storage)})]");
                        _w.Line($"{Visibility(name, false)} static dynamic? {attribute} " +
                            $"{{ get => {Rt("getattr")}(typeof({context.CsSelfType}), {CsString(name)}); set => {storage} = value; }}");
                    }
            }
        }

        string LateClassSlot(string name) =>
            _classFieldInitializer && _lambdaParams.Count == 0 &&
            _cls?.LateClassSlots.TryGetValue(name, out var storage) == true ? storage : null;

        string EmitLateClassName(string name, string storage) =>
            $"global::Typhon.Runtime.PyClassDescriptors.ReadBodyName({storage}, {CsString(name)}, " +
            $"typeof({ModuleClassName(_m)}), {CsString(PythonModuleName)})";

        sealed class ClassBodyInitialization : Stmt { public ClassDef Definition; }

        bool RunsClassBodyAtDefinition(ClassDef cls) =>
            cls.TypeParams.Count == 0 && !cls.Bases.Any(b => DottedName(b is SubscriptExpr sub ? sub.Target : b) is
                "Enum" or "enum.Enum" or "IntEnum" or "enum.IntEnum" or "Protocol" or "typing.Protocol") &&
            (HasUserMetaclass(cls) || ExecutableClassStatements(cls).Any());

        void EmitClassBodyStatement(Stmt statement, ClassContext context, HashSet<string> declared)
        {
            LineDirective(statement);
            foreach (string name in NamesInClassStatement(statement).Where(declared.Add))
            {
                context.Members.Add(name);
                context.FieldTypes[name] = "dynamic";
                _w.Line($"{Visibility(name, false)} static dynamic? {Esc(name)};");
            }

            // Interleave executable statements and field initializers in the original suite order.
            string step = NewTmp("__typhon_class_body");
            _w.Line("[global::System.Runtime.CompilerServices.CompilerGenerated]");
            _w.Line($"private static readonly object? {step}_result = {step}();");
            _w.Line($"private static object? {step}()");
            _w.Open();
            _w.Line(CheckedKeyword);
            _w.Open();
            var previousScope = _scope;
            bool previousInitializer = _classFieldInitializer;
            _scope = new Scope();
            foreach (var field in context.FieldTypes)
                _scope.Types[field.Key] = field.Value;
            _classFieldInitializer = true;
            try
            {
                EmitBlockStatements(new List<Stmt> { statement });
            }
            finally
            {
                _scope = previousScope;
                _classFieldInitializer = previousInitializer;
            }
            _w.Line("return null;");
            _w.Close();
            _w.Close();
        }

        bool UsesCustomAllocation(ClassDef definition) =>
            !_m.IsGdScript && definition != null && !IsUserMetaclass(definition) && definition.TypeParams.Count == 0 && definition.NamedTupleFields == null &&
            MethodInHierarchy(definition, "__init__") == null &&
            MethodInHierarchy(definition, "__new__") is FunctionDef allocator && allocator.TypeParams.Count == 0 &&
            !allocator.IsAsync && allocator.Params.Count > 0 && allocator.Params[0].Kind == ParamKind.Normal &&
            (allocator.Decorators.Count == 0 || allocator.Decorators.Count == 1 && HasDecorator(allocator.Decorators, "staticmethod"));

        static FunctionDef StaticAllocator(FunctionDef allocator)
        {
            var receiver = allocator.Params[0];
            var parameters = new List<Param>
            {
                new Param
                {
                    Name = receiver.Name, Line = receiver.Line, Col = receiver.Col, Kind = receiver.Kind,
                    PositionalOnly = receiver.PositionalOnly, KeywordOnly = receiver.KeywordOnly,
                    Annotation = new NameExpr { Id = "type", Line = receiver.Line, Col = receiver.Col },
                },
            };
            parameters.AddRange(allocator.Params.Skip(1));
            return new FunctionDef
            {
                Name = allocator.Name, Line = allocator.Line, Col = allocator.Col, Params = parameters,
                Returns = allocator.Returns ?? new NameExpr { Id = "Any" }, Body = allocator.Body,
                Decorators = new List<Expr> { new NameExpr { Id = "staticmethod" } },
            };
        }

        void EmitClassBody(ClassDef c, ClassContext ctx, List<Expr> bases, bool isAbstract)
        {
            var methods = c.Body.OfType<FunctionDef>().Where(method => !method.UsedInClassBody).ToList();
            bool hasGetAttr = DefinesInHierarchy(c, "__getattr__");
            string InstanceStorage(string name, string type, string visibility, string initializer = "")
            {
                // An auto-property reserves get_name and set_name in C#. A Python data attribute can coexist with those methods.
                bool reservesMethod = methods.Any(m => m.Name == "get_" + name || m.Name == "set_" + name);
                if (!reservesMethod && c.NamedTupleFields?.IndexOf(name) is int index && index >= 0)
                    _w.Line($"[global::Typhon.Runtime.PyDescriptor(global::Typhon.Runtime.PyDescriptorKind.NamedTupleField, index: {index})]");
                if (hasGetAttr && !reservesMethod && initializer.Length == 0)
                {
                    string key = CsString(name);
                    return $"[global::Typhon.Runtime.PyLateAttribute] {SurfaceVisibility(visibility, type)} {type} {Esc(name)} " +
                        $"{{ get => ({type}){Rt("__read_instance_attr")}(this, {key}); " +
                        $"set => global::Typhon.Runtime.PyAttrDict.SetExtra(this, {key}, value); }}";
                }
                string suffix = reservesMethod ? (initializer.Length == 0 ? ";" : initializer) : " { get; set; }" + initializer;
                return $"{SurfaceVisibility(visibility, type)} {type} {Esc(name)}{suffix}";
            }

            bool metaclassBody = HasUserMetaclass(c);
            if (metaclassBody)
                _w.Line($"static {Esc(c.Name)}() {{ }}"); // prevent CLR initialization before __prepare__
            var declared = new HashSet<string>();
            foreach (var property in methods.Where(f => IsCachedProperty(f) ||
                HasDecorator(f.Decorators, "property") && _m.Ast.MethodDecorators.ContainsKey(f)))
                ctx.FieldTypes[property.Name] = "dynamic";
            foreach (var m in methods)
                if (m.Returns != null && m.TypeParams.Count == 0 && !AstWalker.ContainsOutsideNestedScopes(m.Body, n => n is YieldExpr))
                    ctx.MethodReturnTypes[m.Name] = AnnotatedCallResult(m);
            foreach (var m in methods)
                if (m.Returns == null && TargetOf(_m, c, m.Name) is InferenceTarget mt && mt.Class == c && !mt.InferParams &&
                    Ti.Returns.TryGetValue(TypeInference.ReturnKey(mt.Key, ""), out var mrt) && mrt != "dynamic")
                    ctx.MethodReturnTypes[m.Name] = mrt;
            // a method decorated in Python returns whatever its decorator's result returns
            foreach (var m in methods) if (_m.Ast.MethodDecorators.ContainsKey(m)) ctx.MethodReturnTypes[m.Name] = "dynamic";
            if (Obs != null && _m.Classes.TryGetValue(c.Name, out var topCls) && topCls == c)
                Obs.MethodReturns[TypeInference.ClassKey(_m, c.Name)] = new Dictionary<string, string>(ctx.MethodReturnTypes);
            bool isDataclass = HasDecorator(c.Decorators, "dataclass");
            var dataFields = new List<AnnAssignStmt>();
            var executableStatements = ExecutableClassStatements(c);
            foreach (var method in c.Body.Where(executableStatements.Contains).SelectMany(ClassBodyMethods))
                ctx.ClassBodyImplementations.Add(method, "__typhon_branch_" + NewTmp("method"));
            EmitLateClassSlots(c, ctx, executableStatements, declared);
            var overwritten = new HashSet<string>(c.Body.OfType<AssignStmt>()
                .Where(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr)
                .GroupBy(a => ((NameExpr)a.Targets[0]).Id)
                .Where(group => group.Count() > 1).Select(group => group.Key));
            overwritten.UnionWith(executableStatements.SelectMany(NamesInClassStatement));

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
                        EmitAnnotatedAttributes(a.Annotation);
                        var fieldSpec = isDataclass && !isClassVar ? DataclassField(a) : null;
                        string init = fieldSpec != null ? (fieldSpec.Default != null ? " = " + Checked(EmitExpr(fieldSpec.Default, targetTyped: true)) + ";"
                                                           : fieldSpec.Factory != null ? " = " + FactoryCall(fieldSpec.Factory, type) + ";" : "")
                            : a.Value != null ? " = " + Checked(EmitExpr(a.Value, targetTyped: true)) + ";" : "";
                        if (isClassVar) _w.Line($"{SurfaceVisibility(Visibility(n.Id, false), type)} {GdStaticKeyword(c, n.Id, a.Value, type)} {type} {Esc(n.Id)}{(init.Length > 0 ? init : ";")}");
                        else
                        {
                            if (_m.IsGdScript) GdFieldAttributes(c, n.Id, type);
                            if (fieldSpec?.Repr == false) _w.Line("[global::Typhon.Runtime.PyDataclassReprExcluded]");
                            _w.Line(InstanceStorage(n.Id, type, Visibility(n.Id, false), init));
                            if (isDataclass) dataFields.Add(a);
                        }
                        break;
                    }
                    case AssignStmt a when a.Targets.Count == 1 && a.Targets[0] is NameExpr n:
                    {
                        bool overwrittenField = overwritten.Contains(n.Id) ||
                            ctx.FieldTypes.TryGetValue(n.Id, out var priorType) && priorType == "dynamic" && declared.Contains(n.Id);
                        if (overwrittenField && declared.Contains(n.Id))
                        {
                            // Another Python binding of the same class variable. Evaluate it here, in class body order.
                            LineDirective(s);
                            string rebound = CaptureClassMember(ctx, n.Id, "dynamic", ClassFieldValue(a.Value, "dynamic"));
                            _w.Line($"static readonly object? {NewTmp("class_assign")} = {Checked($"{Esc(n.Id)} = {rebound}")};");
                            break;
                        }
                        // NAME = None, assigned a value later from outside (Cls.NAME = Cls(...)): the type of those values
                        string later = a.Value is ConstExpr cn0 && cn0.Kind == ConstKind.None ? ExternalType(TypeInference.ClassKey(_m, c.Name) + "|static|" + n.Id) : null;
                        if (later == TypeInference.Pending || later == "dynamic") later = null;
                        if (later != null && IsValueTypeName(later) && !later.EndsWith("?")) later += "?";
                        string type = overwrittenField ? "dynamic" : later ?? PyType(InferType(a.Value)) ?? ImplicitDynamic(s, $"class variable '{n.Id}'");
                        declared.Add(n.Id);
                        ctx.Members.Add(n.Id);
                        ctx.FieldTypes[n.Id] = type; // read as self.name too (through the class)
                        LineDirective(s);
                        string value = CaptureClassMember(ctx, n.Id, type, ClassFieldValue(a.Value, type));
                        _w.Line($"{SurfaceVisibility(Visibility(n.Id, false), type)} {GdStaticKeyword(c, n.Id, a.Value, type)} {type} {Esc(n.Id)} = {Checked(value)};");
                        break;
                    }
                    case FunctionDef f when !f.UsedInClassBody:
                        ctx.Members.Add(f.Name);
                        if (metaclassBody) PublishCompiledClassMember(ctx, f.Name);
                        break;
                    case ClassDef _: break;
                    case ExprStmt e when e.Value is ConstExpr: break; // docstring / ...
                    case PassStmt _: break;
                    // del helper names after using them to build class attributes (del word_punct, letter): they stay fields
                    case DelStmt ds when ds.Targets.All(t => t is NameExpr): break;
                    case Stmt executable when executableStatements.Contains(executable):
                        EmitClassBodyStatement(executable, ctx, declared);
                        if (metaclassBody)
                            foreach (var name in NamesInClassStatement(executable).Distinct()) PublishCompiledClassMember(ctx, name);
                        break;
                    default: Unsupported(s, "this statement in a class body"); break;
                }
            }

            // 2. attributes created via self.x = ... in methods
            var (reflectiveFields, arbitraryReflectiveFields) = ReflectedInstanceWrites(methods);
            foreach (var kv in CollectImplicitFields(c))
            {
                string name = kv.Key;
                if (declared.Contains(name) || ctx.Members.Contains(name)) continue;
                if (BaseHasMember(bases, name, requireOverridable: false)) continue;
                var ann = kv.Value.Key; var method = kv.Value.Value.Key; var value = kv.Value.Value.Value;
                if (ann == null) ctx.InferredFieldInitializers[name] = value;
                string type;
                if (ann != null) type = MapType(ann, method);
                else if (arbitraryReflectiveFields || reflectiveFields.Contains(name)) type = "dynamic";
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
                }
                // An object() sentinel can share a Python field with a value of another type.
                if (ann == null && type == "object") type = "dynamic";
                // A subclass or an external assignment can store a different type in an inferred field.
                if (ann == null && type != "dynamic" && Ti != null && _m.Classes.TryGetValue(c.Name, out var wcls) && wcls == c &&
                    Ti.ExternalFields.TryGetValue(TypeInference.ClassKey(_m, c.Name) + "|" + name, out var outside) && outside != type)
                {
                    if (outside.TrimEnd('?') == type.TrimEnd('?'))
                        type = outside.EndsWith("?") ? outside : type;
                    else
                        type = (type == "long" || type == "double") && (outside == "long" || outside == "double")
                            ? "double" : ImplicitDynamic(method, $"attribute 'self.{name}'");
                }
                declared.Add(name);
                ctx.Members.Add(name);
                ctx.FieldTypes[name] = type;
                LineDirective(method);
                _w.Line(InstanceStorage(name, type, Visibility(name, false)));
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
                    _w.Line(isStatic ? $"{SurfaceVisibility("public", type)} static {type} {Esc(name)};" : InstanceStorage(name, type, "public"));
                }
            }
            if (declared.Count > 0) _w.Line();
            if (Obs != null && _m.Classes.TryGetValue(c.Name, out var topLevel) && topLevel == c)
                Obs.Fields[TypeInference.ClassKey(_m, c.Name)] = new Dictionary<string, string>(ctx.FieldTypes);

            // 3. dataclass constructor / repr / equality
            if (isDataclass)
            {
                _w.Line($"public static readonly string[] __dataclass_fields__ = {{ {string.Join(", ", dataFields.Select(a => CsString(((NameExpr)a.Target).Id)))} }};");
                if (!_m.IsGdScript)
                    _w.Line($"internal static readonly global::Typhon.Stdlib.dataclasses._DataclassParams __dataclass_params__ = " +
                        $"new({(DataclassReprEnabled(c) ? "true" : "false")});");
            }
            if (isDataclass && !methods.Any(m => m.Name == "__init__"))
            {
                string FieldName(AnnAssignStmt a) => ((NameExpr)a.Target).Id;
                var initFields = dataFields.Where(a => DataclassField(a)?.Init != false).ToList();
                var ps = initFields.Select(a =>
                {
                    string n = FieldName(a), t = ctx.FieldTypes[n];
                    var spec = DataclassField(a);
                    // a default_factory: the parameter is optional (null), the factory makes each instance its own value
                    if (spec?.Factory != null) return $"{NullableOf(t)} {Esc(n)} = null";
                    var dv = spec != null ? spec.Default : a.Value;
                    return $"{t} {Esc(n)}" + (dv != null ? " = " + EmitExpr(dv, targetTyped: true) : "");
                }).ToArray();
                _w.Line($"{SurfaceVisibility("public", ps)} {Esc(c.Name)}({string.Join(", ", ps)})");
                _w.Open();
                foreach (var a in initFields)
                {
                    var n = Esc(FieldName(a));
                    var spec = DataclassField(a);
                    _w.Line(spec?.Factory != null ? $"this.{n} = {n} ?? {FactoryCall(spec.Factory, ctx.FieldTypes[FieldName(a)])};" : $"this.{n} = {n};");
                }
                if (methods.Any(m => m.Name == "__post_init__")) _w.Line("this.__post_init__();");
                _w.Close();
                if (initFields.Count > 0) _w.Line($"public {Esc(c.Name)}() {{ }}");
                if (_m.IsGdScript && !methods.Any(m => m.Name == "__repr__" || m.Name == "__str__"))
                {
                    var parts = dataFields.Where(a => DataclassField(a)?.Repr != false).Select(a => { var n = FieldName(a); return $"{n}={{repr(this.{Esc(n)})}}"; });
                    _w.Line($"public override string ToString() => $\"{c.Name}({string.Join(", ", parts)})\";");
                }
                if (!methods.Any(m => m.Name == "__eq__"))
                {
                    var compared = dataFields.Where(a => DataclassField(a)?.Compare != false).ToList();
                    var cmp = compared.Count == 0 ? "true" : string.Join(" && ", compared.Select(a => { var n = Esc(FieldName(a)); return $"{Rt("eq")}((object?)this.{n}, (object?)__o.{n})"; }));
                    _w.Line($"public override bool Equals(object? obj) => obj is {ctx.CsSelfType} __o && {cmp};");
                    _w.Line($"public static bool operator ==({ctx.CsSelfType}? a, {ctx.CsSelfType}? b) => a is null ? b is null : a.Equals(b);");
                    _w.Line($"public static bool operator !=({ctx.CsSelfType}? a, {ctx.CsSelfType}? b) => !(a == b);");
                    var hashFields = dataFields.Take(8).Select(a => "this." + Esc(((NameExpr)a.Target).Id));
                    _w.Line(dataFields.Count == 0 ? "public override int GetHashCode() => 0;" : $"public override int GetHashCode() => HashCode.Combine({string.Join(", ", hashFields)});");
                }
                _w.Line();
            }

            if (GeneratesDataclassRepr(c))
            {
                const string implementation = "__typhon_dataclass_repr_body";
                var parts = dataFields.Where(field => DataclassField(field)?.Repr != false).Select(field =>
                {
                    string name = ((NameExpr)field.Target).Id;
                    return $"{name}={{({Rt("repr")}(this.{Esc(name)}))}}";
                });
                string code = "$\"" + "{(" + Rt("PyTypeName") + "(GetType()))}(" + string.Join(", ", parts) + ")\"";
                _w.Line($"private string {implementation}() => {code};");
                EmitClassSource(new List<Stmt>(), c.Line);
                _w.Line($"[global::Typhon.Runtime.PyDataclassRepr(nameof({implementation}))]");
                _w.Line($"public string __repr__() => {Rt("__dataclass_repr")}(this, {implementation});");
                if (!methods.Any(method => method.Name == "__str__"))
                    _w.Line("public override string ToString() => __repr__();");
            }

            // 4. methods (properties are grouped by name)
            var propertyDone = new HashSet<string>();
            foreach (var f in methods)
            {
                if (!_m.IsGdScript && IsUserMetaclass(c) && IsMetaclassHook(f) && f.Params.Count > 0)
                {
                    EmitMethod(StaticMetaclassHook(f), ctx, bases);
                    continue;
                }
                if (f.Name == "__init__")
                {
                    if (!_m.IsGdScript && !ctx.IsInterface) EmitSignatureProvider(f);
                    EmitConstructor(f, ctx, bases);
                    _w.Line();
                    continue;
                }
                if (IsCachedProperty(f)) { EmitCachedProperty(f, ctx); _w.Line(); continue; }
                bool isProp = HasDecorator(f.Decorators, "property") || f.Decorators.Any(d => DecoratorName(d)?.EndsWith(".setter") == true);
                if (isProp)
                {
                    if (propertyDone.Add(f.Name)) { EmitProperty(methods.Where(m => m.Name == f.Name).ToList(), ctx, bases); _w.Line(); }
                    continue;
                }
                EmitMethod(f.Name == "__new__" && UsesCustomAllocation(c) ? StaticAllocator(f) : f, ctx, bases);
                _w.Line();
            }
            foreach (var implementation in ctx.ClassBodyImplementations)
                EmitMethod(ClassBodyImplementation(implementation.Key, implementation.Value), ctx, bases);
            if (!isDataclass && !IsUserMetaclass(c) && !methods.Any(m => m.Name == "__init__")) EmitInheritedConstructors(ctx, bases);
            EmitDunderBridges(methods, ctx);
            if (c.NamedTupleFields != null) EmitNamedTupleMembers(c, ctx, methods);

            // 5. nested classes
            foreach (var nested in c.Body.OfType<ClassDef>()) EmitClass(nested);
        }

        bool _classFieldInitializer;

        bool ClassBodyMethod(NameExpr name) => _classFieldInitializer && _cls != null &&
            _cls.Members.Contains(name.Id) && !_cls.FieldTypes.ContainsKey(name.Id) &&
            _cls.Def.Body.OfType<FunctionDef>().Any(method => method.Name == name.Id);

        string ClassFieldValue(Expr value, string type)
        {
            bool previous = _classFieldInitializer;
            _classFieldInitializer = true;
            try
            {
                return type != "dynamic" ? EmitExpr(value, targetTyped: true) : BoundMethodValue(value) ?? EmitValue(value);
            }
            finally { _classFieldInitializer = previous; }
        }

        (HashSet<string> Names, bool Arbitrary) ReflectedInstanceWrites(List<FunctionDef> methods)
        {
            var names = new HashSet<string>();
            bool arbitrary = false;
            if (IsShadowed("setattr")) return (names, false);
            foreach (var method in methods)
            {
                if (arbitrary || method.Params.Count == 0 || HasDecorator(method.Decorators, "staticmethod") ||
                    HasDecorator(method.Decorators, "classmethod")) continue;
                string self = method.Params[0].Name;
                AstWalker.VisitAll(method.Body, node =>
                {
                    if (node is not CallExpr { Func: NameExpr { Id: "setattr" }, Args.Count: 3 } call ||
                        call.Args[0].Value is not NameExpr instance || instance.Id != self) return;
                    if (call.Args[1].Value is ConstExpr { Kind: ConstKind.Str } literal) names.Add(literal.Str);
                    else arbitrary = true;
                });
            }
            return (names, arbitrary);
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
                if (explicitOverride || BaseHasMember(bases, f.Name, requireOverridable: true))
                {
                    mods.Add("override");
                    if (DotNetOverrideVisibility(bases, f.Name) is string access) mods[0] = access; // C# keeps the overridden member's access
                }
                else if (!ctx.IsSealed && !HasDecorator(f.Decorators, "final")) mods.Add("virtual");
                if (HasDecorator(f.Decorators, "final") && mods.Contains("override")) mods.Add("sealed");
            }
            if (f.IsAsync && !isAbstract) mods.Add("async");
            return string.Join(" ", mods);
        }

        void EmitMethod(FunctionDef f, ClassContext ctx, List<Expr> bases)
        {
            bool isStaticDecl = HasDecorator(f.Decorators, "staticmethod");
            bool isClassMethod = HasDecorator(f.Decorators, "classmethod");
            if (!isStaticDecl && f.Params.Count == 0) { Error(f, $"method '{f.Name}' needs a 'self' parameter (or use @staticmethod)"); return; }
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            try
            {
                if (!_m.IsGdScript && !ctx.IsInterface && !_m.Ast.MethodDecorators.ContainsKey(f)) EmitSignatureProvider(f);
                EmitMethodCore(f, ctx, bases, isStaticDecl, isClassMethod);
            }
            finally { foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name); }
        }

        void EmitMethodCore(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod)
        {
            var target = TargetOf(_m, ctx.Def, f.Name);
            // parameters inferred from the calls: an overload per signature, and always the late-bound version too (calls the
            // compiler cannot see: late-bound receivers, bound methods passed around, getattr)
            if (target != null && target.Class == ctx.Def && target.InferParams && !isStaticDecl && !isClassMethod)
            {
                Ti.Signatures.TryGetValue(target.Key, out var sigs);
                if (sigs != null)
                    foreach (var types in sigs)
                    {
                        EmitMethodVariant(f, ctx, bases, types, TypeInference.ReturnKey(target.Key, TypeInference.SigKey(types)), null);
                        _w.Line();
                    }
                var savedQuiet = _quietDynamic;
                _quietDynamic = sigs != null && sigs.Count > 0; // (typed overloads exist: the late-bound one is the extra)
                try { EmitMethodVariant(f, ctx, bases, null, null, target.Key); }
                finally { _quietDynamic = savedQuiet; }
                return;
            }
            string retKey = target != null && target.Class == ctx.Def && !target.InferParams && !BaseHasMember(bases, f.Name, requireOverridable: false)
                ? TypeInference.ReturnKey(target.Key, "") : null;
            var savedRet = _retKey; _retKey = retKey;
            var savedScopeKey = _scopeKey; _scopeKey = TypeInference.FuncKey(_m, ctx.Def, f.Name);
            try { EmitMethodTyped(f, ctx, bases, isStaticDecl, isClassMethod, retKey); }
            finally { _retKey = savedRet; _scopeKey = savedScopeKey; }
        }

        /// <summary>One C# overload of a method whose parameters are inferred: typed by a signature, or late-bound (fallback).</summary>
        void EmitMethodVariant(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes, string retKey, string fallbackKey)
        {
            var savedRet = _retKey; var savedCtx = _ctxFallbackKey; var savedScopeKey = _scopeKey;
            _retKey = retKey; _ctxFallbackKey = fallbackKey;
            if (Obs != null) Obs.InFallbackBody = fallbackKey != null;
            _scopeKey = TypeInference.FuncKey(_m, ctx.Def, f.Name) + "#" + TypeInference.SigKey(paramTypes);
            try { EmitMethodTyped(f, ctx, bases, false, false, retKey, paramTypes); }
            finally
            {
                _retKey = savedRet; _ctxFallbackKey = savedCtx; _scopeKey = savedScopeKey;
                if (Obs != null) Obs.InFallbackBody = savedCtx != null;
            }
        }

        FunctionDef FixedBaseOfExtendedOverride(FunctionDef method, List<Expr> bases)
        {
            if (method.TypeParams.Count != 0 || bases.Count == 0 || !ResolveBase(bases[0], out var owner, out _))
                return default;
            for (int depth = 0; owner != null && depth < 20; depth++)
            {
                var inherited = owner.Body.OfType<FunctionDef>().FirstOrDefault(candidate => candidate.Name == method.Name);
                if (inherited != null)
                    return inherited.TypeParams.Count == 0 && inherited.IsAsync == method.IsAsync && inherited.Params.Count > 0 &&
                        inherited.Params.Skip(1).All(parameter => parameter.Kind == ParamKind.Normal) &&
                        !HasDecorator(inherited.Decorators, "staticmethod") && !HasDecorator(inherited.Decorators, "classmethod") &&
                        !HasDecorator(inherited.Decorators, "property") ? inherited : null;
                if (owner.Bases.Count == 0 || !ResolveBase(owner.Bases[0], out owner, out _)) break;
            }
            return default;
        }

        // Keep the CLR virtual slot when a Python override accepts variadic or extra optional arguments.
        bool EmitExtendedOverride(FunctionDef method, Signature signature, List<Expr> bases, string modifiers)
        {
            bool variadic = method.Params.Count == 2 && method.Params[1].Kind == ParamKind.VarArgs;
            if (!variadic && (method.Params.Count < 2 || method.Params.Last().Default == null ||
                method.Params.Skip(1).Any(parameter => parameter.Kind != ParamKind.Normal))) return false;

            var inherited = FixedBaseOfExtendedOverride(method, bases);
            if (inherited == null) return false;
            var extra = method.Params.Skip(inherited.Params.Count).ToList();
            if (variadic && inherited.Params.Skip(1).Any(parameter => parameter.Default != null)) return false;
            if (!variadic && (extra.Count == 0 || extra.Any(parameter => parameter.Default == null || !IsConstantDefault(parameter.Default))))
                return false;

            var inheritedSignature = BuildSignature(inherited, dropFirst: true);

            string arguments;
            if (variadic)
            {
                var rest = method.Params[1];
                string elementType = rest.Annotation == null ? "object" : MapType(rest.Annotation, rest);
                string items = string.Join(", ", inherited.Params.Skip(1).Select(parameter =>
                    elementType == "object" ||
                    inheritedSignature.Scope.Types.TryGetValue(parameter.Name, out var parameterType) && parameterType == elementType
                        ? Esc(parameter.Name) : $"({elementType})(dynamic){Esc(parameter.Name)}"));
                arguments = $"{Esc(rest.Name)}: new {elementType}[] {{ {items} }}";
            }
            else
            {
                // The existing parameters have the same names, CLR types and defaults in both slots.
                if (inheritedSignature.Params.Length > 0 &&
                    !signature.Params.StartsWith(inheritedSignature.Params + ", ", StringComparison.Ordinal)) return false;
                arguments = string.Join(", ", inherited.Params.Skip(1).Select(parameter => Esc(parameter.Name))
                    .Concat(extra.Select(parameter => $"{Esc(parameter.Name)}: {EmitExpr(parameter.Default, targetTyped: true)}")));
            }
            string call = $"this.{Esc(method.Name)}({arguments})";
            bool awaitResult = method.IsAsync && !signature.IsGenerator && !inheritedSignature.IsGenerator &&
                signature.ReturnType != inheritedSignature.ReturnType;
            bool slotVoid = inheritedSignature.ReturnType == "void" || awaitResult && inheritedSignature.ReturnType == "Task";
            bool implementationVoid = signature.ReturnType == "void" || awaitResult && signature.ReturnType == "Task";
            string bridgeModifiers = modifiers.Replace(" async", "") + (awaitResult ? " async" : "");
            string bridge = $"{bridgeModifiers} {inheritedSignature.ReturnType} {Esc(method.Name)}({inheritedSignature.Params})";
            string result = awaitResult ? "await " + call : call;
            string resultType = awaitResult && !slotVoid ? GenericArg(inheritedSignature.ReturnType) : inheritedSignature.ReturnType;
            LineDirective(method);
            _w.Line("[global::Typhon.Runtime.PyOverrideBridge]");
            if (slotVoid) _w.Line($"{bridge} {{ {result}; }}");
            else if (implementationVoid) _w.Line($"{bridge} {{ {result}; return default!; }}");
            else if (signature.ReturnType == inheritedSignature.ReturnType) _w.Line($"{bridge} => {result};");
            else _w.Line($"{bridge} => ({resultType})(dynamic)({result});");
            _w.Line();
            return true;
        }

        void EmitMethodTyped(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod, string retKey, string[] paramTypes = null)
        {
            var sig = BuildSignature(f, dropFirst: !isStaticDecl, paramTypes: paramTypes, retKey: retKey);
            if (isStaticDecl && (f.Name == "__new__" && UsesCustomAllocation(ctx.Def) ||
                !_m.IsGdScript && IsUserMetaclass(ctx.Def) && IsMetaclassHook(f)))
                sig.Scope.ClassArgument = f.Params[0].Name;
            string mods = MethodModifiers(f, ctx, bases, out bool isStatic, out bool isAbstract);
            if (!_m.IsGdScript && !isAbstract && !isStatic && mods.Contains(" override") &&
                EmitExtendedOverride(f, sig, bases, mods))
                mods = mods.Replace(" sealed", "").Replace(" override",
                    ctx.IsSealed || HasDecorator(f.Decorators, "final") ? "" : " virtual");
            if (!isStatic) { sig.Scope.HasSelf = true; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (isClassMethod)
            {
                sig.Scope.ClassArgument = f.Params[0].Name;
                sig.Scope.Params.Add(sig.Scope.ClassArgument);
                sig.Scope.Types[sig.Scope.ClassArgument] = "global::System.Type";
            }
            if (sig.IsAsync && sig.IsGenerator) mods = mods.Replace(" async", "");

            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            // (an override of a runtime class's public method stays public: the class is internal anyway, and an override may not
            // change the base's access)
            if (mods.StartsWith("public ") && SurfaceVisibility("public", sig.ReturnType, sig.Params) == "internal" &&
                !(mods.Contains(" override") && OverridesRuntimeMember(ctx.Def, f.Name)))
                mods = "internal " + mods.Substring("public ".Length);
            // (a _method is protected internal: with an internal type in its signature, internal, unless it overrides)
            else if (mods.StartsWith("protected internal ") && !mods.Contains(" override") && SurfaceVisibility("public", sig.ReturnType, sig.Params) == "internal")
                mods = "internal " + mods.Substring("protected internal ".Length);
            bool decorated = _m.Ast.MethodDecorators.TryGetValue(f, out var decorators);
            if (decorated) { mods = isStatic ? "internal static" : "internal"; if (f.IsAsync && !sig.IsGenerator) mods += " async"; }
            string methodName = decorated ? Decorators.UndecoratedPrefix + f.Name : f.Name;
            string classMethodBody = isClassMethod ? "__typhon_classmethod_" + methodName : null;
            if (!_m.IsGdScript && !ctx.IsInterface && !decorated)
            {
                EmitClassSource(f.Body, f.Line, CallableKind(f, sig));
                EmitSignatureAttribute(f);
                if (isStatic) EmitDescriptorAttribute(isClassMethod ? "ClassMethod" : "StaticMethod", classMethodBody);
            }
            string generic = TypeParamList(f.TypeParams), constraints = TypeParamConstraints(f.TypeParams, f);
            string header = $"{(isClassMethod ? mods.Replace("async ", "") : mods)} {sig.ReturnType} {Esc(methodName)}{generic}({sig.Params}){constraints}";
            if (isAbstract) { _w.Line(header + ";"); return; }
            if (isClassMethod)
            {
                var arguments = f.Params.Skip(1).Select(p => sig.Scope.Aliases.ContainsKey(p.Name) ? "__alias_" + p.Name : Esc(p.Name));
                string forwarded = string.Join(", ", new[] { $"typeof({ctx.CsSelfType})" }.Concat(arguments));
                _w.Line($"{header} => {Esc(classMethodBody)}{generic}({forwarded});");
                string parameters = "global::System.Type " + Esc(sig.Scope.ClassArgument) + (sig.Params.Length == 0 ? "" : ", " + sig.Params);
                _w.Line($"private static {(f.IsAsync && !sig.IsGenerator ? "async " : "")}{sig.ReturnType} {Esc(classMethodBody)}{generic}({parameters}){constraints}");
            }
            else _w.Line(header);
            EmitSignatureBody(f.Body, sig);
            if (decorated) EmitDecoratedMethod(f, ctx, sig, decorators, isStatic);
        }

        /// <summary>
        /// A method decorated in Python: the class keeps the decorators' result (applied to the undecorated method once, when
        /// the class is first used), and the method under its own name calls that result with self and its arguments, so
        /// obj.m(x) behaves as in Python (the decorator's wrapper gets self first) and C# callers still see a method.
        /// </summary>
        void EmitDecoratedMethod(FunctionDef f, ClassContext ctx, Signature sig, List<Expr> decorators, bool isStatic)
        {
            string field = EmitMethodDecoratorField(f, ctx, sig, decorators, isStatic);
            // the method under its own name: Python's parameters, each any value; an optional one left out is not passed
            var ps = new List<string>();
            var values = new List<string>();
            var names = new List<string>();
            string rest = "null", kw = "null";
            bool hasKw = f.Params.Any(p => p.Kind == ParamKind.KwArgs);
            foreach (var p in f.Params.Skip(isStatic ? 0 : 1))
                switch (p.Kind)
                {
                    case ParamKind.Normal:
                        ps.Add(p.Default == null ? $"object? {Esc(p.Name)}" : $"[global::System.Runtime.InteropServices.Optional] object? {Esc(p.Name)}");
                        values.Add($"{Esc(p.Name)}");
                        names.Add(CsString(p.Name));
                        break;
                    case ParamKind.VarArgs:
                        ps.Add(hasKw ? $"object?[]? {Esc(p.Name)} = null" : $"params object?[] {Esc(p.Name)}");
                        rest = Esc(p.Name);
                        break;
                    case ParamKind.KwArgs:
                        ps.Add($"OrderedDictionary<string, dynamic>? {Esc(p.Name)} = null");
                        kw = Esc(p.Name);
                        break;
                }
            string leading = isStatic ? "[]" : "[this]";
            string mods = isStatic ? "public static" : "public";
            _w.Line($"{mods} dynamic {Esc(f.Name)}({string.Join(", ", ps)}) => {Rt("__forward")}((object?){field}, {leading}, [{string.Join(", ", values)}], [{string.Join(", ", names)}], {rest}, {kw});");
        }

        string EmitMethodDecoratorField(FunctionDef f, ClassContext ctx, Signature sig, List<Expr> decorators, bool isStatic)
        {
            string field = "__decorated_" + f.Name;
            string value = ClosureCode(f, sig, Esc(Decorators.UndecoratedPrefix + f.Name), isStatic ? null : ctx.CsSelfType);
            for (int k = decorators.Count - 1; k >= 0; k--)
                value = $"{Rt("__invoke")}((object?){Paren(EmitDecoratorCallee(decorators[k]))}, [(object?){value}])";
            _w.Line($"internal static readonly dynamic {field} = {value};");
            return field;
        }

        /// <summary>A decorator expression as a callee value (a module function as a function value).</summary>
        string EmitDecoratorCallee(Expr d) => d is NameExpr dn && FunctionValue(dn, typed: false) is string fv ? fv : EmitExpr(d);

        static string CallableKind(FunctionDef function, Signature sig) => function.IsAsync
            ? (sig.IsGenerator ? "AsyncGenerator" : "Coroutine") : sig.IsGenerator ? "Generator" : "Ordinary";

        void EmitClassSource(List<Stmt> body, int line, string kind = "Ordinary")
        {
            if (_m.IsGdScript) return;
            string doc = body.FirstOrDefault() is ExprStmt statement && statement.Value is ConstExpr { Kind: ConstKind.Str } text
                ? CsString(text.Str) : "null";
            _w.Line($"[global::Typhon.Runtime.PySource({CsString(_path)}, {CsString(PythonModuleName)}, global::Typhon.Runtime.PyCallableKind.{kind}, doc: {doc}, line: {line})]");
        }

        void EmitDescriptorAttribute(string kind, string getter = null)
        {
            if (_m.IsGdScript) return;
            string argument = getter == null ? "" : ", " + CsString(getter);
            _w.Line($"[global::Typhon.Runtime.PyDescriptor(global::Typhon.Runtime.PyDescriptorKind.{kind}{argument})]");
        }

        bool IsCachedProperty(FunctionDef function) => !_m.IsGdScript && function.IsCachedProperty;

        void EmitCachedProperty(FunctionDef f, ClassContext ctx)
        {
            if (f.Params.Count == 0 || ctx.IsInterface)
            {
                Error(f, "cached_property needs an instance method");
                return;
            }
            var sig = BuildSignature(f, dropFirst: true);
            sig.Scope.HasSelf = true;
            sig.Scope.Params.Remove(f.Params[0].Name);
            string name = CsString(f.Name);
            string getter = "__cached_property_" + f.Name;
            EmitSignatureProvider(f);
            LineDirective(f);
            EmitSignatureAttribute(f);
            _w.Line($"private {(f.IsAsync && !sig.IsGenerator ? "async " : "")}{sig.ReturnType} {Esc(getter)}({sig.Params})");
            EmitSignatureBody(f.Body, sig);
            EmitDocComment(f.Body);
            LineDirective(f);
            EmitClassSource(f.Body, f.Line, CallableKind(f, sig));
            EmitDescriptorAttribute("CachedProperty", getter);
            EmitAttributes(f.Decorators.Where(d => !Decorators.IsCachedProperty(d, _m.Ast)).ToList());
            _w.Line($"{Visibility(f.Name, false)} dynamic {Esc(f.Name)}");
            _w.Open();
            _w.Line($"get => {Rt("__cached_property_get__")}(this, typeof({ctx.CsSelfType}), {name});");
            _w.Line($"set => {Rt("__cached_property_set__")}(this, {name}, (object?)value);");
            _w.Close();
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
            bool emitSignature = !_m.IsGdScript && !ctx.IsInterface && !isAbstract;
            string setterProvider = setter != null ? SignatureProviderName(setter) + "_setter" : null;
            if (emitSignature)
            {
                EmitSignatureProvider(getter);
                if (setter != null) EmitSignatureProvider(setter, setterProvider);
            }
            string decoratedGetter = null;
            if (_m.Ast.MethodDecorators.TryGetValue(getter, out var decorators))
            {
                if (ctx.IsInterface || isAbstract)
                {
                    Error(getter, "a decorated property needs a concrete getter");
                    return;
                }
                LineDirective(getter);
                string implementation = Esc(Decorators.UndecoratedPrefix + getter.Name);
                _w.Line($"private {gsig.ReturnType} {implementation}({gsig.Params})");
                EmitSignatureBody(getter.Body, gsig);
                decoratedGetter = EmitMethodDecoratorField(getter, ctx, gsig, decorators, isStatic: false);
            }
            EmitDocComment(getter.Body);
            LineDirective(getter);
            if (!ctx.IsInterface)
            {
                EmitClassSource(getter.Body, getter.Line);
                EmitDescriptorAttribute("Property");
            }
            EmitAttributes(getter.Decorators);
            string propertyType = decoratedGetter != null && !mods.Contains(" override") ? "dynamic" : gsig.ReturnType;
            _w.Line($"{mods} {propertyType} {Esc(getter.Name)}");
            _w.Open();
            if (isAbstract) { _w.Line(setter != null ? "get; set;" : "get;"); _w.Close(); return; }
            if (emitSignature) EmitSignatureAttribute(getter);
            if (decoratedGetter == null)
            {
                _w.Line("get");
                EmitFunctionBody(getter.Body, gsig.Scope, null, gsig.Locals);
            }
            else
                _w.Line($"get => ({propertyType}){Rt("__invoke")}((object?){decoratedGetter}, [this]);");
            if (setter != null)
            {
                var ssig = BuildSignature(setter, dropFirst: true);
                ssig.Scope.HasSelf = true;
                ssig.Scope.ReturnType = "void"; // (a setter returns nothing, whatever the getter of the same name returns)
                ssig.Scope.Params.Remove(setter.Params[0].Name);
                string valueName = setter.Params.Count > 1 ? setter.Params[1].Name : "value";
                LineDirective(setter);
                if (!ctx.IsInterface) EmitClassSource(setter.Body, setter.Line);
                if (emitSignature) EmitSignatureAttribute(setter, setterProvider);
                _w.Line("set");
                EmitFunctionBody(setter.Body, ssig.Scope, null, ssig.Locals,
                    prologue: valueName != "value" ? $"var {Esc(valueName)} = value;" : null);
            }
            _w.Close();
        }
    }
}
