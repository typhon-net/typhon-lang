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

        bool IsUserMetaclass(ClassDef definition, HashSet<ClassDef> seen) =>
            definition != null && seen.Add(definition) && definition.Bases.Any(b =>
                BuiltinTypeBase(b) || ResolveBase(b, out var parent, out _) && IsUserMetaclass(parent, seen));

        bool IsMetaclassExpression(Expr expression) =>
            DottedName(expression) is string name && TryResolveType(name, 0, out _, out var definition) &&
            IsUserMetaclass(definition);

        bool HasUserMetaclass(ClassDef definition) =>
            !_m.IsGdScript && HasUserMetaclass(definition, new HashSet<ClassDef>());

        bool HasUserMetaclass(ClassDef definition, HashSet<ClassDef> seen) =>
            definition != null && seen.Add(definition) &&
            (IsMetaclassExpression(definition.Metaclass) || definition.Bases.Any(b =>
                ResolveBase(b, out var parent, out _) && HasUserMetaclass(parent, seen)));

        bool MetaclassMember(AttributeExpr member, out string type)
        {
            type = null;
            return !_m.IsGdScript && DottedName(member.Target) is string name &&
                TryResolveType(name, 0, out _, out var definition) && HasUserMetaclass(definition) &&
                TryTypeExpr(member.Target, out type);
        }

        static bool IsMetaclassHook(FunctionDef method) => method.Name is "__new__" or "__init__";

        static FunctionDef StaticMetaclassHook(FunctionDef hook)
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
            return new FunctionDef
            {
                Name = hook.Name, Line = hook.Line, Col = hook.Col, Params = parameters,
                Returns = hook.Returns, Body = hook.Body,
                Decorators = new List<Expr> { new NameExpr { Id = "staticmethod" } },
            };
        }

        void EmitClassInitialization(ClassDef definition)
        {
            string type = $"typeof({Esc(definition.Name)})";
            if (!HasUserMetaclass(definition))
            {
                _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor({type}.TypeHandle);");
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

        string EmitBuiltinTypeNew(CallExpr call)
        {
            if (call.Func is not AttributeExpr { Name: "__new__" } member || !BuiltinTypeBase(member.Target)) return null;
            string callable = $"{Rt("getattr")}(typeof(global::System.Type), {CsString("__new__")})";
            var arguments = call.Args.Where(a => a.Name == null && !a.DoubleStar)
                .Select(a => a.Star ? $".. {Rt("__iter_dyn")}((object?){Paren(EmitExpr(a.Value))})" : $"(object?){Paren(EmitLooseValue(a.Value))}");
            return $"{Rt("__invoke_kw")}((object?){callable}, [{string.Join(", ", arguments)}], {KeywordArgs(call.Args)})";
        }
    }
}
