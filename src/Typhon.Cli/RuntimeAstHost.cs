// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Numerics;
using System.Text;
using Typhon.Compiler.Syntax;

namespace Typhon.Cli;

internal sealed class RuntimeAstHost
{
    readonly string _source;
    readonly int[] _lineStarts;

    RuntimeAstHost(string source)
    {
        _source = source;
        var lines = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n') lines.Add(i + 1);
        _lineStarts = lines.ToArray();
    }

    internal static object Project(string source, ModuleAst module, string mode)
    {
        string text = source.Replace("\r\n", "\n").Replace('\r', '\n');
        var projection = new RuntimeAstHost(text);
        return mode switch
        {
            "eval" => Make("Expression", ("body", projection.Expr(((ExprStmt)module.Body[0]).Value))),
            "single" => Make("Interactive", ("body", projection.Body(module.Body))),
            _ => Make("Module", ("body", projection.Body(module.Body)), ("type_ignores", Array.Empty<object>())),
        };
    }

    static object Make(string kind, params (string Name, object? Value)[] fields) => Pack(kind, null, fields);

    object At(string kind, Node node, params (string Name, object? Value)[] fields)
    {
        var (line, column) = Coordinate(node.StartOffset);
        var (endLine, endColumn) = Coordinate(node.EndOffset);
        return Pack(kind, [line, column, endLine, endColumn], fields);
    }

    static object Pack(string kind, long[]? position, (string Name, object? Value)[] fields) =>
        (kind, fields.Select(field => KeyValuePair.Create(field.Name, field.Value)).ToArray(), position);

    (long Line, long Column) Coordinate(int offset)
    {
        offset = Math.Clamp(offset, 0, _source.Length);
        int index = Array.BinarySearch(_lineStarts, offset);
        int line = index >= 0 ? index : ~index - 1;
        int begin = _lineStarts[line];
        return (line + 1, Encoding.UTF8.GetByteCount(_source.AsSpan(begin, offset - begin)));
    }

    object?[] Body(IEnumerable<Stmt> nodes) => nodes.Select(Stmt).ToArray();
    object?[] Exprs(IEnumerable<Expr> nodes, string context = "Load") => nodes.Select(node => Expr(node, context)).ToArray();

    object[] Aliases(IEnumerable<ImportAlias> aliases) => aliases.Select(alias =>
        At("alias", alias, ("name", alias.Name), ("asname", alias.AsName))).ToArray();

