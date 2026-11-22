// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Emit;

namespace Typhon.Compiler.Syntax
{
    /// <summary>Keep a definition separate when its module name is rebound.</summary>
    internal static class ReboundModuleFunctions
    {
        public static void Apply(ModuleInfo module)
        {
            var blocks = ModuleBlocks(module.Ast.Body).ToArray();
            var definitions = blocks.SelectMany(block => block.OfType<FunctionDef>()).GroupBy(f => f.Name).ToArray();
            var classes = module.Ast.Body.OfType<ClassDef>().GroupBy(type => type.Name).ToArray();
            if (definitions.Length == 0 && classes.Length == 0) return;

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
            foreach (var group in classes)
                if (group.Count() > 1 || writes.Contains(group.Key)) module.ReboundClasses.Add(group.Key);
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
            if (module.ReboundFunctions.Count == 0 && module.ReboundClasses.Count == 0) return;

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
                    string name, prefix;
                    switch (block[i])
                    {
                        case FunctionDef method when module.ReboundFunctions.Contains(method.Name):
                            name = method.Name;
                            prefix = "__typhon_rebound_";
                            break;
                        case ClassDef type when block == module.Ast.Body && module.ReboundClasses.Contains(type.Name):
                            name = type.Name;
                            prefix = "__typhon_rebound_class_";
                            break;
                        default:
                            continue;
                    }
                    string implementation;
                    do { implementation = prefix + ++suffix; } while (!used.Add(implementation));
                    if (block[i] is FunctionDef function)
                    {
                        function.PyName ??= name;
                        function.Name = implementation;
                        module.Functions.Add(implementation);
                        module.ReboundImplementations.Add(function);
                    }
                    else
                    {
                        var type = (ClassDef)block[i];
                        type.PyName ??= name;
                        type.Name = implementation;
                    }
                    var definition = block[i];
                    block.Insert(++i, new AssignStmt
                    {
                        Line = definition.Line, Col = definition.Col,
                        Targets = { new NameExpr { Id = name, Line = definition.Line, Col = definition.Col } },
                        Value = new NameExpr { Id = implementation, Line = definition.Line, Col = definition.Col },
                    });
                    module.Globals.Add(name);
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
