// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Typhon.Compiler;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Cli;

/// <summary>Uses the Typhon compiler for snippets passed to compile, exec, and eval in a CLI-hosted program.</summary>
internal static class RuntimeCodeHost
{
    static readonly Lazy<ReferenceSet> References = new(() => new ReferenceSet());
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    const string ModuleType = "__typhon_code";
    const long DontImplyDedent = 0x200, AllowIncompleteInput = 0x4000;
    const long BarryAsFlufl = 0x400000, FutureAnnotations = 0x1000000;
    const long FutureMask = 0x1fe0000;

    public static void Install()
    {
        AppContext.SetData("Typhon.ParseRuntimeAst",
            (Func<string, string, string, (object? Tree, string? Error, int Line, int Column, bool Indent)>)RuntimeAstHost.Parse);
        AppContext.SetData("Typhon.CheckRuntimeSyntax",
            (Func<string, string, string, long, (string? Error, int Line, int Column, int Kind, long Flags)>)CheckSyntax);
        AppContext.SetData("Typhon.RunRuntimeCode",
            (Func<string, string, string, long, IDictionary, IDictionary, bool, (object? Value, string? Error)>)Run);
    }

    static ModuleInfo Parse(string source, string filename, string mode, long flags) =>
        TyphonCompiler.Parse(TyphonCompiler.IsGdScriptPath(filename) ? filename + ".py" : filename, source, "__main__", "",
            barryAsFlufl: (flags & BarryAsFlufl) != 0, runtimeCode: true, expressionOnly: mode == "eval");

    static (string? Error, int Line, int Column, int Kind, long Flags) CheckSyntax(string source, string filename, string mode, long flags) =>
        CheckSyntax(Parse(source, filename, mode, flags), source, mode, flags);

    static (string? Error, int Line, int Column, int Kind, long Flags) CheckSyntax(ModuleInfo module, string source, string mode, long flags)
    {
        const int Syntax = 0, Incomplete = 1, Indentation = 2;
        bool allowIncomplete = (flags & AllowIncompleteInput) != 0;
        bool terminated = source.EndsWith('\n') || source.EndsWith('\r');
        if (module.ParseDiagnostics.FirstOrDefault() is TyDiagnostic error)
        {
            bool needsInput = error.NeedsMoreInput || mode != "exec" && !terminated && error.AtEndOfInput;
            if (allowIncomplete && needsInput) return ("incomplete input", error.Line, error.Col, Incomplete, 0);
            return (error.Message, error.Line, error.Col, error.IsIndentationError ? Indentation : Syntax, 0);
        }

        var body = module.Ast.Body;
        if (body.Count == 0 && mode != "exec") return allowIncomplete
            ? ("incomplete input", 1, 1, Incomplete, 0) : ("invalid syntax", 1, 1, Syntax, 0);
        if (mode == "eval" && (body.Count != 1 || body[0] is not ExprStmt))
            return ("invalid syntax", body[0].Line, body[0].Col, Syntax, 0);
        if (mode == "single")
        {
            if (body.FirstOrDefault(statement => statement.Line != body[0].Line) is Stmt extra)
                return ("multiple statements found while compiling a single statement", extra.Line, extra.Col, Syntax, 0);
            var suite = FirstSuite(body[0]);
            if (!terminated && suite is { Count: > 0 } && ((flags & DontImplyDedent) != 0 || suite[0].Line == body[0].Line))
                return allowIncomplete ? ("incomplete input", body[0].Line, body[0].Col, Incomplete, 0) :
                    ("invalid syntax", body[0].Line, body[0].Col, Syntax, 0);
        }

        long future = flags & FutureMask;
        bool header = true;
        foreach (var statement in body)
        {
            if (statement is ImportFromStmt { Level: 0, Module: "__future__" } from)
            {
                if (!header) return ("from __future__ imports must occur at the beginning of the file", from.Line, from.Col, Syntax, 0);
                foreach (var feature in from.Names)
                {
                    switch (feature.Name)
                    {
                        case "annotations": future |= FutureAnnotations; break;
                        case "barry_as_FLUFL": future |= BarryAsFlufl; break;
                        case "nested_scopes": case "generators": case "division": case "absolute_import": case "with_statement":
                        case "print_function": case "unicode_literals": case "generator_stop": break;
                        default: return ($"future feature {feature.Name} is not defined", from.Line, from.Col, Syntax, 0);
                    }
                }
            }
            else if (statement != body[0] || statement is not ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) header = false;
        }
        return (null, 0, 0, 0, future);
    }

