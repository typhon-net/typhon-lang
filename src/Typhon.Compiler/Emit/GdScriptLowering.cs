// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    /// <summary>
    /// The part of the GDScript front end that needs the referenced assemblies (it runs once, before inference):
    /// <list type="bullet">
    /// <item>With Godot's C# API referenced, a .gd module imports the Godot namespace, and print / the random functions /
    /// load go to Godot's own (GD.Print, GD.Randf, GD.Load), so a compiled script behaves like the GDScript one.</item>
    /// <item>Without it (a .gd program run by <c>typhon run</c>), a script's native base (extends SceneTree, Node, ...) is
    /// dropped: the class is a plain class, and quit() does nothing.</item>
    /// <item>A method that overrides a virtual of the native base by its GDScript name (_process, _ready, _input) gets the
    /// C# name (_Process, _Ready, _Input) and the C# signature's parameter and return types, so it overrides it.</item>
    /// </list>
    /// </summary>
    internal static class GdScriptLowering
    {
        public static void Run(ProjectContext p)
        {
            foreach (var m in p.Modules.Values)
                if (m.IsGdScript && m.GdScript != null && m.ParseDiagnostics.Count == 0) Lower(m, p);
        }

        static string Rt => GdScriptParser.Rt;

        static void Lower(ModuleInfo m, ProjectContext p)
        {
            var r = p.Resolver;
            bool godot = r.TypeExists("Godot.GodotObject");
            var body = m.Ast.Body;
            if (godot)
            {
                int at = body.FindIndex(s => s is ImportStmt i && i.Names.Any(n => n.AsName == Rt)) + 1;
                body.Insert(at, new ImportFromStmt { Module = "Godot", Names = { new ImportAlias { Name = "*" } }, Line = 1, Col = 1 });
            }
            var gd = m.GdScript;
            var typhonClasses = new HashSet<string>(gd.AllClasses.Select(c => c.Name));
            foreach (var cls in gd.AllClasses)
            {
                string meta = NativeBase(cls, gd, r, godot, typhonClasses);
                if (cls.Bases.Count == 1 && !(cls.Bases[0] is NameExpr bn && typhonClasses.Contains(bn.Id)) && meta == null)
                {
                    // extends SceneTree / Node / ... without Godot: a plain class (its inherited calls that matter for a script run are stubs)
                    cls.Bases.Clear();
                    AstWalker.VisitAll(cls, n =>
                    {
                        if (n is CallExpr c && c.Func is AttributeExpr a && a.Target is NameExpr s && s.Id == "self" && a.Name == "quit")
                            c.Func = new AttributeExpr { Target = new NameExpr { Id = Rt, Line = a.Line, Col = a.Col }, Name = "quit", Line = a.Line, Col = a.Col };
                    });
                }
                if (meta != null) RenameOverrides(cls, meta, r);
            }
            if (godot) RouteToGodot(m.Ast);
        }

        /// <summary>The metadata name of the .NET class a GDScript class derives from (through classes of its file), or null.</summary>
        static string NativeBase(ClassDef cls, GdScriptModule gd, ITypeResolver r, bool godot, HashSet<string> typhonClasses)
        {
            for (int depth = 0; cls != null && depth < 20; depth++)
            {
                if (cls.Bases.Count == 0) return null;
                var b = cls.Bases[0];
                if (b is NameExpr n && typhonClasses.Contains(n.Id)) { cls = gd.AllClasses.FirstOrDefault(c => c.Name == n.Id); continue; }
                string dotted = Dotted(b);
                if (dotted == null) return null;
                if (godot && r.TypeExists("Godot." + dotted)) return "Godot." + dotted;
                if (r.TypeExists(dotted)) return dotted;
                return null;
            }
            return null;
        }

        static string Dotted(Expr e) => e is NameExpr n ? n.Id : e is AttributeExpr a && Dotted(a.Target) is string t ? t + "." + a.Name : null;

        /// <summary>snake_case → PascalCase as Godot's C# bindings spell names: get_viewport_rect → GetViewportRect, _process → _Process, ZERO → Zero, world_2d → World2D.</summary>
        public static string Pascal(string name)
        {
            int lead = 0;
            while (lead < name.Length && name[lead] == '_') lead++;
            var sb = new StringBuilder(name.Substring(0, lead));
            foreach (var part in name.Substring(lead).Split(new[] { '_' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                int digits = 0;
                while (digits < part.Length && char.IsDigit(part[digits])) digits++;
                if (digits > 0 && digits < part.Length && part.Length - digits <= 2) { sb.Append(part.Substring(0, digits)).Append(part.Substring(digits).ToUpperInvariant()); continue; }
                sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }

        static void RenameOverrides(ClassDef cls, string meta, ITypeResolver r)
        {
            var renames = new Dictionary<string, string>();
            foreach (var f in cls.Body.OfType<FunctionDef>())
            {
                if (f.Name.StartsWith("__") || r.HasMember(meta, f.Name)) continue;
                string pascal = Pascal(f.Name);
                if (pascal == f.Name || !r.IsOverridable(meta, pascal)) continue;
                int arity = f.Params.Count - 1;
                var sig = r.GetMembers(meta, pascal).FirstOrDefault(s => s.IsMethod && !s.IsStatic && s.Parameters.Count == arity);
                if (sig == null) continue;
                renames[f.Name] = pascal;
                f.Name = pascal;
                // the override takes the C# signature's types (GDScript's int is a long, Godot's what: int an int)
                for (int i = 0; i < arity; i++)
                {
                    var ann = AnnotationFor(sig.Parameters[i].GenericType, f.Params[i + 1]);
                    if (ann != null) f.Params[i + 1].Annotation = ann;
                }
                f.Returns = sig.Type == null && !sig.ReturnsDynamic ? new ConstExpr { Kind = ConstKind.None, Raw = "None", Line = f.Line, Col = f.Col } : AnnotationFor(sig.Type, f) ?? f.Returns;
            }
            if (renames.Count == 0) return;
            AstWalker.VisitAll(cls, n =>
            {
                if (n is AttributeExpr a && renames.TryGetValue(a.Name, out var nn) &&
                    (a.Target is NameExpr s && s.Id == "self" || a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super"))
                    a.Name = nn;
            });
        }

        /// <summary>The Typhon annotation for a C# type as the resolver spells it (double → float, int → int32, global::Godot.InputEvent → InputEvent).</summary>
        static Expr AnnotationFor(string cs, Node at)
        {
            if (cs == null) return null;
            Expr N(string id) => new NameExpr { Id = id, Line = at.Line, Col = at.Col };
            switch (cs)
            {
                case "double": return N("float");
                case "float": return N("float32");
                case "long": return N("int");
                case "int": return N("int32");
                case "bool": return N("bool");
                case "string": return N("str");
                case "object": return N("object");
            }
            if (cs.EndsWith("[]") && AnnotationFor(cs.Substring(0, cs.Length - 2), at) is Expr elem)
                return new SubscriptExpr { Target = N("Array"), Index = elem, Line = at.Line, Col = at.Col };
            if (cs.Contains("<")) return null;
            if (cs.StartsWith("global::")) cs = cs.Substring("global::".Length);
            if (cs.StartsWith("Godot.") && cs.IndexOf('.', 6) < 0) cs = cs.Substring(6);
            Expr e = null;
            foreach (var part in cs.Split('.'))
                e = e == null ? N(part) : new AttributeExpr { Target = e, Name = part, Line = at.Line, Col = at.Col };
            return e;
        }

        static readonly Dictionary<string, string> GodotPrint = new Dictionary<string, string>
        {
            ["print"] = "Print|str", ["prints"] = "Print|str_sp", ["printt"] = "Print|str_tab", ["printraw"] = "PrintRaw|str",
            ["print_rich"] = "PrintRich|str", ["printerr"] = "PrintErr|str", ["push_error"] = "PushError|str", ["push_warning"] = "PushWarning|str",
        };

        /// <summary>Inside a Godot project: printing, random numbers and loading go through Godot (GD.*), like GDScript's.</summary>
        static void RouteToGodot(ModuleAst ast)
        {
            AstWalker.VisitAll(ast, n =>
            {
                if (!(n is CallExpr c) || !(c.Func is AttributeExpr a) || !(a.Target is NameExpr t) || t.Id != Rt) return;
                Expr Gd(string member) => new AttributeExpr { Target = new NameExpr { Id = "GD", Line = a.Line, Col = a.Col }, Name = member, Line = a.Line, Col = a.Col };
                Expr Conv(string fn, Expr v) => new CallExpr { Func = new NameExpr { Id = fn, Line = v.Line, Col = v.Col }, Args = { new Arg { Value = v } }, Line = v.Line, Col = v.Col };
                if (GodotPrint.TryGetValue(a.Name, out var spec))
                {
                    var parts = spec.Split('|');
                    var joined = new CallExpr { Func = new AttributeExpr { Target = new NameExpr { Id = Rt, Line = a.Line, Col = a.Col }, Name = parts[1], Line = a.Line, Col = a.Col }, Args = c.Args, Line = c.Line, Col = c.Col };
                    c.Func = Gd(parts[0]);
                    c.Args = new List<Arg> { new Arg { Value = joined } };
                    return;
                }
                switch (a.Name)
                {
                    case "randf": c.Func = Gd("Randf"); break;
                    case "randi": c.Func = Gd("Randi"); break;
                    case "randomize": c.Func = Gd("Randomize"); break;
                    case "seed": c.Func = Gd("Seed"); break;
                    case "randfn": c.Func = Gd("Randfn"); break;
                    case "randi_range": c.Func = Gd("RandRange"); break;
                    case "randf_range" when c.Args.Count == 2:
                        c.Func = Gd("RandRange");
                        foreach (var arg in c.Args) arg.Value = Conv("float", arg.Value);
                        break;
                    case "load": c.Func = Gd("Load"); break;
                    case "is_instance_valid": c.Func = new AttributeExpr { Target = new NameExpr { Id = "GodotObject", Line = a.Line, Col = a.Col }, Name = "IsInstanceValid", Line = a.Line, Col = a.Col }; break;
                }
            });
        }
    }
}
