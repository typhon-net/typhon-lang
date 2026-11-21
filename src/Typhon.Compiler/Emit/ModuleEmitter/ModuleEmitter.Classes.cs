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
            // def __lt__(self, other) with other of any type: IComparable, so sorting compares through __lt__ too
            else if (!isInterface && LtComparesAny(c)) baseTypes.Add("global::System.IComparable");
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
            if (factory is LambdaExpr lf && lf.Params.Count == 0) return Checked(EmitExpr(lf.Body, targetTyped: true));
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
            // a method decorated in Python returns whatever its decorator's result returns
            foreach (var m in methods) if (_m.Ast.MethodDecorators.ContainsKey(m)) ctx.MethodReturnTypes[m.Name] = "dynamic";
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
                        var fieldSpec = isDataclass && !isClassVar ? DataclassField(a) : null;
                        string init = fieldSpec != null ? (fieldSpec.Default != null ? " = " + Checked(EmitExpr(fieldSpec.Default, targetTyped: true)) + ";"
                                                           : fieldSpec.Factory != null ? " = " + FactoryCall(fieldSpec.Factory, type) + ";" : "")
                            : a.Value != null ? " = " + Checked(EmitExpr(a.Value, targetTyped: true)) + ";" : "";
                        if (isClassVar) _w.Line($"{SurfaceVisibility(Visibility(n.Id, false), type)} static {type} {Esc(n.Id)}{(init.Length > 0 ? init : ";")}");
                        else
                        {
                            _w.Line($"{SurfaceVisibility(Visibility(n.Id, false), type)} {type} {Esc(n.Id)} {{ get; set; }}{init}");
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
                        _w.Line($"{SurfaceVisibility(Visibility(n.Id, false), type)} static {type} {Esc(n.Id)} = {Checked(type != "dynamic" ? EmitExpr(a.Value, targetTyped: true) : EmitValue(a.Value))};");
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
            if (isDataclass)
                _w.Line($"public static readonly string[] __dataclass_fields__ = {{ {string.Join(", ", dataFields.Select(a => CsString(((NameExpr)a.Target).Id)))} }};");
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
                if (!methods.Any(m => m.Name == "__repr__" || m.Name == "__str__"))
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

        void EmitMethodTyped(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod, string retKey, string[] paramTypes = null)
        {
            var sig = BuildSignature(f, dropFirst: !isStaticDecl, paramTypes: paramTypes, retKey: retKey);
            string mods = MethodModifiers(f, ctx, bases, out bool isStatic, out bool isAbstract);
            if (!isStatic) { sig.Scope.HasSelf = true; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (isClassMethod) { sig.Scope.ClsName = ctx.CsSelfType; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (f.IsAsync && sig.IsGenerator) mods = mods.Replace("async", "async");

            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            if (mods.StartsWith("public ") && SurfaceVisibility("public", sig.ReturnType, sig.Params) == "internal") mods = "internal " + mods.Substring("public ".Length);
            bool decorated = _m.Ast.MethodDecorators.TryGetValue(f, out var decorators);
            if (decorated) { mods = isStatic ? "internal static" : "internal"; if (f.IsAsync) mods += " async"; }
            string methodName = decorated ? Decorators.UndecoratedPrefix + f.Name : f.Name;
            string header = $"{mods} {sig.ReturnType} {Esc(methodName)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}";
            if (isAbstract) { _w.Line(header + ";"); return; }
            _w.Line(header);
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
            string field = "__decorated_" + f.Name;
            string undecorated = ClosureCode(f, sig, Esc(Decorators.UndecoratedPrefix + f.Name), isStatic ? null : ctx.CsSelfType);
            string value = undecorated;
            for (int k = decorators.Count - 1; k >= 0; k--)
                value = $"{Rt("__invoke")}((object?){Paren(EmitDecoratorCallee(decorators[k]))}, [(object?){value}])";
            _w.Line($"internal static readonly dynamic {field} = {value};");
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

        /// <summary>A decorator expression as a callee value (a module function as a function value).</summary>
        string EmitDecoratorCallee(Expr d) => d is NameExpr dn && FunctionValue(dn, typed: false) is string fv ? fv : EmitExpr(d);

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
    }
}
