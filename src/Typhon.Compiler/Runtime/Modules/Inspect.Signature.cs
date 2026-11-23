// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    using System;
    using System.Reflection;

    // The provider reads definition-time defaults, including objects that cannot be stored in a CLR attribute.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class PySignatureAttribute(string provider) : Attribute
    {
        internal const string ConstructorProvider = "__typhon_signature___init__";
        const BindingFlags Providers = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static Func<Stdlib.inspect.Signature>? Find(Type? owner, string name) =>
            owner?.GetMethod(name, Providers)?.CreateDelegate<Func<Stdlib.inspect.Signature>>();

        internal static Func<Stdlib.inspect.Signature>? Of(MethodInfo method) =>
            method.GetCustomAttribute<PySignatureAttribute>() is { } attribute ? Find(method.DeclaringType, attribute.Provider) : null;

        string Provider { get; } = provider;
    }
}

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static partial class inspect
    {
        internal sealed class _empty { }

        public enum _ParameterKind : long
        {
            POSITIONAL_ONLY,
            POSITIONAL_OR_KEYWORD,
            VAR_POSITIONAL,
            KEYWORD_ONLY,
            VAR_KEYWORD,
        }

        internal sealed class Parameter(string name, _ParameterKind kind,
            [PyKeywordOnly, Optional] object? @default, [PyKeywordOnly, Optional] object? annotation) : IPyRepr
        {
            public static Type empty => typeof(_empty);
            public const _ParameterKind POSITIONAL_ONLY = _ParameterKind.POSITIONAL_ONLY;
            public const _ParameterKind POSITIONAL_OR_KEYWORD = _ParameterKind.POSITIONAL_OR_KEYWORD;
            public const _ParameterKind VAR_POSITIONAL = _ParameterKind.VAR_POSITIONAL;
            public const _ParameterKind KEYWORD_ONLY = _ParameterKind.KEYWORD_ONLY;
            public const _ParameterKind VAR_KEYWORD = _ParameterKind.VAR_KEYWORD;

            public string name { get; } = name;
            public _ParameterKind kind { get; } = kind;
            public dynamic? @default { get; } = ReferenceEquals(@default, Type.Missing) ? empty : @default;
            public dynamic? annotation { get; } = ReferenceEquals(annotation, Type.Missing) ? empty : annotation;

            public override string ToString()
            {
                string text = kind switch
                {
                    _ParameterKind.VAR_POSITIONAL => "*" + name,
                    _ParameterKind.VAR_KEYWORD => "**" + name,
                    _ => name,
                };
                if (!ReferenceEquals((object?)annotation, empty)) text += ": " + FormatAnnotation((object?)annotation);
                if (!ReferenceEquals((object?)@default, empty))
                    text += (ReferenceEquals((object?)annotation, empty) ? "=" : " = ") + Builtins.repr((object?)@default);
                return text;
            }

            public string __repr__() => $"<Parameter \"{this}\">";
        }

        internal sealed class Signature : IPyRepr
        {
            readonly Parameter[] _parameters;
            internal IReadOnlyList<Parameter> Arguments => _parameters;
            public static Type empty => typeof(_empty);
            public types.MappingProxyType parameters { get; }
            public dynamic? return_annotation { get; }

            public Signature([Optional, DefaultParameterValue(null)] IEnumerable<Parameter>? parameters,
                [PyKeywordOnly, Optional] object? return_annotation)
            {
                _parameters = parameters as Parameter[] ?? parameters?.ToArray() ?? [];
                this.return_annotation = ReferenceEquals(return_annotation, Type.Missing) ? empty : return_annotation;
                var ordered = new OrderedDictionary();
                foreach (var parameter in _parameters) ordered.Add(parameter.name, parameter);
                this.parameters = new types.MappingProxyType(ordered);
            }

            internal Signature BindReceiver() => _parameters.Length > 0 &&
                _parameters[0].kind is _ParameterKind.POSITIONAL_ONLY or _ParameterKind.POSITIONAL_OR_KEYWORD
                    ? new(_parameters[1..], (object?)return_annotation) : this;

            public override string ToString()
            {
                var parts = new List<string>();
                bool star = false;
                for (int i = 0; i < _parameters.Length; i++)
                {
                    var parameter = _parameters[i];
                    if (parameter.kind == _ParameterKind.VAR_POSITIONAL) star = true;
                    if (parameter.kind == _ParameterKind.KEYWORD_ONLY && !star)
                    {
                        parts.Add("*");
                        star = true;
                    }
                    parts.Add(parameter.ToString());
                    if (parameter.kind == _ParameterKind.POSITIONAL_ONLY &&
                        (i + 1 == _parameters.Length || _parameters[i + 1].kind != _ParameterKind.POSITIONAL_ONLY)) parts.Add("/");
                }
                return $"({string.Join(", ", parts)})" +
                    (ReferenceEquals((object?)return_annotation, empty) ? "" : " -> " + FormatAnnotation((object?)return_annotation));
            }

            public string __repr__() => $"<Signature {this}>";
        }

        internal static string FormatAnnotation(object? annotation) => annotation switch
        {
            Type type when type == typeof(object) => "object",
            Type type when type == typeof(Type) => "type",
            Type type when type == typeof(System.Runtime.CompilerServices.ITuple) => "tuple",
            Type type => Builtins.PyTypeName(type),
            _ => Builtins.repr(annotation),
        };

        public static Signature signature(object? obj, [Optional, DefaultParameterValue(true)] bool follow_wrapped,
            [PyKeywordOnly, Optional, DefaultParameterValue(1L)] object? annotation_format)
        {
            if (Builtins.eq(annotation_format, 2L))
                throw new ArgumentException("The VALUE_WITH_FAKE_GLOBALS format is for internal use only");
            bool strings = Builtins.eq(annotation_format, 4L);
            if (!strings && !Builtins.eq(annotation_format, 1L) && !Builtins.eq(annotation_format, 3L))
                throw new ArgumentException($"Unsupported format {Builtins.repr(annotation_format)}");

            var result = ReadSignature(obj, follow_wrapped);
            if (!strings) return result;

            static object? AnnotationString(object? value) =>
                ReferenceEquals(value, Signature.empty) || value is string ? value : FormatAnnotation(value);
            return new Signature(result.Arguments.Select(parameter => new Parameter(parameter.name, parameter.kind,
                (object?)parameter.@default, AnnotationString((object?)parameter.annotation))).ToArray(),
                AnnotationString((object?)result.return_annotation));
        }

        static Signature ReadSignature(object? obj, bool follow_wrapped)
        {
            if (!Builtins.callable(obj)) throw new InvalidCastException($"{Builtins.repr(obj)} is not a callable object");
            if (obj is types.MethodType method) return ReadSignature(method.__func__, follow_wrapped).BindReceiver();
            if (follow_wrapped)
                obj = unwrap(obj, value => HasExplicitSignature(value));
            object? custom = Builtins.getattr(obj, "__signature__", null);
            if (custom is Signature overridden) return overridden;
            if (custom != null) throw new InvalidCastException($"unexpected object {Builtins.repr(custom)} in __signature__ attribute");
            if (obj is PyFunction { _signature: { } factory }) return factory();
            if (obj is PyBoundMethod bound &&
                SourceSignature(bound.Target as Type ?? bound.Target.GetType(), bound.Name,
                    bound.Target is not Type, out var boundSignature)) return boundSignature;
            if (obj is Delegate compiled && PySignatureAttribute.Of(compiled.Method) is { } provider)
                return compiled.Method.IsStatic ? provider() : provider().BindReceiver();
            if (obj is Type type && type.IsDefined(typeof(PySourceAttribute)))
            {
                for (Type? current = type; current != null; current = current.BaseType)
                    if (PySignatureAttribute.Find(current, PySignatureAttribute.ConstructorProvider) is { } constructor)
                        return constructor().BindReceiver();
                if (type.GetConstructor(Type.EmptyTypes) != null) return new([], Signature.empty);
            }
            if (obj != null && obj is not Type && SourceSignature(obj.GetType(), "__call__", true, out var callSignature))
                return callSignature;
            throw new ArgumentException($"no signature found for {Builtins.repr(obj)}");
        }

        static bool HasExplicitSignature(object value) =>
            !ReferenceEquals(Builtins.getattr(value, "__signature__", Missing), Missing);

        static bool SourceSignature(Type owner, string name, bool isBound, out Signature result)
        {
            foreach (var method in owner.GetMethods(PublicMembers | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy))
            {
                if (method.Name != name || PySignatureAttribute.Of(method) is not { } provider) continue;
                result = provider();
                if (isBound && !method.IsStatic || method.GetCustomAttribute<PyDescriptorAttribute>()?.Kind == PyDescriptorKind.ClassMethod)
                    result = result.BindReceiver();
                return true;
            }
            result = null!;
            return false;
        }
    }
}
