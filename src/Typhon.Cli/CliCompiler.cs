// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Typhon.Compiler;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Cli;

/// <summary>A source file the compilation read (with its SHA-256), or a module path it probed that did not exist (null).</summary>
internal sealed record SourceProbe(string Path, string? Sha256);

internal enum CompileTarget
{
    /// <summary>In memory for `typhon run` / `check`: references the precompiled Typhon.Runtime.dll (fast).</summary>
    Script,
    /// <summary>`typhon build` / `emit` / Native AOT: the runtime is compiled into the program, which needs nothing else.</summary>
    Build,
}

internal sealed class CompileOutput
{
    public bool Success;
    public readonly List<string> Messages = new();
    public byte[]? Pe, Pdb;
    public string? EntryCode;
    public readonly List<SourceProbe> Probes = new();
    public CSharpCompilation? Compilation;
    public readonly List<SyntaxTree> Trees = new();
    /// <summary>Shared frameworks besides Microsoft.NETCore.App the program references (Microsoft.AspNetCore.App).</summary>
    public readonly List<string> Frameworks = new();
    /// <summary>The #:package directives, and the implementation assemblies of the restored packages (loaded when it runs).</summary>
    public readonly List<PackageRef> Packages = new();
    public readonly List<string> PackageAssemblies = new();
    public long ReferencesMs, FrontMs, RoslynMs;
}

/// <summary>
/// Metadata references for compiling Typhon programs: the running .NET runtime's assemblies plus the Typhon libraries
/// next to the CLI. Creating them is not free, so the compile server keeps one set for its lifetime (Roslyn then also
/// reuses the assembly symbols it built from them).
/// </summary>
internal sealed class ReferenceSet
{
    public readonly List<MetadataReference> Script = new();
    public readonly List<MetadataReference> Build = new();

    public ReferenceSet()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "Typhon.*.dll"))
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && seen.Add(Path.GetFileName(p)));
        foreach (var p in paths)
        {
            string name = Path.GetFileName(p);
            if (name.StartsWith("Typhon.Compiler", StringComparison.OrdinalIgnoreCase)) continue;
            var reference = MetadataReference.CreateFromFile(p);
            Script.Add(reference);
            // a built program carries its own (embedded) copy of the runtime
            if (!name.Equals("Typhon.Runtime.dll", StringComparison.OrdinalIgnoreCase)) Build.Add(reference);
        }
        _baseNames = seen;
    }

    readonly HashSet<string> _baseNames;
    readonly Dictionary<string, List<MetadataReference>> _frameworks = new();

    /// <summary>The references for a target, plus those of the shared frameworks the program uses (created once per set).</summary>
    public List<MetadataReference> For(CompileTarget target, IReadOnlyCollection<string> frameworks)
    {
        var refs = target == CompileTarget.Script ? Script : Build;
        if (frameworks.Count == 0) return refs;
        var all = new List<MetadataReference>(refs);
        lock (_frameworks)
            foreach (var name in frameworks)
            {
                if (!_frameworks.TryGetValue(name, out var extra))
                    _frameworks[name] = extra = SharedFrameworks.Assemblies(name).Where(p => !_baseNames.Contains(Path.GetFileName(p)))
                                                                              .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
                all.AddRange(extra);
            }
        return all;
    }
}

/// <summary>Compiles a Typhon program (the entry file plus the sibling modules it imports) with Roslyn.</summary>
internal static class CliCompiler
{
    public const string ScriptAssemblyName = "typhon_script"; // must match Typhon.Runtime's InternalsVisibleTo
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>The CLI passes the type mode (0 strict, 1 warn, 2 permissive) and big-int mode (BigIntFlag) as one int.</summary>
    public const int BigIntFlag = 0x10;
    public static TypeMode TypeModeOf(int modeBits) => (TypeMode)(modeBits & 0xF);
    public static bool BigIntsOf(int modeBits) => (modeBits & BigIntFlag) != 0;

