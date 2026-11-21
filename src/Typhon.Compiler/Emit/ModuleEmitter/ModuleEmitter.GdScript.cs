// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    // GDScript modules (.gd): GDScript's semantics where they differ from Python's, and Godot's C# naming.
    //
    // GDScript calls Godot's API in snake_case (position, add_child, Vector2.ZERO), Godot's C# API is PascalCase (Position,
    // AddChild, Vector2.Zero). A .gd module sees .NET types through GdScriptResolver, which answers for a snake_case name
    // with the members of its PascalCase twin, so inference, overloads and conversions work on the GDScript names; the C#
    // name is chosen where the member is written (EmitAttribute → GdMemberAccess). Python modules never see the mapping.
    //
    // Operators follow GDScript: int / int truncates (7 / 2 == 3), % is the remainder of truncated division (-7 % 3 == -1),
    // String % args formats like String::sprintf, ints wrap around (unchecked), and Godot's float structs (Vector2, Color)
    // combine with GDScript floats (C# doubles) through a float conversion C# needs (Vector2 * float32(delta)).
    internal sealed partial class ModuleEmitter
    {
        /// <summary>Set while a GDScript module is emitted: GDScript has no access control, every member is public.</summary>
        [ThreadStatic] static bool t_allPublic;

        /// <summary>The script class of a GDScript module (emitted as a top-level class), or null.</summary>
        ClassDef GdScriptClass => _m.IsGdScript ? _m.GdScript?.ScriptClass : null;

        /// <summary>The checked context of function bodies: GDScript's ints wrap around.</summary>
        string CheckedKeyword => _m.IsGdScript ? "unchecked" : "checked";

        /// <summary>The resolver a module sees: a .gd module finds Godot's PascalCase members by their snake_case names.</summary>
        static ITypeResolver ResolverFor(ModuleInfo m, ProjectContext p) => m.IsGdScript ? new GdScriptResolver(p.Resolver) : p.Resolver;

        /// <summary>A .NET type's members by their GDScript names (snake_case, ALL_CAPS constants, Color.RED in Colors).</summary>
        internal sealed class GdScriptResolver : ITypeResolver
        {
            readonly ITypeResolver _r;
            public GdScriptResolver(ITypeResolver r) { _r = r; }

            /// <summary>The C# member a GDScript name denotes on the type: the name itself, else its PascalCase spelling (or null: neither).</summary>
            public string CsName(string meta, string member)
            {
                if (member == ".ctor" || _r.HasMember(meta, member)) return member;
                // only Godot's API is spelled differently in GDScript (System.String's Split is not GDScript's split)
                if (!meta.StartsWith("Godot.")) return null;
                string pascal = GdScriptLowering.Pascal(member);
                return pascal != member && _r.HasMember(meta, pascal) ? pascal : null;
            }

            string Map(string meta, string member) => CsName(meta, member) ?? member;

            public bool TypeExists(string metadataName) => _r.TypeExists(metadataName);
            public bool NamespaceExists(string dottedNamespace) => _r.NamespaceExists(dottedNamespace);
            public bool HasMember(string metadataName, string memberName) => CsName(metadataName, memberName) != null || ColorConstant(metadataName, memberName) != null;
            public bool IsOverridable(string metadataName, string memberName) => _r.IsOverridable(metadataName, Map(metadataName, memberName));
            public string OverrideAccessibility(string metadataName, string memberName) => _r.OverrideAccessibility(metadataName, Map(metadataName, memberName));
            public IReadOnlyList<CtorSignature> GetConstructors(string metadataName) => _r.GetConstructors(metadataName);
            public IReadOnlyList<MemberSig> GetExtensionMethods(string receiverMetadataName, string name, IReadOnlyList<string> namespaces) =>
                _r.GetExtensionMethods(receiverMetadataName, name, namespaces);

            public IReadOnlyList<MemberSig> GetMembers(string metadataName, string memberName)
            {
                var ms = _r.GetMembers(metadataName, Map(metadataName, memberName));
                if (ms.Count == 0 && ColorConstant(metadataName, memberName) is string named) return _r.GetMembers("Godot.Colors", named);
                return ms;
            }

            /// <summary>Color.RED: Godot's C# API has the named colors in a class of their own (Colors.Red).</summary>
            public string ColorConstant(string meta, string member)
            {
                if (meta != "Godot.Color" || _r.HasMember(meta, member)) return null;
                string pascal = GdScriptLowering.Pascal(member);
                return _r.HasMember("Godot.Colors", pascal) ? pascal : null;
            }
        }

        GdScriptResolver GdResolver => _r as GdScriptResolver;

        /// <summary>The .NET class this class derives from through Typhon classes (Godot.Node2D for a script that extends Node2D), or null.</summary>
        string GdNativeBase(ClassDef c, int depth = 0)
        {
            if (c == null || depth > 20) return null;
            foreach (var b in c.Bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py != null) { if (GdNativeBase(py, depth + 1) is string m) return m; }
                else if (meta != null) return meta;
            }
            return null;
        }

        /// <summary>Is the member declared by the current Typhon class or a Typhon base (then it is not the native base's)?</summary>
        bool GdTyphonMember(string name)
        {
            if (_cls == null) return false;
            if (_cls.Members.Contains(name) || _cls.FieldTypes.ContainsKey(name)) return true;
            return BaseHasMemberTyphonOnly(_cls.Def, name, 0);
        }

        bool BaseHasMemberTyphonOnly(ClassDef c, string name, int depth)
        {
            if (depth > 20) return false;
            if (c.Body.Any(s => s is FunctionDef f && f.Name == name || s is AnnAssignStmt a && a.Target is NameExpr n && n.Id == name || s is AssignStmt asg && asg.Targets.Any(t => t is NameExpr tn && tn.Id == name)))
                return true;
            if (CollectImplicitFields(c).ContainsKey(name)) return true;
            foreach (var b in c.Bases)
                if (ResolveBase(b, out var py, out _) && py != null && BaseHasMemberTyphonOnly(py, name, depth + 1)) return true;
            return false;
        }

        /// <summary>Members of the native base for self.name in a GDScript class (null when the class declares it).</summary>
        IEnumerable<MemberSig> GdSelfMembers(string name)
        {
            if (!_m.IsGdScript || _cls == null || GdTyphonMember(name)) return Enumerable.Empty<MemberSig>();
            string meta = GdNativeBase(_cls.Def);
            return meta == null ? Enumerable.Empty<MemberSig>() : _r.GetMembers(meta, name).Where(m => !m.IsStatic);
        }

        /// <summary>The type of self.name where name is a property or field of the native base (self.position: Vector2).</summary>
        string GdSelfMemberType(string name)
        {
            var ms = GdSelfMembers(name).Where(m => !m.IsMethod).ToList();
            return ms.Count > 0 ? ms[0].Type : null;
        }

        /// <summary>The .NET type whose member a GDScript attribute names: a type (Vector2.ZERO), a .NET value, or self's native base.</summary>
        string GdReceiverMeta(Expr target, string member)
        {
            if (target is NameExpr sn && sn.Id == "self")
                return _cls != null && !GdTyphonMember(member) ? GdNativeBase(_cls.Def) : null;
            if (target is CallExpr sc && sc.Func is NameExpr sup && sup.Id == "super")
                return _cls != null ? GdNativeBase(_cls.Def) : null;
            if (DotNetStaticMeta(target) is string sm) return sm;
            string t = InferType(target);
            if (t == null || ClassKeyOfType(t, out _) != null) return null;
            return DotNetMeta(t);
        }

        /// <summary>target.name in a GDScript module: the C# member (position → Position, Color.RED → Colors.Red), or null to keep it.</summary>
        string GdMemberAccess(AttributeExpr a, string target)
        {
            var gr = GdResolver;
            if (gr == null) return null;
            string meta = GdReceiverMeta(a.Target, a.Name);
            if (meta == null) return null;
            if (gr.ColorConstant(meta, a.Name) is string color && DotNetStaticMeta(a.Target) != null) return "global::Godot.Colors." + color;
            string cs = gr.CsName(meta, a.Name);
            return cs != null && cs != a.Name ? target + "." + Esc(cs) : null;
        }

        // ───────────────────────────── operators ─────────────────────────────

        static readonly HashSet<string> GodotFloatStructs = new HashSet<string>
        {
            "global::Godot.Vector2", "global::Godot.Vector3", "global::Godot.Vector4", "global::Godot.Color", "global::Godot.Quaternion",
            "global::Godot.Basis", "global::Godot.Transform2D", "global::Godot.Transform3D", "global::Godot.Rect2", "global::Godot.Plane",
            "global::Godot.Aabb", "global::Godot.Projection",
        };

        static readonly HashSet<string> GodotIntStructs = new HashSet<string> { "global::Godot.Vector2I", "global::Godot.Vector3I", "global::Godot.Vector4I", "global::Godot.Rect2I" };

        static bool IsGodotStruct(string t) => t != null && (GodotFloatStructs.Contains(t) || GodotIntStructs.Contains(t));

        /// <summary>A scalar operand next to a Godot struct, converted to what the struct's operators take (float32, or int32 for Vector2I).</summary>
        string GdStructScalar(string structType, string scalarType, string code)
        {
            if (GodotFloatStructs.Contains(structType) && (scalarType == "double" || scalarType == "dynamic")) return $"((float){Paren(code)})";
            if (GodotIntStructs.Contains(structType) && (scalarType == "long" || scalarType == "double" || scalarType == "dynamic")) return $"checked((int){Paren(code)})";
            return code;
        }

        static readonly HashSet<string> GdArithmetic = new HashSet<string> { "+", "-", "*", "/", "%" };

        /// <summary>The type of a GDScript binary operation where it differs from Python's, or null.</summary>
        string GdBinOpType(BinOpExpr b, string l, string r)
        {
            if (b.Op == "%" && (l == "string" || b.Left is ConstExpr sc && sc.Kind == ConstKind.Str)) return "string";
            if ((b.Op == "/" || b.Op == "%") && IsIntegral(l) && IsIntegral(r)) return l == r ? l : "long";
            if (b.Op == "%" && IsNumeric(l) && IsNumeric(r)) return "double";
            if (GdArithmetic.Contains(b.Op) && (IsGodotStruct(l) || IsGodotStruct(r)) && l != r)
            {
                bool structLeft = IsGodotStruct(l);
                string s = structLeft ? l : r, scalar = structLeft ? r : l;
                if (IsNumeric(scalar) || scalar == "dynamic")
                {
                    string asScalar = GodotIntStructs.Contains(s) ? "int" : "float";
                    return DotNetOperatorType(b.Op, structLeft ? s : asScalar, structLeft ? asScalar : s) ?? s;
                }
            }
            return null;
        }

        /// <summary>A GDScript binary operation where its C# code differs from Python's, or null.</summary>
        string GdEmitBinOp(BinOpExpr b)
        {
            string lt = InferType(b.Left), rt = InferType(b.Right);
            if (b.Op == "%" && (lt == "string" || b.Left is ConstExpr sc && sc.Kind == ConstKind.Str))
            {
                // "%d of %d" % [a, b]: the array's items are the arguments (boxed as they are: 1 stays an int)
                string args = b.Right is ListExpr le && !le.Elts.Any(x => x is StarredExpr)
                    ? $"new object?[] {{ {string.Join(", ", le.Elts.Select(x => EmitValue(x)))} }}" : $"(object?){Paren(EmitValue(b.Right))}";
                return $"global::Typhon.Runtime.GdScript.format({EmitExpr(b.Left)}, {args})";
            }
            if (b.Op == "/" || b.Op == "%")
            {
                // int / int truncates, % is the remainder of that division (C#'s own operators); floats divide by zero to inf
                if ((IsIntegral(lt) || lt == "bool") && (IsIntegral(rt) || rt == "bool") || IsNumeric(lt) && IsNumeric(rt))
                {
                    string l = EmitExpr(b.Left), r = EmitExpr(b.Right);
                    if (lt == "bool") l = $"({Paren(l)} ? 1L : 0L)";
                    if (rt == "bool") r = $"({Paren(r)} ? 1L : 0L)";
                    return $"({Paren(l)} {b.Op} {Paren(r)})";
                }
            }
            if (GdArithmetic.Contains(b.Op) && (IsGodotStruct(lt) || IsGodotStruct(rt)) && lt != rt)
            {
                string l = EmitExpr(b.Left), r = EmitExpr(b.Right);
                if (IsGodotStruct(lt)) r = GdStructScalar(lt, rt, r);
                else l = GdStructScalar(rt, lt, l);
                return $"({Paren(l)} {b.Op} {Paren(r)})";
            }
            return null;
        }

        // ───────────────────────────── conversions ─────────────────────────────

        /// <summary>
        /// A GDScript float (C# double) passed to a .NET parameter of type float (Godot's real_t: Vector2(x, y), rotate(a)):
        /// "float" when no applicable overload takes a double there, else null.
        /// </summary>
        string GdFloatNarrowing(IReadOnlyList<MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            string at = InferType(a.Value);
            if (at != "double" && at != "dynamic") return null;
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            bool takesFloat = false;
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p == null) continue;
                string pt = p.GenericType;
                if (pt == "float") takesFloat = true;
                else if (pt == "double" || pt == "object" || pt == "dynamic" || pt == null || p.Fit == LongFit.Accepts && pt != "float") return null;
            }
            return takesFloat ? "float" : null;
        }

        /// <summary>A GDScript float assigned to a float property or field (node.rotation = angle, v.x = 0.5): "float", else null.</summary>
        string GdFloatNarrowingForMember(AttributeExpr at, Expr value)
        {
            string vt = InferType(value);
            if (vt != "double" && vt != "dynamic") return null;
            var m = DotNetMembers(at.Target, at.Name).FirstOrDefault(x => !x.IsMethod);
            return m != null && m.Type == "float" ? "float" : null;
        }
    }
}
