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

        string ImportModule(ModuleInfo module) =>
            $"global::Typhon.Runtime.PyModule.Import(typeof({ModuleClassName(module)}), {CsString(module.Name)})";

        string ImportProjectAttribute(ModuleInfo module, string name) =>
            $"global::Typhon.Runtime.PyModule.ImportAttribute(typeof({ModuleClassName(module)}), {CsString(module.Name)}, {CsString(name)})";

        string RuntimeProjectMember(ModuleInfo module, string name) =>
            $"{Rt("getattr")}({(module.MayReplaceModule ? ImportModule(module) : ModuleObject(module))}, {CsString(name)})";

        static bool ConditionalModuleClass(ModuleInfo module, string name) =>
            module.Classes.TryGetValue(name, out var definition) && definition.ConditionalModuleDefinition;

        static bool HasModuleAttributeFallback(ModuleInfo module) =>
            module.Functions.Contains("__getattr__") || module.Globals.Contains("__getattr__");

        bool UsesRuntimeProjectMember(ModuleInfo module, string name) =>
            module.MayReplaceModule || ConditionalModuleClass(module, name) || module.ConditionalImportBindings.Contains(name) ||
            (module.WritesModuleGlobals || HasModuleAttributeFallback(module) || ConditionalStarExports(module, name)) &&
            ProjectExport(module, name).Owner == null && RuntimeReExport(module, name).Module == null;

        bool ConditionalClassReference(Expr reference) => reference switch
        {
            NameExpr name when !IsLocal(name.Id) => ConditionalModuleClass(_m, name.Id) ||
                _runtimeProjectBindings.ContainsKey(name.Id),
            AttributeExpr member when DottedName(member.Target) is string moduleName && ModuleRef(moduleName) is ModuleInfo source =>
                source.MayReplaceModule || ConditionalModuleClass(source, member.Name),
            _ => false,
        };

        string ImportedIterableType(string name)
        {
            if (_m.Globals.Contains(name) || !_importedFromModules.TryGetValue(name, out var imported) ||
                _runtimeProjectBindings.ContainsKey(name) || Ti == null ||
                !Ti.Fields.TryGetValue(TypeInference.ModuleFieldsKey(imported.Key), out var fields) ||
                !fields.TryGetValue(imported.Value, out string type) || type == TypeInference.Pending)
                return null;
            return ElementTypeOf(type) != null ? type : null;
        }

        string QualifiedProjectImport(string name)
        {
            if (IsLocal(name) || _m.Globals.Contains(name) || _m.Functions.Contains(name) ||
                _m.Classes.ContainsKey(name) || _runtimeProjectBindings.ContainsKey(name)) return null;
            bool packageImport = _qualifiedPackageImports.Contains(name);
            if (packageImport && _moduleAliases.TryGetValue(name, out var module)) return ModuleClassName(module);
            // A Python class imported explicitly takes precedence over CLR types with the same short name.
            return _importedFromModules.TryGetValue(name, out var member) && (packageImport || member.Key.Classes.ContainsKey(member.Value))
                ? ModuleClassName(member.Key) + "." + Esc(member.Value) : null;
        }

        /// <summary>A member's declaration, following source re-exports. A null member name denotes a module.</summary>
        (ModuleInfo Owner, string Member) ProjectExport(ModuleInfo module, string name,
            HashSet<(ModuleInfo, string)> seen = null)
        {
            if (module.Functions.Contains(name) || module.Classes.ContainsKey(name) || module.Globals.Contains(name))
                return (module, name);
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
                        string original = a.Name == "*" && SourceStarExports(imported, name) ? name
                            : (a.AsName ?? a.Name) == name ? a.Name : null;
                        if (original == null) continue;
                        var export = ProjectExport(imported, original, seen);
                        if (export.Owner != null) return export;
                    }
                }
            }
            return module.IsPackage ? (_p.FindModule(module.Name + "." + name), null) : default;
        }

        string ProjectModuleMember(string dotted)
        {
            int dot = dotted.LastIndexOf('.');
            if (dot <= 0 || ModuleRef(dotted.Substring(0, dot)) is not ModuleInfo module) return null;
            var (owner, member) = ProjectExport(module, dotted.Substring(dot + 1));
            return owner == null ? null : member == null ? ModuleClassName(owner) : ModuleClassName(owner) + "." + Esc(member);
        }

        static bool SourceStarExports(ModuleInfo module, string name)
        {
            var values = module.Ast.Body.OfType<AssignStmt>().LastOrDefault(statement =>
                statement.Targets.Count == 1 && statement.Targets[0] is NameExpr { Id: "__all__" })?.Value;
            List<Expr> exports = values is ListExpr list ? list.Elts : (values as TupleExpr)?.Elts;
            if (exports != null && exports.All(value => value is ConstExpr { Kind: ConstKind.Str }))
                return exports.OfType<ConstExpr>().Any(value => value.Str == name);
            return !name.StartsWith("_");
        }

        string SourceStarReExport(string name)
        {
            for (int index = _wildcardModules.Count - 1; index >= 0; index--)
            {
                var module = _wildcardModules[index];
                if (!SourceStarExports(module, name)) continue;
                var (owner, member) = ProjectExport(module, name);
                if (owner != null)
                    return owner == module ? null : member != null
                        ? ModuleClassName(owner) + "." + Esc(member) : ModuleClassName(owner);
                var (source, binding) = RuntimeReExport(module, name);
                if (source != null && StdlibType(source) is string metadata)
                    return CsTypeName(metadata) + "." + Esc(binding);
            }
            return null;
        }

        // A source module can expose a runtime member through the modules it imports.
        (string Module, string Member) RuntimeReExport(ModuleInfo module, string name, HashSet<(ModuleInfo, string)> seen = null)
        {
            if (module.RebindsUnknownGlobals || module.Functions.Contains(name) || module.Classes.ContainsKey(name) || module.Globals.Contains(name)) return default;
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
                        if (_p.FindModule(path) is ModuleInfo imported && SourceStarExports(imported, name))
                        {
                            var result = RuntimeReExport(imported, name, seen);
                            if (result.Module != null) return result;
                        }
                        else if (!name.StartsWith("_") && _p.FindModule(path) == null &&
                                 StdlibType(path) is string starMeta && RuntimeExport(path, starMeta, name))
                            return (path, name);
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

        static bool IsPlatformModule(string name) => name is "termios" or "msvcrt" or "posix" or "nt" or "_winapi" or "_posixsubprocess";

        IEnumerable<string> PlatformModules(Stmt statement)
        {
            if (statement is ImportStmt direct)
                return direct.Names.Select(alias => alias.Name).Where(name => IsPlatformModule(name) && ResolvePsModule(name) == null);
            if (statement is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string module &&
                IsPlatformModule(module) && ResolvePsModule(module) == null) return new[] { module };
            return Enumerable.Empty<string>();
        }

        bool _nativeImportsInNamespace;

        IEnumerable<(string Module, string Metadata, string Member, string Binding)> NativeNamespaceImports(Stmt statement)
        {
            if (statement is ImportStmt direct)
                foreach (var alias in direct.Names)
                {
                    string name = alias.AsName == null ? alias.Name.Split('.')[0] : alias.Name;
                    if (ResolvePsModule(name) == null && StdlibType(name) is string metadata)
                        yield return (name, metadata, null, alias.AsName ?? name);
                }
            if (statement is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string module &&
                ResolvePsModule(module) == null && StdlibType(module) is string owner)
                foreach (var alias in from.Names)
                    if (RuntimePackageChild(module, owner, alias.Name) == null &&
                        (alias.Name == "*" || RuntimeExport(module, owner, alias.Name)))
                        yield return (module, owner, alias.Name, alias.AsName ?? alias.Name);
        }

        void EmitProjectImports(Stmt stmt)
        {
            foreach (var module in PlatformModules(stmt))
                _w.Line($"global::Typhon.Runtime.Builtins.__import_platform_module({CsString(module)});");
            foreach (var module in ProjectImports(stmt))
                _w.Line($"{ImportModule(module)};");
            if ((_nativeImportsInNamespace || _m.ConditionalImportBindings.Count > 0) &&
                (_scopeKey == TypeInference.FuncKey(_m, null, "<main>") ||
                 _scopeKey == TypeInference.FuncKey(_m, null, "<module>")))
            {
                string module = ModuleObject(_m);
                foreach (var binding in NativeNamespaceImports(stmt))
                {
                    if (!_nativeImportsInNamespace && !_m.ConditionalImportBindings.Contains(binding.Binding)) continue;
                    string source = $"global::Typhon.Runtime.PyModule.Of(typeof({CsTypeName(binding.Metadata)}), {CsString(PublicModule(binding.Module))})";
                    if (binding.Member == "*")
                        _w.Line($"{module}.ImportNamespaceNames({source});");
                    else if (!_lateModuleFields.Contains(binding.Binding))
                    {
                        string value = binding.Member == null ? source : $"{source}.ImportAttribute({CsString(binding.Member)})";
                        _w.Line($"{module}.__dict__[{CsString(binding.Binding)}] = {value};");
                    }
                }
            }
            if (stmt is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string absolute)
            {
                var importedSource = ResolvePsModule(absolute);
                bool moduleScope = _scopeKey == TypeInference.FuncKey(_m, null, "<main>") ||
                    _scopeKey == TypeInference.FuncKey(_m, null, "<module>");
                foreach (var alias in from.Names)
                {
                    string name = alias.AsName ?? alias.Name;
                    if (importedSource != null && _runtimeProjectBindings.TryGetValue(name, out var binding) &&
                        binding.Owner == importedSource && binding.Name == alias.Name)
                        _w.Line($"{binding.Storage} = {ImportProjectAttribute(importedSource, alias.Name)};");
                    else if (moduleScope && _lateModuleFields.Contains(name) ||
                             _scope?.Locals.Globals.Contains(name) == true && _m.Globals.Contains(name))
                    {
                        string value = importedSource != null ? ImportProjectAttribute(importedSource, alias.Name) :
                            StdlibType(absolute) is string runtime ?
                                $"global::Typhon.Runtime.PyModule.Of(typeof({CsTypeName(runtime)}), {CsString(absolute)}).ImportAttribute({CsString(alias.Name)})" : null;
                        if (value != null)
                            EmitAssignToName(new NameExpr { Id = name, Line = from.Line, Col = from.Col }, null, from, value);
                    }
                }
            }
            if (stmt is not ImportStmt imported) return;
            foreach (var alias in imported.Names)
            {
                string binding = alias.AsName ?? alias.Name.Split('.')[0];
                if (!_lateModuleFields.Contains(binding)) continue;
                string moduleName = alias.AsName == null ? binding : alias.Name;
                if (ResolvePsModule(moduleName) is ModuleInfo source)
                    _w.Line($"{Esc(binding)} = {ImportModule(source)};");
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
                if (name is "typing" or "_typing") _typingModules.Add(local ?? name);
                var mod = ResolvePsModule(name);
                if (mod == null && name == "typing" && StdlibType("_typing") is string nativeTyping)
                {
                    BindRuntimeModule(local ?? name, nativeTyping);
                    continue;
                }
                if (mod == null && PublicModule(name) == "collections.abc")
                {
                    string bound = local ?? name.Split('.')[0];
                    _typingOnlyNames.Add(bound);
                    BindRuntimeModule(bound, local == null && name == "collections.abc" ? "Typhon.Stdlib.collections" : "Typhon.Stdlib.collections+abc");
                    continue;
                }
                if (mod == null && name is not ("__future__" or "enum") && IgnoredModules.Contains(PublicModule(name))) { _typingOnlyNames.Add(local ?? name.Split('.')[0]); continue; }
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
                    BindRuntimeModule(l, stdlib, name == "_ast" ? name : null);
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
        readonly Dictionary<string, string> _runtimeModuleAliases = new Dictionary<string, string>();

        void BindRuntimeModule(string name, string metadata, string importedName = null)
        {
            // A generated namespace or global type can reserve the CLR name of a native import.
            if (_p.Modules.Values.Any(module => module.Namespace == name || module.Namespace.StartsWith(name + ".", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(module.Namespace) && module.ClassName == name))
                _qualifiedRuntimeModuleImports.Add(name);
            else _usingLines.Add($"using {Esc(name)} = {CsTypeName(metadata)};");
            _dotnetAliases[name] = metadata;
            if (importedName != null) _runtimeModuleAliases[name] = importedName;
        }

        bool RuntimeExport(string module, string metadata, string name) =>
            module == "_ast"
                ? name.StartsWith("PyCF_", StringComparison.Ordinal) && _r.GetMembers(metadata, name).Count > 0 ||
                  name is not ("NodeVisitor" or "NodeTransformer") && AnyArity(metadata + "+" + name, out _)
                : _r.GetMembers(metadata, name).Count > 0 || AnyArity(metadata + "+" + name, out _) ||
                  StdlibType(module + "." + name) != null;

        ModuleInfo RuntimePackageChild(string package, string metadata, string name) =>
            name != "*" && !RuntimeExport(package, metadata, name) ? _p.FindModule(package + "." + name) : null;

        void ProcessImportFrom(ImportFromStmt s, bool resolveRuntimeMember = false)
        {
            if (_conditionalNativeStars.Contains(s)) return;
            string modName = _m.ResolveImport(s.Module, s.Level);
            if (modName == null) { Report(s, "TY0101", Severity.Error, "attempted relative import beyond the current package"); return; }
            string publicModule = PublicModule(modName);
            var mod = ResolvePsModule(modName);
            if (mod == null && !resolveRuntimeMember && IgnoredModules.Contains(publicModule))
            {
                string runtimeModule = modName == "typing" ? "_typing" : modName;
                string runtime = publicModule is ("collections.abc" or "typing" or "enum") ? StdlibType(runtimeModule) : null;
                foreach (var a in s.Names)
                {
                    if (runtime != null && (_r.GetMembers(runtime, a.Name).Any(member => member.IsStatic && member.IsMethod) ||
                        a.Name != "Mapping" && _r.TypeExists(runtime + "+" + a.Name)))
                    {
                        ProcessImportFrom(new ImportFromStmt
                        {
                            Module = runtimeModule, Level = s.Level, Line = s.Line, Col = s.Col, Names = { a },
                        }, resolveRuntimeMember: true);
                        continue;
                    }
                    string local = a.AsName ?? a.Name;
                    _typingOnlyNames.Add(local);
                    if (modName == "typing" && a.Name == "Union") _typingUnionImports.Add(local);
                    if (PublicModule(modName) == "collections.abc" && a.Name is "Mapping" or "MutableMapping")
                        _importedTypes[local] = a.Name == "Mapping" ? MappingMeta : MutableMappingMeta;
                }
                return;
            }

            if (mod != null)
            {
                if (!IsProjectPackageModule(mod) || s.Names.Any(a => a.Name == "*"))
                    _usingLines.Add($"using static {ModuleClassName(mod)};");
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _wildcardModules.Add(mod); continue; }
                    string bound = a.AsName ?? a.Name;
                    // A conditional fallback import can share its name with a source class.
                    // Keep the class declaration visible to inheritance and metaclass resolution.
                    if (mod.MayReplaceModule || mod.ConditionalImportBindings.Contains(a.Name) && !mod.Classes.ContainsKey(a.Name))
                    {
                        _runtimeProjectBindings[bound] = (mod, a.Name, "__typhon_" + NewTmp("import"));
                        continue;
                    }
                    var (owner, member) = ProjectExport(mod, a.Name);
                    if (owner == null)
                    {
                        var export = RuntimeReExport(mod, a.Name);
                        if (export.Module != null)
                            ProcessImportFrom(new ImportFromStmt
                            {
                                Module = export.Module, Line = s.Line, Col = s.Col,
                                Names = { new ImportAlias { Name = export.Member, AsName = bound } },
                            });
                        else if (mod.WritesModuleGlobals || HasModuleAttributeFallback(mod) || ConditionalStarExports(mod, a.Name))
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
                    if (IsProjectPackageModule(mod) || owner != mod || a.AsName != null)
                        _qualifiedPackageImports.Add(bound);
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
                    if (modName == "_ast" && a.Name != "*" && !RuntimeExport(modName, meta, a.Name))
                    {
                        Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}'");
                        continue;
                    }
                    if (modName == "_typing" && a.Name == "Union") _typingUnionImports.Add(a.AsName ?? a.Name);
                    if (stdlib != null)
                    {
                        if (RuntimePackageChild(modName, meta, a.Name) is ModuleInfo child)
                        {
                            string bound = a.AsName ?? a.Name;
                            _moduleAliases[bound] = child;
                            _qualifiedPackageImports.Add(bound);
                            continue;
                        }
                        if (a.Name == "*") _stdlibStar.Add((modName, meta));
                        else if (a.AsName == null)
                        {
                            _stdlibImported[a.Name] = meta;
                            string nested = meta + "+" + a.Name;
                            if (_r.TypeExists(nested)) _importedTypes[a.Name] = nested;
                        }
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
            ["_json"] = "json", ["_statistics"] = "statistics", ["_string"] = "string", ["_csv"] = "csv",
            ["_collections"] = "collections", ["_contextvars"] = "contextvars",
            ["_md5"] = "hashlib", ["_sha1"] = "hashlib", ["_pydecimal"] = "decimal",
            ["_codecs"] = "codecs", ["_decimal"] = "decimal", ["_bz2"] = "bz2", ["_locale"] = "locale",
            ["_stat"] = "stat", ["_warnings"] = "warnings", ["_py_warnings"] = "warnings", ["_queue"] = "queue", ["_socket"] = "socket",
        };

        string PublicModule(string dotted)
        {
            var parts = dotted.Split('.');
            if (parts[0] == "_collections_abc") return "collections.abc";
            if (parts[0] == "_functools" && _p.FindModule("functools") != null) return dotted;
            return PrivateModuleAliases.TryGetValue(parts[0], out var pub) ? pub + dotted.Substring(parts[0].Length) : dotted;
        }

        /// <summary>from m import a, b: m exists and has each name (a function, value, class or submodule), or is a typing-only module.</summary>
        bool ImportFromResolves(ImportFromStmt imf)
        {
            string absolute = _m.ResolveImport(imf.Module, imf.Level);
            if (absolute == null || !ModuleExists(absolute)) return false;
            string name = PublicModule(absolute);
            if (ResolvePsModule(absolute) is ModuleInfo mod)
                return mod.WritesModuleGlobals || imf.Names.All(a => a.Name == "*" ||
                    AvailableProjectExport(mod, a.Name) || RuntimeReExport(mod, a.Name).Module != null);
            if (IgnoredModules.Contains(name)) return true;
            if (StdlibType(name) is string meta)
                return imf.Names.All(a => a.Name == "*" || RuntimeExport(name, meta, a.Name) ||
                                          RuntimePackageChild(name, meta, a.Name) != null);
            return true; // (a .NET namespace or type: its members are checked where they are used)

            bool AvailableProjectExport(ModuleInfo owner, string member)
            {
                if (owner.ConditionalImportBindings.Contains(member) || ConditionalStarExports(owner, member)) return true;
                if (ProjectExport(owner, member).Owner == null) return false;
                if (owner != _m || !_m.IsPackage || !_m.ConditionalGlobals.Contains(member) ||
                    _p.FindModule(owner.Name + "." + member) != null) return true;

                return AstWalker.ContainsOutsideNestedScopes(_m.Ast.Body, node => node.Line < imf.Line && (node switch
                {
                    AssignStmt assign => assign.Targets.Any(target => NamesIn(target).Contains(member)),
                    AnnAssignStmt annotated => annotated.Value != null && NamesIn(annotated.Target).Contains(member),
                    ForStmt loop => NamesIn(loop.Target).Contains(member),
                    ImportStmt import => import.Names.Any(alias => (alias.AsName ?? alias.Name.Split('.')[0]) == member),
                    ImportFromStmt import => import.Names.Any(alias => (alias.AsName ?? alias.Name) == member),
                    _ => false,
                }));
            }
        }

        /// <summary>Does the import name a module Typhon can bind (a Typhon module, a standard module, a .NET namespace or type)?</summary>
        bool ModuleExists(string name)
        {
            return IgnoredModules.Contains(PublicModule(name)) || ResolvePsModule(name) != null ||
                StdlibType(name) != null || _r.TypeExists(name) || _r.NamespaceExists(name);
        }

        /// <summary>
        /// try: import x / except ImportError: ... where x is no module Typhon has (pwd on Windows, _winapi, a C extension):
        /// what Python runs when that import fails, the statements before it and the handler (Typhon resolves imports when
        /// it compiles, so the outcome is known then).
        /// </summary>
        bool HasOnlyResolvedImports(TryStmt statement) =>
            statement.Handlers.Count > 0 && statement.FinalBody.Count == 0 &&
            statement.Handlers.All(handler => handler.Type != null &&
                (handler.Name == null || statement.Body.All(IsNativeImport)) &&
                (handler.Type is TupleExpr tuple ? tuple.Elts : new List<Expr> { handler.Type })
                    .All(e => e is NameExpr n && n.Id is "ImportError" or "ModuleNotFoundError")) &&
            statement.Body.Count > 0 && statement.Body.All(s => s switch
            {
                ImportStmt import => import.Names.All(alias => ModuleExists(alias.Name)),
                ImportFromStmt from => ImportFromResolves(from),
                PassStmt => true,
                _ => false,
            }) &&
            !statement.Body.Any(s => PlatformModules(s).Any()) &&
            !statement.Body.OfType<ImportFromStmt>().Any(from => _m.ResolveImport(from.Module, from.Level) is string absolute &&
                ResolvePsModule(absolute) is ModuleInfo source && from.Names.Any(a => a.Name != "*" && UsesRuntimeProjectMember(source, a.Name)));

        bool IsNativeImport(Stmt statement)
        {
            bool IsNative(string module) => module != null && ResolvePsModule(module) == null && StdlibType(module) != null;
            return statement switch
            {
                ImportStmt direct => direct.Names.All(alias => IsNative(alias.Name)),
                ImportFromStmt from => IsNative(_m.ResolveImport(from.Module, from.Level)),
                PassStmt => true,
                _ => false,
            };
        }

        List<Stmt> ResolvedImportSuite(TryStmt statement, out Stmt unavailable)
        {
            unavailable = null;
            if (statement.FinalBody.Count != 0) return null;
            int failing = statement.Body.FindIndex(s => s is ImportStmt import && import.Names.Any(alias => !ModuleExists(alias.Name)) ||
                                                        s is ImportFromStmt from && !ImportFromResolves(from));
            if (failing < 0)
                return HasOnlyResolvedImports(statement) ? statement.Body.Concat(statement.OrElse).ToList() : null;

            var handler = statement.Handlers.FirstOrDefault(HandlesMissingImport);
            if (handler == null || handler.Name != null ||
                statement.Body.Take(failing).Any(s => s is ImportStmt or ImportFromStmt or FunctionDef or ClassDef))
                return null;

            unavailable = statement.Body[failing];
            return statement.Body.Take(failing).Concat(handler.Body).ToList();
        }

        // The selected declarations must be visible to the project class and function models.
        internal void PrepareImportBindings()
        {
            if (_m.RuntimeCodeMode != null) return;
            var body = _m.Ast.Body;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i] is not TryStmt branch || ResolvedImportSuite(branch, out var unavailable) is not { } selected ||
                    !selected.Any(s => s is ClassDef or FunctionDef)) continue;

                if (unavailable != null) RememberUnavailableBindings(unavailable);
                body.RemoveAt(i);
                body.InsertRange(i, selected);
                foreach (var declaration in selected)
                {
                    string name;
                    if (declaration is ClassDef cls)
                    {
                        name = cls.Name;
                        _m.Classes[name] = cls;
                    }
                    else if (declaration is FunctionDef function)
                    {
                        name = function.Name;
                        _m.Functions.Add(name);
                        _m.ConditionalFunctions.Remove(name);
                    }
                    else continue;

                    if (_m.ConditionalGlobals.Remove(name)) _m.Globals.Remove(name);
                    _m.Ast.FailedModuleImports.Remove(name);
                }
            }
        }

        void ResolveFailingImports(List<Stmt> body, bool moduleScope = true, bool optional = false)
        {
            for (int i = 0; i < body.Count; i++)
            {
                switch (body[i])
                {
                    case TryStmt t when t.FinalBody.Count == 0:
                    {
                        if (ResolvedImportSuite(t, out var unavailable) is { } selected)
                        {
                            if (moduleScope && unavailable != null) RememberUnavailableBindings(unavailable);
                            body.RemoveAt(i);
                            body.InsertRange(i, selected);
                            if (unavailable == null)
                            {
                                i += selected.Count - 1;
                                continue;
                            }
                            if (selected.LastOrDefault() is ReturnStmt or RaiseStmt)
                                body.RemoveRange(i + selected.Count, body.Count - i - selected.Count);
                            i--;
                            continue;
                        }
                        ResolveFailingImports(t.Body, moduleScope, optional || t.Handlers.Any(HandlesMissingImport));
                        foreach (var h in t.Handlers) ResolveFailingImports(h.Body, moduleScope, optional);
                        ResolveFailingImports(t.OrElse, moduleScope, optional);
                        break;
                    }
                    case TryStmt t2:
                        if (!_m.IsGdScript && t2.FinalBody.Count != 0 && t2.Handlers.Any(HandlesMissingImport))
                            LowerRuntimeImports(t2.Body, moduleScope: moduleScope);
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
                        if (moduleScope) RememberUnavailableBindings(from);
                        ReplaceUnavailableImport(body, i, from, "ImportError", $"cannot import name from '{from.Module}'");
                        break;
                    case FunctionDef fd: ResolveFailingImports(fd.Body, moduleScope: false); break;
                    case ClassDef cd: ResolveFailingImports(cd.Body, moduleScope: false); break;
                }
            }
        }

        void RememberUnavailableBindings(Stmt statement)
        {
            if (statement is ImportStmt { Names.Count: 1 } unavailable)
            {
                var alias = unavailable.Names[0];
                _m.Ast.FailedModuleImports.Add(alias.AsName ?? alias.Name.Split('.')[0]);
            }
            else if (statement is ImportFromStmt from &&
                (_m.ResolveImport(from.Module, from.Level) is not string absolute || !ModuleExists(absolute)))
                _m.Ast.FailedModuleImports.UnionWith(from.Names.Where(alias => alias.Name != "*")
                    .Select(alias => alias.AsName ?? alias.Name));
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
            if (dotted == "_ast") dotted = "ast";
            if (dotted == "importlib.readers") dotted = "importlib.resources.readers";
            var parts = dotted.Split('.');
            foreach (var head in new[] { parts[0], parts[0] + "_" })
            {
                string meta = "Typhon.Stdlib." + head + (parts.Length > 1 ? "+" + string.Join("+", parts.Skip(1)) : "");
                if (_r.TypeExists(meta)) return meta;
                if (parts.Length > 1 && _r.TypeExists(meta + "_")) return meta + "_";
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
