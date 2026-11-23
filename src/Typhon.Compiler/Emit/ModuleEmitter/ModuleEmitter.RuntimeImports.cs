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

        void FindConditionalImportAliases(List<Stmt> body)
        {
            var modules = new Dictionary<string, string>();
            _conditionedImportAliases = new HashSet<string>();
            AstWalker.VisitAll(body, node =>
            {
                if (node is not ImportStmt import) return;
                foreach (var alias in import.Names)
                {
                    if (alias.AsName == null || ResolvePsModule(alias.Name) == null && StdlibType(alias.Name) == null) continue;
                    if (modules.TryGetValue(alias.AsName, out string previous) && previous != alias.Name)
                        _conditionedImportAliases.Add(alias.AsName);
                    else
                        modules[alias.AsName] = alias.Name;
                }
            }, includeNestedScopes: false);
        }

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
                    case ImportFromStmt from when allowImports && (_m.RuntimeCodeMode != null && from.Level > 0 || !ImportFromResolves(from)) &&
                        from.Names.All(n => n.Name != "*"):
                        replacements = from.Names.Select(n => (Stmt)RuntimeImportBinding(from, from.Module, from.Level, n.Name, n.AsName ?? n.Name, false)).ToList();
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
