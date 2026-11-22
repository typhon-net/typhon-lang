// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    // Imports: Typhon modules, .NET namespaces and the standard modules of the Typhon runtime.
    internal sealed partial class ModuleEmitter
    {
        ModuleInfo ResolvePsModule(string dotted)
        {
            var m = _p.FindModule(dotted);
            if (m != null) return m;
            return _m.PackageName.Length == 0 ? null : _p.FindModule(_m.PackageName + "." + dotted);
        }

        bool IsProjectPackageModule(ModuleInfo module) =>
            module.IsPackage || module.PackageName.Length > 0 &&
            (_p.FindModule(module.PackageName)?.IsPackage == true || StdlibType(module.PackageName) != null);

        readonly HashSet<string> _qualifiedPackageImports = new HashSet<string>();
        readonly Dictionary<string, (ModuleInfo Owner, string Name, string Storage)> _runtimeProjectBindings = new();

        string RuntimeProjectMember(ModuleInfo module, string name) =>
            $"{Rt("getattr")}(global::Typhon.Runtime.PyModule.Of(typeof({ModuleClassName(module)}), {CsString(module.Name)}), {CsString(name)})";

        static bool ConditionalModuleClass(ModuleInfo module, string name) =>
            module.Classes.TryGetValue(name, out var definition) && definition.ConditionalModuleDefinition;

        bool UsesRuntimeProjectMember(ModuleInfo module, string name) =>
            ConditionalModuleClass(module, name) || module.WritesModuleGlobals &&
            ProjectExport(module, name).Owner == null && RuntimeReExport(module, name).Module == null;

        bool ConditionalClassReference(Expr reference) => reference switch
        {
            NameExpr name when !IsLocal(name.Id) => ConditionalModuleClass(_m, name.Id) ||
                _runtimeProjectBindings.TryGetValue(name.Id, out var binding) && ConditionalModuleClass(binding.Owner, binding.Name),
            AttributeExpr member when DottedName(member.Target) is string moduleName && ModuleRef(moduleName) is ModuleInfo source =>
                ConditionalModuleClass(source, member.Name),
            _ => false,
        };

        string PackageImportedMember(string name)
        {
            if (!_qualifiedPackageImports.Contains(name) || IsLocal(name) || _m.Globals.Contains(name) ||
                _m.Functions.Contains(name) || _m.Classes.ContainsKey(name)) return null;
            if (_moduleAliases.TryGetValue(name, out var module)) return ModuleClassName(module);
            return _importedFromModules.TryGetValue(name, out var member)
                ? ModuleClassName(member.Key) + "." + Esc(member.Value) : null;
        }

        /// <summary>A member's declaration, following package re-exports. A null member name denotes a submodule.</summary>
        (ModuleInfo Owner, string Member) ProjectExport(ModuleInfo module, string name,
            HashSet<(ModuleInfo, string)> seen = null)
        {
            if (module.Functions.Contains(name) || module.Classes.ContainsKey(name) || module.Globals.Contains(name))
                return (module, name);
            if (!IsProjectPackageModule(module)) return default;
            if (seen == null) seen = new HashSet<(ModuleInfo, string)>();
            if (!seen.Add((module, name))) return default;
            foreach (var s in module.Ast.Body)
            {
                if (s is ImportStmt direct)
                {
                    foreach (var a in direct.Names)
                    {
                        var boundName = a.AsName ?? a.Name.Split('.')[0];
                        if (boundName == name && _p.FindModule(a.AsName == null ? boundName : a.Name) is ModuleInfo submodule)
                            return (submodule, null);
                    }
                }
                else if (s is ImportFromStmt from && module.ResolveImport(from.Module, from.Level) is string absolute &&
                         _p.FindModule(absolute) is ModuleInfo imported)
                {
                    foreach (var a in from.Names)
                    {
                        string original = a.Name == "*" && !name.StartsWith("_") ? name
                            : (a.AsName ?? a.Name) == name ? a.Name : null;
                        if (original == null) continue;
                        var export = ProjectExport(imported, original, seen);
                        if (export.Owner != null) return export;
                    }
                }
            }
            return module.IsPackage ? (_p.FindModule(module.Name + "." + name), null) : default;
        }

        string ProjectPackageMember(string dotted)
        {
            int dot = dotted.LastIndexOf('.');
            if (dot <= 0 || ModuleRef(dotted.Substring(0, dot)) is not ModuleInfo module || !IsProjectPackageModule(module)) return null;
            var (owner, member) = ProjectExport(module, dotted.Substring(dot + 1));
            return owner == null ? null : member == null ? ModuleClassName(owner) : ModuleClassName(owner) + "." + Esc(member);
        }

        // A source module can expose a runtime member through a named from-import, as cProfile does.
        (string Module, string Member) RuntimeReExport(ModuleInfo module, string name, HashSet<(ModuleInfo, string)> seen = null)
        {
            if (module.Functions.Contains(name) || module.Classes.ContainsKey(name) || module.Globals.Contains(name)) return default;
            seen ??= new HashSet<(ModuleInfo, string)>();
            if (!seen.Add((module, name))) return default;
            foreach (var import in module.Ast.Body.OfType<ImportFromStmt>())
            {
                string path = module.ResolveImport(import.Module, import.Level);
                if (path == null) continue;
                foreach (var alias in import.Names)
                {
                    if (alias.Name == "*")
                    {
                        if (!name.StartsWith("_") && _p.FindModule(path) == null && StdlibType(path) is string starMeta &&
                            RuntimeExport(path, starMeta, name)) return (path, name);
                        continue;
                    }
                    if ((alias.AsName ?? alias.Name) != name) continue;
                    if (_p.FindModule(path) is ModuleInfo source)
                    {
                        var result = RuntimeReExport(source, alias.Name, seen);
                        if (result.Module != null) return result;
                    }
                    else if (StdlibType(path) is string metadata && RuntimeExport(path, metadata, alias.Name))
                        return (path, alias.Name);
                }
            }
            return default;
        }

        IEnumerable<ModuleInfo> ProjectImports(Stmt stmt)
        {
            IEnumerable<ModuleInfo> Ancestors(ModuleInfo module)
            {
                var parents = new Stack<ModuleInfo>();
                while (module != null)
                {
                    if (module != _m) parents.Push(module);
                    if (!IsProjectPackageModule(module)) break;
                    string parent = module.IsPackage
                        ? module.Name.Substring(0, Math.Max(0, module.Name.LastIndexOf('.')))
                        : module.PackageName;
                    module = _p.FindModule(parent);
                }
                return parents;
            }
            if (stmt is ImportStmt direct)
                return direct.Names.SelectMany(a => Ancestors(ResolvePsModule(a.Name))).Distinct();
            if (stmt is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string absolute)
            {
                if (ResolvePsModule(absolute) is ModuleInfo imported)
                    return Ancestors(imported).Concat(from.Names.Select(a => ProjectExport(imported, a.Name))
                        .Where(x => x.Member == null).SelectMany(x => Ancestors(x.Owner))).Distinct();
                if (StdlibType(absolute) is string runtime)
                    return from.Names.SelectMany(a => Ancestors(RuntimePackageChild(absolute, runtime, a.Name))).Distinct();
            }
            return Enumerable.Empty<ModuleInfo>();
        }

        IEnumerable<string> PlatformModules(Stmt statement)
        {
            if (statement is ImportStmt direct)
                return direct.Names.Select(alias => alias.Name).Where(name => name is "termios" or "msvcrt" && ResolvePsModule(name) == null);
            if (statement is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string module &&
                module is "termios" or "msvcrt" && ResolvePsModule(module) == null) return new[] { module };
            return Enumerable.Empty<string>();
        }

        void EmitProjectImports(Stmt stmt)
        {
            foreach (var module in PlatformModules(stmt))
                _w.Line($"global::Typhon.Runtime.Builtins.__import_platform_module({CsString(module)});");
            foreach (var module in ProjectImports(stmt))
                _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof({ModuleClassName(module)}).TypeHandle);");
            if (stmt is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string absolute &&
                ResolvePsModule(absolute) is ModuleInfo importedSource)
                foreach (var alias in from.Names)
                    if (_runtimeProjectBindings.TryGetValue(alias.AsName ?? alias.Name, out var binding) &&
                        binding.Owner == importedSource && binding.Name == alias.Name)
                        _w.Line($"{binding.Storage} = global::Typhon.Runtime.PyModule.Of(typeof({ModuleClassName(importedSource)}), {CsString(importedSource.Name)}).ImportAttribute({CsString(alias.Name)});");
            if (stmt is not ImportStmt imported) return;
            foreach (var alias in imported.Names)
            {
                string binding = alias.AsName ?? alias.Name.Split('.')[0];
                if (!_lateModuleFields.Contains(binding)) continue;
                string moduleName = alias.AsName == null ? binding : alias.Name;
                if (ResolvePsModule(moduleName) is ModuleInfo source)
                    _w.Line($"{Esc(binding)} = global::Typhon.Runtime.PyModule.Of(typeof({ModuleClassName(source)}), {CsString(source.Name)});");
                else if (StdlibType(moduleName) is string runtime)
                    _w.Line($"{Esc(binding)} = global::Typhon.Runtime.PyModule.Of(typeof({CsTypeName(runtime)}), {CsString(moduleName)});");
            }
        }

        static readonly HashSet<string> IgnoredModules = new HashSet<string> { "typing", "abc", "enum", "__future__", "collections.abc", "typing_extensions", "clr" };

        void ProcessImport(ImportStmt s)
        {
            foreach (var alias in s.Names)
            {
                string name = alias.Name, local = alias.AsName;
                if (name == "typing") _typingModules.Add(local ?? name);
                if (PublicModule(name) == "collections.abc")
                {
                    string bound = local ?? name.Split('.')[0];
                    _typingOnlyNames.Add(bound);
                    BindRuntimeModule(bound, local == null && name == "collections.abc" ? "Typhon.Stdlib.collections" : "Typhon.Stdlib.collections+abc");
                    continue;
                }
                if (name != "__future__" && IgnoredModules.Contains(PublicModule(name))) { _typingOnlyNames.Add(local ?? name.Split('.')[0]); continue; }
                var mod = ResolvePsModule(name);
                if (mod != null)
                {
                    _moduleAliases[local ?? name] = mod;
                    if (IsProjectPackageModule(mod))
                    {
                        string head = name.Split('.')[0];
                        if (local != null) _qualifiedPackageImports.Add(local);
                        else if (_p.FindModule(head) is ModuleInfo package)
                        {
                            _moduleAliases[head] = package;
                            _qualifiedPackageImports.Add(head);
                        }
                        else if (StdlibType(head) is string runtimeParent)
                            BindRuntimeModule(head, runtimeParent);
                    }
                    else if (local != null || !name.Contains(".") && mod.ClassName != name)
                        _usingLines.Add($"using {Esc(local ?? name)} = {ModuleClassName(mod)};");
                    continue;
                }
                string stdlib = StdlibType(name);
                if (stdlib != null)
                {
                    string l = local ?? name.Split('.')[0];
                    // import a.b binds a. The standard library represents b as a nested type.
                    if (local == null && name.Contains(".")) stdlib = StdlibType(l);
                    BindRuntimeModule(l, stdlib);
                    continue;
                }
                if (_r.TypeExists(name))
                {
                    if (local != null) { _usingLines.Add($"using {Esc(local)} = global::{name};"); _dotnetAliases[local] = name; }
                    continue;
                }
                if (_r.NamespaceExists(name))
                {
                    if (local != null) { _usingLines.Add($"using {Esc(local)} = global::{name};"); _dotnetAliases[local] = name; }
                    else { _usingLines.Add($"using {name};"); _usingNamespaces.Add(name); }
                    continue;
                }
                Report(s, "TY0101", Severity.Error, $"No module named '{name}' (not a Typhon module in this project, a Typhon standard module, or a .NET namespace/type in the referenced assemblies)");
            }
        }

        readonly HashSet<string> _qualifiedRuntimeModuleImports = new HashSet<string>();

        void BindRuntimeModule(string name, string metadata)
        {
            // A source package child can put the same package name in the generated CLR namespace.
            if (_p.Modules.Values.Any(module => module.Namespace == name || module.Namespace.StartsWith(name + ".", StringComparison.Ordinal)))
                _qualifiedRuntimeModuleImports.Add(name);
            else _usingLines.Add($"using {Esc(name)} = {CsTypeName(metadata)};");
            _dotnetAliases[name] = metadata;
        }

        bool RuntimeExport(string module, string metadata, string name) =>
            _r.GetMembers(metadata, name).Count > 0 || AnyArity(metadata + "+" + name, out _) ||
            StdlibType(module + "." + name) != null;

        ModuleInfo RuntimePackageChild(string package, string metadata, string name) =>
            name != "*" && !RuntimeExport(package, metadata, name) ? _p.FindModule(package + "." + name) : null;

        void ProcessImportFrom(ImportFromStmt s, bool resolveRuntimeMember = false)
        {
            string modName = _m.ResolveImport(s.Module, s.Level);
            if (modName == null) { Report(s, "TY0101", Severity.Error, "attempted relative import beyond the current package"); return; }
            string publicModule = PublicModule(modName);
            if (!resolveRuntimeMember && IgnoredModules.Contains(publicModule))
            {
                string runtime = publicModule == "collections.abc" ? StdlibType(modName) : null;
                foreach (var a in s.Names)
                {
                    if (runtime != null && (_r.GetMembers(runtime, a.Name).Any(member => member.IsStatic && member.IsMethod) ||
                        a.Name != "Mapping" && _r.TypeExists(runtime + "+" + a.Name)))
                    {
                        ProcessImportFrom(new ImportFromStmt
                        {
                            Module = s.Module, Level = s.Level, Line = s.Line, Col = s.Col, Names = { a },
                        }, resolveRuntimeMember: true);
                        continue;
                    }
                    string local = a.AsName ?? a.Name;
                    _typingOnlyNames.Add(local);
                    if (modName == "typing" && a.Name == "Union") _typingUnionImports.Add(local);
                    if (PublicModule(modName) == "collections.abc" && a.Name == "Mapping") _importedTypes[local] = MappingMeta;
                }
                return;
            }

            var mod = ResolvePsModule(modName);
            if (mod != null)
            {
                if (!IsProjectPackageModule(mod) || s.Names.Any(a => a.Name == "*"))
                    _usingLines.Add($"using static {mod.QualifiedClassName};");
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _wildcardModules.Add(mod); continue; }
                    var (owner, member) = ProjectExport(mod, a.Name);
                    string bound = a.AsName ?? a.Name;
                    if (owner == null)
                    {
                        var export = RuntimeReExport(mod, a.Name);
                        if (export.Module != null)
                            ProcessImportFrom(new ImportFromStmt
                            {
                                Module = export.Module, Line = s.Line, Col = s.Col,
                                Names = { new ImportAlias { Name = export.Member, AsName = bound } },
                            });
                        else if (mod.WritesModuleGlobals)
                            _runtimeProjectBindings[bound] = (mod, a.Name, "__typhon_" + NewTmp("import"));
                        else Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}'");
                        continue;
                    }
                    if (member == null) _moduleAliases[bound] = owner;
                    else
                    {
                        _importedFromModules[bound] = new KeyValuePair<ModuleInfo, string>(owner, member);
                        if (owner == mod && ConditionalModuleClass(owner, member))
                            _runtimeProjectBindings[bound] = (mod, a.Name, "__typhon_" + NewTmp("import"));
                    }
                    if (IsProjectPackageModule(mod)) _qualifiedPackageImports.Add(bound);
                    if (a.AsName != null && !IsProjectPackageModule(mod))
                    {
                        if (mod.Classes.ContainsKey(a.Name)) _usingLines.Add($"using {Esc(a.AsName)} = {mod.QualifiedClassName}.{Esc(a.Name)};");
                        else Unsupported(s, $"renaming the function or variable '{a.Name}' on import");
                    }
                }
                return;
            }

            string stdlib = StdlibType(modName);
            if (stdlib != null || _r.TypeExists(modName))
            {
                string meta = stdlib ?? modName;
                string csName = string.Join(".", meta.Replace('+', '.').Split('.').Select(Esc));
                _usingLines.Add($"using static global::{csName};");
                _staticTypes.Add(meta);
                foreach (var a in s.Names)
                {
                    if (stdlib != null)
                    {
                        if (RuntimePackageChild(modName, meta, a.Name) is ModuleInfo child)
                        {
                            string bound = a.AsName ?? a.Name;
                            _moduleAliases[bound] = child;
                            _qualifiedPackageImports.Add(bound);
                            continue;
                        }
                        if (a.Name == "*") _stdlibStar.Add(meta);
                        else if (a.AsName == null) _stdlibImported[a.Name] = meta;
                    }
                    if (a.AsName != null)
                    {
                        if (_r.TypeExists(meta + "+" + a.Name))
                        {
                            _usingLines.Add($"using {Esc(a.AsName)} = {CsTypeName(meta + "+" + a.Name)};");
                            _importedTypes[a.AsName] = meta + "+" + a.Name; // a class: calling it constructs one
                        }
                        else
                        {
                            // from math import log as _log, pi as _pi: the name stands for module.member (calls and values)
                            string hidden = "__std_" + meta.Split('.', '+').Last();
                            if (!_dotnetAliases.ContainsKey(hidden)) { _dotnetAliases[hidden] = meta; _usingLines.Add($"using {hidden} = global::{csName};"); }
                            var target = new AttributeExpr { Target = new NameExpr { Id = hidden, Line = s.Line, Col = s.Col }, Name = a.Name, Line = s.Line, Col = s.Col };
                            _importAliases[a.AsName] = target;
                        }
                    }
                }
                return;
            }

            if (_r.NamespaceExists(modName))
            {
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _usingLines.Add($"using {modName};"); _usingNamespaces.Add(modName); continue; }
                    string full = modName + "." + a.Name;
                    if (_r.NamespaceExists(full))
                    {
                        string l = a.AsName ?? a.Name;
                        _usingLines.Add($"using {Esc(l)} = global::{full};");
                        _dotnetAliases[l] = full;
                        continue;
                    }
                    // an attribute by its short name, as C# writes it: from Xunit import Fact (FactAttribute) for @Fact
                    if (!AnyArity(full, out _) && a.AsName == null && _r.TypeExists(full + "Attribute"))
                    {
                        _usingLines.Add($"using {modName};");
                        if (!_usingNamespaces.Contains(modName)) _usingNamespaces.Add(modName);
                        continue;
                    }
                    if (!AnyArity(full, out _)) { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}' (no such .NET type or namespace)"); continue; }
                    if (a.AsName != null && _r.TypeExists(full))
                    {
                        _usingLines.Add($"using {Esc(a.AsName)} = global::{full};");
                        _importedTypes[a.AsName] = full;
                    }
                    else
                    {
                        _usingLines.Add($"using {modName};");
                        if (!_usingNamespaces.Contains(modName)) _usingNamespaces.Add(modName);
                        _importedTypes[a.Name] = full;
                    }
                }
                return;
            }
            Report(s, "TY0101", Severity.Error, $"No module named '{modName}'");
        }

        /// <summary>
        /// Typhon standard modules are .NET types in namespace Typhon.Stdlib: "math" → Typhon.Stdlib.math_ (built in),
        /// "numpy" → Typhon.Stdlib.numpy (Typhon.NumPy package). The trailing '_' avoids C#'s rule that a member
        /// cannot share its class's name (time.time, random.random). Submodules are nested types (os.path → os_+path).
        /// </summary>
        /// <summary>Names bound by imports of the typing-only modules (typing, abc, collections.abc): no values at run time.</summary>
        readonly HashSet<string> _typingOnlyNames = new HashSet<string>();

        /// <summary>
        /// CPython's private modules (C accelerators and pure-Python twins) whose public module the runtime provides: the
        /// standard library imports them (from _bisect import *, from _pydatetime import *) where Typhon has the module.
        /// </summary>
        static readonly Dictionary<string, string> PrivateModuleAliases = new Dictionary<string, string>
        {
            ["_bisect"] = "bisect", ["_heapq"] = "heapq", ["_struct"] = "struct", ["_datetime"] = "datetime", ["_pydatetime"] = "datetime",
            ["_operator"] = "operator", ["_functools"] = "functools", ["_io"] = "io", ["_pyio"] = "io", ["_weakref"] = "weakref",
            ["_json"] = "json", ["_random"] = "random", ["_statistics"] = "statistics", ["_string"] = "string", ["_csv"] = "csv",
            ["_collections"] = "collections", ["_contextvars"] = "contextvars",
            ["_md5"] = "hashlib", ["_sha1"] = "hashlib", ["_pydecimal"] = "decimal",
            ["_decimal"] = "decimal", ["_pickle"] = "pickle", ["_bz2"] = "bz2", ["_lzma"] = "lzma", ["_locale"] = "locale",
            ["_stat"] = "stat", ["_warnings"] = "warnings", ["_py_warnings"] = "warnings", ["_abc"] = "abc", ["_typing"] = "typing", ["_queue"] = "queue", ["_socket"] = "socket",
        };

        static string PublicModule(string dotted)
        {
            var parts = dotted.Split('.');
            if (parts[0] == "_collections_abc") return "collections.abc";
            return PrivateModuleAliases.TryGetValue(parts[0], out var pub) ? pub + dotted.Substring(parts[0].Length) : dotted;
        }

        /// <summary>from m import a, b: m exists and has each name (a function, value, class or submodule), or is a typing-only module.</summary>
        bool ImportFromResolves(ImportFromStmt imf)
        {
            string absolute = _m.ResolveImport(imf.Module, imf.Level);
            if (absolute == null || !ModuleExists(absolute)) return false;
            string name = PublicModule(absolute);
            if (IgnoredModules.Contains(name)) return true;
            if (ResolvePsModule(name) is ModuleInfo mod)
                return mod.WritesModuleGlobals || imf.Names.All(a => a.Name == "*" || ProjectExport(mod, a.Name).Owner != null || RuntimeReExport(mod, a.Name).Module != null);
            if (StdlibType(name) is string meta)
                return imf.Names.All(a => a.Name == "*" || RuntimeExport(name, meta, a.Name) ||
                                          RuntimePackageChild(name, meta, a.Name) != null);
            return true; // (a .NET namespace or type: its members are checked where they are used)
        }

        /// <summary>Does the import name a module Typhon can bind (a Typhon module, a standard module, a .NET namespace or type)?</summary>
        bool ModuleExists(string name)
        {
            name = PublicModule(name);
            return IgnoredModules.Contains(name) || ResolvePsModule(name) != null || StdlibType(name) != null || _r.TypeExists(name) || _r.NamespaceExists(name);
        }

        /// <summary>
        /// try: import x / except ImportError: ... where x is no module Typhon has (pwd on Windows, _winapi, a C extension):
        /// what Python runs when that import fails, the statements before it and the handler (Typhon resolves imports when
        /// it compiles, so the outcome is known then).
        /// </summary>
        void ResolveFailingImports(List<Stmt> body, bool moduleScope = true, bool optional = false)
        {
            for (int i = 0; i < body.Count; i++)
            {
                switch (body[i])
                {
                    case TryStmt t when t.FinalBody.Count == 0:
                    {
                        var handler = t.Handlers.FirstOrDefault(HandlesMissingImport);
                        int failing = t.Body.FindIndex(st => st is ImportStmt im && im.Names.Any(a => !ModuleExists(a.Name)) ||
                                                             st is ImportFromStmt imf && !ImportFromResolves(imf));
                        // try: from _queue import Empty / except ImportError: class Empty...: the imports all succeed, so
                        // the handler never runs (Typhon resolves imports when it compiles)
                        if (failing < 0 && t.Handlers.Count > 0 && t.FinalBody.Count == 0 &&
                            t.Handlers.All(h => h.Type != null && h.Name == null &&
                                (h.Type is TupleExpr ht ? ht.Elts : new List<Expr> { h.Type }).All(e => e is NameExpr en && en.Id is "ImportError" or "ModuleNotFoundError")) &&
                            t.Body.Count > 0 && t.Body.All(st => st is ImportStmt || st is ImportFromStmt || st is PassStmt) &&
                            !t.Body.Any(st => PlatformModules(st).Any()) &&
                            !t.Body.OfType<ImportFromStmt>().Any(from => _m.ResolveImport(from.Module, from.Level) is string absolute &&
                                ResolvePsModule(absolute) is ModuleInfo source && from.Names.Any(a => a.Name != "*" && UsesRuntimeProjectMember(source, a.Name))))
                        {
                            var kept = t.Body.Concat(t.OrElse).ToList();
                            body.RemoveAt(i);
                            body.InsertRange(i, kept);
                            i += kept.Count - 1;
                            continue;
                        }
                        if (handler != null && failing >= 0 && handler.Name == null &&
                            !t.Body.Take(failing).Any(st => st is ImportStmt || st is ImportFromStmt || st is FunctionDef || st is ClassDef))
                        {
                            if (moduleScope && t.Body[failing] is ImportStmt { Names.Count: 1 } unavailable)
                            {
                                var alias = unavailable.Names[0];
                                _m.Ast.FailedModuleImports.Add(alias.AsName ?? alias.Name.Split('.')[0]);
                            }
                            var replacement = t.Body.Take(failing).Concat(handler.Body).ToList();
                            body.RemoveAt(i);
                            body.InsertRange(i, replacement);
                            if (replacement.LastOrDefault() is ReturnStmt or RaiseStmt)
                                body.RemoveRange(i + replacement.Count, body.Count - i - replacement.Count);
                            i--;
                            continue;
                        }
                        ResolveFailingImports(t.Body, moduleScope, optional || handler != null);
                        foreach (var h in t.Handlers) ResolveFailingImports(h.Body, moduleScope, optional);
                        ResolveFailingImports(t.OrElse, moduleScope, optional);
                        break;
                    }
                    case TryStmt t2:
                        ResolveFailingImports(t2.Body, moduleScope, optional || t2.Handlers.Any(HandlesMissingImport));
                        foreach (var h in t2.Handlers) ResolveFailingImports(h.Body, moduleScope, optional);
                        ResolveFailingImports(t2.OrElse, moduleScope, optional);
                        ResolveFailingImports(t2.FinalBody, moduleScope, optional);
                        break;
                    case IfStmt ifs:
                        ResolveFailingImports(ifs.Body, moduleScope, optional);
                        ResolveFailingImports(ifs.OrElse, moduleScope, optional);
                        break;
                    case ImportStmt import when optional && import.Names.FirstOrDefault(a => !ModuleExists(a.Name)) is ImportAlias missing:
                        var available = import.Names.TakeWhile(a => a != missing).ToList();
                        if (available.Count > 0)
                        {
                            body.Insert(i, new ImportStmt { Line = import.Line, Col = import.Col, Names = available });
                            i++;
                        }
                        ReplaceUnavailableImport(body, i, import, "ModuleNotFoundError", $"No module named '{missing.Name}'");
                        break;
                    case ImportFromStmt from when optional && !ImportFromResolves(from):
                        ReplaceUnavailableImport(body, i, from, "ImportError", $"cannot import name from '{from.Module}'");
                        break;
                    case FunctionDef fd: ResolveFailingImports(fd.Body, moduleScope: false); break;
                    case ClassDef cd: ResolveFailingImports(cd.Body, moduleScope: false); break;
                }
            }
        }

        void MaterializeNativeTypeExports(List<Stmt> body)
        {
            if (_r is not Semantics.RoslynTypeResolver resolver || ResolvePsModule("_types") != null) return;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i] is not ImportFromStmt { Module: "_types", Level: 0, Names.Count: 1 } from || from.Names[0].Name != "*") continue;
                string target = "__typhon_native_types";
                body[i] = new ImportStmt
                {
                    Line = from.Line, Col = from.Col,
                    Names = { new ImportAlias { Name = "_types", AsName = target } },
                };
                foreach (string name in resolver.StaticFieldNames("Typhon.Stdlib._types").Where(name => !name.StartsWith("_")))
                {
                    _m.Globals.Add(name);
                    body.Insert(++i, new AssignStmt
                    {
                        Line = from.Line, Col = from.Col,
                        Targets = { new NameExpr { Id = name, Line = from.Line, Col = from.Col } },
                        Value = new AttributeExpr { Target = new NameExpr { Id = target }, Name = name, Line = from.Line, Col = from.Col },
                    });
                }
            }
        }

        void RemoveResolvedImportBindings()
        {
            if (_m.ConditionalGlobals.Count == 0) return;
            var imports = _m.Ast.Body.OfType<ImportFromStmt>().SelectMany(statement => statement.Names)
                .Select(alias => alias.AsName ?? alias.Name).Where(_m.ConditionalGlobals.Contains).Distinct().ToList();
            if (imports.Count == 0) return;
            // A resolved import can replace a try suite whose fallback defined the name.
            // The old conditional binding cannot shadow the imported class after the rewrite.
            var remaining = LocalsAnalyzer.Analyze(_m.Ast.Body, Enumerable.Empty<string>(), null);
            foreach (string name in imports.Where(name => !remaining.AllLocals.Contains(name) && !remaining.LocalFunctions.Contains(name)))
            {
                _m.ConditionalGlobals.Remove(name);
                _m.Globals.Remove(name);
            }
        }

        static bool HandlesMissingImport(ExceptHandler handler) => handler.Type == null ||
            (handler.Type is TupleExpr tuple ? tuple.Elts : new List<Expr> { handler.Type })
                .Any(e => e is NameExpr name && name.Id is "ImportError" or "ModuleNotFoundError" or "Exception");

        static void ReplaceUnavailableImport(List<Stmt> body, int position, Stmt import, string kind, string message)
        {
            body[position] = new RaiseStmt
            {
                Line = import.Line,
                Col = import.Col,
                Exc = new CallExpr
                {
                    Func = new NameExpr { Id = kind },
                    Args = { new Arg { Value = new ConstExpr { Kind = ConstKind.Str, Str = message } } },
                },
            };
            body.RemoveRange(position + 1, body.Count - position - 1);
        }

        string StdlibType(string dotted)
        {
            dotted = PublicModule(dotted);
            var parts = dotted.Split('.');
            foreach (var head in new[] { parts[0], parts[0] + "_" })
            {
                string meta = "Typhon.Stdlib." + head + (parts.Length > 1 ? "+" + string.Join("+", parts.Skip(1)) : "");
                if (_r.TypeExists(meta)) return meta;
            }
            return null;
        }

        bool AnyArity(string meta, out int arity)
        {
            for (arity = 0; arity <= 8; arity++)
                if (_r.TypeExists(arity == 0 ? meta : meta + "`" + arity)) return true;
            return false;
        }
    }
}
