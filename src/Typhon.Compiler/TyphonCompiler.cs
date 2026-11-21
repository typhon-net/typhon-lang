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
        public override string ToString() => $"{Path}({Line},{Col}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
    }

    public sealed class ModuleInfo
    {
        public string Name;          // dotted python module name, e.g. "utils.strings"
        public string ClassName;     // C# static class name, e.g. "strings"
        public string Namespace;     // C# namespace, e.g. "MyApp.utils"
        public string Path;
        public ModuleAst Ast;
        public readonly Dictionary<string, ClassDef> Classes = new Dictionary<string, ClassDef>();
        public readonly HashSet<string> Functions = new HashSet<string>();
        public readonly HashSet<string> Globals = new HashSet<string>();
        /// <summary>Module-level type aliases: EdgeT = tuple[int, int], Matrix = list[list[float]], X: TypeAlias = ...</summary>
        public readonly Dictionary<string, Expr> TypeAliases = new Dictionary<string, Expr>();
        public readonly List<TyDiagnostic> ParseDiagnostics = new List<TyDiagnostic>();
        /// <summary>The module asks for big-int mode with a "# typhon: int=bigint" comment (it applies to the whole program).</summary>
        public bool BigIntPragma;

        public string QualifiedClassName => "global::" + (string.IsNullOrEmpty(Namespace) ? "" : Namespace + ".") + ClassName;
        public bool IsMainModule => Name == "__main__" || Name.EndsWith(".__main__");
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
        /// <summary>Classes constructed in two phases (allocation, then an __init__ method), see Typhon.Runtime.NoInit.</summary>
        internal HashSet<Syntax.ClassDef> TwoPhaseClasses;

        public ModuleInfo FindModule(string dotted) => Modules.TryGetValue(dotted, out var m) ? m : null;

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

        public static ModuleInfo Parse(string path, string source, string moduleName, string rootNamespace)
        {
            var info = new ModuleInfo { Name = moduleName, Path = path };
            int lastDot = moduleName.LastIndexOf('.');
            info.ClassName = lastDot < 0 ? moduleName : moduleName.Substring(lastDot + 1);
            string pkg = lastDot < 0 ? "" : moduleName.Substring(0, lastDot);
            info.Namespace = string.Join(".", new[] { rootNamespace, pkg }.Where(s => !string.IsNullOrEmpty(s)));
            info.BigIntPragma = System.Text.RegularExpressions.Regex.IsMatch(source, @"^[ \t]*#[ \t]*typhon:[ \t]*int[ \t]*=[ \t]*bigint\b", System.Text.RegularExpressions.RegexOptions.Multiline);
            try
            {
                info.Ast = Parser.ParseModule(source, path);
            }
            catch (SyntaxError e)
            {
                info.ParseDiagnostics.Add(new TyDiagnostic { Id = "TY0001", Severity = Severity.Error, Message = e.Message, Path = path, Line = e.Line, Col = e.Col });
                info.Ast = new ModuleAst { Path = path };
            }
            foreach (var s in info.Ast.Body)
            {
                if (s is AssignStmt alias && alias.Targets.Count == 1 && alias.Targets[0] is NameExpr an0 && IsTypeAliasValue(alias.Value))
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
            return info;
        }

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
                ClassModel.Complete(project);
                TypeInference.Run(project);
            }
            var emitter = new ModuleEmitter(module, project, diagnostics);
            return emitter.Emit();
        }

        /// <summary>C# source of the Typhon runtime (builtins + stdlib shims) that is compiled into every Typhon assembly.</summary>
        public static string RuntimeSource
        {
            get
            {
                // Builtins.cs (builtins, runtime helpers, small modules) + Stdlib.cs (itertools, collections, functools, ...)
                var asm = typeof(TyphonCompiler).Assembly;
                var sb = new StringBuilder();
                foreach (var name in new[] { "Typhon.Compiler.Runtime.Builtins.cs", "Typhon.Compiler.Runtime.Stdlib.cs", "Typhon.Compiler.Runtime.Modules.cs",
                                             "Typhon.Compiler.Runtime.Json.cs", "Typhon.Compiler.Runtime.Regex.cs", "Typhon.Compiler.Runtime.Bytes.cs", "Typhon.Compiler.Runtime.Random.cs" })
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