    object Stmt(Stmt node) => node switch
    {
        ClassDef n => At("ClassDef", n, ("name", n.Name), ("bases", Exprs(n.Bases)),
            ("keywords", n.Metaclass == null ? Array.Empty<object>() : new[] { Make("keyword", ("arg", "metaclass"), ("value", Expr(n.Metaclass))) }),
            ("body", Body(n.Body)), ("decorator_list", Exprs(n.Decorators)), ("type_params", TypeParams(n.TypeParams))),
        FunctionDef n => At(n.IsAsync ? "AsyncFunctionDef" : "FunctionDef", n, ("name", n.Name),
            ("args", Parameters(n.Params)), ("body", Body(n.Body)), ("decorator_list", Exprs(n.Decorators)),
            ("returns", Expr(n.Returns)), ("type_comment", null), ("type_params", TypeParams(n.TypeParams))),
        ImportStmt n => At("Import", n, ("names", Aliases(n.Names)), ("is_lazy", n.IsLazy ? 1L : 0L)),
        ImportFromStmt n => At("ImportFrom", n, ("module", string.IsNullOrEmpty(n.Module) ? null : n.Module),
            ("names", Aliases(n.Names)), ("level", (long)n.Level), ("is_lazy", n.IsLazy ? 1L : 0L)),
        ExprStmt n => At("Expr", n, ("value", Expr(n.Value))),
        AssignStmt n => At("Assign", n, ("targets", Exprs(n.Targets, "Store")), ("value", Expr(n.Value)), ("type_comment", null)),
        AugAssignStmt n => At("AugAssign", n, ("target", Expr(n.Target, "Store")), ("op", Operator(n.Op.TrimEnd('='))), ("value", Expr(n.Value))),
        AnnAssignStmt n => At("AnnAssign", n, ("target", Expr(n.Target, "Store")), ("annotation", Expr(n.Annotation)), ("value", Expr(n.Value)),
            ("simple", n.Target is NameExpr ? 1L : 0L)),
        DelStmt n => At("Delete", n, ("targets", Exprs(n.Targets, "Del"))),
        GlobalStmt n => At(n.Nonlocal ? "Nonlocal" : "Global", n, ("names", n.Names.ToArray())),
        ReturnStmt n => At("Return", n, ("value", Expr(n.Value))),
        RaiseStmt n => At("Raise", n, ("exc", Expr(n.Exc)), ("cause", Expr(n.Cause))),
        AssertStmt n => At("Assert", n, ("test", Expr(n.Test)), ("msg", Expr(n.Msg))),
        IfStmt n => At("If", n, ("test", Expr(n.Test)), ("body", Body(n.Body)), ("orelse", Body(n.OrElse))),
        WhileStmt n => At("While", n, ("test", Expr(n.Test)), ("body", Body(n.Body)), ("orelse", Body(n.OrElse))),
        ForStmt n => At(n.IsAsync ? "AsyncFor" : "For", n, ("target", Expr(n.Target, "Store")), ("iter", Expr(n.Iter)),
            ("body", Body(n.Body)), ("orelse", Body(n.OrElse)), ("type_comment", null)),
        WithStmt n => At(n.IsAsync ? "AsyncWith" : "With", n, ("items", n.Items.Select(item => Make("withitem",
            ("context_expr", Expr(item.Context)), ("optional_vars", Expr(item.Var, "Store")))).ToArray()),
            ("body", Body(n.Body)), ("type_comment", null)),
        TryStmt n => At("Try", n, ("body", Body(n.Body)), ("handlers", n.Handlers.Select(handler => At("ExceptHandler", handler,
            ("type", Expr(handler.Type)), ("name", handler.Name), ("body", Body(handler.Body)))).ToArray()),
            ("orelse", Body(n.OrElse)), ("finalbody", Body(n.FinalBody))),
        MatchStmt n => At("Match", n, ("subject", Expr(n.Subject)), ("cases", n.Cases.Select(item => Make("match_case",
            ("pattern", Pattern(item.Pattern)), ("guard", Expr(item.Guard)), ("body", Body(item.Body)))).ToArray())),
        PassStmt n => At("Pass", n),
        BreakStmt n => At("Break", n),
        ContinueStmt n => At("Continue", n),
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    object[] TypeParams(IEnumerable<TypeParam> parameters) => parameters.Select(parameter =>
        Make("TypeVar", ("name", parameter.Name), ("bound", Expr(parameter.Bound)), ("default_value", null))).ToArray();

    object Parameter(Param param) => At("arg", param, ("arg", param.Name), ("annotation", Expr(param.Annotation)), ("type_comment", null));
    object Parameters(List<Param> parameters)
    {
        var positional = parameters.Where(p => p.Kind == ParamKind.Normal && !p.KeywordOnly).ToArray();
        var keywords = parameters.Where(p => p.Kind == ParamKind.Normal && p.KeywordOnly).ToArray();
        var vararg = parameters.FirstOrDefault(p => p.Kind == ParamKind.VarArgs);
        var kwarg = parameters.FirstOrDefault(p => p.Kind == ParamKind.KwArgs);
        return Make("arguments", ("posonlyargs", positional.Where(p => p.PositionalOnly).Select(Parameter).ToArray()),
            ("args", positional.Where(p => !p.PositionalOnly).Select(Parameter).ToArray()), ("vararg", vararg == null ? null : Parameter(vararg)),
            ("kwonlyargs", keywords.Select(Parameter).ToArray()), ("kw_defaults", keywords.Select(p => Expr(p.Default)).ToArray()),
            ("kwarg", kwarg == null ? null : Parameter(kwarg)), ("defaults", positional.Where(p => p.Default != null).Select(p => Expr(p.Default)).ToArray()));
    }

    static object KeywordArg(string? name, object? value) => Make("keyword", ("arg", name), ("value", value));
    object CallArg(Arg arg) => arg.Star ? At("Starred", arg.Value, ("value", Expr(arg.Value)), ("ctx", Make("Load"))) : Expr(arg.Value)!;

