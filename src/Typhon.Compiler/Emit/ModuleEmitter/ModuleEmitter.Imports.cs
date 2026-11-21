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
            // relative to this module's package
            int lastDot = _m.Name.LastIndexOf('.');
            if (lastDot > 0) return _p.FindModule(_m.Name.Substring(0, lastDot) + "." + dotted);
            return null;
        }

        string ResolveRelative(string module, int level)
        {
            if (level == 0) return module;
            var parts = _m.Name.Split('.').ToList();
            parts.RemoveAt(parts.Count - 1);
            for (int i = 1; i < level && parts.Count > 0; i++) parts.RemoveAt(parts.Count - 1);
            if (!string.IsNullOrEmpty(module)) parts.Add(module);
            return string.Join(".", parts);
        }

        static readonly HashSet<string> IgnoredModules = new HashSet<string> { "typing", "abc", "enum", "__future__", "collections.abc", "typing_extensions", "clr" };

        void ProcessImport(ImportStmt s)
        {
            foreach (var alias in s.Names)
            {
                string name = alias.Name, local = alias.AsName;
                if (IgnoredModules.Contains(name)) continue;
                var mod = ResolvePsModule(name);
                if (mod != null)
                {
                    _moduleAliases[local ?? name] = mod;
                    if (local != null) _usingLines.Add($"using {Esc(local)} = {mod.QualifiedClassName};");
                    continue;
                }
                string stdlib = StdlibType(name);
                if (stdlib != null)
                {
                    string l = local ?? name.Split('.')[0];
                    if (local == null && name.Contains(".")) { Unsupported(s, "'import a.b' of a Typhon standard module (use 'from a import b')"); continue; }
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
            string modName = ResolveRelative(s.Module, s.Level);
            if (IgnoredModules.Contains(modName)) return;

            var mod = ResolvePsModule(modName);
            if (mod != null)
            {
                _usingLines.Add($"using static {mod.QualifiedClassName};");
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _wildcardModules.Add(mod); continue; }
                    if (!mod.Classes.ContainsKey(a.Name) && !mod.Functions.Contains(a.Name) && !mod.Globals.Contains(a.Name))
                    { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}'"); continue; }
                    _importedFromModules[a.AsName ?? a.Name] = new KeyValuePair<ModuleInfo, string>(mod, a.Name);
                    if (a.AsName != null)
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
                        if (_r.TypeExists(meta + "+" + a.Name)) _usingLines.Add($"using {Esc(a.AsName)} = global::{csName}.{a.Name};");
                        else Unsupported(s, $"renaming the member '{a.Name}' on import");
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
        string StdlibType(string dotted)
        {
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
