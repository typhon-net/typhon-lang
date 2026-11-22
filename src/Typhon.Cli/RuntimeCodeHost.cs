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

    public static void Install()
    {
        AppContext.SetData("Typhon.CheckRuntimeSyntax", (Func<string, string, string, string?>)CheckSyntax);
        AppContext.SetData("Typhon.RunRuntimeCode",
            (Func<string, string, string, IDictionary, IDictionary, (object? Value, string? Error)>)Run);
    }

    static ModuleInfo Parse(string source, string filename) =>
        TyphonCompiler.Parse(TyphonCompiler.IsGdScriptPath(filename) ? filename + ".py" : filename, source, "__main__", "");

    static string? SyntaxError(ModuleInfo module, string mode)
    {
        if (module.ParseDiagnostics.FirstOrDefault() is TyDiagnostic error) return error.Message;
        if (mode == "eval" && (module.Ast.Body.Count != 1 || module.Ast.Body[0] is not ExprStmt))
            return "invalid syntax";
        if (mode == "single" && module.Ast.Body.Select(statement => statement.Line).Distinct().Skip(1).Any())
            return "multiple statements found while compiling a single statement";
        return null;
    }

    static string? CheckSyntax(string source, string filename, string mode) => SyntaxError(Parse(source, filename), mode);

    static (object? Value, string? Error) Run(string source, string filename, string mode, IDictionary globals, IDictionary locals)
    {
        var module = Parse(source, filename);
        if (SyntaxError(module, mode) is string syntax) return (null, syntax);
        module.RuntimeCodeMode = mode;
        module.ClassName = ModuleType;
        module.Path = filename;
        foreach (string name in globals.Keys.OfType<string>().Concat(locals.Keys.OfType<string>()).Where(IsIdentifier))
            module.RuntimeBindings.Add(name);

        if (mode == "eval" && module.Ast.Body[0] is ExprStmt expression)
            module.Ast.Body[0] = new ReturnStmt { Value = expression.Value, Line = expression.Line, Col = expression.Col };

        var compilation = CSharpCompilation.Create(CliCompiler.ScriptAssemblyName, references: References.Value.Script,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var project = new ProjectContext { MainModule = "__main__", TypeMode = TypeMode.Permissive, Resolver = new RoslynTypeResolver(compilation) };
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
