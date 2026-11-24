// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        bool BuiltinTypeBase(Expr expression) =>
            DottedName(expression) == "builtins.type" ||
            expression is NameExpr { Id: "type" } && !IsShadowed("type");

        bool IsBuiltinClassCreation(CallExpr call) => BuiltinTypeBase(call.Func) &&
            call.Args.Count(argument => argument.Name == null && !argument.DoubleStar) == 3;

        bool IsUserMetaclass(ClassDef definition) => IsUserMetaclass(definition, new HashSet<ClassDef>());

        bool IsUserMetaclass(ClassDef definition, HashSet<ClassDef> seen)
        {
            if (definition == null || !seen.Add(definition)) return false;
            var resolver = ClassDeclarationResolver(definition);
            return (definition.BasesBeforeFlattening ?? definition.Bases).Any(b => resolver.BuiltinTypeBase(b) ||
                resolver.ResolveBase(b, out var parent, out _) && IsUserMetaclass(parent, seen));
        }

        bool IsMetaclassExpression(Expr expression) =>
            DottedName(expression) is string name && TryResolveType(name, 0, out _, out var definition) &&
            IsUserMetaclass(definition);

        bool HasUserMetaclass(ClassDef definition) =>
            !_m.IsGdScript && HasUserMetaclass(definition, new HashSet<ClassDef>());

        bool HasUserMetaclass(ClassDef definition, HashSet<ClassDef> seen, string member = null)
        {
            if (definition == null || !seen.Add(definition)) return false;
            var resolver = ClassDeclarationResolver(definition);
            return DottedName(definition.Metaclass) is string name &&
                resolver.TryResolveType(name, 0, out _, out var meta) && IsUserMetaclass(meta) &&
                (member == null || resolver.DeclaresMember(meta, member)) ||
                (definition.BasesBeforeFlattening ?? definition.Bases).Any(b =>
                    resolver.ResolveBase(b, out var parent, out _) && HasUserMetaclass(parent, seen, member));
        }

        bool MetaclassMember(AttributeExpr member, out string type)
        {
            type = null;
            if (_m.IsGdScript || DottedName(member.Target) is not string name ||
                !TryResolveType(name, 0, out _, out var definition) || !HasUserMetaclass(definition) ||
                !TryTypeExpr(member.Target, out string className)) return false;
            type = ClassTypeValue(className, definition);
            return true;
        }

        bool IsMetaclassInstance(Expr expression, string member = null) => !_m.IsGdScript &&
            DottedName(expression) is string name && TryResolveType(name, 0, out _, out var definition) &&
            HasUserMetaclass(definition, new HashSet<ClassDef>(), member);

        bool RuntimeClassConstruction(Expr expression) =>
            expression is CallExpr call && IsRuntimeTypeValue(call.Func) && !TryTypeExpr(call.Func, out _) &&
            (call.Func is NameExpr name ? IsLocal(name.Id) || IsShadowed(name.Id) || StdlibOwner(name.Id) == null
                : StdlibModuleOf(call, out _) == null);

        bool RuntimeClassInstance(Expr expression) => !_m.IsGdScript &&
            (RuntimeClassConstruction(expression) ||
                expression is NameExpr name && _scope?.RuntimeClassInstances?.Contains(name.Id) == true);

        HashSet<ClassDef> _runtimeMixinClasses;

        bool NeedsRuntimeMixinBody(ClassContext context, FunctionDef method)
        {
            if (_m.IsGdScript || context.IsInterface || context.Def.TypeParams.Count != 0 ||
                method.Params.Count == 0 || method.TypeParams.Count != 0 || method.Decorators.Count != 0 ||
                method.Name is "__init__" or "__new__" || HasStringBase(context.Def) || IsUserMetaclass(context.Def))
                return false;
            if (_runtimeMixinClasses == null)
            {
                _runtimeMixinClasses = new HashSet<ClassDef>();
                bool createsDynamicBases = false;
                AstWalker.VisitAll(_m.Ast.Body, node =>
                {
                    if (node is CallExpr { Func: NameExpr { Id: "type" }, Args.Count: >= 3 } call &&
                        call.Args[1].Value is TupleExpr { Elts.Count: >= 2 })
                        createsDynamicBases = true;
                });
                if (createsDynamicBases)
                {
                    AstWalker.VisitAll(_m.Ast.Body, node =>
                    {
                        if (node is NameExpr name && !Receivers.Contains(name) && _m.Classes.TryGetValue(name.Id, out var definition))
                            _runtimeMixinClasses.Add(definition);
                    });

                    var pending = new Stack<ClassDef>(_runtimeMixinClasses);
                    while (pending.Count > 0)
                    {
                        var definition = pending.Pop();
                        var resolver = ClassDeclarationResolver(definition);
                        foreach (var basis in definition.Bases)
                            if (resolver.ResolveBase(basis, out var parent, out _) && parent != null && _runtimeMixinClasses.Add(parent))
                                pending.Push(parent);
                    }
                }
            }
            return _runtimeMixinClasses.Contains(context.Def);
        }

        void EmitRuntimeMixinBody(FunctionDef method, ClassContext context)
        {
            var previous = _cls;
            _cls = null;
            try
            {
                var signature = BuildSignature(method, dropFirst: false);
                signature.Scope.LocalClassOwner = $"typeof({context.CsSelfType})";
                signature.Scope.LocalClassReceiver = method.Params[0].Name;
                _w.Line($"[global::Typhon.Runtime.PyRuntimeMixinMethod({CsString(method.Name)})]");
                string asyncModifier = method.IsAsync && !signature.IsGenerator ? "async " : "";
                _w.Line($"private static {asyncModifier}{signature.ReturnType} {NewTmp("mixin")}({signature.Params})");
                EmitSignatureBody(method.Body, signature);
            }
            finally { _cls = previous; }
        }

        readonly HashSet<FunctionDef> _metaclassMethods = new();

        static bool HasMetaclassReceiver(FunctionDef method) => method.Params.Count > 0 &&
            (method.Name is "__new__" or "__init__" ||
                !HasDecorator(method.Decorators, "staticmethod") &&
                !HasDecorator(method.Decorators, "classmethod") &&
                !HasDecorator(method.Decorators, "property") &&
                !method.Decorators.Any(d => DecoratorName(d)?.EndsWith(".setter") == true));

        FunctionDef StaticMetaclassMethod(FunctionDef hook, string accessorName = null)
        {
            var receiver = hook.Params[0];
            var parameters = new List<Param>
            {
                new Param
                {
                    Name = receiver.Name, Line = receiver.Line, Col = receiver.Col, Kind = receiver.Kind,
                    PositionalOnly = receiver.PositionalOnly, KeywordOnly = receiver.KeywordOnly,
                    Annotation = new NameExpr { Id = "type", Line = receiver.Line, Col = receiver.Col },
                },
            };
            parameters.AddRange(hook.Params.Skip(1));
            var transformed = new FunctionDef
            {
                Name = accessorName ?? hook.Name, PyName = hook.PyName ?? (accessorName != null ? hook.Name : null),
                Line = hook.Line, Col = hook.Col, Params = parameters,
                Returns = hook.Returns, Body = hook.Body, IsAsync = hook.IsAsync, TypeParams = hook.TypeParams,
                Decorators = hook.Decorators.Where(d => accessorName == null && DecoratorName(d) != "staticmethod")
                    .Append(new NameExpr { Id = "staticmethod" }).ToList(),
            };
            _metaclassMethods.Add(transformed);
            return transformed;
        }

        void EmitMetaclassProperty(FunctionDef getter, FunctionDef setter, ClassContext context, List<Expr> bases)
        {
            string Accessor(FunctionDef accessor, string prefix)
            {
                if (accessor == null) return "null";
                string name = NewTmp(prefix);
                EmitMethod(StaticMetaclassMethod(accessor, name), context, bases);
                return $"new global::Typhon.Runtime.PyBoundMethod(typeof({context.CsSelfType}), {CsString(name)}, " +
                    $"includeNonPublic: true, displayName: {CsString(accessor.Name)})";
            }
            string get = Accessor(getter, "__typhon_metaproperty_get");
            string set = Accessor(setter, "__typhon_metaproperty_set");
            _w.Line($"internal static readonly global::Typhon.Stdlib.builtins.property {Esc(getter.Name)} = " +
                $"new({get}, {set}) {{ __name__ = {CsString(getter.Name)} }};");
        }

        bool CapturesClassModuleName => !_m.IsGdScript && _m.RuntimeCodeMode == null && _m.Globals.Contains("__name__");

        void EmitClassInitialization(ClassDef definition)
        {
            string type = ClassTypeValue(Esc(definition.Name), definition);
            if (CapturesClassModuleName)
                _w.Line($"{Rt("__remember_class_module")}({type}, (object?)__name__);");
            if (!RunsClassBodyAtDefinition(definition))
            {
                if (definition.TypeParams.Count == 0 && (IsStringEnum(definition) || IsObjectIntegerEnum(definition) ||
                    IsPlainObjectEnum(definition) || definition.CapturesNamedTupleFactoryDefaults))
                    _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor({type}.TypeHandle);");
                _w.Line($"{Rt("__register_subclass")}({type});");
                return;
            }
            if (!HasUserMetaclass(definition))
            {
                _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor({type}.TypeHandle);");
                if (RunsSubclassHook(definition))
                {
                    string arguments = definition.ClassKeywords.Count == 0 ? "null" : KeywordArgs(definition.ClassKeywords);
                    _w.Line($"{Rt("__initialize_subclass")}({type}, {arguments});");
                }
                else _w.Line($"{Rt("__register_subclass")}({type});");
                return;
            }
            string metaclass = definition.Metaclass == null ? "null" : $"(global::System.Type){Paren(EmitValue(definition.Metaclass))}";
            string BaseValue(Expr basis) => basis is SubscriptExpr && TryTypeExpr(basis, out var constructedType)
                ? $"typeof({constructedType})" : $"(global::System.Type){Paren(EmitValue(basis))}";
            string bases = string.Join(", ", (definition.BasesBeforeFlattening ?? definition.Bases).Select(BaseValue));
            var names = definition.Body.Where(IsPythonClassBodyMember).SelectMany(statement => statement switch
            {
                FunctionDef method => new[] { method.Name },
                ClassDef nested => new[] { nested.Name },
                _ => NamesInClassStatement(statement),
            }).Distinct();
            string keywords = definition.ClassKeywords.Count == 0 ? "null" : KeywordArgs(definition.ClassKeywords);
            _w.Line($"{Rt("__define_metaclass_type")}({type}, {metaclass}, {CsString(definition.PyName ?? definition.Name)}, " +
                $"{CsString(PythonModuleName)}, [{bases}], [{string.Join(", ", names.Select(CsString))}], {keywords});");
        }

        static bool IsPythonClassBodyMember(Stmt statement) =>
            !statement.IsFlattenedClassMember && statement is not FunctionDef { IsMissingMethodStub: true };

        string CaptureClassMember(ClassContext context, string name, string type, string value, Stmt statement = null) =>
            HasUserMetaclass(context.Def) && (statement == null || IsPythonClassBodyMember(statement))
                ? $"{Rt("__class_namespace_set")}<{type}>(typeof({context.CsSelfType}), {CsString(name)}, {value})"
                : value;

        void PublishCompiledClassMember(ClassContext context, string name)
        {
            _w.Line($"private static readonly object? {NewTmp("__typhon_class_member")} = " +
                $"{Rt("__class_namespace_publish")}(typeof({context.CsSelfType}), {CsString(name)});");
        }

        string EmitPreparedName(string name) =>
            $"{Rt("__class_namespace_read")}(typeof({_cls.CsSelfType}), {CsString(name)}, " +
            $"typeof({ModuleClassName(_m)}), {CsString(PythonModuleName)})";

        static bool IsTypeSlot(string name) => name is "__instancecheck__" or "__subclasscheck__" or "__subclasses__";

        string EmitBuiltinTypeSlot(AttributeExpr member) =>
            $"{Rt("getattr")}(typeof(global::System.Type), {CsString(member.Name)})";

        string SubclassLookupReceiver(Expr receiver) =>
            (receiver is NameExpr builtin ? BuiltinTypeValue(builtin) : null) ??
            ModuleOrClassValue(receiver) ??
            (TryTypeExpr(receiver, out string type) ? $"typeof({type})" : EmitValue(receiver));

        string EmitTypeSlotCall(CallExpr call)
        {
            if (call.Func is not AttributeExpr member) return null;
            if (_scope?.ClassArgument != null && _cls != null &&
                member.Target is CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } && !IsShadowed("super") &&
                (member.Name == "__new__" && (IsUserMetaclass(_cls.Def) || HasStringBase(_cls.Def)) ||
                 (member.Name == "__repr__" || IsTypeSlot(member.Name)) && IsUserMetaclass(_cls.Def)))
                return InvokeLogicalMethod(member, $"(object?){Paren(EmitExpr(member.Target))}", call.Args);
            if (member.Name == "__subclasses__" && !BuiltinTypeBase(member.Target))
                return InvokeLogicalMethod(member, SubclassLookupReceiver(member.Target), call.Args);
            if (member.Name != "__new__" && !IsTypeSlot(member.Name) || !BuiltinTypeBase(member.Target)) return null;
            string callable = EmitBuiltinTypeSlot(member);
            var arguments = call.Args.Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
            return $"{Rt("__invoke_kw")}((object?){callable}, [{string.Join(", ", arguments)}], {KeywordArgs(call.Args)})";
        }
    }
}
