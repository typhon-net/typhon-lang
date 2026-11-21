// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Emit;

namespace Typhon.Compiler.Syntax
{
    /// <summary>
    /// m = math at module level, with math an imported module and m bound nowhere else: the same as import math as m, so
    /// m.floor(x) is a call of the module's function (a module object would be late-bound). The assignment is replaced
    /// by that import.
    /// </summary>
    internal static class ModuleAliases
    {
        public static void Apply(ModuleAst m)
        {
            var modules = new Dictionary<string, string>();
            foreach (var imp in m.Body.OfType<ImportStmt>())
                foreach (var a in imp.Names)
                    if (a.AsName != null) modules[a.AsName] = a.Name;
                    else if (!a.Name.Contains(".")) modules[a.Name] = a.Name;
            if (modules.Count == 0) return;
            for (int i = 0; i < m.Body.Count; i++)
            {
                if (!(m.Body[i] is AssignStmt s) || s.Targets.Count != 1 || !(s.Targets[0] is NameExpr t) || !(s.Value is NameExpr v) ||
                    !modules.TryGetValue(v.Id, out var module) || modules.ContainsKey(t.Id) || Bindings(m.Body, t.Id) != 1 || Bindings(m.Body, v.Id) != 0)
                    continue;
                m.Body[i] = new ImportStmt { Names = { new ImportAlias { Name = module, AsName = t.Id } }, Line = s.Line, Col = s.Col };
                modules[t.Id] = module;
            }
        }

        /// <summary>How often the name is bound anywhere in the module (assigned, a loop or with target, defined, declared global, imported, deleted).</summary>
        static int Bindings(List<Stmt> body, string name)
        {
            int n = 0;
            void Target(Expr e) => AstWalker.VisitAll(e, x => { if (x is NameExpr ne && ne.Id == name) n++; });
            AstWalker.VisitAll(body, node =>
            {
                switch (node)
                {
                    case AssignStmt a: foreach (var t in a.Targets) Target(t); break;
                    case AugAssignStmt au: Target(au.Target); break;
                    case AnnAssignStmt an: if (an.Value != null) Target(an.Target); break;
                    case ForStmt f: Target(f.Target); break;
                    case FunctionDef fd: if (fd.Name == name) n++; break;
                    case ClassDef cd: if (cd.Name == name) n++; break;
                    case GlobalStmt g: if (g.Names.Contains(name)) n++; break;
                    case DelStmt d: foreach (var t in d.Targets) Target(t); break;
                    case ImportFromStmt imf: if (imf.Names.Any(x => (x.AsName ?? x.Name) == name)) n++; break;
                    case NamedExpr ne when ne.Target is NameExpr wt && wt.Id == name: n++; break;
                }
            });
            return n;
        }
    }
}