    object? Expr(Expr? node, string context = "Load") => node switch
    {
        null => null,
        NameExpr n => At("Name", n, ("id", n.Id), ("ctx", Make(context))),
        ConstExpr n => At("Constant", n, ("value", Constant(n)), ("kind", n.Kind == ConstKind.Str &&
            (_source[n.StartOffset] is 'u' or 'U') ? "u" : null)),
        AttributeExpr n => At("Attribute", n, ("value", Expr(n.Target)), ("attr", n.Name), ("ctx", Make(context))),
        SubscriptExpr n => At("Subscript", n, ("value", Expr(n.Target)), ("slice", Expr(n.Index)), ("ctx", Make(context))),
        SliceExpr n => At("Slice", n, ("lower", Expr(n.Lower)), ("upper", Expr(n.Upper)), ("step", Expr(n.Step))),
        StarredExpr n => At("Starred", n, ("value", Expr(n.Value, context)), ("ctx", Make(context))),
        AwaitExpr n => At("Await", n, ("value", Expr(n.Value))),
        YieldExpr n => At(n.From ? "YieldFrom" : "Yield", n, ("value", Expr(n.Value))),
        NamedExpr n => At("NamedExpr", n, ("target", Expr(n.Target, "Store")), ("value", Expr(n.Value))),
        BinOpExpr n => At("BinOp", n, ("left", Expr(n.Left)), ("op", Operator(n.Op)), ("right", Expr(n.Right))),
        UnaryExpr n => At("UnaryOp", n, ("op", Operator(n.Op == "+" ? "UAdd" : n.Op == "-" ? "USub" : n.Op)), ("operand", Expr(n.Operand))),
        BoolOpExpr n => At("BoolOp", n, ("op", Operator(n.Op)), ("values", Exprs(n.Values))),
        CompareExpr n => At("Compare", n, ("left", Expr(n.Left)), ("ops", n.Ops.Select(Operator).ToArray()), ("comparators", Exprs(n.Comparators))),
        IfExpr n => At("IfExp", n, ("test", Expr(n.Test)), ("body", Expr(n.Body)), ("orelse", Expr(n.OrElse))),
        LambdaExpr n => At("Lambda", n, ("args", LambdaParameters(n)), ("body", Expr(n.Body))),
        CallExpr n => At("Call", n, ("func", Expr(n.Func)), ("args", n.Args.Where(arg => arg.Name == null && !arg.DoubleStar).Select(CallArg).ToArray()),
            ("keywords", n.Args.Where(arg => arg.Name != null || arg.DoubleStar).Select(arg => KeywordArg(arg.Name, Expr(arg.Value))).ToArray())),
        TupleExpr n => At("Tuple", n, ("elts", Exprs(n.Elts, context)), ("ctx", Make(context))),
        ListExpr n => At("List", n, ("elts", Exprs(n.Elts, context)), ("ctx", Make(context))),
        SetExpr n => At("Set", n, ("elts", Exprs(n.Elts))),
        DictExpr n => At("Dict", n, ("keys", Exprs(n.Keys)), ("values", Exprs(n.Values))),
        ComprehensionExpr n => Comprehension(n),
        FStringExpr n => FormattedString(n, n.Parts),
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    object LambdaParameters(LambdaExpr function)
    {
        static object Arg(string name) => Make("arg", ("arg", name), ("annotation", null), ("type_comment", null));
        return Make("arguments", ("posonlyargs", Array.Empty<object>()), ("args", function.Params.Select(Arg).ToArray()),
            ("vararg", function.VarArgs == null ? null : Arg(function.VarArgs)), ("kwonlyargs", Array.Empty<object>()),
            ("kw_defaults", Array.Empty<object>()), ("kwarg", function.KwArgs == null ? null : Arg(function.KwArgs)), ("defaults", Exprs(function.Defaults)));
    }

    object Comprehension(ComprehensionExpr node)
    {
        var generators = node.Generators.Select(n => Make("comprehension", ("target", Expr(n.Target, "Store")),
            ("iter", Expr(n.Iter)), ("ifs", Exprs(n.Ifs)), ("is_async", n.IsAsync ? 1L : 0L))).ToArray();
        return node.Kind switch
        {
            ComprehensionKind.Set => At("SetComp", node, ("elt", Expr(node.Elt)), ("generators", generators)),
            ComprehensionKind.Dict => At("DictComp", node, ("key", Expr(node.Elt)), ("value", Expr(node.ValueElt)), ("generators", generators)),
            ComprehensionKind.Generator => At("GeneratorExp", node, ("elt", Expr(node.Elt)), ("generators", generators)),
            _ => At("ListComp", node, ("elt", Expr(node.Elt)), ("generators", generators)),
        };
    }

