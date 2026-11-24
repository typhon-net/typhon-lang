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
            foreach (var statement in blocks.SelectMany(block => block))
                foreach (var bound in WrittenNames(statement)) writes.Add(bound);
            foreach (var group in classes)
                if (group.Count() > 1 || writes.Contains(group.Key)) module.ReboundClasses.Add(group.Key);
            var globalBindings = new HashSet<string>();
            AstWalker.VisitAll(module.Ast.Body, node =>
            {
                if (node is GlobalStmt { Nonlocal: false } global) globalBindings.UnionWith(global.Names);
            });
            writes.UnionWith(globalBindings);
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
            var activeTypes = new Dictionary<string, ClassDef>();
            foreach (var block in blocks)
            {
                for (int i = 0; i < block.Count; i++)
                {
                    if (block == module.Ast.Body)
                    {
                        if (block[i] is ClassDef declaration)
                        {
                            for (int basis = 0; basis < declaration.Bases.Count; basis++)
                                declaration.Bases[basis] = ActiveBase(declaration.Bases[basis]);
                            declaration.Metaclass = ActiveBase(declaration.Metaclass);
                        }
                        foreach (var statement in ModuleBlocks(new List<Stmt> { block[i] }).SelectMany(scope => scope))
                        {
                            foreach (string bound in WrittenNames(statement)) activeTypes.Remove(bound);
                            if (statement is ImportStmt import)
                                foreach (var alias in import.Names) activeTypes.Remove(alias.AsName ?? alias.Name.Split('.')[0]);
                            if (statement is ImportFromStmt from)
                                foreach (var alias in from.Names) activeTypes.Remove(alias.AsName ?? alias.Name);
                            if (statement is FunctionDef method) activeTypes.Remove(method.Name);
                            if (statement is ClassDef type) activeTypes.Remove(type.Name);
                        }
                    }
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
                        if (type.Decorators.Count == 0) activeTypes[name] = type;
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

            foreach (var active in activeTypes)
            {
                if (globalBindings.Contains(active.Key)) continue;
                int definition = module.Ast.Body.IndexOf(active.Value);
                var following = module.Ast.Body.Skip(definition + 1).ToList();
                bool mayRebind = AstWalker.ContainsOutsideNestedScopes(following, node => node switch
                {
                    NamedExpr assignment => assignment.Target.Id == active.Key,
                    ExceptHandler handler => handler.Name == active.Key,
                    ImportFromStmt import => import.Names.Any(alias => alias.Name == "*"),
                    MatchStmt match => match.Cases.Any(branch => LocalsAnalyzer.PatternCaptures(branch.Pattern, match.Subject)
                        .Any(capture => capture.Key == active.Key)),
                    _ => false,
                });
                if (!mayRebind) module.FinalReboundClasses.Add(active.Key, active.Value);
            }

            Expr ActiveBase(Expr expression)
            {
                if (expression is NameExpr name && activeTypes.TryGetValue(name.Id, out var implementation))
                    return new NameExpr { Id = implementation.Name, Line = name.Line, Col = name.Col };
                if (expression is SubscriptExpr generic) generic.Target = ActiveBase(generic.Target);
                return expression;
            }
        }

        static IEnumerable<string> WrittenNames(Stmt statement)
        {
            var targets = statement switch
            {
                AssignStmt assign => assign.Targets,
                AugAssignStmt assign => new List<Expr> { assign.Target },
                AnnAssignStmt assign when assign.Value != null => new List<Expr> { assign.Target },
                DelStmt delete => delete.Targets,
                ForStmt loop => new List<Expr> { loop.Target },
                WithStmt with => with.Items.Select(item => item.Var).ToList(),
                _ => new List<Expr>(),
            };
            return targets.SelectMany(Names);

            static IEnumerable<string> Names(Expr target) => target switch
            {
                NameExpr name => new[] { name.Id },
                TupleExpr tuple => tuple.Elts.SelectMany(Names),
                ListExpr list => list.Elts.SelectMany(Names),
                StarredExpr star => Names(star.Value),
                _ => Enumerable.Empty<string>(),
            };
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
