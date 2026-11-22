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

        bool IsUserMetaclass(ClassDef definition) => IsUserMetaclass(definition, new HashSet<ClassDef>());

        bool IsUserMetaclass(ClassDef definition, HashSet<ClassDef> seen)
        {
            if (definition == null || !seen.Add(definition)) return false;
            var resolver = ClassDeclarationResolver(definition);
            return definition.Bases.Any(b => resolver.BuiltinTypeBase(b) ||
                resolver.ResolveBase(b, out var parent, out _) && IsUserMetaclass(parent, seen));
        }

        bool IsMetaclassExpression(Expr expression) =>
            DottedName(expression) is string name && TryResolveType(name, 0, out _, out var definition) &&
            IsUserMetaclass(definition);

        bool HasUserMetaclass(ClassDef definition) =>
            !_m.IsGdScript && HasUserMetaclass(definition, new HashSet<ClassDef>());

        bool HasUserMetaclass(ClassDef definition, HashSet<ClassDef> seen)
        {
            if (definition == null || !seen.Add(definition)) return false;
            var resolver = ClassDeclarationResolver(definition);
            return DottedName(definition.Metaclass) is string name &&
                resolver.TryResolveType(name, 0, out _, out var meta) && IsUserMetaclass(meta) ||
                definition.Bases.Any(b => resolver.ResolveBase(b, out var parent, out _) && HasUserMetaclass(parent, seen));
        }

        bool MetaclassMember(AttributeExpr member, out string type)
        {
            type = null;
            return !_m.IsGdScript && DottedName(member.Target) is string name &&
                TryResolveType(name, 0, out _, out var definition) && HasUserMetaclass(definition) &&
                TryTypeExpr(member.Target, out type);
        }

        bool IsMetaclassInstance(Expr expression) => DottedName(expression) is string name &&
            TryResolveType(name, 0, out _, out var definition) && HasUserMetaclass(definition);

        readonly HashSet<FunctionDef> _metaclassMethods = new();

        static bool HasMetaclassReceiver(FunctionDef method) => method.Params.Count > 0 &&
            (method.Name is "__new__" or "__init__" ||
                !HasDecorator(method.Decorators, "staticmethod") &&
                !HasDecorator(method.Decorators, "classmethod") &&
                !HasDecorator(method.Decorators, "property") &&
                !method.Decorators.Any(d => DecoratorName(d)?.EndsWith(".setter") == true));

        FunctionDef StaticMetaclassMethod(FunctionDef hook)
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
                Name = hook.Name, Line = hook.Line, Col = hook.Col, Params = parameters,
                Returns = hook.Returns, Body = hook.Body, IsAsync = hook.IsAsync, TypeParams = hook.TypeParams,
                Decorators = hook.Decorators.Where(d => DecoratorName(d) != "staticmethod")
                    .Append(new NameExpr { Id = "staticmethod" }).ToList(),
            };
            _metaclassMethods.Add(transformed);
            return transformed;
        }

        bool CapturesClassModuleName => !_m.IsGdScript && _m.RuntimeCodeMode == null && _m.Globals.Contains("__name__");

        void EmitClassInitialization(ClassDef definition)
        {
            string type = ClassTypeValue(Esc(definition.Name), definition);
            if (CapturesClassModuleName)
                _w.Line($"{Rt("__remember_class_module")}({type}, (object?)__name__);");
            if (!RunsClassBodyAtDefinition(definition)) return;
            if (!HasUserMetaclass(definition))
            {
                _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor({type}.TypeHandle);");
                if (RunsSubclassHook(definition))
                {
                    string arguments = definition.ClassKeywords.Count == 0 ? "null" : KeywordArgs(definition.ClassKeywords);
                    _w.Line($"{Rt("__initialize_subclass")}({type}, {arguments});");
                }
                return;
            }
            string metaclass = definition.Metaclass == null ? "null" : $"(global::System.Type){Paren(EmitValue(definition.Metaclass))}";
            string bases = string.Join(", ", definition.Bases.Select(b => $"(global::System.Type){Paren(EmitValue(b))}"));
            var names = definition.Body.SelectMany(statement => statement switch
            {
                FunctionDef method => new[] { method.Name },
                ClassDef nested => new[] { nested.Name },
                _ => NamesInClassStatement(statement),
            }).Distinct();
            string keywords = definition.ClassKeywords.Count == 0 ? "null" : KeywordArgs(definition.ClassKeywords);
            _w.Line($"{Rt("__define_metaclass_type")}({type}, {metaclass}, {CsString(definition.Name)}, " +
                $"{CsString(PythonModuleName)}, [{bases}], [{string.Join(", ", names.Select(CsString))}], {keywords});");
        }

        string CaptureClassMember(ClassContext context, string name, string type, string value) =>
            HasUserMetaclass(context.Def)
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

        static bool IsTypeCheckSlot(string name) => name is "__instancecheck__" or "__subclasscheck__";

        string EmitBuiltinTypeSlot(AttributeExpr member) =>
            $"{Rt("getattr")}(typeof(global::System.Type), {CsString(member.Name)})";

        string EmitTypeSlotCall(CallExpr call)
        {
            if (call.Func is not AttributeExpr member) return null;
            if (_scope?.ClassArgument != null && _cls != null &&
                member.Target is CallExpr { Func: NameExpr { Id: "super" }, Args.Count: 0 } && !IsShadowed("super") &&
                (member.Name == "__new__" && (IsUserMetaclass(_cls.Def) || HasStringBase(_cls.Def)) ||
                 (member.Name == "__repr__" || IsTypeCheckSlot(member.Name)) && IsUserMetaclass(_cls.Def)))
                return InvokeLogicalMethod(member, $"(object?){Paren(EmitExpr(member.Target))}", call.Args);
            if (member.Name != "__new__" && !IsTypeCheckSlot(member.Name) || !BuiltinTypeBase(member.Target)) return null;
            string callable = EmitBuiltinTypeSlot(member);
            var arguments = call.Args.Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
            return $"{Rt("__invoke_kw")}((object?){callable}, [{string.Join(", ", arguments)}], {KeywordArgs(call.Args)})";
        }
    }
}
