// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        const string RuntimeUnionType = "global::Typhon.Stdlib.types.UnionType";
        readonly HashSet<string> _typingModules = new HashSet<string>();
        readonly HashSet<string> _typingUnionImports = new HashSet<string>();

        bool IsTypingUnion(Expr expression) => expression switch
        {
            NameExpr name => _typingUnionImports.Contains(name.Id) && !IsShadowed(name.Id),
            AttributeExpr { Target: NameExpr module, Name: "Union" } =>
                _typingModules.Contains(module.Id) && !IsShadowed(module.Id),
            _ => false,
        };

        bool IsUnionFactory(Expr expression) => IsTypingUnion(expression) ||
            (DottedName(expression) is string name &&
                TryResolveType(name, 0, out var meta, out _) && meta == "Typhon.Stdlib.types+UnionType");

        bool IsKnownClassOrUnion(Expr expression) => expression switch
        {
            NameExpr name => !IsShadowed(name.Id) && BuiltinTypeValues.ContainsKey(name.Id) ||
                IsTypingUnion(name) || IsRuntimeTypeValue(name) ||
                TryResolveType(name.Id, 0, out _, out var cls) && cls != null && !IsLocal(name.Id),
            AttributeExpr member => IsTypingUnion(member) || IsRuntimeTypeValue(member) ||
                TryResolveType(DottedName(member) ?? "", 0, out var meta, out var cls) &&
                (cls != null || meta != null && _r.TypeExists(meta) && IsRuntimeClassMeta(meta)),
            BinOpExpr { Op: "|" } union => IsUnionOperation(union),
            SubscriptExpr subscript => IsUnionFactory(subscript.Target) || BuiltinGenericOrigin(subscript.Target) != null,
            CallExpr call => IsRuntimeTypeValue(call),
            _ => false,
        };

        bool IsRuntimeTypeValue(Expr expression) => InferType(expression) is "global::System.Type" or "System.Type";

        bool IsUnionOperation(BinOpExpr binary) => binary.Op == "|" &&
            (IsKnownClassOrUnion(binary.Left) || IsKnownClassOrUnion(binary.Right));

        bool IsUnionValue(Expr expression) => expression switch
        {
            BinOpExpr { Op: "|" } binary => IsUnionOperation(binary),
            SubscriptExpr subscript => IsUnionFactory(subscript.Target),
            _ => false,
        };

        bool IsBuiltinGenericAlias(Expr expression) =>
            expression is SubscriptExpr subscript && subscript.Index is not SliceExpr && BuiltinGenericOrigin(subscript.Target) != null;

        bool CanMaterializeTypeAlias(Expr expression) =>
            (IsUnionValue(expression) || IsBuiltinGenericAlias(expression)) && CanMaterializeUnionMember(expression);

        bool CanMaterializeUnionMember(Expr expression) => expression switch
        {
            ConstExpr { Kind: ConstKind.None or ConstKind.Ellipsis } => true,
            BinOpExpr { Op: "|" } union => CanMaterializeUnionMember(union.Left) && CanMaterializeUnionMember(union.Right),
            SubscriptExpr subscript => (IsUnionFactory(subscript.Target) || BuiltinGenericOrigin(subscript.Target) != null) &&
                MaterializedArguments(subscript.Index).All(CanMaterializeUnionMember),
            _ => IsKnownClassOrUnion(expression),
        };

        bool NeedsRuntimeClassInfo(Expr expression) => IsUnionValue(expression) ||
            IsBuiltinGenericAlias(expression) ||
            expression is NameExpr name && CanMaterializeTypeAlias(TypeAlias(name.Id));

        static IEnumerable<Expr> MaterializedArguments(Expr argument) =>
            argument is TupleExpr tuple ? tuple.Elts : new[] { argument };

        string BuiltinGenericOrigin(Expr expression)
        {
            if (expression is not NameExpr name || IsShadowed(name.Id)) return null;
            if (name.Id == "frozenset") return "global::Typhon.Runtime.frozenset<object>";
            return name.Id is "list" or "set" or "dict" or "type" or "tuple" ? BuiltinTypeValues[name.Id] : null;
        }

        string EmitUnionArguments(Expr index)
        {
            if (index is not TupleExpr)
                return $"(object?){Paren(EmitUnionOperand(index))}";
            var items = MaterializedArguments(index).Select(item => $"(object?){Paren(EmitUnionOperand(item))}");
            return $"new global::Typhon.Runtime.PyTuple<object?>(new object?[] {{ {string.Join(", ", items)} }})";
        }

        string EmitGenericAlias(SubscriptExpr subscript, string origin) =>
            $"new global::Typhon.Stdlib.types.GenericAlias(typeof({origin}), {EmitUnionArguments(subscript.Index)})";

        string EmitUnionOperand(Expr expression)
        {
            if (IsTypingUnion(expression)) return $"typeof({RuntimeUnionType})";
            if (expression is ConstExpr { Kind: ConstKind.Ellipsis }) return Rt("Ellipsis");
            if (expression is SubscriptExpr subscript && BuiltinGenericOrigin(subscript.Target) is string origin)
                return EmitGenericAlias(subscript, origin);
            if (expression is NameExpr name && !IsShadowed(name.Id) && BuiltinTypeValues.TryGetValue(name.Id, out var builtin))
                return $"typeof({builtin})";
            if (!IsLocal(DottedName(expression)?.Split('.')[0] ?? "") && DottedName(expression) is string dotted &&
                TryResolveType(dotted, 0, out var runtime, out var cls) &&
                (cls != null || runtime != null && _r.TypeExists(runtime) && IsRuntimeClassMeta(runtime)) &&
                TryTypeExpr(expression, out var type))
                return $"typeof({type})";
            return EmitValue(expression);
        }
    }
}
