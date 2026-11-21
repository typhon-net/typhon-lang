// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Typhon.Compiler.Semantics;

namespace Typhon.Compiler.Generator
{
    /// <summary>
    /// Compiles every *.ty file of a C# project (passed as AdditionalFiles by Typhon.targets) to C#, which Roslyn then
    /// compiles to IL together with the project's own C# files. Generated code carries #line directives, so compiler
    /// errors, stack traces and breakpoints point at the .ty source.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class TyphonGenerator : IIncrementalGenerator
    {
        sealed class Options
        {
            public string RootNamespace = "", ProjectDir = "", TypeMode = "warn", MainModule = "", IntMode = "";
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(ctx => ctx.AddSource("Typhon.Runtime.g.cs", SourceText.From(TyphonCompiler.RuntimeSource, System.Text.Encoding.UTF8)));

            var options = context.AnalyzerConfigOptionsProvider.Select((p, _) =>
            {
                var o = new Options();
                if (p.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns)) o.RootNamespace = ns;
                if (p.GlobalOptions.TryGetValue("build_property.TyphonProjectDir", out var dir) && dir.Length > 0)
                    o.ProjectDir = dir.Replace("[[semi]]", ";").Replace("[[hash]]", "#");
                else if (p.GlobalOptions.TryGetValue("build_property.ProjectDir", out var pd)) o.ProjectDir = pd;
                if (p.GlobalOptions.TryGetValue("build_property.TyphonTypeMode", out var mode)) o.TypeMode = mode;
                if (p.GlobalOptions.TryGetValue("build_property.TyphonMainModule", out var main)) o.MainModule = main;
                if (p.GlobalOptions.TryGetValue("build_property.TyphonIntMode", out var intMode)) o.IntMode = intMode;
                return o;
            });

            var files = context.AdditionalTextsProvider
                .Where(f => f.Path.EndsWith(TyphonCompiler.FileExtension, StringComparison.OrdinalIgnoreCase) || TyphonCompiler.IsGdScriptPath(f.Path))
                .Select((f, ct) => new KeyValuePair<string, string>(f.Path, f.GetText(ct)?.ToString() ?? ""));

            var all = files.Collect().Combine(options).Combine(context.CompilationProvider);
            context.RegisterSourceOutput(all, (spc, data) => Execute(spc, data.Left.Left, data.Left.Right, data.Right));
        }

        static void Execute(SourceProductionContext spc, ImmutableArray<KeyValuePair<string, string>> files, Options opt, Compilation compilation)
        {
            if (files.IsDefaultOrEmpty) return;
            var project = new ProjectContext
            {
                RootNamespace = opt.RootNamespace,
                TypeMode = TyphonCompiler.ParseTypeMode(opt.TypeMode),
                BigInts = TyphonCompiler.ParseIntMode(opt.IntMode),
                Resolver = new RoslynTypeResolver(compilation),
                MainModule = string.IsNullOrWhiteSpace(opt.MainModule) ? null : opt.MainModule.Trim(),
            };
            var modules = new List<ModuleInfo>();
            foreach (var f in files)
            {
                spc.CancellationToken.ThrowIfCancellationRequested();
                string name = TyphonCompiler.ModuleNameFromPath(opt.ProjectDir, f.Key);
                var m = TyphonCompiler.Parse(f.Key, f.Value, name, opt.RootNamespace);
                project.Modules[name] = m;
                modules.Add(m);
                foreach (var d in m.ParseDiagnostics) Report(spc, d);
            }
            foreach (var m in modules)
            {
                if (m.ParseDiagnostics.Count > 0) continue;
                var diags = new List<TyDiagnostic>();
                string code;
                try { code = TyphonCompiler.Emit(m, project, diags); }
                catch (Exception ex)
                {
                    Report(spc, new TyDiagnostic { Id = "TY9999", Severity = Severity.Error, Path = m.Path, Line = 1, Col = 1, Message = "internal Typhon compiler error: " + ex });
                    continue;
                }
                foreach (var d in diags) Report(spc, d);
                spc.AddSource(m.Name + (m.IsGdScript ? ".gd.g.cs" : ".ty.g.cs"), SourceText.From(code, System.Text.Encoding.UTF8));
            }
        }

        static readonly Dictionary<string, DiagnosticDescriptor> Descriptors = new Dictionary<string, DiagnosticDescriptor>();

        static void Report(SourceProductionContext spc, TyDiagnostic d)
        {
            var severity = d.Severity == Severity.Error ? DiagnosticSeverity.Error : d.Severity == Severity.Warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info;
            string key = d.Id + "/" + severity;
            DiagnosticDescriptor desc;
            lock (Descriptors)
            {
                if (!Descriptors.TryGetValue(key, out desc))
                    Descriptors[key] = desc = new DiagnosticDescriptor(d.Id, "Typhon " + d.Id, "{0}", "Typhon", severity, isEnabledByDefault: true);
            }
            var pos = new LinePosition(Math.Max(0, d.Line - 1), Math.Max(0, d.Col - 1));
            var location = Location.Create(d.Path, new TextSpan(0, 0), new LinePositionSpan(pos, pos));
            spc.ReportDiagnostic(Diagnostic.Create(desc, location, d.Message));
        }
    }
}
