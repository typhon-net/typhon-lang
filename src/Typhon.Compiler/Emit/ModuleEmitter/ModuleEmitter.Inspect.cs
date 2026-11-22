// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    partial class ModuleEmitter
    {
        const string InspectSignatureClass = "global::Typhon.Stdlib.inspect";
        static string SignatureProviderName(FunctionDef function) => "__typhon_signature_" + function.Name;

        void EmitSignatureProvider(FunctionDef function, string provider = null)
        {
            _w.Line($"private static {InspectSignatureClass}.Signature {provider ?? SignatureProviderName(function)}() => {SignatureValue(function)};");
        }

        void EmitSignatureAttribute(FunctionDef function, string provider = null) =>
            _w.Line($"[global::Typhon.Runtime.PySignature({CsString(provider ?? SignatureProviderName(function))})]");

        string RuntimeAnnotation(Expr expression) => expression switch
        {
            NameExpr name when BuiltinTypeValues.TryGetValue(name.Id, out var builtin) && !IsShadowed(name.Id) => $"typeof({builtin})",
            ConstExpr { Kind: ConstKind.Str } forward => CsString(forward.Str),
            ConstExpr { Kind: ConstKind.None } => "null",
            NameExpr or AttributeExpr when TryTypeExpr(expression, out string type) => $"typeof({type})",
            _ when CanMaterializeTypeAlias(expression) => EmitUnionOperand(expression),
            _ => null,
        };

        void EmitAnnotationsMap(List<Stmt> body)
        {
            if (_m.IsGdScript || _m.RuntimeCodeMode != null || body.Any(statement => statement is AssignStmt assignment &&
                assignment.Targets.Any(target => target is NameExpr { Id: "__annotations__" }) ||
                statement is AnnAssignStmt { Target: NameExpr { Id: "__annotations__" } })) return;
            var assignments = body.OfType<AnnAssignStmt>().Where(entry => entry.Target is NameExpr)
                .GroupBy(entry => ((NameExpr)entry.Target).Id).Select(group => (Name: group.Key, Value: RuntimeAnnotation(group.Last().Annotation)))
                .Where(entry => entry.Value != null).Select(entry => $"[{CsString(entry.Name)}] = {entry.Value}").ToList();
            if (assignments.Count == 0) return;
            _w.Line("private static object? __typhon_annotations;");
            _w.Line("private static object __annotations__ { get => __typhon_annotations ??= new OrderedDictionary<string, object?> { " +
                string.Join(", ", assignments) + " }; set => __typhon_annotations = value; }");
        }

        string SignatureValue(FunctionDef function)
        {
            const string empty = InspectSignatureClass + ".Signature.empty";
            string Annotation(Expr expression) => RuntimeAnnotation(expression) ?? empty;
            string CallableDefault(Expr expression)
            {
                if (expression is AttributeExpr member && ModuleRef(DottedName(member.Target) ?? "") is ModuleInfo module)
                    return $"new global::Typhon.Runtime.PyBoundMethod(typeof({ModuleClassName(module)}), {CsString(member.Name)}, includeNonPublic: true)";
                if (expression is NameExpr name && StdlibOwner(name.Id) is string owner)
                    return $"new global::Typhon.Runtime.PyBoundMethod(typeof({CsTypeName(owner)}), {CsString(name.Id)})";
                return EmitLooseValue(expression);
            }
            string Parameter(Param parameter)
            {
                string kind = parameter.Kind switch
                {
                    ParamKind.VarArgs => "VAR_POSITIONAL",
                    ParamKind.KwArgs => "VAR_KEYWORD",
                    _ when parameter.PositionalOnly => "POSITIONAL_ONLY",
                    _ when parameter.KeywordOnly => "KEYWORD_ONLY",
                    _ => "POSITIONAL_OR_KEYWORD",
                };
                string value = parameter.Default == null ? empty
                    : _defaultSources.TryGetValue(parameter, out var source) ? source
                    : IsConstantDefault(parameter.Default) ? EmitValue(parameter.Default)
                    : IsParamAlias(function, parameter) ? CallableDefault(parameter.Default)
                    : empty;
                return $"new {InspectSignatureClass}.Parameter({CsString(parameter.Name)}, " +
                    $"{InspectSignatureClass}._ParameterKind.{kind}, (object?)({value}), {Annotation(parameter.Annotation)})";
            }
            string parameters = string.Join(", ", function.Params.Select(Parameter));
            return $"new {InspectSignatureClass}.Signature(new {InspectSignatureClass}.Parameter[] {{ {parameters} }}, {Annotation(function.Returns)})";
        }
    }
}