    public static CompileOutput Compile(string entryPath, TypeMode mode, CompileTarget target, ReferenceSet? refs = null, bool csharpOnly = false, bool bigInts = false)
    {
        var output = new CompileOutput();
        var sw = Stopwatch.StartNew();
        refs ??= new ReferenceSet();
        output.ReferencesMs = sw.ElapsedMilliseconds;

        // The entry file is the __main__ module; sibling .ty files it imports (transitively) are compiled with it.
        // Every path probed is recorded, so a cached build can tell when any of them changes or appears.
        sw.Restart();
        var project = new ProjectContext { TypeMode = mode, BigInts = bigInts, MainModule = "__main__" };
        string dir = Path.GetDirectoryName(entryPath)!;
        var modules = new List<ModuleInfo>();
        var pending = new Queue<ModuleInfo>();
        var probedModules = new HashSet<string>(StringComparer.Ordinal);
        var sourcePaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            Path.GetFullPath(entryPath),
        };
        string? aspNetReason = null;
        // #:pylib: modules neither the folder nor the runtime has come from CPython's standard library source
        string? pylib = null;
        ModuleInfo ReadModule(string path, string name, bool isPackage)
        {
            string source = ReadSource(path, output.Probes);
            if (aspNetReason == null && SharedFrameworks.HasWebSdkDirective(source)) aspNetReason = $"{Path.GetFileName(path)} has #:sdk Microsoft.NET.Sdk.Web";
            foreach (var package in Packages.Directives(source))
                if (!output.Packages.Any(x => string.Equals(x.Id, package.Id, StringComparison.OrdinalIgnoreCase))) output.Packages.Add(package);
            if (name == "__main__" && PyLib.Requested(source, dir, out var givenLib))
            {
                pylib = PyLib.Resolve(givenLib);
                if (pylib != null) pylib = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pylib));
                if (pylib == null) output.Messages.Add($"{path}: warning TY0102: #:pylib: no CPython standard library found (give #:pylib DIR or set TYPHON_PYLIB)");
            }
            var m = TyphonCompiler.Parse(path, source, name, "");
            if (m.BigIntPragma) project.BigInts = true;
            if (isPackage)
            {
                m.IsPackage = true;
                m.Namespace = name;
                m.ClassName = "__init__";
            }
            project.Modules[name] = m;
            modules.Add(m);
            pending.Enqueue(m);
            return m;
        }

        var runtimePackages = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsRuntimePackage(string name)
        {
            if (pylib == null || !PyLib.ProvidedByTyphon(name, project.BigInts)) return false;
            if (runtimePackages.TryGetValue(name, out bool cached)) return cached;
            var (initializer, _) = PyLib.SourceCandidates(pylib, name).First();
            bool exists = File.Exists(initializer);
            if (exists) ReadSource(initializer, output.Probes);
            else output.Probes.Add(new SourceProbe(initializer, null));
            return runtimePackages[name] = exists;
        }

        ModuleInfo? Discover(string name)
        {
            if (project.FindModule(name) is ModuleInfo seen) return seen;
            if (!probedModules.Add(name)) return null;
            int dot = name.LastIndexOf('.');
            string parentName = dot < 0 ? "" : name[..dot];
            var parent = dot < 0 ? null : Discover(parentName);
            IEnumerable<(string Path, bool Package)> choices = PyLib.SourceCandidates(dir, name, TyphonCompiler.FileExtension);
            // A library child may be supplied by source even if its package is provided by the runtime.
            if (pylib != null && !PyLib.ProvidedByTyphon(name, project.BigInts) &&
                (dot < 0 || parent?.IsPackage == true && parent.Path.StartsWith(Path.EndsInDirectorySeparator(pylib) ? pylib : pylib + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                 parent == null && IsRuntimePackage(parentName)))
                choices = choices.Concat(PyLib.SourceCandidates(pylib, name));
            foreach (var (candidate, package) in choices)
            {
                if (File.Exists(candidate))
                    return sourcePaths.Add(candidate) ? ReadModule(candidate, name, package) : null;
                output.Probes.Add(new SourceProbe(candidate, null));
            }
            return null;
        }

        ReadModule(entryPath, "__main__", isPackage: false);
        while (pending.TryDequeue(out var m))
        {
            ModuleInfo? Import(string name)
            {
                if (aspNetReason == null && SharedFrameworks.IsAspNetCoreNamespace(name)) aspNetReason = $"{Path.GetFileName(m.Path)} imports {name}";
                return Discover(name);
            }
            foreach (var stmt in TyphonCompiler.Imports(m))
            {
                if (stmt is ImportStmt direct)
                    foreach (var alias in direct.Names) Import(alias.Name);
                else if (stmt is ImportFromStmt from && m.ResolveImport(from.Module, from.Level) is string name)
                {
                    var imported = Import(name);
                    if (imported is ModuleInfo { IsPackage: true } package)
                    {
                        foreach (var alias in from.Names)
                            if (alias.Name != "*" && !package.Classes.ContainsKey(alias.Name) &&
                                !package.Functions.Contains(alias.Name) && !package.Globals.Contains(alias.Name))
                                Discover(name + "." + alias.Name);
                    }
                    else if (imported == null && IsRuntimePackage(name))
                    {
                        foreach (var alias in from.Names)
                            if (alias.Name != "*" && !PyLib.ProvidesExport(name, alias.Name))
                                Discover(name + "." + alias.Name);
                    }
                }
            }
        }

        // C# reserves the containing class name, which Python permits as a module member.
        // Keep replacement names distinct from all discovered module classes and namespaces.
        bool ConflictsWithMember(ModuleInfo m) =>
            m.Functions.Contains(m.ClassName) || m.Globals.Contains(m.ClassName) || m.Classes.ContainsKey(m.ClassName);
        var occupiedNames = modules.Select(m => m.QualifiedClassName).ToHashSet(StringComparer.Ordinal);
        foreach (var m in modules)
            for (string ns = m.Namespace; ns.Length > 0; ns = ns[..Math.Max(0, ns.LastIndexOf('.'))])
                occupiedNames.Add("global::" + ns);
        foreach (var m in modules.Where(ConflictsWithMember))
        {
            do { m.ClassName = "_" + m.ClassName; }
            while (ConflictsWithMember(m) || !occupiedNames.Add(m.QualifiedClassName));
        }

        // the shared frameworks the imports need (decided before the compilation: they are its references)
        if (aspNetReason != null)
        {
            if (SharedFrameworks.Directory(SharedFrameworks.AspNetCore) == null)
            {
                output.Messages.Add(SharedFrameworks.NotInstalled(SharedFrameworks.AspNetCore, aspNetReason));
                return output;
            }
            output.Frameworks.Add(SharedFrameworks.AspNetCore);
        }
        // the #:package packages (restored once; their reference assemblies join the compilation's references)
        var references = refs.For(target, output.Frameworks);
        if (output.Packages.Count > 0)
        {
            var restored = Packages.Restore(output.Packages, out var restoreError);
            if (restored == null) { output.Messages.Add(restoreError!); return output; }
            references = new List<MetadataReference>(references);
            references.AddRange(restored.Compile.Select(PackageReference));
            output.PackageAssemblies.AddRange(restored.Runtime);
        }
        var trees = new List<SyntaxTree>();
        if (target == CompileTarget.Build)
            trees.Add(CSharpSyntaxTree.ParseText(TyphonCompiler.RuntimeSource, ParseOptions, path: "Typhon.Runtime.g.cs", encoding: Encoding.UTF8));
        var compilation = CSharpCompilation.Create(
            target == CompileTarget.Script ? ScriptAssemblyName : "typhon_" + Path.GetFileNameWithoutExtension(entryPath),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
        project.Resolver = new RoslynTypeResolver(compilation);

        var diagnostics = new List<TyDiagnostic>();
        foreach (var m in modules) diagnostics.AddRange(m.ParseDiagnostics);
        foreach (var m in modules.Where(m => m.ParseDiagnostics.Count == 0))
        {
            string code = TyphonCompiler.Emit(m, project, diagnostics);
            if (m.Name == "__main__") output.EntryCode = code;
            output.Trees.Add(CSharpSyntaxTree.ParseText(code, ParseOptions, path: m.Path + ".g.cs", encoding: Encoding.UTF8));
        }
        output.FrontMs = sw.ElapsedMilliseconds;
        // (warnings in the standard library's own source are its business: implicit dynamic code is expected there)
        foreach (var d in diagnostics)
            if (!(pylib != null && d.Severity != Severity.Error && d.Path != null && d.Path.StartsWith(pylib, StringComparison.Ordinal))) output.Messages.Add(d.ToString());
        bool hasErrors = diagnostics.Any(d => d.Severity == Severity.Error);
        if (csharpOnly) { output.Success = !hasErrors; return output; }

        sw.Restart();
        compilation = compilation.AddSyntaxTrees(output.Trees);
        // a module with no code of its own to run (only definitions: a library module) has no Main: compile it as a library
        if (!output.Trees.Any(t => t.GetText().ToString().Contains(" Main(string[] __argv)")))
            compilation = compilation.WithOptions(compilation.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
        output.Compilation = compilation;
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        output.RoslynMs = sw.ElapsedMilliseconds;
        // errors, and warnings in the program's own lines (not the generated C#, not referenced assemblies: no source path)
        foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error ||
                                                         (d.Severity == DiagnosticSeverity.Warning && d.Location.GetMappedLineSpan().Path is string wp && wp.Length > 0 && !wp.EndsWith(".g.cs"))))
        {
            var span = d.Location.GetMappedLineSpan();
            string where = string.IsNullOrEmpty(span.Path) ? "typhon" : $"{span.Path}({span.StartLinePosition.Line + 1})";
            if (pylib != null && d.Severity == DiagnosticSeverity.Warning && span.Path.StartsWith(pylib, StringComparison.Ordinal)) continue;
            output.Messages.Add($"{where}: {d.Severity.ToString().ToLowerInvariant()} {d.Id}: {d.GetMessage()}");
            hasErrors |= d.Severity == DiagnosticSeverity.Error;
        }
        output.Success = result.Success && !hasErrors;
        if (output.Success) { output.Pe = pe.ToArray(); output.Pdb = pdb.ToArray(); }
        return output;
    }

    static readonly Dictionary<string, MetadataReference> _packageReferences = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A package assembly as a reference (kept, so a compile server reuses it and Roslyn its symbols).</summary>
    static MetadataReference PackageReference(string path)
    {
        lock (_packageReferences)
        {
            if (!_packageReferences.TryGetValue(path, out var r)) _packageReferences[path] = r = MetadataReference.CreateFromFile(path);
            return r;
        }
    }

    static string ReadSource(string path, List<SourceProbe> probes)
    {
        byte[] bytes = File.ReadAllBytes(path);
        probes.Add(new SourceProbe(path, Sha256(bytes)));
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

}
