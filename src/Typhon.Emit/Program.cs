// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Typhon.Compiler;
using Typhon.Compiler.Semantics;

// typhon-emit @args.rsp — compiles a project's Typhon sources to C# files (TyphonEmitToDisk, see build/Typhon.targets).
// The response file has one key=value per line:
//   out=DIR                 where the .cs files go (files of earlier runs that are no longer produced are deleted)
//   source=PATH             a Typhon module (.ty) or GDScript file (.gd); repeated
//   cs=PATH                 a C# source of the project, so Typhon code can use its types; repeated
//   ref=PATH                a referenced assembly (the project's resolved references); repeated
//   define=SYMBOL           a preprocessor symbol for parsing the C# sources; repeated
//   project-dir=DIR, root-namespace=NS, type-mode=strict|warn|permissive, int-mode=int64|bigint, main-module=NAME
// Diagnostics go to stdout in MSBuild's format (path(line,col): error TY0002: ...); the exit code is 1 on errors.
// Files are only rewritten when their text changes, so an unchanged project does not recompile.

var opts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
foreach (var arg in args)
{
    IEnumerable<string> lines = arg.StartsWith('@') ? File.ReadAllLines(arg[1..]) : new[] { arg };
    foreach (var raw in lines)
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        int eq = line.IndexOf('=');
        if (eq <= 0) { Console.Error.WriteLine($"typhon-emit: expected key=value, got '{line}'"); return 2; }
        string key = line[..eq].TrimStart('-'), value = line[(eq + 1)..];
        if (!opts.TryGetValue(key, out var list)) opts[key] = list = new List<string>();
        list.Add(value);
    }
}
string One(string key, string dflt = "") => opts.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : dflt;
List<string> Many(string key) => opts.TryGetValue(key, out var v) ? v : new List<string>();

string outDir = One("out");
if (outDir.Length == 0) { Console.Error.WriteLine("typhon-emit: out=DIR is required"); return 2; }
string projectDir = One("project-dir", Directory.GetCurrentDirectory());
string rootNs = One("root-namespace");

var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: Many("define").SelectMany(d => d.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(TyphonCompiler.RuntimeSource, parseOptions, path: "Typhon.Runtime.g.cs", encoding: Encoding.UTF8) };
string fullOut = Path.GetFullPath(outDir);
foreach (var cs in Many("cs").Distinct(StringComparer.OrdinalIgnoreCase))
{
    // (the project's C# sources, without what this tool wrote last time)
    if (Path.GetFullPath(cs).StartsWith(fullOut, StringComparison.OrdinalIgnoreCase) || !File.Exists(cs)) continue;
    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(cs), parseOptions, path: cs, encoding: Encoding.UTF8));
}
var references = Many("ref").Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
var compilation = CSharpCompilation.Create("typhon_emit", trees, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable, allowUnsafe: true));

var project = new ProjectContext
{
    RootNamespace = rootNs,
    TypeMode = TyphonCompiler.ParseTypeMode(One("type-mode", "warn")),
    BigInts = TyphonCompiler.ParseIntMode(One("int-mode")),
    Resolver = new RoslynTypeResolver(compilation),
    MainModule = One("main-module") is { Length: > 0 } mm ? mm : null,
};
var modules = new List<ModuleInfo>();
var diagnostics = new List<TyDiagnostic>();
foreach (var src in Many("source").Distinct(StringComparer.OrdinalIgnoreCase))
{
    string path = Path.GetFullPath(src);
    string name = TyphonCompiler.ModuleNameFromPath(projectDir, path);
    var m = TyphonCompiler.Parse(path, File.ReadAllText(path), name, rootNs);
    project.Modules[name] = m;
    modules.Add(m);
    diagnostics.AddRange(m.ParseDiagnostics);
}

Directory.CreateDirectory(fullOut);
var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
void Write(string fileName, string text)
{
    string path = Path.Combine(fullOut, fileName);
    written.Add(path);
    if (File.Exists(path) && File.ReadAllText(path) == text) return;
    File.WriteAllText(path, text, new UTF8Encoding(false));
}

var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var m in modules.Where(m => m.ParseDiagnostics.Count == 0))
{
    string code;
    try { code = TyphonCompiler.Emit(m, project, diagnostics); }
    catch (Exception ex)
    {
        diagnostics.Add(new TyDiagnostic { Id = "TY9999", Severity = Severity.Error, Path = m.Path, Line = 1, Col = 1, Message = "internal Typhon compiler error: " + ex });
        continue;
    }
    // a GDScript module's file is named after its script class: Godot's generators give a class a script path
    // (res://...) only when its file has the class's name
    string file = m.IsGdScript && m.GdScript?.ScriptClass is { } sc ? sc.Name + ".cs" : m.Name + ".ty.g.cs";
    if (!usedNames.Add(file)) file = m.Name + Path.GetExtension(m.Path) + ".g.cs";
    Write(file, code);
}
Write("Typhon.Runtime.g.cs", TyphonCompiler.RuntimeSource);
foreach (var stale in Directory.GetFiles(fullOut, "*.cs").Where(f => !written.Contains(f))) File.Delete(stale);

foreach (var d in diagnostics) Console.WriteLine(d.ToString());
return diagnostics.Any(d => d.Severity == Severity.Error) ? 1 : 0;
