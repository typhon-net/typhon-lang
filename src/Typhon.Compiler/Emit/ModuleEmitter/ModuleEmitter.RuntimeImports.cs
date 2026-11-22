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
        void LowerRuntimeImports(List<Stmt> body)
        {
            for (int index = 0; index < body.Count; index++)
            {
                var original = body[index];
                List<Stmt> replacements = null;
                switch (original)
                {
                    case ImportFromStmt from when (from.Level > 0 || !ImportFromResolves(from)) && from.Names.All(n => n.Name != "*"):
                        replacements = from.Names.Select(n => (Stmt)RuntimeImportBinding(from, from.Module, from.Level, n.Name, n.AsName ?? n.Name, false)).ToList();
                        break;
                    case ImportStmt import when import.Names.Any(alias => !ModuleExists(alias.Name)):
                        replacements = import.Names.Select(alias => ModuleExists(alias.Name)
                            ? (Stmt)new ImportStmt { Line = import.Line, Col = import.Col, Names = { alias } }
                            : RuntimeImportBinding(import, alias.Name, 0, null, alias.AsName ?? alias.Name.Split('.')[0], alias.AsName == null)).ToList();
                        break;
                    case FunctionDef function: LowerRuntimeImports(function.Body); break;
                    case IfStmt conditional: LowerRuntimeImports(conditional.Body); LowerRuntimeImports(conditional.OrElse); break;
                    case WhileStmt loop: LowerRuntimeImports(loop.Body); LowerRuntimeImports(loop.OrElse); break;
                    case ForStmt loop: LowerRuntimeImports(loop.Body); LowerRuntimeImports(loop.OrElse); break;
                    case WithStmt with: LowerRuntimeImports(with.Body); break;
                    case TryStmt handler:
                        LowerRuntimeImports(handler.Body);
                        foreach (var clause in handler.Handlers) LowerRuntimeImports(clause.Body);
                        LowerRuntimeImports(handler.OrElse);
                        LowerRuntimeImports(handler.FinalBody);
                        break;
                    case MatchStmt match:
                        foreach (var clause in match.Cases) LowerRuntimeImports(clause.Body);
                        break;
                }
                if (replacements == null) continue;
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
