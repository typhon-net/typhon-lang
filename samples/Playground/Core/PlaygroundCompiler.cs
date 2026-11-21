// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Typhon.Compiler;
using Typhon.Compiler.Semantics;

namespace Typhon.Playground;

/// <summary>Options of a playground compile: the CLI's --strict / --permissive and --bigint.</summary>
public sealed record PlaygroundOptions(TypeMode TypeMode = TypeMode.Warn, bool BigInts = false);

public enum DiagnosticOrigin { Typhon, CSharp }

/// <summary>A Typhon or C# diagnostic: <see cref="Line"/> is in the Typhon source (0 when it has no Typhon line),
/// <see cref="CSharpLine"/> in the generated C# (C# diagnostics only).</summary>
public sealed record PlaygroundDiagnostic(DiagnosticOrigin Origin, string Severity, string Id, int Line, int Column, string Message, int CSharpLine = 0)
{
    public bool IsError => Severity == "error";

    public override string ToString() => (Line > 0 ? $"{PlaygroundCompiler.FileName}({Line},{Column})"
                                          : CSharpLine > 0 ? $"{PlaygroundCompiler.FileName}.g.cs({CSharpLine},{Column})"
                                          : PlaygroundCompiler.FileName) + $": {Severity} {Id}: {Message}";
}

/// <summary>What a compile produced so far: the C# after <see cref="PlaygroundCompiler.Translate"/>, the assembly after
/// <see cref="PlaygroundCompiler.Emit"/>.</summary>
public sealed class CompileResult
{
    /// <summary>The C# Typhon generated for the program (null when it did not parse).</summary>
    public string? CSharp { get; internal set; }
    public List<PlaygroundDiagnostic> Diagnostics { get; } = new();
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
    /// <summary>The program assembly and its portable PDB (null until emitted, or when there are errors).</summary>
    public byte[]? Pe { get; internal set; }
    public byte[]? Pdb { get; internal set; }
    public bool Success => Pe != null;
    /// <summary>Typhon → C# (parse, inference, emit), and C# → IL (Roslyn).</summary>
    public TimeSpan TranslateTime { get; internal set; }
    public TimeSpan EmitTime { get; internal set; }

    internal SyntaxTree? Tree;
}

/// <summary>
/// Compiles a single-file Typhon program in memory, like <c>typhon run</c> (src/Typhon.Cli/CliCompiler.cs, Script target):
/// Typhon → C#, then Roslyn → IL against the precompiled Typhon.Runtime.dll, in an assembly named
/// <see cref="ScriptAssemblyName"/> so it may use the runtime's internal types. Nothing touches the file system: the
/// references are created from byte arrays (in the browser, fetched over HTTP). One instance keeps one set of
/// references for its lifetime, so Roslyn builds their symbols once. Not thread-safe.
/// </summary>
public sealed class PlaygroundCompiler
{
    public const string FileName = "main.ty";
    public const string RuntimeFileName = "Typhon.Runtime.dll";
    /// <summary>Must match the InternalsVisibleTo of Typhon.Runtime (src/Typhon.Runtime/RuntimeAssembly.cs).</summary>
    public const string ScriptAssemblyName = "typhon_script";

    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    static readonly EmitOptions EmitOptions = new(debugInformationFormat: DebugInformationFormat.PortablePdb);

    /// <summary>The references and no source: every compile adds its syntax tree to it, reusing its reference symbols.</summary>
    readonly CSharpCompilation _empty;

