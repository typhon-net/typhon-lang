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
            module.IsPackage || module.PackageName.Length > 0 && _p.FindModule(module.PackageName)?.IsPackage == true;

        readonly HashSet<string> _qualifiedPackageImports = new HashSet<string>();

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
            if (stmt is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string absolute &&
                ResolvePsModule(absolute) is ModuleInfo imported)
                return Ancestors(imported).Concat(from.Names.Select(a => ProjectExport(imported, a.Name))
                    .Where(x => x.Member == null).SelectMany(x => Ancestors(x.Owner))).Distinct();
            return Enumerable.Empty<ModuleInfo>();
        }

        void EmitProjectImports(Stmt stmt)
        {
            foreach (var module in ProjectImports(stmt))
                _w.Line($"global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof({ModuleClassName(module)}).TypeHandle);");
        }

        static readonly HashSet<string> IgnoredModules = new HashSet<string> { "typing", "abc", "enum", "__future__", "collections.abc", "typing_extensions", "clr" };

        void ProcessImport(ImportStmt s)
        {
            foreach (var alias in s.Names)
            {
                string name = alias.Name, local = alias.AsName;
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
                    _usingLines.Add($"using {Esc(l)} = global::{string.Join(".", stdlib.Replace('+', '.').Split('.').Select(Esc))};");
                    _dotnetAliases[l] = stdlib;
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

        void ProcessImportFrom(ImportFromStmt s)
        {
            string modName = _m.ResolveImport(s.Module, s.Level);
            if (modName == null) { Report(s, "TY0101", Severity.Error, "attempted relative import beyond the current package"); return; }
            if (IgnoredModules.Contains(PublicModule(modName))) { foreach (var a in s.Names) _typingOnlyNames.Add(a.AsName ?? a.Name); return; }

            var mod = ResolvePsModule(modName);
            if (mod != null)
            {
                if (!IsProjectPackageModule(mod) || s.Names.Any(a => a.Name == "*"))
                    _usingLines.Add($"using static {mod.QualifiedClassName};");
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _wildcardModules.Add(mod); continue; }
                    var (owner, member) = ProjectExport(mod, a.Name);
                    if (owner == null)
                    { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}'"); continue; }
                    string bound = a.AsName ?? a.Name;
                    if (member == null) _moduleAliases[bound] = owner;
                    else _importedFromModules[bound] = new KeyValuePair<ModuleInfo, string>(owner, member);
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
                if (meta.StartsWith("Typhon.Stdlib."))
                    foreach (var a in s.Names)
                        if (a.Name == "*") _stdlibStar.Add(meta);
                        else if (a.AsName == null) _stdlibImported[a.Name] = meta;
                foreach (var a in s.Names)
                    if (a.AsName != null)
                    {
                        if (_r.TypeExists(meta + "+" + a.Name))
                        {
                            _usingLines.Add($"using {Esc(a.AsName)} = global::{csName}.{a.Name};");
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
            ["_collections"] = "collections", ["_contextvars"] = "contextvars", ["_hashlib"] = "hashlib", ["_sha2"] = "hashlib",
            ["_md5"] = "hashlib", ["_sha1"] = "hashlib", ["_sha3"] = "hashlib", ["_blake2"] = "hashlib", ["_pydecimal"] = "decimal",
            ["_decimal"] = "decimal", ["_pickle"] = "pickle", ["_bz2"] = "bz2", ["_lzma"] = "lzma", ["_locale"] = "locale",
            ["_stat"] = "stat", ["_warnings"] = "warnings", ["_py_warnings"] = "warnings", ["_abc"] = "abc", ["_typing"] = "typing", ["_queue"] = "queue",
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
                return imf.Names.All(a => a.Name == "*" || ProjectExport(mod, a.Name).Owner != null);
            if (StdlibType(name) is string meta)
                return imf.Names.All(a => a.Name == "*" || _r.GetMembers(meta, a.Name).Count > 0 || AnyArity(meta + "+" + a.Name, out _) ||
                                          StdlibType(name + "." + a.Name) != null);
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
        void ResolveFailingImports(List<Stmt> body)
        {
            for (int i = 0; i < body.Count; i++)
            {
                switch (body[i])
                {
                    case TryStmt t when t.FinalBody.Count == 0:
                    {
                        var handler = t.Handlers.FirstOrDefault(h => h.Type == null ||
                            (h.Type is TupleExpr tt ? tt.Elts : new List<Expr> { h.Type }).Any(e => e is NameExpr en && en.Id is "ImportError" or "ModuleNotFoundError" or "Exception"));
                        int failing = t.Body.FindIndex(st => st is ImportStmt im && im.Names.Any(a => !ModuleExists(a.Name)) ||
                                                             st is ImportFromStmt imf && !ImportFromResolves(imf));
                        // try: from _queue import Empty / except ImportError: class Empty...: the imports all succeed, so
                        // the handler never runs (Typhon resolves imports when it compiles)
                        if (failing < 0 && t.Handlers.Count > 0 && t.FinalBody.Count == 0 &&
                            t.Handlers.All(h => h.Type != null && h.Name == null &&
                                (h.Type is TupleExpr ht ? ht.Elts : new List<Expr> { h.Type }).All(e => e is NameExpr en && en.Id is "ImportError" or "ModuleNotFoundError")) &&
                            t.Body.Count > 0 && t.Body.All(st => st is ImportStmt || st is ImportFromStmt || st is PassStmt))
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
                            var replacement = t.Body.Take(failing).Concat(handler.Body).ToList();
                            body.RemoveAt(i);
                            body.InsertRange(i, replacement);
                            i--;
                            continue;
                        }
                        ResolveFailingImports(t.Body);
                        foreach (var h in t.Handlers) ResolveFailingImports(h.Body);
                        ResolveFailingImports(t.OrElse);
                        break;
                    }
                    case TryStmt t2: ResolveFailingImports(t2.Body); foreach (var h in t2.Handlers) ResolveFailingImports(h.Body); ResolveFailingImports(t2.OrElse); ResolveFailingImports(t2.FinalBody); break;
                    case IfStmt ifs: ResolveFailingImports(ifs.Body); ResolveFailingImports(ifs.OrElse); break;
                    case FunctionDef fd: ResolveFailingImports(fd.Body); break;
                    case ClassDef cd: ResolveFailingImports(cd.Body); break;
                }
            }
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
