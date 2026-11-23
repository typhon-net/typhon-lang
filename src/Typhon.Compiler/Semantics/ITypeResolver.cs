// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Typhon.Compiler.Semantics
{
    /// <summary>
    /// Answers questions about .NET symbols the Typhon compiler cannot know from Typhon source alone
    /// (e.g. "is System.Text.StringBuilder a type?" — needed because Python has no 'new').
    /// Implemented on top of a Roslyn Compilation, so it sees referenced assemblies and C# code in the same project.
    /// </summary>
    public interface ITypeResolver
    {
        /// <summary>CLR metadata name, e.g. "System.Collections.Generic.List`1" or "Outer+Inner".</summary>
        bool TypeExists(string metadataName);
        bool NamespaceExists(string dottedNamespace);
        /// <summary>Does the type (or one of its base types) declare a member with that name?</summary>
        bool HasMember(string metadataName, string memberName);
        /// <summary>Is there a virtual/abstract/override member with that name in the type or its bases?</summary>
        bool IsOverridable(string metadataName, string memberName, int? parameterCount = null);
        /// <summary>
        /// The access modifier an override of that member must declare ("public", "protected", "protected internal", ...),
        /// or null when the type has no overridable member of that name (ComponentBase.OnInitialized → "protected").
        /// </summary>
        string OverrideAccessibility(string metadataName, string memberName, int? parameterCount = null);
        /// <summary>Public and protected instance constructors of a non-generic class (empty when unknown).</summary>
        IReadOnlyList<CtorSignature> GetConstructors(string metadataName);
        /// <summary>
        /// Public methods, properties and fields named <paramref name="memberName"/> of the type and its base types
        /// (".ctor": its public constructors), described as far as passing Python ints (64-bit) needs: which parameters
        /// accept a long, which need a narrowing conversion, and the C# keyword of a special result type.
        /// </summary>
        IReadOnlyList<MemberSig> GetMembers(string metadataName, string memberName);
        /// <summary>Extension methods of that name in the namespaces that apply to the receiver type, with the receiver left out.</summary>
        IReadOnlyList<MemberSig> GetExtensionMethods(string receiverMetadataName, string name, IReadOnlyList<string> namespaces);
    }

    /// <summary>How a parameter (or property) takes a value of C# type long.</summary>
    public enum LongFit
    {
        /// <summary>Not an integer-like parameter (string, a class, ...).</summary>
        Other,
        /// <summary>long converts implicitly (long, double, decimal, object, a type parameter, ...).</summary>
        Accepts,
        /// <summary>Takes a narrower integer (int, short, ...): a long needs a checked narrowing conversion.</summary>
        Narrow,
    }

    public sealed class ParamSig
    {
        public string Name;
        public LongFit Fit;
        /// <summary>The C# type to narrow to when <see cref="Fit"/> is <see cref="LongFit.Narrow"/> ("int", "short", ...).</summary>
        public string NarrowTo;
        public bool IsParams;
        public bool IsPackedArguments;
        public bool IsOptional;
        /// <summary>The parameter is System.Delegate itself (ASP.NET Core's MapGet): any function, by its own signature.</summary>
        public bool IsDelegateBase;
        /// <summary>The parameter is an array (not a params array): a Python list is passed as list.ToArray().</summary>
        public bool IsArray;
        /// <summary>The parameter takes a function: a delegate type, an expression tree, or a type parameter (which may bind to one).</summary>
        public bool TakesFunction;
        /// <summary>A one-argument delegate supplies its concrete input type to a lambda.</summary>
        public bool HasConcreteCallbackInput;
        /// <summary>The parameter type as C# code with the method's type parameters left in ("List&lt;T&gt;", "IEnumerable&lt;T&gt;").</summary>
        public string GenericType;
        /// <summary>Big-int mode: a System.Numerics.BigInteger converts implicitly (BigInteger, object, a type parameter, ...).</summary>
        public bool BigAccepts;
        /// <summary>Big-int mode: the parameter is exactly BigInteger (an overload that keeps Python ints exact).</summary>
        public bool IsBigInteger;
        /// <summary>Big-int mode: the numeric type a BigInteger is converted to explicitly ("long", "int", "double", ...), or null.</summary>
        public string BigNarrowTo;
    }

    public sealed class MemberSig
    {
        public bool IsMethod;
        public bool IsStatic;
        public bool IsOptionalModuleMember;
        public bool IsConstant;
        public bool IsProperty;
        public bool IsOverridable;
        public bool RequiresClrArguments;
        /// <summary>The result type (method return, property or field type) as C# code, or null when Typhon should not adopt it.</summary>
        public string Type;
        /// <summary>For properties and fields: how a long assigned to it fits.</summary>
        public LongFit ValueFit;
        public string ValueNarrowTo;
        /// <summary>Big-int mode: for properties and fields of a numeric type, what a BigInteger is converted to.</summary>
        public string ValueBigNarrowTo;
        public List<ParamSig> Parameters = new List<ParamSig>();
        /// <summary>A generic method's type parameters, and its return type with them left in ("T", "List&lt;T&gt;"); null if not generic.</summary>
        public List<string> TypeParams;
        public string GenericType;
        /// <summary>The method is declared to return dynamic (its Type is null then).</summary>
        public bool ReturnsDynamic;
        /// <summary>The method is declared void, so its Python result is None.</summary>
        public bool ReturnsVoid;
    }

    /// <summary>A constructor as the emitter needs it: fully qualified C# parameter types and names.</summary>
    public sealed class CtorSignature
    {
        public bool IsProtected;
        public List<KeyValuePair<string, string>> Parameters = new List<KeyValuePair<string, string>>();
        /// <summary>C# expressions for optional defaults in parameter order, or null when no expression is available.</summary>
        public List<string> Defaults = new List<string>();
        public MemberSig Signature;
    }

    /// <summary>Resolver used when no compilation is available (unit tests, syntax-only tools).</summary>
    public sealed class NullTypeResolver : ITypeResolver
    {
        public static readonly NullTypeResolver Instance = new NullTypeResolver();
        public bool TypeExists(string metadataName) => false;
        public bool NamespaceExists(string dottedNamespace) => false;
        public IReadOnlyList<MemberSig> GetExtensionMethods(string receiverMetadataName, string name, IReadOnlyList<string> namespaces) => new List<MemberSig>();
        public bool HasMember(string metadataName, string memberName) => false;
        public bool IsOverridable(string metadataName, string memberName, int? parameterCount = null) => false;
        public string OverrideAccessibility(string metadataName, string memberName, int? parameterCount = null) => null;
        public IReadOnlyList<CtorSignature> GetConstructors(string metadataName) => new CtorSignature[0];
        public IReadOnlyList<MemberSig> GetMembers(string metadataName, string memberName) => new MemberSig[0];
    }
}
