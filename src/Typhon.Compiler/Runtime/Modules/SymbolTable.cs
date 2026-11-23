// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Typhon.Runtime;
    using Node = ast.AST;

    internal static class _symtable
    {
        public const long DEF_GLOBAL = 1, DEF_LOCAL = 2, DEF_PARAM = 4, DEF_NONLOCAL = 8, USE = 16;
        public const long DEF_FREE_CLASS = 64, DEF_IMPORT = 128, DEF_ANNOT = 256, DEF_COMP_ITER = 512;
        public const long DEF_TYPE_PARAM = 1024, DEF_COMP_CELL = 2048, DEF_BOUND = DEF_LOCAL | DEF_PARAM | DEF_IMPORT;
        public const long SCOPE_OFF = 12, SCOPE_MASK = 15;
        public const long LOCAL = 1, GLOBAL_EXPLICIT = 2, GLOBAL_IMPLICIT = 3, FREE = 4, CELL = 5;
        public const long TYPE_FUNCTION = 0, TYPE_CLASS = 1, TYPE_MODULE = 2, TYPE_ANNOTATION = 3;
        public const long TYPE_TYPE_ALIAS = 4, TYPE_TYPE_PARAMETERS = 5, TYPE_TYPE_VARIABLE = 6;

        internal sealed class Entry(string name, long kind, long line, bool isNested)
        {
            static long nextId;
            public long id { get; } = Interlocked.Increment(ref nextId);
            public string name { get; } = name;
            public long type { get; } = kind;
            public long lineno { get; } = line;
            public long nested { get; } = isNested ? 1 : 0;
            public OrderedDictionary<string, long> symbols { get; } = new();
            public List<Entry> children { get; } = new();
        }

        public static Entry symtable(object code, object filename, string compile_type, string? module = null)
        {
            if (compile_type is not ("exec" or "eval" or "single"))
                throw new ArgumentException("symtable() arg 3 must be 'exec' or 'eval' or 'single'");
            var tree = (Node)Builtins.compile(code, filename, compile_type, ast.PyCF_ONLY_AST, module: module);
            bool futureAnnotations = Items<ast.ImportFrom>(tree, "body").Any(import =>
                Equals(Field(import, "module"), "__future__") &&
                Items<Node>(import, "names").Any(alias => Equals(Field(alias, "name"), "annotations")));
            var root = new Block(new Entry("top", TYPE_MODULE, 0, false), null, null, futureAnnotations);
            root.Visit(tree);
            root.Resolve(new HashSet<string>(), new HashSet<string>());
            return root.Table;
        }

        static object? Field(Node node, string name) => node.Fields.GetValueOrDefault(name);

        static IEnumerable<T> Items<T>(Node node, string name) =>
            Field(node, name) is IEnumerable values ? values.OfType<T>() : Enumerable.Empty<T>();

        static long Line(Node node) => node.Attributes.TryGetValue("lineno", out var line) ? Convert.ToInt64(line) : 0;

        sealed class Block(Entry table, string? privateName, Block? parent, bool futureAnnotations)
        {
            internal Entry Table => table;
            Block? Parent => parent;
            readonly List<Block> children = new();
            Block? annotations;

            string PrivateName(string name)
            {
                if (privateName == null || !name.StartsWith("__", StringComparison.Ordinal) ||
                    name.EndsWith("__", StringComparison.Ordinal) || name.Contains('.')) return name;
                string owner = privateName.TrimStart('_');
                return owner.Length == 0 ? name : "_" + owner + name;
            }

            void Add(string name, long flags)
            {
                name = PrivateName(name);
                table.symbols[name] = table.symbols.TryGetValue(name, out long before) ? before | flags : flags;
            }

            void VisitField(Node node, string field) => Visit(Field(node, field));

            Block Child(string name, long kind, Node node, string? className = null)
            {
                bool nested = table.nested != 0 || table.type is TYPE_FUNCTION or TYPE_TYPE_PARAMETERS or TYPE_TYPE_ALIAS;
                var result = new Block(new Entry(name, kind, Line(node), nested), className ?? privateName, this, futureAnnotations);
                children.Add(result);
                table.children.Add(result.Table);
                return result;
            }

            Block Annotation(Node node)
            {
                var result = Child("__annotate__", TYPE_ANNOTATION, node);
                result.Add(".format", DEF_PARAM | USE);
                if (table.type == TYPE_CLASS) result.Add("__classdict__", USE);
                return result;
            }

            Block TypeParameters(Node node, string name, bool method, bool type)
            {
                var parameters = Items<Node>(node, "type_params").ToArray();
                if (parameters.Length == 0) return this;
                var scope = Child(name, TYPE_TYPE_PARAMETERS, node);
                if (method) scope.Add(".defaults", DEF_PARAM);
                if (type)
                {
                    scope.Add(".type_params", DEF_LOCAL | USE);
                    scope.Add(".generic_base", DEF_LOCAL | USE);
                }
                foreach (var parameter in parameters)
                {
                    string identifier = (string)Field(parameter, "name")!;
                    scope.Add(identifier, DEF_LOCAL | DEF_TYPE_PARAM);
                    foreach (string expression in new[] { "bound", "default_value" })
                    {
                        if (Field(parameter, expression) is not Node value) continue;
                        var evaluator = scope.Child(identifier, TYPE_TYPE_VARIABLE, parameter);
                        evaluator.Add(".format", DEF_PARAM | USE);
                        evaluator.Visit(value);
                    }
                }
                return scope;
            }

            void VisitFunction(Node node, bool lambda)
            {
                string name = lambda ? "<lambda>" : (string)Field(node, "name")!;
                if (!lambda) Add(name, DEF_LOCAL);
                var parameters = (Node)Field(node, "args")!;
                VisitField(parameters, "defaults");
                VisitField(parameters, "kw_defaults");
                VisitField(node, "decorator_list");
                var arguments = Items<Node>(parameters, "posonlyargs").Concat(Items<Node>(parameters, "args"))
                    .Concat(Items<Node>(parameters, "kwonlyargs")).ToList();
                foreach (string field in new[] { "vararg", "kwarg" })
                    if (Field(parameters, field) is Node argument) arguments.Add(argument);
                Block definition = lambda ? this : TypeParameters(node, name, method: true, type: false);
                if (!lambda && !futureAnnotations)
                {
                    var annotate = definition.Annotation(node);
                    foreach (var argument in arguments) annotate.VisitField(argument, "annotation");
                    annotate.VisitField(node, "returns");
                }
                var body = definition.Child(name, TYPE_FUNCTION, node);
                foreach (var argument in arguments) body.Add((string)Field(argument, "arg")!, DEF_PARAM);
                body.VisitField(node, "body");
            }

            void VisitComprehension(Node node, bool isGenerator)
            {
                var generators = Items<Node>(node, "generators").ToList();
                if (generators.Count == 0) return;
                VisitField(generators[0], "iter");
                Block body = isGenerator ? Child("<genexpr>", TYPE_FUNCTION, node) : this;
                if (isGenerator) body.Add(".0", DEF_PARAM);
                foreach (var generator in generators)
                {
                    if (generator != generators[0]) body.VisitField(generator, "iter");
                    body.VisitTarget(Field(generator, "target"));
                    body.VisitField(generator, "ifs");
                }
                body.VisitField(node, "elt");
                body.VisitField(node, "key");
                body.VisitField(node, "value");
            }

            void VisitTarget(object? target)
            {
                if (target is ast.Name name) Add((string)Field(name, "id")!, DEF_LOCAL | DEF_COMP_ITER);
                else if (target is ast.Tuple or ast.List)
                    foreach (var element in Items<Node>((Node)target, "elts")) VisitTarget(element);
                else if (target is ast.Starred starred) VisitTarget(Field(starred, "value"));
                else Visit(target);
            }

            internal void Visit(object? value)
            {
                if (value is not Node node)
                {
                    if (value is IEnumerable list and not string)
                        foreach (var item in list) Visit(item);
                    return;
                }
                switch (node)
                {
                    case ast.Name:
                        string name = (string)Field(node, "id")!;
                        bool load = Field(node, "ctx") is ast.Load;
                        Add(name, load ? USE : DEF_LOCAL);
                        if (load && name == "super" && table.type == TYPE_FUNCTION) Add("__class__", USE);
                        return;
                    case ast.FunctionDef or ast.AsyncFunctionDef:
                        VisitFunction(node, false);
                        return;
                    case ast.Lambda:
                        VisitFunction(node, true);
                        return;
                    case ast.ClassDef:
                        string cls = (string)Field(node, "name")!;
                        Add(cls, DEF_LOCAL);
                        VisitField(node, "decorator_list");
                        Block declaration = TypeParameters(node, cls, method: false, type: true);
                        declaration.VisitField(node, "bases");
                        declaration.VisitField(node, "keywords");
                        Block classBody = declaration.Child(cls, TYPE_CLASS, node, cls);
                        if (!ReferenceEquals(declaration, this))
                        {
                            classBody.Add("__type_params__", DEF_LOCAL);
                            classBody.Add(".type_params", USE);
                        }
                        classBody.VisitField(node, "body");
                        return;
                    case ast.Global or ast.Nonlocal:
                        foreach (string declared in Items<string>(node, "names"))
                        {
                            Add(declared, node is ast.Global ? DEF_GLOBAL : DEF_NONLOCAL);
                            if (node is ast.Global && parent != null)
                            {
                                Block module = parent;
                                while (module.Parent != null) module = module.Parent;
                                module.Add(PrivateName(declared), DEF_GLOBAL);
                            }
                        }
                        return;
                    case ast.Import or ast.ImportFrom:
                        foreach (var alias in Items<Node>(node, "names"))
                        {
                            string imported = (string)Field(alias, "name")!;
                            if (imported != "*") Add((string?)Field(alias, "asname") ?? imported.Split('.')[0], DEF_IMPORT);
                        }
                        return;
                    case ast.AnnAssign when Field(node, "target") is ast.Name target:
                        Add((string)Field(target, "id")!, DEF_LOCAL | DEF_ANNOT);
                        if (!futureAnnotations)
                        {
                            annotations ??= Annotation(node);
                            if (table.type != TYPE_FUNCTION) annotations.VisitField(node, "annotation");
                        }
                        if (table.type == TYPE_MODULE) Add("__conditional_annotations__", USE);
                        VisitField(node, "value");
                        return;
                    case ast.NamedExpr:
                        VisitField(node, "value");
                        VisitField(node, "target");
                        return;
                    case ast.For or ast.AsyncFor:
                        VisitField(node, "target");
                        VisitField(node, "iter");
                        VisitField(node, "body");
                        VisitField(node, "orelse");
                        return;
                    case ast.ListComp or ast.SetComp or ast.DictComp or ast.GeneratorExp:
                        VisitComprehension(node, node is ast.GeneratorExp);
                        return;
                    case ast.ExceptHandler or ast.MatchAs or ast.MatchStar:
                        if (Field(node, "name") is string bound) Add(bound, DEF_LOCAL);
                        break;
                    case ast.MatchMapping:
                        if (Field(node, "rest") is string rest) Add(rest, DEF_LOCAL);
                        break;
                }
                foreach (var field in node.Fields.Values) Visit(field);
            }

            internal HashSet<string> Resolve(HashSet<string> enclosing, HashSet<string> global)
            {
                var inherited = new HashSet<string>(enclosing);
                var globals = new HashSet<string>(global);
                var local = new HashSet<string>();
                var free = new HashSet<string>();
                foreach (var (name, flags) in table.symbols.ToArray())
                {
                    long scope;
                    if ((flags & DEF_GLOBAL) != 0)
                    {
                        scope = GLOBAL_EXPLICIT;
                        globals.Add(name);
                        inherited.Remove(name);
                    }
                    else if ((flags & DEF_NONLOCAL) != 0)
                    {
                        if (!enclosing.Contains(name)) throw new SyntaxError($"no binding for nonlocal '{name}' found");
                        scope = FREE;
                    }
                    else if ((flags & DEF_BOUND) != 0)
                    {
                        scope = LOCAL;
                        local.Add(name);
                        if (table.type != TYPE_CLASS) globals.Remove(name);
                    }
                    else scope = !global.Contains(name) && enclosing.Contains(name) ? FREE : GLOBAL_IMPLICIT;
                    if (scope == FREE) free.Add(name);
                    table.symbols[name] = flags | (scope << (int)SCOPE_OFF);
                }
                if (table.type == TYPE_CLASS) inherited.UnionWith(new[] { "__class__", "__classdict__" });
                else if (table.type != TYPE_MODULE) inherited.UnionWith(local);
                foreach (var child in children)
                    foreach (string value in child.Resolve(inherited, globals))
                    {
                        if (table.type == TYPE_CLASS && value is "__class__" or "__classdict__") continue;
                        if (table.type != TYPE_CLASS && local.Contains(value))
                        {
                            table.symbols[value] = (table.symbols[value] & ~(SCOPE_MASK << (int)SCOPE_OFF)) | (CELL << (int)SCOPE_OFF);
                            continue;
                        }
                        if (table.type == TYPE_CLASS && table.symbols.ContainsKey(value)) table.symbols[value] |= DEF_FREE_CLASS;
                        else if (!table.symbols.ContainsKey(value)) table.symbols[value] = FREE << (int)SCOPE_OFF;
                        free.Add(value);
                    }
                return free;
            }
        }
    }
}