    public PlaygroundCompiler(IEnumerable<MetadataReference> references)
    {
        _empty = CSharpCompilation.Create(ScriptAssemblyName, null, references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true,
                                         nullableContextOptions: NullableContextOptions.Disable));
    }

    /// <summary>The compiler for a set of assembly images (file name → bytes), which must include Typhon.Runtime.dll.</summary>
    public static PlaygroundCompiler FromImages(IEnumerable<KeyValuePair<string, byte[]>> images)
    {
        var list = images.ToList();
        if (!list.Any(i => i.Key.Equals(RuntimeFileName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"the references must include {RuntimeFileName}", nameof(images));
        return new PlaygroundCompiler(list.Select(i => (MetadataReference)MetadataReference.CreateFromImage(
            ImmutableCollectionsMarshal.AsImmutableArray(i.Value), filePath: i.Key)));
    }

    /// <summary>Typhon → C#, then C# → IL.</summary>
    public CompileResult Compile(string source, PlaygroundOptions? options = null)
    {
        var result = Translate(source, options);
        Emit(result);
        return result;
    }

    /// <summary>Typhon → C#: parses the program, infers its types and generates C# (and the Typhon diagnostics).</summary>
    public CompileResult Translate(string source, PlaygroundOptions? options = null)
    {
        options ??= new PlaygroundOptions();
        var result = new CompileResult();
        var sw = Stopwatch.StartNew();
        try
        {
            var project = new ProjectContext { TypeMode = options.TypeMode, BigInts = options.BigInts, MainModule = "__main__" };
            var module = TyphonCompiler.Parse(FileName, source, "__main__", "");
            project.Modules[module.Name] = module;
            project.Resolver = new RoslynTypeResolver(_empty);
            var diagnostics = new List<TyDiagnostic>(module.ParseDiagnostics);
            if (module.ParseDiagnostics.Count == 0)
            {
                result.CSharp = TyphonCompiler.Emit(module, project, diagnostics);
                result.Tree = CSharpSyntaxTree.ParseText(result.CSharp, ParseOptions, path: FileName + ".g.cs", encoding: Encoding.UTF8);
            }
            foreach (var d in diagnostics)
                result.Diagnostics.Add(new PlaygroundDiagnostic(DiagnosticOrigin.Typhon, d.Severity.ToString().ToLowerInvariant(), d.Id, d.Line, d.Col, d.Message));
        }
        catch (Exception e)
        {
            result.Tree = null;
            result.Diagnostics.Add(InternalError(e));
        }
        result.TranslateTime = sw.Elapsed;
        return result;
    }

    /// <summary>C# → IL with Roslyn, unless the translation failed; fills in <see cref="CompileResult.Pe"/> on success.</summary>
    public void Emit(CompileResult result)
    {
        if (result.Tree == null || result.HasErrors) return;
        var sw = Stopwatch.StartNew();
        try
        {
            var compilation = _empty.AddSyntaxTrees(result.Tree);
            using var pe = new MemoryStream();
            using var pdb = new MemoryStream();
            var emitted = compilation.Emit(pe, pdb, options: EmitOptions);
            // as the CLI reports them: every error, and the warnings Roslyn maps to the Typhon source (#line)
            foreach (var d in emitted.Diagnostics)
            {
                var mapped = d.Location.GetMappedLineSpan();
                bool inTyphon = mapped.HasMappedPath;
                if (d.Severity != DiagnosticSeverity.Error && !(d.Severity == DiagnosticSeverity.Warning && inTyphon)) continue;
                var csharp = d.Location.GetLineSpan();
                result.Diagnostics.Add(new PlaygroundDiagnostic(DiagnosticOrigin.CSharp, d.Severity.ToString().ToLowerInvariant(), d.Id,
                    inTyphon ? mapped.StartLinePosition.Line + 1 : 0,
                    (inTyphon ? mapped : csharp).StartLinePosition.Character + 1, d.GetMessage(),
                    d.Location.IsInSource ? csharp.StartLinePosition.Line + 1 : 0));
            }
            if (emitted.Success && !result.HasErrors) { result.Pe = pe.ToArray(); result.Pdb = pdb.ToArray(); }
        }
        catch (Exception e)
        {
            result.Diagnostics.Add(InternalError(e));
        }
        result.EmitTime = sw.Elapsed;
    }

    static PlaygroundDiagnostic InternalError(Exception e) =>
        new(DiagnosticOrigin.Typhon, "error", "TY9999", 0, 0, $"internal compiler error: {e.GetType().Name}: {e.Message}");
}