    object FormattedString(FStringExpr node, List<FStringPart> parts) => At(node.IsTemplate ? "TemplateStr" : "JoinedStr", node,
        ("values", parts.Select(part => Interpolation(node, part)).ToArray()));

    object Interpolation(FStringExpr node, FStringPart part)
    {
        if (part.Expr == null) return At("Constant", node, ("value", part.Literal), ("kind", null));
        object? spec = part.SpecParts != null ? FormattedString(node, part.SpecParts) :
            part.FormatSpec == null ? null : At("JoinedStr", node, ("values", new[] { At("Constant", node, ("value", part.FormatSpec), ("kind", null)) }));
        long conversion = part.Conversion == '\0' ? -1 : part.Conversion;
        return node.IsTemplate ? At("Interpolation", node, ("value", Expr(part.Expr)), ("str", part.ExprText), ("conversion", conversion), ("format_spec", spec)) :
            At("FormattedValue", node, ("value", Expr(part.Expr)), ("conversion", conversion), ("format_spec", spec));
    }

    object Pattern(Pattern node) => node switch
    {
        ValuePattern n when n.Value is ConstExpr { Kind: ConstKind.None or ConstKind.Bool } literal => At("MatchSingleton", n, ("value", Constant(literal))),
        ValuePattern n => At("MatchValue", n, ("value", Expr(n.Value))),
        CapturePattern n => At("MatchAs", n, ("pattern", null), ("name", n.Name)),
        AsPattern n => At("MatchAs", n, ("pattern", Pattern(n.Inner)), ("name", n.Name)),
        OrPattern n => At("MatchOr", n, ("patterns", n.Alternatives.Select(Pattern).ToArray())),
        SequencePattern n => At("MatchSequence", n, ("patterns", SequenceItems(n))),
        MappingPattern n => At("MatchMapping", n, ("keys", Exprs(n.Keys)), ("patterns", n.Values.Select(Pattern).ToArray()), ("rest", n.Rest)),
        ClassPattern n => At("MatchClass", n, ("cls", Expr(n.Cls)), ("patterns", n.Positional.Select(Pattern).ToArray()),
            ("kwd_attrs", n.KeywordNames.ToArray()), ("kwd_patterns", n.KeywordPatterns.Select(Pattern).ToArray())),
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    object[] SequenceItems(SequencePattern node)
    {
        var items = node.Items.Select(Pattern).ToList();
        if (node.StarIndex >= 0) items.Insert(node.StarIndex, At("MatchStar", node, ("name", node.StarName)));
        return items.ToArray();
    }

    static object Operator(string symbol) => Make(symbol switch
    {
        "+" => "Add", "-" => "Sub", "*" => "Mult", "@" => "MatMult", "/" => "Div", "%" => "Mod",
        "**" => "Pow", "//" => "FloorDiv", "<<" => "LShift", ">>" => "RShift", "|" => "BitOr", "^" => "BitXor", "&" => "BitAnd",
        "and" => "And", "or" => "Or", "not" => "Not", "~" => "Invert", "==" => "Eq", "!=" or "<>" => "NotEq",
        "<" => "Lt", "<=" => "LtE", ">" => "Gt", ">=" => "GtE", "is" => "Is", "is not" => "IsNot", "in" => "In", "not in" => "NotIn",
        _ => symbol,
    });

    static object? Constant(ConstExpr node) => node.Kind switch
    {
        ConstKind.None => null,
        ConstKind.Bool => node.Raw == "True",
        ConstKind.Str => node.Str,
        ConstKind.Bytes => Encoding.Latin1.GetBytes(node.Str),
        ConstKind.Ellipsis => DBNull.Value,
        ConstKind.Int => Integer(node.Raw.Replace("_", "")),
        ConstKind.Float => double.Parse(node.Raw.Replace("_", ""), CultureInfo.InvariantCulture),
        ConstKind.Imaginary => new Complex(0.0, double.Parse(node.Raw.Replace("_", ""), CultureInfo.InvariantCulture)),
        _ => throw new NotSupportedException(node.Kind.ToString()),
    };

    static object Integer(string text)
    {
        BigInteger value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? BigInteger.Parse("0" + text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? Radix(text, 2)
            : text.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? Radix(text, 8)
            : BigInteger.Parse(text, CultureInfo.InvariantCulture);
        return value <= long.MaxValue ? (object)(long)value : value;
    }

    static BigInteger Radix(string text, int radix) => text.Skip(2).Aggregate(BigInteger.Zero, (value, digit) => value * radix + digit - '0');
}