    static List<Stmt>? FirstSuite(Stmt statement) => statement switch
    {
        FunctionDef function => function.Body, ClassDef type => type.Body, IfStmt conditional => conditional.Body,
        WhileStmt loop => loop.Body, ForStmt loop => loop.Body, TryStmt handler => handler.Body, WithStmt with => with.Body,
        MatchStmt match => match.Cases.FirstOrDefault()?.Body, _ => null,
    };

    static (object? Value, string? Error) Run(string source, string filename, string mode, long flags, IDictionary globals, IDictionary locals, bool profiling)
    {
        var module = Parse(source, filename, mode, flags);
        if (CheckSyntax(module, source, mode, flags).Error is string syntax) return (null, syntax);
        module.RuntimeCodeMode = mode;
        module.ClassName = ModuleType;
        module.Path = filename;
        foreach (string name in globals.Keys.OfType<string>().Concat(locals.Keys.OfType<string>()).Where(IsIdentifier))
            module.RuntimeBindings.Add(name);

        if (mode == "eval" && module.Ast.Body[0] is ExprStmt expression)
            module.Ast.Body[0] = new ReturnStmt { Value = expression.Value, Line = expression.Line, Col = expression.Col };

        var compilation = CSharpCompilation.Create(CliCompiler.ScriptAssemblyName, references: References.Value.Script,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var project = new ProjectContext
        {
            MainModule = "__main__", TypeMode = TypeMode.Permissive,
            Resolver = new RoslynTypeResolver(compilation), ProfileRuntimeCode = profiling,
        };
        project.Modules.Add(module.Name, module);
        var diagnostics = new List<TyDiagnostic>();
        string csharp = TyphonCompiler.Emit(module, project, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == Severity.Error) is TyDiagnostic diagnostic) return (null, diagnostic.Message);
        var tree = CSharpSyntaxTree.ParseText(csharp, ParseOptions, filename + ".g.cs", Encoding.UTF8);
        compilation = compilation.AddSyntaxTrees(tree);
        if (mode == "single")
        {
            var root = tree.GetRoot();
            var semantics = compilation.GetSemanticModel(tree);
            var rewritten = new VoidDisplayCalls(semantics).Visit(root);
            if (rewritten != root) compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(rewritten!, ParseOptions));
        }
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success) return (null, result.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).GetMessage());
        pe.Position = pdb.Position = 0;
        var context = new SnippetContext();
        try
        {
            var assembly = context.LoadFromStream(pe, pdb);
            var entry = assembly.GetType(ModuleType, throwOnError: true)!.GetMethod("ExecutePythonCode")!;
            return (entry.Invoke(null, [globals, locals]), null);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
        finally { context.Unload(); }
    }

    static bool IsIdentifier(string name) => name.Length > 0 && (name[0] == '_' || char.IsLetter(name[0])) &&
        name.All(c => c == '_' || char.IsLetterOrDigit(c));

    // A statically void CLR call already has Python's implicit None result.
    // Drop the display wrapper after Roslyn has chosen its actual overload.
    sealed class VoidDisplayCalls(SemanticModel semantics) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == "__display_runtime_result" &&
                node.ArgumentList.Arguments.Count == 1 &&
                node.ArgumentList.Arguments[0].Expression is ExpressionSyntax argument &&
                semantics.GetTypeInfo(argument).Type?.SpecialType == SpecialType.System_Void)
                return argument.WithTriviaFrom(node);
            return base.VisitInvocationExpression(node);
        }
    }

    sealed class SnippetContext : AssemblyLoadContext
    {
        public SnippetContext() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
