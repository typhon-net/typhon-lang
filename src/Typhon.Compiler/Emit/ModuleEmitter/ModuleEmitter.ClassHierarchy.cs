// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    // Class hierarchies across the program: bases, members they declare, attributes created from outside.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>Does the class (or a Typhon base) declare the member: a method, class variable, annotation or self.x field?</summary>
        bool DeclaresMember(ClassDef c, string name, int depth = 0)
        {
            if (c == null || depth > 20) return false;
            if (c.Body.Any(st => st is FunctionDef f && f.Name == name || st is AssignStmt a && a.Targets.Any(t => t is NameExpr tn && tn.Id == name) ||
                                 st is AnnAssignStmt aa && aa.Target is NameExpr an && an.Id == name || st is ClassDef nc && nc.Name == name)) return true;
            if (CollectImplicitFields(c).ContainsKey(name)) return true;
            foreach (var b in c.Bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py == null) return true; // a .NET base: it may well have it
                if (DeclaresMember(py, name, depth + 1)) return true;
            }
            return false;
        }

        /// <summary>
        /// The access modifier of the .NET member an override in a class with these bases overrides (through Typhon bases too),
        /// or null when none is overridden: C# requires an override to keep it, so ComponentBase.OnInitialized stays protected.
        /// </summary>
        string DotNetOverrideVisibility(List<Expr> bases, string member, int? parameterCount, int depth = 0)
        {
            if (depth > 20) return null;
            foreach (var b in bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                string access = py != null ? DotNetOverrideVisibility(py.Bases, member, parameterCount, depth + 1) : meta != null ? _r.OverrideAccessibility(meta, member, parameterCount) : null;
                if (access != null) return access;
            }
            return null;
        }

        /// <summary>obj.attr = v / Cls.ATTR = v where the class does not declare it: the attribute becomes a field (dry run).</summary>
        void RecordExternalAttribute(AttributeExpr at, Expr value)
        {
            string vt = value is ConstExpr nc && nc.Kind == ConstKind.None ? TypeInference.NoneType : PyType(InferType(value)) ?? TypeInference.Unknown;
            if (at.Target is NameExpr self && self.Id == "self")
            {
                if (_cls == null) return;
                if (_cls.InferredFieldInitializers.TryGetValue(at.Name, out var initial) && initial != value &&
                    (!TypeInference.IsEmptyContainer(value) || !TypeInference.IsEmptyContainer(initial) ||
                     TypeInference.ContainerKind(initial) != TypeInference.ContainerKind(value)))
                    Obs.AddExternal(TypeInference.ClassKey(_m, _cls.Name) + "|" + at.Name, vt);
                foreach (var parent in TyphonBases(_cls.Def))
                {
                    if (ImplicitFieldOwner(parent, at.Name) is ClassDef declared &&
                        _p.TryFindClass(_m, declared.Name, out var found, out var baseModule) && found == declared)
                    {
                        // An empty literal can reuse a field of the same kind, even before its element type is known.
                        if (TypeInference.IsUnknown(vt) && TypeInference.IsEmptyContainer(value))
                        {
                            var fieldValue = CollectImplicitFields(declared)[at.Name].Value.Value;
                            if (TypeInference.ContainerKind(value) == TypeInference.ContainerKind(fieldValue)) continue;
                            vt = "dynamic";
                        }
                        Obs.AddExternal(TypeInference.ClassKey(baseModule, declared.Name) + "|" + at.Name, vt);
                    }
                }
                return;
            }
            if (at.Target is NameExpr cn && !IsLocal(cn.Id) && cn.Id != "self" && TryResolveType(cn.Id, 0, out _, out var pyClass) && pyClass != null &&
                _p.TryFindClass(_m, pyClass.Name, out var cls, out var owner) && cls == pyClass)
            {
                bool declaredNone = cls.Body.Any(st => st is AssignStmt sa && sa.Targets.Count == 1 && sa.Targets[0] is NameExpr sn && sn.Id == at.Name &&
                                                       sa.Value is ConstExpr sc && sc.Kind == ConstKind.None);
                if (declaredNone || !DeclaresMember(cls, at.Name)) Obs.AddExternal(TypeInference.ClassKey(owner, cls.Name) + "|static|" + at.Name, vt);
                return;
            }
            if (ClassKeyOfType(InferType(at.Target), out _) is string ck && ClassDefOfKey(ck) is ClassDef cd &&
                (!DeclaresMember(cd, at.Name) || CollectImplicitFields(cd).ContainsKey(at.Name)))
                Obs.AddExternal(ck + "|" + at.Name, vt);
        }

        ClassDef ImplicitFieldOwner(ClassDef c, string name, int depth = 0)
        {
            if (depth > 20) return null;
            foreach (var parent in TyphonBases(c))
                if (ImplicitFieldOwner(parent, name, depth + 1) is ClassDef inherited) return inherited;
            return CollectImplicitFields(c).TryGetValue(name, out var field) && field.Key == null ? c : null;
        }

        FunctionDef MethodInHierarchy(ClassDef c, string name, int depth = 0)
        {
            if (depth > 20) return null;
            return c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name) ??
                   TyphonBases(c).Select(b => MethodInHierarchy(b, name, depth + 1)).FirstOrDefault(f => f != null);
        }

        internal void MarkUnboundSourceMethod(AttributeExpr member)
        {
            if (DottedName(member.Target) is not string target ||
                !TryResolveType(target, 0, out _, out var owner) || owner == null || owner.TypeParams.Count != 0) return;
            if (MethodInHierarchy(owner, member.Name) is FunctionDef method && method.Params.Count > 0 &&
                method.Decorators.Count == 0 && method.TypeParams.Count == 0 && !method.IsMissingMethodStub &&
                !method.IsAsync && method.Name is not ("__init__" or "__new__"))
                method.UsedWithUnboundReceiver = true;
        }

        ClassDef InstanceMethodOwner(AttributeExpr member, out string classKey)
        {
            string receiver = InferType(member.Target);
            classKey = ClassKeyOfType(receiver, out _);
            ClassDef cls = member.Target is NameExpr { Id: "self" } && HasSelfInScope() ? _cls?.Def :
                classKey != null ? ClassDefOfKey(classKey) : null;
            if (cls == null || MethodInHierarchy(cls, member.Name) is not FunctionDef method ||
                HasDecorator(method.Decorators, "property") || IsCachedProperty(method)) return null;
            return cls;
        }

        string NativePythonInstanceMethodOwner(AttributeExpr member)
        {
            if (_m.IsGdScript || DotNetMeta(InferType(member.Target)) is not string owner ||
                !owner.StartsWith("Typhon.Stdlib.", StringComparison.Ordinal)) return null;
            var members = _r.GetMembers(owner, member.Name);
            return members.Count > 0 && members.All(candidate => candidate.IsMethod && !candidate.IsStatic) ? owner : null;
        }

        bool InstanceMethodMayBeShadowed(AttributeExpr member)
        {
            ClassDef cls = InstanceMethodOwner(member, out var classKey);
            if (cls == null) classKey = NativePythonInstanceMethodOwner(member);
            if (classKey != null && Ti?.ExternalFields.ContainsKey(classKey + "|" + member.Name) == true) return true;
            return cls != null && AssignedInHierarchy(cls, 0);

            bool AssignedInHierarchy(ClassDef c, int depth) => depth <= 20 &&
                (CollectImplicitFields(c).ContainsKey(member.Name) ||
                 TyphonBases(c).Any(parent => AssignedInHierarchy(parent, depth + 1)));
        }

        string ExternalType(string key) =>
            Ti != null && Ti.ExternalFields.TryGetValue(key, out var t) ? t : _dry ? TypeInference.Pending : null;


        IEnumerable<ClassDef> AllClasses() => _p.Modules.Values.Where(m => m.Ast != null).SelectMany(m => m.Classes.Values);

        /// <summary>The Typhon base classes of a class (resolved by name across the project).</summary>
        IEnumerable<ClassDef> TyphonBases(ClassDef c)
        {
            foreach (var b in c.Bases)
                if ((b is SubscriptExpr s ? s.Target : b) is NameExpr bn && _p.TryFindClass(_m, bn.Id, out var bc, out _) && bc != c) yield return bc;
                else if (b is AttributeExpr ba && _p.TryFindClass(_m, ba.Name, out var bc2, out _) && bc2 != c) yield return bc2;
        }

        bool DerivesFrom(ClassDef c, ClassDef ancestor, int depth = 0) =>
            depth < 20 && TyphonBases(c).Any(b => b == ancestor || DerivesFrom(b, ancestor, depth + 1));

        static bool Defines(ClassDef c, string method) => c.Body.OfType<FunctionDef>().Any(f => f.Name == method);

        /// <summary>Does any definition of the method in the class's hierarchy (its bases and subclasses) return a value?</summary>
        bool FamilyReturnsValue(ClassDef c, string method)
        {
            // (a mixin's family also has the classes its members were copied into: multiple inheritance)
            var copies = _p.MixedInto.TryGetValue(c, out var into) ? into.Select(x => x.Class).ToList() : new List<ClassDef>();
            foreach (var k in AllClasses())
            {
                if (!(k == c || DerivesFrom(k, c) || DerivesFrom(c, k) || copies.Any(x => k == x || DerivesFrom(k, x)))) continue;
                foreach (var f in k.Body.OfType<FunctionDef>())
                {
                    if (f.Name != method) continue;
                    // The CLR wrapper of a Python-decorated method returns the decorator's result.
                    if (ClassDeclarationResolver(k)._m.Ast.MethodDecorators.ContainsKey(f)) return true;
                    if (f.Returns != null && !(f.Returns is ConstExpr rc && rc.Kind == ConstKind.None)) return true;
                    var loc = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(x => x.Name), null);
                    if (loc.HasValueReturn || loc.HasYield) return true;
                }
            }
            return false;
        }

        /// <summary>Does another class of the method's family (base, subclass, a class its members were copied into) define it?</summary>
        bool FamilyDefinesElsewhere(ClassDef c, string method)
        {
            var copies = _p.MixedInto.TryGetValue(c, out var into) ? into.Select(x => x.Class).ToList() : new List<ClassDef>();
            return AllClasses().Any(k => k != c && (DerivesFrom(k, c) || DerivesFrom(c, k) || copies.Any(x => k == x || DerivesFrom(k, x))) &&
                                         k.Body.OfType<FunctionDef>().Any(f => f.Name == method));
        }

        bool DefinesInHierarchy(ClassDef c, string method, int depth = 0) =>
            depth < 20 && (Defines(c, method) || TyphonBases(c).Any(b => DefinesInHierarchy(b, method, depth + 1)));

        /// <summary>A Typhon class deriving from list / dict / set (directly or through other Typhon classes).</summary>
        bool IsBuiltinSubclass(string t)
        {
            if (t == null || ClassKeyOfType(t, out _) is not string key || ClassDefOfKey(key) is not ClassDef c) return false;
            for (int depth = 0; c != null && depth < 20; depth++)
            {
                if (c.Bases.Any(b => (b is SubscriptExpr bs ? bs.Target : b) is NameExpr bn && (bn.Id == "list" || bn.Id == "dict" || bn.Id == "set"))) return true;
                c = c.Bases.Count > 0 && ResolveBase(c.Bases[0], out var py, out _) ? py : null;
            }
            return false;
        }

        /// <summary>The list/dict/set type a Typhon class derives from (class Stack(list) → List&lt;dynamic&gt;), or null.</summary>
        string BuiltinBaseType(string t)
        {
            if (t == null || ClassKeyOfType(t, out _) is not string key || ClassDefOfKey(key) is not ClassDef c) return null;
            for (int depth = 0; c != null && depth < 20; depth++)
            {
                foreach (var b in c.Bases)
                    if ((b is SubscriptExpr bs ? bs.Target : b) is NameExpr bn && (bn.Id == "list" || bn.Id == "dict" || bn.Id == "set") && !IsShadowed(bn.Id))
                        return MapType(b, c);
                c = c.Bases.Count > 0 && ResolveBase(c.Bases[0], out var py, out _) ? py : null;
            }
            return null;
        }

        bool DerivesFromType(string sub, string sup) =>
            ClassKeyOfType(sub, out _) is string sk && ClassDefOfKey(sk) is ClassDef sc && ClassKeyOfType(sup, out _) is string pk && ClassDefOfKey(pk) is ClassDef pc && DerivesFrom(sc, pc);
    }
}
