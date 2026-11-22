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
            public bool IsOverridable(string metadataName, string memberName, int? parameterCount = null) => _r.IsOverridable(metadataName, Map(metadataName, memberName), parameterCount);
            public string OverrideAccessibility(string metadataName, string memberName, int? parameterCount = null) => _r.OverrideAccessibility(metadataName, Map(metadataName, memberName), parameterCount);
            public IReadOnlyList<CtorSignature> GetConstructors(string metadataName) => _r.GetConstructors(metadataName);
            public IReadOnlyList<MemberSig> GetExtensionMethods(string receiverMetadataName, string name, IReadOnlyList<string> namespaces) =>
                _r.GetExtensionMethods(receiverMetadataName, name, namespaces);

            public IReadOnlyList<MemberSig> GetMembers(string metadataName, string memberName)
            {
                var ms = _r.GetMembers(metadataName, Map(metadataName, memberName));
                if (ms.Count == 0 && ColorConstant(metadataName, memberName) is string named) return _r.GetMembers("Godot.Colors", named);
                // a Godot method returning an unsigned int (Time.get_ticks_usec, randi) gives GDScript an int: the call is
                // converted to long where it is emitted (GdUnsignedCall)
                if (metadataName.StartsWith("Godot.") && ms.Any(m => m.IsMethod && (m.Type == "ulong" || m.Type == "uint")))
                    return ms.Select(m => m.IsMethod && (m.Type == "ulong" || m.Type == "uint") ? Signed(m) : m).ToList();
                return ms;
            }

            static MemberSig Signed(MemberSig m) => new MemberSig
            {
                IsMethod = m.IsMethod, IsStatic = m.IsStatic, Type = "long", ValueFit = m.ValueFit, ValueNarrowTo = m.ValueNarrowTo,
                ValueBigNarrowTo = m.ValueBigNarrowTo, Parameters = m.Parameters, TypeParams = m.TypeParams, GenericType = m.GenericType,
                ReturnsDynamic = m.ReturnsDynamic,
            };

            /// <summary>Color.RED: Godot's C# API has the named colors in a class of their own (Colors.Red).</summary>
            public string ColorConstant(string meta, string member)
            {
                if (meta != "Godot.Color" || _r.HasMember(meta, member)) return null;
                string pascal = GdScriptLowering.Pascal(member);
                return _r.HasMember("Godot.Colors", pascal) ? pascal : null;
            }
        }

        GdScriptResolver GdResolver => _r as GdScriptResolver;

        /// <summary>
        /// @export var x: T: Godot's [Export] on the property (with Godot referenced, for the types Godot can export: numbers,
        /// bool, String, its math structs and objects), so the inspector shows it.
        /// </summary>
        void GdFieldAttributes(ClassDef c, string name, string type)
        {
            if (_m.GdScript == null || !_m.GdScript.Exports.TryGetValue(c, out var exports) || !exports.Contains(name) || !_r.TypeExists("Godot.ExportAttribute")) return;
            bool exportable = type is "long" or "double" or "bool" or "string" or "int" or "float" ||
                              DotNetMeta(type) is string meta && meta.StartsWith("Godot.") && !meta.StartsWith("Godot.Collections.");
            if (exportable) _w.Line("[global::Godot.Export]");
        }

        /// <summary>
        /// A class variable's modifier: "static", or "const" for a GDScript const (or enum value) with a literal number, bool or
        /// string value, which C# then folds where it is used.
        /// </summary>
        string GdStaticKeyword(ClassDef c, string name, Expr value, string type)
        {
            if (!_m.IsGdScript || _m.GdScript == null || !_m.GdScript.Constants.TryGetValue(c, out var consts) || !consts.Contains(name)) return "static";
            bool literal = value is ConstExpr k && k.Kind != ConstKind.None && k.Kind != ConstKind.Ellipsis && k.Kind != ConstKind.Bytes ||
                           value is UnaryExpr u && u.Op == "-" && u.Operand is ConstExpr n && (n.Kind == ConstKind.Int || n.Kind == ConstKind.Float);
            return literal && (type == "long" || type == "double" || type == "bool" || type == "string") ? "const" : "static";
        }

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

        /// <summary>
        /// Is the member the current Typhon class's (or a Typhon base's), not the native base's? Declared members are; a
        /// member that is only assigned (self.velocity = v, also from position.x = 1 rewritten as a copy) is the native
        /// base's when the base has it, as GDScript does not let a script redeclare an inherited member.
        /// </summary>
        bool GdTyphonMember(string name)
        {
            if (_cls == null) return false;
            if (DeclaredInTyphonClasses(_cls.Def, name, 0)) return true;
            string meta = GdNativeBase(_cls.Def);
            if (meta != null && _r.HasMember(meta, name)) return false;
            return _cls.Members.Contains(name) || _cls.FieldTypes.ContainsKey(name);
        }

        bool DeclaredInTyphonClasses(ClassDef c, string name, int depth)
        {
            if (depth > 20) return false;
            if (c.Body.Any(s => s is FunctionDef f && f.Name == name || s is AnnAssignStmt a && a.Target is NameExpr n && n.Id == name || s is AssignStmt asg && asg.Targets.Any(t => t is NameExpr tn && tn.Id == name)))
                return true;
            foreach (var b in c.Bases)
                if (ResolveBase(b, out var py, out _) && py != null && DeclaredInTyphonClasses(py, name, depth + 1)) return true;
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
            // a member the static type does not have, on a Godot object (var s := $Sprite2D is a Node; s.flip_h): GDScript
            // looks it up at run time ("unsafe access"), so does C#'s binder, by the C# spelling
            if (cs == null && meta.StartsWith("Godot.") && !GodotFloatStructs.Contains(meta) && !GodotIntStructs.Contains(meta) && !(a.Target is NameExpr st && DotNetStaticMeta(st) != null))
                return $"((dynamic){Paren(target)}).{Esc(GdScriptLowering.Pascal(a.Name))}";
            return cs != null && cs != a.Name ? target + "." + Esc(cs) : null;
        }

        /// <summary>A call of a Godot method that returns an unsigned int (Time.get_ticks_usec()): its value is a GDScript int (long).</summary>
        bool GdUnsignedCall(CallExpr c)
        {
            if (!(c.Func is AttributeExpr fa) || GdResolver == null) return false;
            string meta = GdReceiverMeta(fa.Target, fa.Name);
            if (meta == null || !meta.StartsWith("Godot.")) return false;
            string cs = GdResolver.CsName(meta, fa.Name);
            var ms = cs == null ? null : _p.Resolver.GetMembers(meta, cs).Where(m => m.IsMethod).ToList();
            return ms != null && ms.Count > 0 && ms.All(m => m.Type == "ulong" || m.Type == "uint");
        }

        static readonly HashSet<string> IntegerParams = new HashSet<string> { "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort" };

        /// <summary>
        /// The result type of a call of overloads that return different types (GD.RandRange(double, double) / (int, int)):
        /// the overloads a GDScript float argument cannot reach (an integer parameter) are left out, as C# leaves them out.
        /// </summary>
        string GdOverloadType(List<MemberSig> overloads, CallExpr call)
        {
            var positional = call.Args.Where(x => x.Name == null && !x.Star && !x.DoubleStar).ToList();
            if (overloads.Any(m => m.TypeParams == null)) overloads = overloads.Where(m => m.TypeParams == null).ToList();
            var fitting = overloads.Where(m =>
            {
                for (int i = 0; i < positional.Count; i++)
                {
                    var p = ParamAt(m, i);
                    if (p == null) return false;
                    string at = InferType(positional[i].Value);
                    if ((at == "double" || at == "float") && IntegerParams.Contains(p.GenericType ?? "")) return false;
                }
                return true;
            }).ToList();
            if (fitting.Count == 0) return null;
            string t = fitting[0].Type;
            return t != null && fitting.All(m => m.Type == t) ? t : null;
        }

        /// <summary>
        /// The type of a call in a GDScript module where Typhon's own rules do not give one: x as T (cast(T, x)) is a T, a
        /// method of the native base called on self (get_parent()) returns what the C# method returns.
        /// </summary>
        string GdCallType(CallExpr call)
        {
            if (call.Func is NameExpr cn && cn.Id == "cast" && !IsShadowed("cast") && call.Args.Count == 2) return MapType(call.Args[0].Value, call);
            if (call.Func is AttributeExpr gm && gm.Target is NameExpr gs && gs.Id == "self") return DotNetResultType(gm.Target, gm.Name, call);
            return null;
        }

        // ───────────────────────────── operators ─────────────────────────────

        static readonly HashSet<string> GodotFloatStructs = new HashSet<string>
        {
            "Godot.Vector2", "Godot.Vector3", "Godot.Vector4", "Godot.Color", "Godot.Quaternion", "Godot.Basis", "Godot.Transform2D",
            "Godot.Transform3D", "Godot.Rect2", "Godot.Plane", "Godot.Aabb", "Godot.Projection",
        };

        static readonly HashSet<string> GodotIntStructs = new HashSet<string> { "Godot.Vector2I", "Godot.Vector3I", "Godot.Vector4I", "Godot.Rect2I" };

        /// <summary>Is the C# type (as the inference spells it: Vector2 or global::Godot.Vector2) one of Godot's math structs?</summary>
        bool IsGodotStruct(string t) => t != null && t != "dynamic" && DotNetMeta(t) is string m && (GodotFloatStructs.Contains(m) || GodotIntStructs.Contains(m));
        bool IsGodotFloatStruct(string t) => t != null && t != "dynamic" && DotNetMeta(t) is string m && GodotFloatStructs.Contains(m);

        /// <summary>A scalar operand next to a Godot struct, converted to what the struct's operators take (float32, or int32 for Vector2I).</summary>
        string GdStructScalar(string structType, string scalarType, string code)
        {
            bool isFloat = IsGodotFloatStruct(structType);
            if (isFloat && (scalarType == "double" || scalarType == "dynamic")) return $"((float){Paren(code)})";
            if (!isFloat && (scalarType == "long" || scalarType == "double" || scalarType == "dynamic")) return $"checked((int){Paren(code)})";
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
                    string asScalar = IsGodotFloatStruct(s) ? "float" : "int";
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
