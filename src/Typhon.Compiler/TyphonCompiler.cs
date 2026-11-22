// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Typhon.Compiler.Emit;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler
{
    /// <summary>How the compiler treats types it cannot determine statically (TypeScript-style noImplicitAny).</summary>
    public enum TypeMode
    {
        /// <summary>Implicit 'dynamic' is a compile error. Explicit 'Any' is still allowed. AOT/trimming friendly.</summary>
        Strict,
        /// <summary>Implicit 'dynamic' compiles, with a warning per occurrence (default).</summary>
        Warn,
        /// <summary>Implicit 'dynamic' compiles silently. For porting existing Python code.</summary>
        Permissive,
    }

    public enum Severity { Info, Warning, Error }

    public sealed class TyDiagnostic
    {
        public string Id;
        public Severity Severity;
        public string Message;
        public string Path;
        public int Line;
        public int Col;
        public bool AtEndOfInput;
        public bool NeedsMoreInput;
        public bool IsIndentationError;
        public override string ToString() => $"{Path}({Line},{Col}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
    }

    public sealed class ModuleInfo
    {
        public string Name;          // dotted python module name, e.g. "utils.strings"
        public string ClassName;     // C# static class name, e.g. "strings"
        public string Namespace;     // C# namespace, e.g. "MyApp.utils"
        public string Path;
        internal string SourceText;
        public ModuleAst Ast;
        public readonly Dictionary<string, ClassDef> Classes = new Dictionary<string, ClassDef>();
        public readonly HashSet<string> Functions = new HashSet<string>();
        public readonly HashSet<string> Globals = new HashSet<string>();
        internal readonly HashSet<string> ConditionalGlobals = new HashSet<string>();
        internal readonly HashSet<string> ReboundFunctions = new HashSet<string>();
        internal readonly HashSet<string> ReboundClasses = new HashSet<string>();
        internal readonly List<FunctionDef> ReboundImplementations = new List<FunctionDef>();
        /// <summary>The source changes its module namespace through globals()[key].</summary>
        internal bool WritesModuleGlobals;
        internal bool MayReplaceModule;
        /// <summary>Module-level type aliases: EdgeT = tuple[int, int], Matrix = list[list[float]], X: TypeAlias = ...</summary>
        public readonly Dictionary<string, Expr> TypeAliases = new Dictionary<string, Expr>();
        internal readonly HashSet<string> RuntimeTypeVariables = new HashSet<string>();
        public readonly List<TyDiagnostic> ParseDiagnostics = new List<TyDiagnostic>();
        /// <summary>The module asks for big-int mode with a "# typhon: int=bigint" comment (it applies to the whole program).</summary>
        public bool BigIntPragma;
        /// <summary>A GDScript (.gd) module: parsed by the GDScript front end, emitted with GDScript's semantics where they differ.</summary>
        public bool IsGdScript;
        /// <summary>The module is the initializer of a Python package.</summary>
        public bool IsPackage;
        /// <summary>Execution mode for a snippet compiled by the host at run time, or null for a regular module.</summary>
        public string RuntimeCodeMode;
        public readonly HashSet<string> RuntimeBindings = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>What the GDScript front end knows about a .gd module (its script class, exports, inner classes).</summary>
        public GdScriptModule GdScript;

        public string QualifiedClassName => "global::" + (string.IsNullOrEmpty(Namespace) ? "" : Namespace + ".") + ClassName;
        public bool IsMainModule => Name == "__main__" || Name.EndsWith(".__main__");
        public string PackageName => IsPackage ? Name : Name.Substring(0, Math.Max(0, Name.LastIndexOf('.')));

        /// <summary>The absolute name of an import from this module, or null when its level goes beyond the package.</summary>
        public string ResolveImport(string module, int level)
        {
            if (level == 0) return module;
            string package = PackageName;
            for (int i = 1; i < level && package.Length > 0; i++)
                package = package.Substring(0, Math.Max(0, package.LastIndexOf('.')));
            return package.Length == 0 ? null : module.Length == 0 ? package : package + "." + module;
        }
    }

    public sealed class ProjectContext
    {
        public string RootNamespace = "";
        public TypeMode TypeMode = TypeMode.Warn;
        /// <summary>
        /// Big-int mode (typhon --bigint, TyphonIntMode=bigint, or a "# typhon: int=bigint" comment in any module): Python's int
        /// is System.Numerics.BigInteger (unbounded, like Python) instead of a checked 64-bit long.
        /// </summary>
        public bool BigInts;
        public ITypeResolver Resolver = NullTypeResolver.Instance;
        /// <summary>If set, only this module's `if __name__ == "__main__":` block becomes the program entry point.</summary>
        public string MainModule;
        public readonly Dictionary<string, ModuleInfo> Modules = new Dictionary<string, ModuleInfo>();
        /// <summary>Whole-program inference results, computed on the first Emit (all modules must be parsed by then).</summary>
        internal TypeInference Inference;
        /// <summary>Module functions referenced as values somewhere in the project ("module.func"); each gets a function type.</summary>
        internal HashSet<string> FunctionValueKeys;
        bool? _emitsProfileEvents;
        public bool ProfileRuntimeCode;
        internal bool EmitsProfileEvents => ProfileRuntimeCode || (_emitsProfileEvents ??= Modules.Values.Any(UsesProfiling));

        bool? _emitsTraceEvents;
        public bool TraceRuntimeCode;
        internal bool EmitsTraceEvents => TraceRuntimeCode || (_emitsTraceEvents ??= Modules.Values.Any(UsesTracing));

        static bool UsesTracing(ModuleInfo module)
        {
            if (module.Ast == null) return false;
            bool found = false;
            AstWalker.VisitAll(module.Ast.Body, node =>
            {
                if (found) return;
                found = node is NameExpr { Id: "settrace" or "settrace_all_threads" or "_settraceallthreads" } ||
                        node is AttributeExpr { Name: "settrace" or "settrace_all_threads" or "_settraceallthreads" or "monitoring" } ||
                        node is ConstExpr { Kind: ConstKind.Str, Str: "settrace" or "settrace_all_threads" or "_settraceallthreads" } ||
                        node is ImportFromStmt { Module: "sys" or "threading" } from &&
                        from.Names.Any(n => n.Name is "settrace" or "settrace_all_threads" or "_settraceallthreads" or "monitoring");
            });
            return found;
        }

        static bool UsesProfiling(ModuleInfo module)
        {
            if (module.Ast == null) return false;
            bool found = false;
            AstWalker.VisitAll(module.Ast.Body, node =>
            {
                if (found) return;
                found = node is NameExpr { Id: "setprofile" or "_setprofileallthreads" } ||
                        node is AttributeExpr { Name: "setprofile" or "_setprofileallthreads" } ||
                        node is ConstExpr { Kind: ConstKind.Str, Str: "setprofile" or "_setprofileallthreads" } ||
                        node is ImportStmt direct && direct.Names.Any(n => ProfilingModule(n.Name)) ||
                        node is ImportFromStmt from && (ProfilingModule(from.Module) ||
                            from.Module == "sys" && from.Names.Any(n => n.Name is "setprofile" or "_setprofileallthreads"));
            });
            return found;

            static bool ProfilingModule(string name) => name is "cProfile" or "profile" or "profiling" ||
                name?.StartsWith("profiling.", StringComparison.Ordinal) == true;
        }
        /// <summary>Classes constructed in two phases (allocation, then an __init__ method), see Typhon.Runtime.NoInit.</summary>
        internal HashSet<Syntax.ClassDef> TwoPhaseClasses;
        /// <summary>
        /// Classes whose members were copied into classes with several bases (multiple inheritance): each with the classes it
        /// was copied into and their modules, so isinstance(x, Mixin) is also true for those.
        /// </summary>
        internal readonly Dictionary<Syntax.ClassDef, List<(Syntax.ClassDef Class, ModuleInfo Module)>> MixedInto =
            new Dictionary<Syntax.ClassDef, List<(Syntax.ClassDef, ModuleInfo)>>();

        public ModuleInfo FindModule(string dotted) => dotted != null && Modules.TryGetValue(dotted, out var m) ? m : null;

        Dictionary<ClassDef, ModuleInfo> _classModules;

        internal ModuleInfo DeclaringModule(ClassDef cls)
        {
            if (_classModules == null)
            {
                _classModules = new Dictionary<ClassDef, ModuleInfo>();
                foreach (var owner in Modules.Values)
                    foreach (var declaration in owner.Classes.Values)
                        _classModules[declaration] = owner;
            }
            return _classModules.TryGetValue(cls, out var module) ? module : null;
        }


        /// <summary>Find a Typhon class by simple name across modules (used for base-class lookups).</summary>
        public bool TryFindClass(ModuleInfo module, string className, out ClassDef cls, out ModuleInfo owner)
        {
            owner = module;
            if (module.Classes.TryGetValue(className, out cls)) return true;
            foreach (var m in Modules.Values)
                if (m.Classes.TryGetValue(className, out cls)) { owner = m; return true; }
            cls = null; owner = null;
            return false;
        }
    }

    public static class TyphonCompiler
    {
        public const string FileExtension = ".ty";
        /// <summary>GDScript files (Godot 4): compiled by the GDScript front end into the same AST.</summary>
        public const string GdScriptExtension = ".gd";

        /// <summary>Imports anywhere in the module, including imports in functions and control-flow blocks.</summary>
        public static IReadOnlyList<Stmt> Imports(ModuleInfo module)
        {
            var imports = new List<Stmt>();
            AstWalker.VisitAll(module.Ast.Body, node =>
            {
                if (node is ImportStmt || node is ImportFromStmt) imports.Add((Stmt)node);
            });
            return imports;
        }

        /// <summary>Is the file a GDScript module (by its extension)?</summary>
        public static bool IsGdScriptPath(string path) => path != null && path.EndsWith(GdScriptExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Module name from the path relative to the project directory: "utils/strings.ty" → "utils.strings".</summary>
        public static string ModuleNameFromPath(string projectDir, string path)
        {
            string rel = path;
            if (!string.IsNullOrEmpty(projectDir))
            {
                string full = System.IO.Path.GetFullPath(path);
                string dir = System.IO.Path.GetFullPath(projectDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) rel = full.Substring(dir.Length);
                else rel = System.IO.Path.GetFileName(path);
            }
            else rel = System.IO.Path.GetFileName(path);
            rel = rel.Substring(0, rel.Length - System.IO.Path.GetExtension(rel).Length);
            var parts = rel.Split('\\', '/').Where(p => p.Length > 0).Select(Sanitize);
            return string.Join(".", parts);
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        public static ModuleInfo Parse(string path, string source, string moduleName, string rootNamespace,
            bool barryAsFlufl = false, bool runtimeCode = false, bool expressionOnly = false, bool runtimeAstOnly = false)
        {
            var info = new ModuleInfo { Name = moduleName, Path = path, SourceText = source };
            int lastDot = moduleName.LastIndexOf('.');
            info.ClassName = lastDot < 0 ? moduleName : moduleName.Substring(lastDot + 1);
            string pkg = lastDot < 0 ? "" : moduleName.Substring(0, lastDot);
            info.Namespace = string.Join(".", new[] { rootNamespace, pkg }.Where(s => !string.IsNullOrEmpty(s)));
            info.BigIntPragma = System.Text.RegularExpressions.Regex.IsMatch(source, @"^[ \t]*#[ \t]*typhon:[ \t]*int[ \t]*=[ \t]*bigint\b", System.Text.RegularExpressions.RegexOptions.Multiline);
            info.IsGdScript = IsGdScriptPath(path);
            // a GDScript module's own static class holds its inner classes; the script class itself is a top-level class
            if (info.IsGdScript) info.ClassName += "_gd";
            try
            {
                if (info.IsGdScript)
                {
                    info.Ast = GdScriptParser.ParseModule(source, path, info.IsMainModule, out var gd);
                    info.GdScript = gd;
                }
                else info.Ast = Parser.ParseModule(source, path, barryAsFlufl, expressionOnly);
            }
            catch (SyntaxError e)
            {
                info.ParseDiagnostics.Add(new TyDiagnostic
                {
                    Id = "TY0001", Severity = Severity.Error, Message = e.Message, Path = path, Line = e.Line, Col = e.Col,
                    AtEndOfInput = e.AtEndOfInput, NeedsMoreInput = e.NeedsMoreInput, IsIndentationError = e.IsIndentationError,
                });
                info.Ast = new ModuleAst { Path = path };
            }
            if (runtimeCode && info.ParseDiagnostics.Count == 0)
            {
                Node invalid = null;
                if (!runtimeAstOnly && AstWalker.ContainsOutsideNestedScopes(info.Ast.Body, node =>
                {
                    if (RuntimeModuleContextError(node) == null) return false;
                    invalid = node;
                    return true;
                }))
                    info.ParseDiagnostics.Add(new TyDiagnostic
                    {
                        Id = "TY0001", Severity = Severity.Error, Path = path, Line = invalid.Line, Col = invalid.Col,
                        Message = RuntimeModuleContextError(invalid),
                    });
                else if (!runtimeAstOnly && FindInvalidLoopControl(info.Ast.Body, false) is Node loopControl)
                    info.ParseDiagnostics.Add(new TyDiagnostic
                    {
                        Id = "TY0001", Severity = Severity.Error, Path = path, Line = loopControl.Line, Col = loopControl.Col,
                        Message = loopControl is BreakStmt ? "'break' outside loop" : "'continue' not properly in loop",
                    });
                else
                    AstWalker.VisitAll(info.Ast.Body, statement =>
                    {
                        if (info.ParseDiagnostics.Count > 0) return;
                        Expr target = statement switch
                        {
                            AssignStmt assign => assign.Targets.FirstOrDefault(t => !IsAssignmentTarget(t)),
                            AugAssignStmt aug when !IsSingleAssignmentTarget(aug.Target) => aug.Target,
                            AnnAssignStmt ann when !IsSingleAssignmentTarget(ann.Target) => ann.Target,
                            ForStmt loop when !IsAssignmentTarget(loop.Target) => loop.Target,
                            _ => null,
                        };
                        if (target != null)
                            info.ParseDiagnostics.Add(new TyDiagnostic
                            {
                                Id = "TY0001", Severity = Severity.Error, Path = path, Line = target.Line, Col = target.Col,
                                Message = "cannot assign to expression",
                            });
                    });
            }
            if (runtimeAstOnly) return info;
            Decorators.Apply(info.Ast);
            ModuleAliases.Apply(info.Ast);
            if (!runtimeCode && !info.IsGdScript) ReboundModuleFunctions.Apply(info);
            foreach (var s in info.Ast.Body)
            {
                if (s is AssignStmt alias && alias.Targets.Count == 1 && alias.Targets[0] is NameExpr an0 &&
                    IsTypeAliasValue(alias.Value) && !UsesKnownValue(alias.Value, info))
                    info.TypeAliases[an0.Id] = alias.Value;
                if (s is AnnAssignStmt ta && ta.Target is NameExpr tn0 && ta.Value != null && (ta.Annotation is NameExpr tan && tan.Id == "TypeAlias" || ta.Annotation is AttributeExpr taa && taa.Name == "TypeAlias"))
                    info.TypeAliases[tn0.Id] = ta.Value;
                switch (s)
                {
                    case ClassDef c: info.Classes[c.Name] = c; break;
                    case FunctionDef f: info.Functions.Add(f.Name); break;
                    case AssignStmt a: foreach (var t in a.Targets) if (t is NameExpr n) info.Globals.Add(n.Id); break;
                    case AnnAssignStmt a: if (a.Target is NameExpr an) info.Globals.Add(an.Id); break;
                }
            }
            var branches = info.Ast.Body.Where(s => s is IfStmt or TryStmt).ToList();
            if (branches.Count > 0)
            {
                var locals = LocalsAnalyzer.Analyze(branches, Enumerable.Empty<string>(), null);
                info.ConditionalGlobals.UnionWith(locals.AllLocals);
                info.ConditionalGlobals.ExceptWith(locals.LocalFunctions);
            }
            if (!runtimeCode && !info.IsGdScript)
                AstWalker.VisitAll(info.Ast.Body, node =>
                {
                    // A global can be created by a function, with no assignment in the module suite.
                    if (node is GlobalStmt declaration) info.ConditionalGlobals.UnionWith(declaration.Names);
                });
            if (info.ConditionalGlobals.Count > 0)
            {
                info.ConditionalGlobals.ExceptWith(info.Globals);
                info.ConditionalGlobals.ExceptWith(info.Functions);
                info.ConditionalGlobals.ExceptWith(info.Classes.Keys);
                foreach (var statement in info.Ast.Body)
                {
                    if (statement is ImportStmt import)
                        info.ConditionalGlobals.ExceptWith(import.Names.Select(n => n.AsName ?? n.Name.Split('.')[0]));
                    if (statement is ImportFromStmt from)
                        info.ConditionalGlobals.ExceptWith(from.Names.Select(n => n.AsName ?? n.Name));
                }
                info.Globals.UnionWith(info.ConditionalGlobals);
            }
            bool WritesGlobals(Expr target) => target is SubscriptExpr
                { Target: CallExpr { Func: NameExpr { Id: "globals" }, Args.Count: 0 } };
            AstWalker.VisitAll(info.Ast.Body, statement =>
            {
                if (statement is AssignStmt assign && assign.Targets.Any(WritesGlobals) ||
                    statement is AnnAssignStmt ann && WritesGlobals(ann.Target) ||
                    statement is AugAssignStmt aug && WritesGlobals(aug.Target) ||
                    statement is DelStmt del && del.Targets.Any(WritesGlobals))
                    info.WritesModuleGlobals = true;
            });
            info.MayReplaceModule = ReplacesModule(info.Ast.Body);
            return info;
        }

        static bool ReplacesModule(List<Stmt> body)
        {
            var system = new HashSet<string>();
            var registries = new HashSet<string>();
            AstWalker.VisitAll(body, node =>
            {
                if (node is ImportStmt direct)
                    foreach (var alias in direct.Names.Where(name => name.Name == "sys")) system.Add(alias.AsName ?? alias.Name);
                if (node is ImportFromStmt { Level: 0, Module: "sys" } from)
                    foreach (var alias in from.Names.Where(name => name.Name == "modules")) registries.Add(alias.AsName ?? alias.Name);
            });
            bool IsReplacement(Expr target) => target is SubscriptExpr { Index: NameExpr { Id: "__name__" } } item &&
                (item.Target is NameExpr name && registries.Contains(name.Id) ||
                 item.Target is AttributeExpr { Name: "modules", Target: NameExpr owner } && system.Contains(owner.Id));
            return AstWalker.ContainsOutsideNestedScopes(body, node => node is AssignStmt assignment && assignment.Targets.Any(IsReplacement) ||
                node is AnnAssignStmt annotated && IsReplacement(annotated.Target));
        }

        static string RuntimeModuleContextError(Node node) => node switch
        {
            ReturnStmt => "'return' outside function",
            YieldExpr => "'yield' outside function",
            AwaitExpr => "'await' outside function",
            ForStmt { IsAsync: true } => "'async for' outside async function",
            WithStmt { IsAsync: true } => "'async with' outside async function",
            GlobalStmt { Nonlocal: true } => "nonlocal declaration not allowed at module level",
            _ => null,
        };

        static bool IsSingleAssignmentTarget(Expr target) => target is NameExpr || target is AttributeExpr || target is SubscriptExpr;

        static bool IsAssignmentTarget(Expr target) => IsSingleAssignmentTarget(target) || (target switch
        {
            TupleExpr tuple => tuple.Elts.All(IsUnpackTarget),
            ListExpr list => list.Elts.All(IsUnpackTarget),
            _ => false,
        });

        static bool IsUnpackTarget(Expr target) => target is StarredExpr star ? IsAssignmentTarget(star.Value) : IsAssignmentTarget(target);

        static Node FindInvalidLoopControl(object source, bool insideLoop)
        {
            if (source is System.Collections.IEnumerable items && !(source is string))
            {
                foreach (var item in items)
                    if (FindInvalidLoopControl(item, insideLoop) is Node invalid) return invalid;
                return null;
            }
            if (!(source is Node node)) return null;
            if (node is BreakStmt || node is ContinueStmt) return insideLoop ? null : node;
            if (node is FunctionDef function) return FindInvalidLoopControl(function.Body, false);
            if (node is ClassDef type) return FindInvalidLoopControl(type.Body, false);
            if (node is ForStmt @for) return FindInvalidLoopControl(@for.Body, true) ?? FindInvalidLoopControl(@for.OrElse, insideLoop);
            if (node is WhileStmt @while) return FindInvalidLoopControl(@while.Body, true) ?? FindInvalidLoopControl(@while.OrElse, insideLoop);
            return FindInvalidLoopControl(AstWalker.Children(node), insideLoop);
        }

        static bool UsesKnownValue(Expr expression, ModuleInfo module) => expression switch
        {
            NameExpr name => module.Globals.Contains(name.Id) && !module.TypeAliases.ContainsKey(name.Id),
            BinOpExpr binary => UsesKnownValue(binary.Left, module) || UsesKnownValue(binary.Right, module),
            _ => false,
        };

        static readonly HashSet<string> AliasGenerics = new HashSet<string>
        {
            "list", "dict", "set", "frozenset", "tuple", "List", "Dict", "Set", "FrozenSet", "Tuple", "Optional", "Union", "Callable",
            "Iterable", "Iterator", "Sequence", "Mapping", "MutableMapping", "MutableSequence", "Generator", "Type", "type",
        };

        /// <summary>Is the right-hand side of a module-level assignment a type (list[int], int | None, Callable[[int], str])?</summary>
        static bool IsTypeAliasValue(Expr e)
        {
            switch (e)
            {
                case SubscriptExpr s:
                    string b = s.Target is NameExpr n ? n.Id : s.Target is AttributeExpr a && a.Target is NameExpr an && (an.Id == "typing" || an.Id == "t") ? a.Name : null;
                    return b != null && AliasGenerics.Contains(b);
                case BinOpExpr bin when bin.Op == "|":
                    return IsTypeLeaf(bin.Left) && IsTypeLeaf(bin.Right);
                default: return false;
            }
        }

        static bool IsTypeLeaf(Expr e) => IsTypeAliasValue(e) || e is ConstExpr c && c.Kind == ConstKind.None ||
            e is NameExpr n && (n.Id == "int" || n.Id == "float" || n.Id == "str" || n.Id == "bool" || n.Id == "None" || char.IsUpper(n.Id[0]));

        public static string Emit(ModuleInfo module, ProjectContext project, List<TyDiagnostic> diagnostics)
        {
            if (project.Inference == null)
            {
                if (project.Modules.Values.Any(m => m.BigIntPragma)) project.BigInts = true;
                GdScriptLowering.Run(project);
                foreach (var source in project.Modules.Values)
                    if (source.Ast != null && source.ParseDiagnostics.Count == 0)
                        new ModuleEmitter(source, project, diagnostics).PrepareImportClassBindings();
                ClassModel.Complete(project);
                TypeInference.Run(project);
            }
            var emitter = new ModuleEmitter(module, project, diagnostics);
            return emitter.Emit();
        }

        const string RuntimeResourcePrefix = "Typhon.Compiler.Runtime/";

        /// <summary>C# source of the Typhon runtime (builtins + stdlib shims) that is compiled into every Typhon assembly.</summary>
        public static string RuntimeSource
        {
            get
            {
                // every file under Runtime/ (embedded by the project file), in a fixed order; each file declares its usings
                // inside its namespace blocks, so the order does not matter to the compilation
                var asm = typeof(TyphonCompiler).Assembly;
                var sb = new StringBuilder();
                var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(RuntimeResourcePrefix, StringComparison.Ordinal))
                    .OrderBy(n => n.Replace('\\', '/'), StringComparer.Ordinal);
                foreach (var name in names)
                    using (var s = asm.GetManifestResourceStream(name))
                    using (var r = new StreamReader(s)) sb.Append(r.ReadToEnd()).Append('\n');
                return sb.ToString();
            }
        }

        /// <summary>TyphonIntMode: "bigint" → big-int mode; anything else (int64, empty) → 64-bit ints.</summary>
        public static bool ParseIntMode(string value) => (value ?? "").Trim().ToLowerInvariant() == "bigint";

        public static TypeMode ParseTypeMode(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "strict": return TypeMode.Strict;
                case "permissive": return TypeMode.Permissive;
                default: return TypeMode.Warn;
            }
        }
    }
}
