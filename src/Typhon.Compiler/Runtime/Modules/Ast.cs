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
        public const long PyCF_TYPE_COMMENTS = 0x1000;
        public const long PyCF_ALLOW_TOP_LEVEL_AWAIT = 0x2000;
        public const long PyCF_OPTIMIZED_AST = 0x8000 | PyCF_ONLY_AST;

        internal static bool IsPrimitiveExport(string name) => name == "AST" ||
            name.StartsWith("PyCF_", StringComparison.Ordinal) || NodeTypes.ContainsKey(name);

        // The node hierarchy follows CPython 3.15's Python.asdl. See CPython-LICENSE.txt.

        [PyMutableClass]
        internal class AST : DynamicObject, IPyInstanceAttributes
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
            void IPyInstanceAttributes.SetAttribute(string name, object? value) =>
                (Fields.ContainsKey(name) ? Fields : Attributes)[name] = value;
            bool IPyInstanceAttributes.RemoveAttribute(string name) =>
                Fields.Remove(name) || Attributes.Remove(name);

            public override bool TryGetMember(GetMemberBinder binder, out object? result)
            {
                if (Builtins.ClassProperty(GetType(), binder.Name) is { } property)
                {
                    result = property.__get__(this, GetType());
                    return true;
                }
                return Fields.TryGetValue(binder.Name, out result) || Attributes.TryGetValue(binder.Name, out result) ||
                    PyClassDescriptors.TryGetAddedInstanceAttribute(this, binder.Name, out result);
            }
            public override bool TrySetMember(SetMemberBinder binder, object? value)
            {
                if (Builtins.ClassProperty(GetType(), binder.Name) is { } property) property.__set__(this, value);
                else ((IPyInstanceAttributes)this).SetAttribute(binder.Name, value);
                return true;
            }
        }

        internal class mod : AST { }
        internal class FunctionType : mod { }
        internal class Module : mod { }
        internal class Expression : mod { }
        internal class Interactive : mod { }
        internal class stmt : AST { }
        internal class TypeAlias : stmt { }
        internal class TryStar : stmt { }
        internal class ClassDef : stmt { }
        internal class FunctionDef : stmt { }
        internal class AsyncFunctionDef : stmt { }
        internal class Return : stmt { }
        internal class Delete : stmt { }
        internal class Assign : stmt { }
        internal class AugAssign : stmt { }
        internal class AnnAssign : stmt { }
        internal class For : stmt { }
        internal class AsyncFor : stmt { }
        internal class While : stmt { }
        internal class If : stmt { }
        internal class With : stmt { }
        internal class AsyncWith : stmt { }
        internal class Match : stmt { }
        internal class Raise : stmt { }
        internal class Try : stmt { }
        internal class Assert : stmt { }
        internal class Import : stmt { }
        internal class ImportFrom : stmt { }
        internal class Global : stmt { }
        internal class Nonlocal : stmt { }
        internal class Expr : stmt { }
        internal class Pass : stmt { }
        internal class Break : stmt { }
        internal class Continue : stmt { }
        internal class expr : AST
        {
            public expr() { }
            protected expr(params (string Name, object? Value)[] fields) : base(fields) { }
        }
        internal class BoolOp : expr { }
        internal class NamedExpr : expr { }
        internal class BinOp : expr
        {
            protected BinOp() { }
            public BinOp(AST left, AST op, AST right)
                : base(("left", left), ("op", op), ("right", right)) { }
        }
        internal class UnaryOp : expr
        {
            protected UnaryOp() { }
            public UnaryOp(AST op, AST operand)
                : base(("op", op), ("operand", operand)) { }
        }
        internal class Lambda : expr { }
        internal class IfExp : expr { }
        internal class Dict : expr
        {
            protected Dict() { }
            public Dict(IList? keys = null, IList? values = null)
                : base(("keys", keys ?? new List<object?>()), ("values", values ?? new List<object?>())) { }
        }
        internal class Set : expr
        {
            protected Set() { }
            public Set(IList? elts = null)
                : base(("elts", elts ?? new List<object?>())) { }
        }
        internal class ListComp : expr { }
        internal class SetComp : expr { }
        internal class DictComp : expr { }
        internal class GeneratorExp : expr { }
        internal class Await : expr { }
        internal class Yield : expr { }
        internal class YieldFrom : expr { }
        internal class Compare : expr
        {
            protected Compare() { }
            public Compare(AST left, IList? ops = null, IList? comparators = null)
                : base(("left", left), ("ops", ops ?? new List<object?>()), ("comparators", comparators ?? new List<object?>())) { }
        }
        internal class Call : expr
        {
            protected Call() { }
            public Call(AST func, IList? args = null, IList? keywords = null)
                : base(("func", func), ("args", args ?? new List<object?>()), ("keywords", keywords ?? new List<object?>())) { }
        }
        internal class FormattedValue : expr { }
        internal class JoinedStr : expr { }
        internal class TemplateStr : expr
        {
            protected TemplateStr() { }
            public TemplateStr(IList? values = null)
                : base(("values", values ?? new List<object?>())) { }
        }
        internal class Interpolation : expr
        {
            protected Interpolation() { }
            public Interpolation(AST value, string? str, object conversion, AST? format_spec = null)
                : base(("value", value), ("str", str), ("conversion", conversion), ("format_spec", format_spec)) { }
        }
        internal class Constant : expr
        {
            protected Constant() { }
            public Constant(object? value, string? kind = null)
                : base(("value", value), ("kind", kind)) { }
        }
        internal class Attribute : expr
        {
            protected Attribute() { }
            public Attribute(AST value, string attr, AST? ctx = null)
                : base(("value", value), ("attr", attr), ("ctx", ctx ?? new Load())) { }
        }
        internal class Subscript : expr
        {
            protected Subscript() { }
            public Subscript(AST value, AST slice, AST? ctx = null)
                : base(("value", value), ("slice", slice), ("ctx", ctx ?? new Load())) { }
        }
        internal class Starred : expr
        {
            protected Starred() { }
            public Starred(AST value, AST? ctx = null)
                : base(("value", value), ("ctx", ctx ?? new Load())) { }
        }
        internal class Name : expr
        {
            protected Name() { }
            public Name(string id, AST? ctx = null)
                : base(("id", id), ("ctx", ctx ?? new Load())) { }
        }
        internal class List : expr
        {
            protected List() { }
            public List(IList? elts = null, AST? ctx = null)
                : base(("elts", elts ?? new List<object?>()), ("ctx", ctx ?? new Load())) { }
        }
        internal class Tuple : expr
        {
            protected Tuple() { }
            public Tuple(IList? elts = null, AST? ctx = null)
                : base(("elts", elts ?? new List<object?>()), ("ctx", ctx ?? new Load())) { }
        }
        internal class Slice : expr
        {
            protected Slice() { }
            public Slice(AST? lower = null, AST? upper = null, AST? step = null)
                : base(("lower", lower), ("upper", upper), ("step", step)) { }
        }
        internal class expr_context : AST { }
        internal class Load : expr_context { }
        internal class Store : expr_context { }
        internal class Del : expr_context { }
        internal class @operator : AST { }
        internal class Add : @operator { }
        internal class Sub : @operator { }
        internal class Mult : @operator { }
        internal class MatMult : @operator { }
        internal class Div : @operator { }
        internal class Mod : @operator { }
        internal class Pow : @operator { }
        internal class LShift : @operator { }
        internal class RShift : @operator { }
        internal class BitOr : @operator { }
        internal class BitXor : @operator { }
        internal class BitAnd : @operator { }
        internal class FloorDiv : @operator { }
        internal class boolop : AST { }
        internal class And : boolop { }
        internal class Or : boolop { }
        internal class unaryop : AST { }
        internal class Invert : unaryop { }
        internal class Not : unaryop { }
        internal class UAdd : unaryop { }
        internal class USub : unaryop { }
        internal class cmpop : AST { }
        internal class Eq : cmpop { }
        internal class NotEq : cmpop { }
        internal class Lt : cmpop { }
        internal class LtE : cmpop { }
        internal class Gt : cmpop { }
        internal class GtE : cmpop { }
        internal class Is : cmpop { }
        internal class IsNot : cmpop { }
        internal class In : cmpop { }
        internal class NotIn : cmpop { }
        internal class comprehension : AST { }
        internal class excepthandler : AST { }
        internal class ExceptHandler : excepthandler { }
        internal class arguments : AST { }
        internal class arg : AST { }
        internal class keyword : AST
        {
            protected keyword() { }
            public keyword(string? arg, AST value)
                : base(("arg", arg), ("value", value)) { }
        }
        internal class alias : AST { }
        internal class withitem : AST { }
        internal class match_case : AST { }
        internal class pattern : AST { }
        internal class MatchValue : pattern { }
        internal class MatchSingleton : pattern { }
        internal class MatchSequence : pattern { }
        internal class MatchMapping : pattern { }
        internal class MatchClass : pattern { }
        internal class MatchStar : pattern { }
        internal class MatchAs : pattern { }
        internal class MatchOr : pattern { }
        internal class type_ignore : AST { }
        internal class TypeIgnore : type_ignore { }
        internal class type_param : AST { }
        internal class ParamSpec : type_param { }
        internal class TypeVarTuple : type_param { }
        internal class TypeVar : type_param { }

        internal class NodeVisitor
        {
            public dynamic? visit(object? node)
            {
                object? visit = Builtins.getattr(this, "visit_" + Builtins.__name_of(Builtins.__type_of(node)), null);
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

        public static dynamic parse(object source, string filename = "<unknown>", string mode = "exec",
            [PyKeywordOnly] bool type_comments = false, [PyKeywordOnly] object? feature_version = null,
            [PyKeywordOnly] long optimize = -1, [PyKeywordOnly] object? module = null)
        {
            long flags = optimize > 0 ? PyCF_OPTIMIZED_AST : PyCF_ONLY_AST;
            if (type_comments) flags |= PyCF_TYPE_COMMENTS;
            return Builtins.compile(source, filename, mode, flags, optimize: optimize, module: module,
                _feature_version: FeatureMinor(feature_version));
        }

        static long FeatureMinor(object? version)
        {
            if (version is System.Runtime.CompilerServices.ITuple or IPyTuple)
            {
                var parts = Builtins.__iter_dyn(version).Take(3).ToArray();
                if (parts.Length < 2)
                    throw new ArgumentException($"not enough values to unpack (expected 2, got {parts.Length})");
                if (parts.Length > 2) throw new ArgumentException("too many values to unpack (expected 2)");
                if (!Builtins.eq((object?)parts[0], 3L))
                    throw new ArgumentException($"Unsupported major version: {Builtins.str(parts[0])}");
                version = parts[1];
            }
            else if (version == null) return -1;

            object minor = Builtins.__index_value(version);
            return minor is System.Numerics.BigInteger wide ? (long)wide : (long)minor;
        }

        internal static AST CompileTree(string text, string filename, string mode, long flags, int featureVersion = -1, int optimize = -1)
        {
            var parseSource = AppContext.GetData("Typhon.ParseRuntimeAst") as
                Func<string, string, string, long, int, int, (object? Tree, string? Error, int Line, int Column, int Kind)>
                ?? throw new NotSupportedException("ast.parse() requires the Typhon runtime compiler host");
            var parsed = parseSource(text, filename, mode, flags, featureVersion, optimize);
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
