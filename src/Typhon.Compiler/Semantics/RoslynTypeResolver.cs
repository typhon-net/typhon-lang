// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Typhon.Compiler.Semantics
{
    public sealed class RoslynTypeResolver : ITypeResolver
    {
        readonly Compilation _compilation;
        readonly Dictionary<string, INamedTypeSymbol> _types = new Dictionary<string, INamedTypeSymbol>();
        readonly Dictionary<string, bool> _namespaces = new Dictionary<string, bool>();

        public RoslynTypeResolver(Compilation compilation) { _compilation = compilation; }

        INamedTypeSymbol Find(string metadataName)
        {
            if (_types.TryGetValue(metadataName, out var cached)) return cached;
            var t = _compilation.GetTypeByMetadataName(metadataName);
            if (t == null)
            {
                // Ambiguous across references (GetTypeByMetadataName returns null) — pick the first public one.
                foreach (var asm in _compilation.SourceModule.ReferencedAssemblySymbols)
                {
                    var candidate = asm.GetTypeByMetadataName(metadataName);
                    if (candidate != null && candidate.DeclaredAccessibility == Accessibility.Public) { t = candidate; break; }
                }
            }
            _types[metadataName] = t;
            return t;
        }

        public bool TypeExists(string metadataName) => Find(metadataName) != null;

        internal IEnumerable<string> StaticFieldNames(string metadataName) =>
            Find(metadataName)?.GetMembers().OfType<IFieldSymbol>()
                .Where(field => field.IsStatic && field.DeclaredAccessibility == Accessibility.Public)
                .Select(field => field.Name) ?? Enumerable.Empty<string>();

        public bool NamespaceExists(string dottedNamespace)
        {
            if (_namespaces.TryGetValue(dottedNamespace, out var known)) return known;
            INamespaceSymbol ns = _compilation.GlobalNamespace;
            foreach (var part in dottedNamespace.Split('.'))
            {
                ns = ns.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                if (ns == null) break;
            }
            return _namespaces[dottedNamespace] = ns != null;
        }

        // Interfaces are implemented, not inherited: their members must be declared (not hidden, not overridden).
        public bool HasMember(string metadataName, string memberName)
        {
            var start = Find(metadataName);
            if (start == null || start.TypeKind == TypeKind.Interface) return false;
            for (ITypeSymbol t = start; t != null; t = t.BaseType)
                if (t.GetMembers(memberName).Length > 0) return true;
            return false;
        }

        public bool IsOverridable(string metadataName, string memberName)
        {
            var start = Find(metadataName);
            if (start == null || start.TypeKind == TypeKind.Interface) return false;
            for (ITypeSymbol t = start; t != null; t = t.BaseType)
                foreach (var m in t.GetMembers(memberName))
                    if ((m.IsVirtual || m.IsAbstract || m.IsOverride) && !m.IsSealed) return true;
            return false;
        }

        public string OverrideAccessibility(string metadataName, string memberName)
        {
            var start = Find(metadataName);
            if (start == null || start.TypeKind == TypeKind.Interface) return null;
            for (ITypeSymbol t = start; t != null; t = t.BaseType)
                foreach (var m in t.GetMembers(memberName))
                    if ((m.IsVirtual || m.IsAbstract || m.IsOverride) && !m.IsSealed)
                        switch (m.DeclaredAccessibility)
                        {
                            case Accessibility.Public: return "public";
                            case Accessibility.Protected: return "protected";
                            // overridden from another assembly, a protected internal member is protected
                            case Accessibility.ProtectedOrInternal:
                                return SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, _compilation.Assembly) ? "protected internal" : "protected";
                            case Accessibility.Internal: return "internal";
                            case Accessibility.ProtectedAndInternal: return "private protected";
                        }
            return null;
        }

        readonly Dictionary<string, IReadOnlyList<MemberSig>> _members = new Dictionary<string, IReadOnlyList<MemberSig>>();

        MemberSig MethodSig(IMethodSymbol m)
        {
            var sig = new MemberSig
            {
                IsMethod = true,
                IsStatic = m.IsStatic,
                Type = Display(m.ReturnType),
                ReturnsDynamic = m.ReturnType.TypeKind == TypeKind.Dynamic,
                ReturnsVoid = m.ReturnsVoid,
            };
            // C# constructs a delegate from one callable. The CLR metadata's target and
            // method pointer are implementation details, not the source parameter list.
            if (m.MethodKind == MethodKind.Constructor && m.ContainingType.TypeKind == TypeKind.Delegate)
            {
                sig.Parameters.Add(new ParamSig { Name = "method", TakesFunction = true, GenericType = Display(m.ContainingType) });
                return sig;
            }
            if (m.TypeParameters.Length > 0)
            {
                sig.TypeParams = m.TypeParameters.Select(tp => tp.Name).ToList();
                sig.GenericType = GenericDisplay(m.ReturnType);
            }
            foreach (var p in m.Parameters)
            {
                var t = p.IsParams && p.Type is IArrayTypeSymbol arr ? arr.ElementType : p.Type;
                var fit = Fit(t, out var narrowTo);
                bool bigAccepts = BigFit(t, out var bigNarrow, out bool isBig);
                if (p.RefKind != RefKind.None) { fit = LongFit.Other; bigAccepts = false; bigNarrow = null; isBig = false; }
                var elem = p.IsParams && t is IArrayTypeSymbol pa ? pa.ElementType : t;
                // (System.Delegate itself too: MapGet("/", handler) takes any function as a method group)
                bool takesFunction = elem.TypeKind == TypeKind.Delegate || elem.TypeKind == TypeKind.TypeParameter ||
                                     elem.SpecialType == SpecialType.System_Delegate || elem.SpecialType == SpecialType.System_MulticastDelegate ||
                                     elem is INamedTypeSymbol en && en.Name == "Expression" && en.IsGenericType;
                sig.Parameters.Add(new ParamSig { Name = p.Name, Fit = fit, NarrowTo = narrowTo, IsParams = p.IsParams, IsOptional = p.IsOptional,
                                                  IsDelegateBase = elem.SpecialType == SpecialType.System_Delegate || elem.SpecialType == SpecialType.System_MulticastDelegate,
                                                              IsArray = !p.IsParams && p.Type is IArrayTypeSymbol,
                                                  BigAccepts = bigAccepts, BigNarrowTo = bigNarrow, IsBigInteger = isBig, TakesFunction = takesFunction,
                                                  GenericType = GenericDisplay(elem) });
            }
            return sig;
        }

        public IReadOnlyList<MemberSig> GetExtensionMethods(string receiverMetadataName, string name, IReadOnlyList<string> namespaces)
        {
            string key = receiverMetadataName + "::ext::" + name + "::" + string.Join(",", namespaces);
            if (_members.TryGetValue(key, out var cached)) return cached;
            var result = new List<MemberSig>();
            var receiver = Find(receiverMetadataName);
            if (receiver != null)
                foreach (var dotted in namespaces.Distinct())
                {
                    INamespaceSymbol ns = _compilation.GlobalNamespace;
                    foreach (var part in dotted.Split('.')) { ns = ns?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part); }
                    if (ns == null) continue;
                    foreach (var type in ns.GetTypeMembers())
                    {
                        if (!type.IsStatic || !type.MightContainExtensionMethods || type.DeclaredAccessibility != Accessibility.Public) continue;
                        foreach (var m in type.GetMembers(name).OfType<IMethodSymbol>())
                        {
                            if (!m.IsExtensionMethod || m.DeclaredAccessibility != Accessibility.Public) continue;
                            // applicable to the receiver (generic ones get their type arguments from it): the parameters after 'this'
                            if (m.ReduceExtensionMethod(receiver) is IMethodSymbol reduced)
                            {
                                var sig = MethodSig(reduced);
                                sig.IsStatic = false;
                                result.Add(sig);
                            }
                        }
                    }
                }
            return _members[key] = result;
        }

        public IReadOnlyList<MemberSig> GetMembers(string metadataName, string memberName)
        {
            string key = metadataName + "::" + memberName;
            if (_members.TryGetValue(key, out var cached)) return cached;
            var result = new List<MemberSig>();
            var start = Find(metadataName);
            if (start != null)
            {
                IEnumerable<ISymbol> symbols;
                if (memberName == ".ctor") symbols = start.InstanceConstructors;
                else
                {
                    var types = new List<ITypeSymbol>();
                    for (ITypeSymbol t = start; t != null; t = t.BaseType) types.Add(t);
                    if (start.TypeKind == TypeKind.Interface) types.AddRange(start.AllInterfaces);
                    symbols = types.SelectMany(t => t.GetMembers(memberName));
                }
                foreach (var sym in symbols)
                {
                    if (sym.DeclaredAccessibility != Accessibility.Public) continue;
                    switch (sym)
                    {
                        // (user-defined operators too, looked up by their metadata names: op_Division, ...)
                        case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary || m.MethodKind == MethodKind.Constructor || m.MethodKind == MethodKind.UserDefinedOperator:
                            result.Add(MethodSig(m));
                            break;
                        case IPropertySymbol prop when !prop.IsIndexer:
                            BigFit(prop.Type, out var pbn, out _);
                            result.Add(new MemberSig { IsStatic = prop.IsStatic, Type = Display(prop.Type), ReturnsDynamic = prop.Type.TypeKind == TypeKind.Dynamic, ValueFit = Fit(prop.Type, out var pn), ValueNarrowTo = pn, ValueBigNarrowTo = pbn });
                            break;
                        case IFieldSymbol field:
                            BigFit(field.Type, out var fbn, out _);
                            result.Add(new MemberSig { IsStatic = field.IsStatic, Type = Display(field.Type), ReturnsDynamic = field.Type.TypeKind == TypeKind.Dynamic, ValueFit = Fit(field.Type, out var fn), ValueNarrowTo = fn, ValueBigNarrowTo = fbn });
                            break;
                    }
                }
            }
            return _members[key] = result;
        }

        /// <summary>
        /// The type as C# code usable anywhere in generated code (keywords, else fully qualified; System.Collections.Generic
        /// short, as Typhon spells it), or null when Typhon should not adopt it: void, object/dynamic, open generic types,
        /// ref structs, pointers.
        /// </summary>
        static string Display(ITypeSymbol t)
        {
            if (t == null || t.SpecialType == SpecialType.System_Void || t.SpecialType == SpecialType.System_Object || t.TypeKind == TypeKind.Dynamic) return null;
            if (Keyword(t) is string k) return k;
            if (t.IsRefLikeType || t.TypeKind == TypeKind.Pointer || t.TypeKind == TypeKind.FunctionPointer || t.TypeKind == TypeKind.Error || HasTypeParameter(t)) return null;
            return t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::System.Collections.Generic.", "");
        }

        /// <summary>A type as C# code, type parameters included (for binding a generic method's type arguments).</summary>
        static string GenericDisplay(ITypeSymbol t)
        {
            if (t == null || t.SpecialType == SpecialType.System_Void) return null;
            if (t.SpecialType == SpecialType.System_Object || t.TypeKind == TypeKind.Dynamic) return "object";
            if (Keyword(t) is string k) return k;
            if (t is ITypeParameterSymbol tp) return tp.Name;
            return t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::System.Collections.Generic.", "");
        }

        static bool HasTypeParameter(ITypeSymbol t)
        {
            switch (t)
            {
                case ITypeParameterSymbol _: return true;
                case IArrayTypeSymbol a: return HasTypeParameter(a.ElementType);
                case INamedTypeSymbol n: return n.TypeArguments.Any(HasTypeParameter) || (n.ContainingType != null && HasTypeParameter(n.ContainingType));
                default: return false;
            }
        }

        static string Keyword(ITypeSymbol t)
        {
            switch (t.SpecialType)
            {
                case SpecialType.System_String: return "string";
                case SpecialType.System_Boolean: return "bool";
                case SpecialType.System_Char: return "char";
                case SpecialType.System_Int32: return "int";
                case SpecialType.System_Int64: return "long";
                case SpecialType.System_Int16: return "short";
                case SpecialType.System_Byte: return "byte";
                case SpecialType.System_SByte: return "sbyte";
                case SpecialType.System_UInt16: return "ushort";
                case SpecialType.System_UInt32: return "uint";
                case SpecialType.System_UInt64: return "ulong";
                case SpecialType.System_Double: return "double";
                case SpecialType.System_Single: return "float";
                case SpecialType.System_Decimal: return "decimal";
                default: return null;
            }
        }

        /// <summary>How a value of type long fits a parameter of type <paramref name="t"/> (and what to narrow it to).</summary>
        LongFit Fit(ITypeSymbol t, out string narrowTo)
        {
            narrowTo = null;
            if (t == null) return LongFit.Other;
            if (t.TypeKind == TypeKind.TypeParameter || t.TypeKind == TypeKind.Dynamic) return LongFit.Accepts;
            var int64 = _compilation.GetSpecialType(SpecialType.System_Int64);
            var int32 = _compilation.GetSpecialType(SpecialType.System_Int32);
            var toLong = _compilation.ClassifyCommonConversion(int64, t);
            if (toLong.Exists && toLong.IsImplicit) return LongFit.Accepts;
            var u = t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? n.TypeArguments[0] : t;
            switch (u.SpecialType)
            {
                case SpecialType.System_Int32: narrowTo = "int"; return LongFit.Narrow;
                case SpecialType.System_Int16: narrowTo = "short"; return LongFit.Narrow;
                case SpecialType.System_UInt16: narrowTo = "ushort"; return LongFit.Narrow;
                case SpecialType.System_Byte: narrowTo = "byte"; return LongFit.Narrow;
                case SpecialType.System_SByte: narrowTo = "sbyte"; return LongFit.Narrow;
                case SpecialType.System_UInt32: narrowTo = "uint"; return LongFit.Narrow;
                case SpecialType.System_UInt64: narrowTo = "ulong"; return LongFit.Narrow;
                case SpecialType.System_IntPtr: narrowTo = "nint"; return LongFit.Narrow;
            }
            // e.g. System.Index: an int converts implicitly, a long does not
            var fromInt = _compilation.ClassifyCommonConversion(int32, t);
            if (fromInt.Exists && fromInt.IsImplicit) { narrowTo = "int"; return LongFit.Narrow; }
            return LongFit.Other;
        }

        /// <summary>
        /// Big-int mode: does a BigInteger convert implicitly to <paramref name="t"/>? If not, the numeric type it converts to
        /// explicitly (long, int, double, ...; null for non-numeric types).
        /// </summary>
        bool BigFit(ITypeSymbol t, out string narrowTo, out bool isBig)
        {
            narrowTo = null; isBig = false;
            if (t == null) return false;
            if (t.TypeKind == TypeKind.TypeParameter || t.TypeKind == TypeKind.Dynamic) return true;
            var big = _compilation.GetTypeByMetadataName("System.Numerics.BigInteger");
            if (big == null) return false;
            var u = t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? n.TypeArguments[0] : t;
            isBig = SymbolEqualityComparer.Default.Equals(u, big);
            var conv = _compilation.ClassifyCommonConversion(big, t);
            if (conv.Exists && conv.IsImplicit) return true;
            if (conv.Exists && Keyword(u) is string k && k != "string" && k != "bool") narrowTo = k;
            return false;
        }

        public IReadOnlyList<CtorSignature> GetConstructors(string metadataName)
        {
            var result = new List<CtorSignature>();
            var t = Find(metadataName);
            if (t == null || t.TypeKind != TypeKind.Class || t.IsGenericType) return result;
            foreach (var c in t.InstanceConstructors)
            {
                bool isProtected = c.DeclaredAccessibility == Accessibility.Protected || c.DeclaredAccessibility == Accessibility.ProtectedOrInternal;
                if (c.DeclaredAccessibility != Accessibility.Public && !isProtected) continue;
                // skip [Obsolete] (e.g. Exception's serialization constructor) and by-ref parameters
                if (c.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute")) continue;
                if (c.Parameters.Any(p => p.RefKind != RefKind.None || p.IsParams)) continue;
                var sig = new CtorSignature { IsProtected = isProtected, Signature = MethodSig(c) };
                foreach (var p in c.Parameters)
                {
                    string type = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    sig.Parameters.Add(new KeyValuePair<string, string>(type, p.Name));
                    sig.Defaults.Add(ConstructorDefault(p, type));
                }
                result.Add(sig);
            }
            return result;
        }

        static string ConstructorDefault(IParameterSymbol parameter, string type)
        {
            if (!parameter.IsOptional || !parameter.HasExplicitDefaultValue) return null;
            object value = parameter.ExplicitDefaultValue;
            if (value == null) return parameter.Type.IsValueType ? "default" : "null";
            string literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false);
            if (literal == null) return null;
            if (value is float or double && literal is "NaN" or "Infinity" or "-Infinity")
                return (value is float ? "float." : "double.") +
                    (literal == "Infinity" ? "PositiveInfinity" : literal == "-Infinity" ? "NegativeInfinity" : "NaN");
            literal = value switch
            {
                sbyte => "(sbyte)" + literal, byte => "(byte)" + literal,
                short => "(short)" + literal, ushort => "(ushort)" + literal,
                uint => literal + "u", long => literal + "L", ulong => literal + "UL",
                float => literal + "f", double => literal + "d", decimal => literal + "m",
                _ => literal,
            };
            return parameter.Type.TypeKind == TypeKind.Enum ? $"({type})({literal})" : literal;
        }
    }
}
