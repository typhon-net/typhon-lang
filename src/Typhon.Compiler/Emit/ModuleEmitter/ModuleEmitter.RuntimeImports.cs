// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        HashSet<string> _conditionedImportAliases;
        readonly HashSet<ImportFromStmt> _conditionalNativeStars = new HashSet<ImportFromStmt>();

        void FindConditionalImportAliases(List<Stmt> body)
        {
            foreach (var import in _m.ConditionalStarImports)
                if (NativeStarSource(_m, import).Metadata != null)
                    _conditionalNativeStars.Add(import);
            var modules = new Dictionary<string, string>();
            _conditionedImportAliases = new HashSet<string>();
            void Record(string alias, string source)
            {
                if (modules.TryGetValue(alias, out string previous) && previous != source)
                    _conditionedImportAliases.Add(alias);
                else
                    modules[alias] = source;
            }

            AstWalker.VisitAll(body, node =>
            {
                if (node is ImportStmt import)
                    foreach (var alias in import.Names)
                    {
                        if (alias.AsName == null || ResolvePsModule(alias.Name) == null && StdlibType(alias.Name) == null) continue;
                        Record(alias.AsName, alias.Name);
                    }
                if (node is ImportFromStmt from && _m.ResolveImport(from.Module, from.Level) is string module &&
                    (ResolvePsModule(module) != null || StdlibType(module) != null))
                    foreach (var alias in from.Names)
                        if (alias.Name != "*") Record(alias.AsName ?? alias.Name, module + ":" + alias.Name);
            }, includeNestedScopes: false);
        }

        (string Module, string Metadata) NativeStarSource(ModuleInfo owner, ImportFromStmt import)
        {
            string name = owner.ResolveImport(import.Module, import.Level);
            if (name == null || _p.FindModule(name) != null ||
                owner.PackageName.Length > 0 && _p.FindModule(owner.PackageName + "." + name) != null)
                return default;
            return (name, StdlibType(name));
        }

        bool ConditionalStarExports(ModuleInfo owner, string name) =>
            !name.StartsWith("_", System.StringComparison.Ordinal) &&
            owner.ConditionalStarImports.Any(import =>
                NativeStarSource(owner, import) is (string module, string metadata) &&
                RuntimeExport(module, metadata, name));

        bool ConditionalStarBinding(NameExpr name) =>
            _conditionalNativeStars.Count > 0 && !_m.Globals.Contains(name.Id) && !IsShadowed(name.Id) &&
            IsSourceNameRead(name) && ConditionalStarExports(_m, name.Id);

        bool IsConditionalImportAlias(ImportAlias alias, bool moduleScope) =>
            moduleScope && alias.AsName != null && _conditionedImportAliases?.Contains(alias.AsName) == true &&
                (ResolvePsModule(alias.Name) != null || StdlibType(alias.Name) != null);

        void LowerRuntimeImports(List<Stmt> body, bool allowImports = true, bool moduleScope = true)
        {
            for (int index = 0; index < body.Count; index++)
            {
                var original = body[index];
                List<Stmt> replacements = null;
                switch (original)
                {
                    case ImportFromStmt from when from.Names.All(alias => alias.Name != "*") &&
                        (allowImports && (_m.RuntimeCodeMode != null && from.Level > 0 || !ImportFromResolves(from)) ||
                         moduleScope && from.Names.Any(alias => _conditionedImportAliases?.Contains(alias.AsName ?? alias.Name) == true)):
                        bool unresolved = allowImports && (_m.RuntimeCodeMode != null && from.Level > 0 || !ImportFromResolves(from));
                        replacements = from.Names.Select(alias => unresolved ||
                            _conditionedImportAliases?.Contains(alias.AsName ?? alias.Name) == true
                            ? (Stmt)RuntimeImportBinding(from, from.Module, from.Level, alias.Name, alias.AsName ?? alias.Name, false)
                            : new ImportFromStmt { Module = from.Module, Level = from.Level, Line = from.Line, Col = from.Col, Names = { alias } }).ToList();
                        break;
                    case ImportStmt import when import.Names.Any(alias =>
                        IsConditionalImportAlias(alias, moduleScope) || allowImports && !ModuleExists(alias.Name)):
                        replacements = import.Names.Select(alias => ModuleExists(alias.Name) && !IsConditionalImportAlias(alias, moduleScope)
                            ? (Stmt)new ImportStmt { Line = import.Line, Col = import.Col, Names = { alias } }
                            : RuntimeImportBinding(import, alias.Name, 0, null, alias.AsName ?? alias.Name.Split('.')[0], alias.AsName == null)).ToList();
                        break;
                    case FunctionDef function: LowerRuntimeImports(function.Body, moduleScope: false); break;
                    case ClassDef definition: LowerRuntimeImports(definition.Body, allowImports: false, moduleScope: false); break;
                    case IfStmt conditional: LowerRuntimeImports(conditional.Body, moduleScope: moduleScope); LowerRuntimeImports(conditional.OrElse, moduleScope: moduleScope); break;
                    case WhileStmt loop: LowerRuntimeImports(loop.Body, moduleScope: moduleScope); LowerRuntimeImports(loop.OrElse, moduleScope: moduleScope); break;
                    case ForStmt loop: LowerRuntimeImports(loop.Body, moduleScope: moduleScope); LowerRuntimeImports(loop.OrElse, moduleScope: moduleScope); break;
                    case WithStmt with: LowerRuntimeImports(with.Body, moduleScope: moduleScope); break;
                    case TryStmt handler:
                        LowerRuntimeImports(handler.Body, moduleScope: moduleScope);
                        foreach (var clause in handler.Handlers) LowerRuntimeImports(clause.Body, moduleScope: moduleScope);
                        LowerRuntimeImports(handler.OrElse, moduleScope: moduleScope);
                        LowerRuntimeImports(handler.FinalBody, moduleScope: moduleScope);
                        break;
                    case MatchStmt match:
                        foreach (var clause in match.Cases) LowerRuntimeImports(clause.Body, moduleScope: moduleScope);
                        break;
                }
                if (replacements == null) continue;
                if (moduleScope && _m.RuntimeCodeMode == null)
                    foreach (var assignment in replacements.OfType<AssignStmt>())
                        foreach (var target in assignment.Targets.OfType<NameExpr>())
                            if (_m.Globals.Add(target.Id)) _m.ConditionalGlobals.Add(target.Id);
                body.RemoveAt(index);
                body.InsertRange(index, replacements);
                index += replacements.Count - 1;
            }
        }

        static AssignStmt RuntimeImportBinding(Stmt from, string module, int level, string member, string binding, bool bindRoot)
        {
            ConstExpr Text(string value) => new ConstExpr { Kind = value == null ? ConstKind.None : ConstKind.Str, Str = value };
            return new AssignStmt
            {
                Line = from.Line, Col = from.Col,
                Targets = { new NameExpr { Id = binding, Line = from.Line, Col = from.Col } },
                Value = new CallExpr
                {
                    Line = from.Line, Col = from.Col, Func = new NameExpr { Id = "__typhon_source_import" },
                    Args =
                    {
                        new Arg { Value = Text(module) },
                        new Arg { Value = new ConstExpr { Kind = ConstKind.Int, Raw = level.ToString(CultureInfo.InvariantCulture) } },
                        new Arg { Value = Text(member) },
                        new Arg { Value = new ConstExpr { Kind = ConstKind.Bool, Raw = bindRoot ? "True" : "False" } },
                    },
                },
            };
        }
    }
}
