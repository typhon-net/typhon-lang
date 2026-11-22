// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Emit;

namespace Typhon.Compiler.Syntax
{
    /// <summary>Keep the function's implementation separate when its module name is rebound.</summary>
    internal static class ReboundModuleFunctions
    {
        public static void Apply(ModuleInfo module)
        {
            var blocks = ModuleBlocks(module.Ast.Body).ToArray();
            var definitions = blocks.SelectMany(block => block.OfType<FunctionDef>()).GroupBy(f => f.Name).ToArray();
            if (definitions.Length == 0) return;

            var writes = new HashSet<string>();
            foreach (var block in blocks)
            {
                foreach (var statement in block)
                {
                    switch (statement)
                    {
                        case AssignStmt assign: foreach (var target in assign.Targets) AddTarget(target); break;
                        case AugAssignStmt assign: AddTarget(assign.Target); break;
                        case AnnAssignStmt assign when assign.Value != null: AddTarget(assign.Target); break;
                        case DelStmt delete: foreach (var target in delete.Targets) AddTarget(target); break;
                        case ForStmt loop: AddTarget(loop.Target); break;
                        case WithStmt with: foreach (var item in with.Items) AddTarget(item.Var); break;
                    }
                }
            }
            AstWalker.VisitAll(module.Ast.Body, node =>
            {
                if (node is GlobalStmt { Nonlocal: false } global) writes.UnionWith(global.Names);
            });
            foreach (var group in definitions)
            {
                if ((group.Count() > 1 || writes.Contains(group.Key)) &&
                    !group.Any(definition => definition.Decorators.Any(decorator => Decorators.Name(decorator) is "overload" or "typing.overload")))
                    module.ReboundFunctions.Add(group.Key);
            }
            if (module.ReboundFunctions.Count == 0) return;

            var used = new HashSet<string>();
            AstWalker.VisitAll(module.Ast.Body, node =>
            {
                if (node is NameExpr name) used.Add(name.Id);
                else if (node is FunctionDef definition) used.Add(definition.Name);
                else if (node is ClassDef type) used.Add(type.Name);
            });
            int suffix = 0;
            foreach (var block in blocks)
            {
                for (int i = 0; i < block.Count; i++)
                {
                    if (block[i] is not FunctionDef function || !module.ReboundFunctions.Contains(function.Name)) continue;
                    string name = function.Name;
                    string implementation;
                    do { implementation = "__typhon_rebound_" + ++suffix; } while (!used.Add(implementation));
                    function.PyName ??= name;
                    function.Name = implementation;
                    block.Insert(++i, new AssignStmt
                    {
                        Line = function.Line, Col = function.Col,
                        Targets = { new NameExpr { Id = name, Line = function.Line, Col = function.Col } },
                        Value = new NameExpr { Id = implementation, Line = function.Line, Col = function.Col },
                    });
                    module.Functions.Add(implementation);
                    module.Globals.Add(name);
                    module.ReboundImplementations.Add(function);
                }
            }

            void AddTarget(Expr target)
            {
                if (target is NameExpr name) writes.Add(name.Id);
                else if (target is TupleExpr tuple) foreach (var item in tuple.Elts) AddTarget(item);
                else if (target is ListExpr list) foreach (var item in list.Elts) AddTarget(item);
                else if (target is StarredExpr star) AddTarget(star.Value);
            }
        }

        static IEnumerable<List<Stmt>> ModuleBlocks(List<Stmt> statements)
        {
            yield return statements;
            foreach (var statement in statements)
            {
                if (statement is FunctionDef or ClassDef) continue;
                var inner = AstWalker.Children(statement).OfType<List<Stmt>>();
                if (statement is TryStmt attempt) inner = inner.Concat(attempt.Handlers.Select(handler => handler.Body));
                foreach (var block in inner)
                    foreach (var nested in ModuleBlocks(block))
                        yield return nested;
            }
        }
    }
}
