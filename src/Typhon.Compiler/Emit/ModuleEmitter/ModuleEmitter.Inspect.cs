// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        const long FutureAnnotationsFlag = 0x1000000;
        bool? _futureAnnotations;
        bool HasFutureAnnotations => _futureAnnotations ??= _m.Ast.Body.OfType<ImportFromStmt>().Any(
            import => import.Level == 0 && import.Module == "__future__" && import.Names.Any(name => name.Name == "annotations"));

        static string AnnotationStringLiteral(string text)
        {
            char quote = text.Contains("'") && !text.Contains("\"") ? '"' : '\'';
            var code = new StringBuilder().Append(quote);
            foreach (char character in text)
            {
                if (character == '\\' || character == quote) code.Append('\\').Append(character);
                else if (character == '\n') code.Append("\\n");
                else if (character == '\r') code.Append("\\r");
                else if (character == '\t') code.Append("\\t");
                else if (char.IsControl(character)) code.Append("\\x").Append(((int)character).ToString("x2"));
                else code.Append(character);
            }
            return code.Append(quote).ToString();
        }

        string AnnotationText(Expr expression) => expression switch
        {
            NameExpr name => name.Id,
            AttributeExpr attribute => AnnotationText(attribute.Target) + "." + attribute.Name,
            SubscriptExpr { Index: TupleExpr arguments } subscript =>
                AnnotationText(subscript.Target) + "[" + AnnotationArguments(arguments) + "]",
            SubscriptExpr subscript => AnnotationText(subscript.Target) + "[" + AnnotationText(subscript.Index) + "]",
            BinOpExpr { Op: "|" } union => AnnotationText(union.Left) + " | " + AnnotationText(union.Right),
            TupleExpr tuple => "(" + AnnotationArguments(tuple) + ")",
            ListExpr list => "[" + string.Join(", ", list.Elts.Select(AnnotationText)) + "]",
            ConstExpr { Kind: ConstKind.Str } text => AnnotationStringLiteral(text.Str),
            ConstExpr { Kind: ConstKind.None } => "None",
            ConstExpr { Kind: ConstKind.Ellipsis } => "...",
            _ when expression.EndOffset > expression.StartOffset && expression.EndOffset <= _m.SourceText?.Length =>
                _m.SourceText.Substring(expression.StartOffset, expression.EndOffset - expression.StartOffset),
            ConstExpr constant => constant.Raw,
            _ => null,
        };

        string AnnotationArguments(TupleExpr tuple) => tuple.Elts.Count == 0 ? "()" :
            string.Join(", ", tuple.Elts.Select(AnnotationText)) + (tuple.Elts.Count == 1 ? "," : "");

        string RuntimeAnnotation(Expr expression) => expression == null ? null : HasFutureAnnotations
            ? CsString(AnnotationText(expression) ?? "")
            : expression switch
        {
            NameExpr name when BuiltinTypeValues.TryGetValue(name.Id, out var builtin) && !IsShadowed(name.Id) => $"typeof({builtin})",
            ConstExpr { Kind: ConstKind.Str } forward => CsString(forward.Str),
            ConstExpr { Kind: ConstKind.None } => "null",
            NameExpr or AttributeExpr when TryTypeExpr(expression, out string type) => $"typeof({type})",
            _ when CanMaterializeTypeAlias(expression) => EmitUnionOperand(expression),
            SubscriptExpr subscription => SourceAnnotationSubscription(subscription),
            _ => null,
        };

        string SourceAnnotationSubscription(SubscriptExpr subscription)
        {
            ModuleInfo module;
            string member;
            if (subscription.Target is AttributeExpr attribute && DottedName(attribute.Target) is string dotted &&
                !IsLocal(dotted.Split('.')[0]) && ModuleRef(dotted) is ModuleInfo owner)
            {
                module = owner;
                member = attribute.Name;
            }
            else if (subscription.Target is NameExpr name && !IsLocal(name.Id) && !_m.Globals.Contains(name.Id) &&
                _importedFromModules.TryGetValue(name.Id, out var imported))
            {
                module = imported.Key;
                member = imported.Value;
            }
            else return null;

            if (!module.Globals.Contains(member) && !module.Functions.Contains(member) && !module.Classes.ContainsKey(member))
                return null;
            var arguments = MaterializedArguments(subscription.Index).Select(argument =>
                argument is ConstExpr { Kind: not (ConstKind.None or ConstKind.Str) } ? EmitValue(argument) : RuntimeAnnotation(argument)).ToList();
            if (arguments.Any(argument => argument == null)) return null;
            string index = subscription.Index is TupleExpr ?
                $"new global::Typhon.Runtime.PyTuple<object?>([{string.Join(", ", arguments.Select(argument => $"(object?){Paren(argument)}"))}])" :
                arguments[0];
            string receiver = $"{Rt("getattr")}({ModuleObject(module)}, {CsString(member)})";
            return $"{Rt("__getitem")}((object?){Paren(receiver)}, (object?){Paren(index)})";
        }

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
            _w.Line("internal static object __annotations__ { get => __typhon_annotations ??= new OrderedDictionary<string, object?> { " +
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
