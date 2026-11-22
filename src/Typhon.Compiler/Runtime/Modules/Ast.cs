// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981, CS0108

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Reflection;
    using Typhon.Runtime;

    internal static partial class ast
    {
        public const long PyCF_ONLY_AST = 0x400;

        internal class AST : DynamicObject
        {
            internal readonly Dictionary<string, object?> Fields = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, object?> Attributes = new(StringComparer.Ordinal);
            public AST() { }
            protected AST(params (string Name, object? Value)[] fields)
            {
                foreach (var (name, value) in fields) Fields[name] = value;
            }

            public PyTuple<string> _fields => new PyTuple<string>(Fields.Keys);
            public PyTuple<string> _attributes => new PyTuple<string>(Attributes.Keys);
            public override IEnumerable<string> GetDynamicMemberNames() => Fields.Keys.Concat(Attributes.Keys);
            public override bool TryGetMember(GetMemberBinder binder, out object? result) =>
                Fields.TryGetValue(binder.Name, out result) || Attributes.TryGetValue(binder.Name, out result);
            public override bool TrySetMember(SetMemberBinder binder, object? value)
            {
                (Fields.ContainsKey(binder.Name) ? Fields : Attributes)[binder.Name] = value;
                return true;
            }
        }

        internal sealed class Module : AST { }
        internal sealed class Expression : AST { }
        internal sealed class Interactive : AST { }
        internal sealed class ClassDef : AST { }
        internal sealed class FunctionDef : AST { }
        internal sealed class AsyncFunctionDef : AST { }
        internal sealed class Return : AST { }
        internal sealed class Delete : AST { }
        internal sealed class Assign : AST { }
        internal sealed class AugAssign : AST { }
        internal sealed class AnnAssign : AST { }
        internal sealed class For : AST { }
        internal sealed class AsyncFor : AST { }
        internal sealed class While : AST { }
        internal sealed class If : AST { }
        internal sealed class With : AST { }
        internal sealed class AsyncWith : AST { }
        internal sealed class Match : AST { }
        internal sealed class Raise : AST { }
        internal sealed class Try : AST { }
        internal sealed class Assert : AST { }
        internal sealed class Import : AST { }
        internal sealed class ImportFrom : AST { }
        internal sealed class Global : AST { }
        internal sealed class Nonlocal : AST { }
        internal sealed class Expr : AST { }
        internal sealed class Pass : AST { }
        internal sealed class Break : AST { }
        internal sealed class Continue : AST { }
        internal sealed class BoolOp : AST { }
        internal sealed class NamedExpr : AST { }
        internal sealed class BinOp : AST
        {
            private BinOp() { }
            public BinOp(AST left, AST op, AST right)
                : base(("left", left), ("op", op), ("right", right)) { }
        }
        internal sealed class UnaryOp : AST
        {
            private UnaryOp() { }
            public UnaryOp(AST op, AST operand)
                : base(("op", op), ("operand", operand)) { }
        }
        internal sealed class Lambda : AST { }
        internal sealed class IfExp : AST { }
        internal sealed class Dict : AST
        {
            private Dict() { }
            public Dict(IList? keys = null, IList? values = null)
                : base(("keys", keys ?? new List<object?>()), ("values", values ?? new List<object?>())) { }
        }
        internal sealed class Set : AST
        {
            private Set() { }
            public Set(IList? elts = null)
                : base(("elts", elts ?? new List<object?>())) { }
        }
        internal sealed class ListComp : AST { }
        internal sealed class SetComp : AST { }
        internal sealed class DictComp : AST { }
        internal sealed class GeneratorExp : AST { }
        internal sealed class Await : AST { }
        internal sealed class Yield : AST { }
        internal sealed class YieldFrom : AST { }
        internal sealed class Compare : AST
        {
            private Compare() { }
            public Compare(AST left, IList? ops = null, IList? comparators = null)
                : base(("left", left), ("ops", ops ?? new List<object?>()), ("comparators", comparators ?? new List<object?>())) { }
        }
        internal sealed class Call : AST
        {
            private Call() { }
            public Call(AST func, IList? args = null, IList? keywords = null)
                : base(("func", func), ("args", args ?? new List<object?>()), ("keywords", keywords ?? new List<object?>())) { }
        }
        internal sealed class FormattedValue : AST { }
        internal sealed class JoinedStr : AST { }
        internal sealed class TemplateStr : AST
        {
            private TemplateStr() { }
            public TemplateStr(IList? values = null)
                : base(("values", values ?? new List<object?>())) { }
        }
        internal sealed class Interpolation : AST
        {
            private Interpolation() { }
            public Interpolation(AST value, string? str, object conversion, AST? format_spec = null)
                : base(("value", value), ("str", str), ("conversion", conversion), ("format_spec", format_spec)) { }
        }
        internal sealed class Constant : AST
        {
            private Constant() { }
            public Constant(object? value, string? kind = null)
                : base(("value", value), ("kind", kind)) { }
        }
        internal sealed class Attribute : AST
        {
            private Attribute() { }
            public Attribute(AST value, string attr, AST? ctx = null)
                : base(("value", value), ("attr", attr), ("ctx", ctx ?? new Load())) { }
        }
        internal sealed class Subscript : AST
        {
            private Subscript() { }
            public Subscript(AST value, AST slice, AST? ctx = null)
                : base(("value", value), ("slice", slice), ("ctx", ctx ?? new Load())) { }
        }
        internal sealed class Starred : AST
        {
            private Starred() { }
            public Starred(AST value, AST? ctx = null)
                : base(("value", value), ("ctx", ctx ?? new Load())) { }
        }
        internal sealed class Name : AST
        {
            private Name() { }
            public Name(string id, AST? ctx = null)
                : base(("id", id), ("ctx", ctx ?? new Load())) { }
        }
        internal sealed class List : AST
        {
            private List() { }
            public List(IList? elts = null, AST? ctx = null)
                : base(("elts", elts ?? new List<object?>()), ("ctx", ctx ?? new Load())) { }
        }
        internal sealed class Tuple : AST
        {
            private Tuple() { }
            public Tuple(IList? elts = null, AST? ctx = null)
                : base(("elts", elts ?? new List<object?>()), ("ctx", ctx ?? new Load())) { }
        }
        internal sealed class Slice : AST
        {
            private Slice() { }
            public Slice(AST? lower = null, AST? upper = null, AST? step = null)
                : base(("lower", lower), ("upper", upper), ("step", step)) { }
        }
        internal sealed class Load : AST { }
        internal sealed class Store : AST { }
        internal sealed class Del : AST { }
        internal sealed class Add : AST { }
        internal sealed class Sub : AST { }
        internal sealed class Mult : AST { }
        internal sealed class MatMult : AST { }
        internal sealed class Div : AST { }
        internal sealed class Mod : AST { }
        internal sealed class Pow : AST { }
        internal sealed class LShift : AST { }
        internal sealed class RShift : AST { }
        internal sealed class BitOr : AST { }
        internal sealed class BitXor : AST { }
        internal sealed class BitAnd : AST { }
        internal sealed class FloorDiv : AST { }
        internal sealed class And : AST { }
        internal sealed class Or : AST { }
        internal sealed class Invert : AST { }
        internal sealed class Not : AST { }
        internal sealed class UAdd : AST { }
        internal sealed class USub : AST { }
        internal sealed class Eq : AST { }
        internal sealed class NotEq : AST { }
        internal sealed class Lt : AST { }
        internal sealed class LtE : AST { }
        internal sealed class Gt : AST { }
        internal sealed class GtE : AST { }
        internal sealed class Is : AST { }
        internal sealed class IsNot : AST { }
        internal sealed class In : AST { }
        internal sealed class NotIn : AST { }
        internal sealed class comprehension : AST { }
        internal sealed class ExceptHandler : AST { }
        internal sealed class arguments : AST { }
        internal sealed class arg : AST { }
        internal sealed class keyword : AST
        {
            private keyword() { }
            public keyword(string? arg, AST value)
                : base(("arg", arg), ("value", value)) { }
        }
        internal sealed class alias : AST { }
        internal sealed class withitem : AST { }
        internal sealed class match_case : AST { }
        internal sealed class MatchValue : AST { }
        internal sealed class MatchSingleton : AST { }
        internal sealed class MatchSequence : AST { }
        internal sealed class MatchMapping : AST { }
        internal sealed class MatchClass : AST { }
        internal sealed class MatchStar : AST { }
        internal sealed class MatchAs : AST { }
        internal sealed class MatchOr : AST { }
        internal sealed class TypeVar : AST { }

        internal class NodeVisitor
        {
            public dynamic? visit(AST node)
            {
                object? visit = Builtins.getattr(this, "visit_" + node.GetType().Name, null);
                visit ??= Builtins.getattr(this, "generic_visit");
                return Builtins.__invoke(visit, [node]);
            }

            public virtual object? generic_visit(AST node)
            {
                foreach (AST child in iter_child_nodes(node)) visit(child);
                return null;
            }
        }

        internal class NodeTransformer : NodeVisitor
        {
            public override object? generic_visit(AST node)
            {
                foreach (var (name, value) in node.Fields.ToArray())
                {
                    if (value is AST child)
                    {
                        object? replacement = visit(child);
                        if (replacement == null) node.Fields.Remove(name);
                        else node.Fields[name] = replacement;
                    }
                    else if (value is IList list)
                    {
                        var revised = new List<object?>();
                        foreach (object? item in list)
                        {
                            if (item is not AST element) { revised.Add(item); continue; }
                            object? replacement = visit(element);
                            if (replacement is AST) revised.Add(replacement);
                            else if (replacement != null) revised.AddRange(Builtins.__iter_dyn(replacement));
                        }
                        list.Clear();
                        foreach (object? item in revised) list.Add(item);
                    }
                }
                return node;
            }
        }

        public static IEnumerable<(string, object?)> iter_fields(AST node)
        {
            foreach (var field in node.Fields) yield return (field.Key, field.Value);
        }

        public static IEnumerable<AST> iter_child_nodes(AST node)
        {
            foreach (object? field in node.Fields.Values)
            {
                if (field is AST child) yield return child;
                else if (field is IList children)
                    foreach (var item in children)
                        if (item is AST nested) yield return nested;
            }
        }

        public static IEnumerable<AST> walk(AST node)
        {
            var queue = new Queue<AST>();
            queue.Enqueue(node);
            while (queue.TryDequeue(out AST? next))
            {
                yield return next;
                foreach (AST child in iter_child_nodes(next)) queue.Enqueue(child);
            }
        }

        static readonly Dictionary<string, Type> NodeTypes = typeof(ast).GetNestedTypes(BindingFlags.NonPublic)
            .Where(type => type.IsSubclassOf(typeof(AST))).ToDictionary(type => type.Name);

        public static dynamic parse(object source, string filename = "<unknown>", string mode = "exec")
        {
            string text = source switch
            {
                string value => value,
                PyBytes bytes => importlib.util.decode_source(bytes),
                _ => throw new InvalidCastException("compile() arg 1 must be a string or bytes"),
            };
            if (mode is not ("exec" or "single" or "eval"))
                throw new ArgumentException("compile() mode must be 'exec', 'eval' or 'single'");
            return CompileTree(text, filename, mode, PyCF_ONLY_AST);
        }

        internal static AST CompileTree(string text, string filename, string mode, long flags)
        {
            var parseSource = AppContext.GetData("Typhon.ParseRuntimeAst") as
                Func<string, string, string, long, (object? Tree, string? Error, int Line, int Column, int Kind)>
                ?? throw new NotSupportedException("ast.parse() requires the Typhon runtime compiler host");
            var parsed = parseSource(text, filename, mode, flags);
            if (parsed.Error != null)
                throw Builtins.RuntimeSyntaxError(text, filename, parsed.Error, parsed.Line, parsed.Column, parsed.Kind);
            return (AST)Materialize(parsed.Tree)!;
        }

        static object? Materialize(object? packed)
        {
            if (packed == DBNull.Value) return Builtins.Ellipsis;
            if (packed is object?[] items) return items.Select(Materialize).ToList();
            if (packed is byte[] data) return new PyBytes(data);
            if (packed is not ValueTuple<string, KeyValuePair<string, object?>[], long[]> node) return packed;
            var result = (AST)Activator.CreateInstance(NodeTypes[node.Item1], nonPublic: true)!;
            foreach (var field in node.Item2) result.Fields[field.Key] = Materialize(field.Value);
            if (node.Item3 != null)
            {
                result.Attributes["lineno"] = node.Item3[0];
                result.Attributes["col_offset"] = node.Item3[1];
                result.Attributes["end_lineno"] = node.Item3[2];
                result.Attributes["end_col_offset"] = node.Item3[3];
            }
            return result;
        }
    }
}
