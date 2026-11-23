// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static class _typing
    {
        public static readonly NoDefaultType NoDefault = new();
        public static readonly Type Union = typeof(types.UnionType);
        public static object? _idfunc(object? value) => value;

        public static OrderedDictionary<string, object?> get_type_hints(object? obj,
            object? globalns = null, object? localns = null, bool include_extras = false,
            [PyKeywordOnly] object? format = null)
        {
            bool forward = Builtins.eq(format, 3L);
            bool strings = Builtins.eq(format, 4L);
            if (format != null && !forward && !strings && !Builtins.eq(format, 1L))
                throw new ArgumentException($"Unsupported annotation format {Builtins.repr(format)}");

            var hints = new OrderedDictionary<string, object?>();
            if (Builtins.truthy((object?)Builtins.getattr(obj, "__no_type_check__", null))) return hints;

            if (obj is Type cls)
            {
                foreach (Type basis in PyMetaclasses.MethodOrder(cls).Reverse())
                {
                    var module = (object?)Builtins.getattr(basis, "__module__", null) is string name &&
                        sys_.modules.TryGetValue(name, out var imported) ? (object?)imported : null;
                    object globals = globalns ?? AnnotationGlobals(module, "__dict__");
                    object locals = localns ?? AnnotationGlobals(basis, "__dict__");
                    if (localns == null && globalns == null) (globals, locals) = (locals, globals);
                    ReadHints(basis, globals, locals);
                }
            }
            else
            {
                object? unwrapped = obj == null ? null : (object?)inspect.unwrap(obj);
                object globals = globalns ?? (unwrapped is PyModule
                    ? AnnotationGlobals(unwrapped, "__dict__") : AnnotationGlobals(unwrapped, "__globals__"));
                ReadHints(obj, globals, localns ?? globals);
                if (hints.Count == 0 && obj is not PyModule && !Builtins.callable(obj) &&
                    !Builtins.hasattr(obj, "__annotations__") && !Builtins.hasattr(obj, "__annotate__"))
                    throw new InvalidCastException($"{Builtins.repr(obj)} is not a module, class, or callable");
            }
            return hints;

            void ReadHints(object? owner, object globals, object locals)
            {
                object? annotations = Builtins.getattr(owner, "__annotations__", null);
                object? annotate = Builtins.getattr(owner, "__annotate__", null);
                if (annotate != null && (strings || forward || annotations == null))
                    annotations = Builtins.__invoke(annotate, [format ?? 1L]);
                if (annotations == null) return;
                if (annotations is not IDictionary members)
                    throw new InvalidCastException("__annotations__ must be a dictionary");
                var entries = members.GetEnumerator();
                while (entries.MoveNext())
                {
                    if (entries.Key is not string name) throw new InvalidCastException("annotation names must be strings");
                    object? value = entries.Value;
                    if (strings)
                        value = annotate != null && value is string source ? source : inspect.FormatAnnotation(value);
                    else
                    {
                        var visited = new HashSet<string>();
                        while (value is string reference && visited.Add(reference))
                        {
                            try { value = Builtins.eval(reference, globals, locals); }
                            catch (NameError) when (forward) { value = ForwardReference(reference); }
                        }
                        value ??= typeof(types.NoneType);
                        if (!include_extras) value = StripAnnotationMetadata(value);
                    }
                    hints[name] = value;
                }
            }
        }

        static object AnnotationGlobals(object? owner, string attribute)
        {
            object? names = Builtins.getattr(owner, attribute, null);
            if (names is IDictionary dictionary) return dictionary;
            if (names is types.MappingProxyType proxy)
            {
                var result = new OrderedDictionary<string, object?>();
                foreach (var (key, value) in proxy.items())
                    if (key is string name) result[name] = value;
                return result;
            }
            return new OrderedDictionary<string, object?>();
        }

        static object? StripAnnotationMetadata(object? annotation)
        {
            if ((object?)Builtins.getattr(annotation, "__metadata__", null) != null)
                return StripAnnotationMetadata((object?)Builtins.getattr(annotation, "__origin__"));
            if (annotation is types.GenericAlias alias)
            {
                var arguments = Enumerable.Range(0, alias.__args__.Count)
                    .Select(index => StripAnnotationMetadata(alias.__args__[index])).ToArray();
                return new types.GenericAlias(alias.__origin__, new PyTuple<object?>(arguments));
            }
            return annotation;
        }

        internal class Any
        {
            public Any()
            {
                if (GetType() == typeof(Any))
                    throw new InvalidCastException("Any cannot be instantiated");
            }
        }

        internal sealed class NoDefaultType : IPyRepr
        {
            internal NoDefaultType() { }
            public string __repr__() => "typing.NoDefault";
            public override string ToString() => __repr__();
        }

        internal abstract class TypeParameter : IPyRepr
        {
            public readonly string __name__;
            public string? __module__;
            public readonly object? __default__;
            readonly string _prefix;

            protected TypeParameter(string name, object? defaultValue, string prefix)
            {
                __name__ = name ?? throw new InvalidCastException("type parameter name must be a string");
                __module__ = sys_._getframemodulename();
                __default__ = ReferenceEquals(defaultValue, Type.Missing) ? NoDefault : defaultValue;
                _prefix = prefix;
            }

            public bool has_default() => !ReferenceEquals(__default__, NoDefault);
            public string __repr__() => _prefix + __name__;
            public override string ToString() => __repr__();
        }

        static string Variance(bool covariant, bool contravariant, bool infer)
        {
            if (covariant && contravariant) throw new ArgumentException("Bivariant types are not supported.");
            if (infer && (covariant || contravariant)) throw new ArgumentException("Variance cannot be specified with infer_variance.");
            return infer ? "" : covariant ? "+" : contravariant ? "-" : "~";
        }

        static object? ForwardReference(object? annotation)
        {
            if (annotation is not string text) return annotation;
            object module = SourceImport.ImportModule("annotationlib");
            return Builtins.__invoke((object?)Builtins.getattr(module, "ForwardRef"), [text]);
        }

        internal sealed class TypeVar : TypeParameter
        {
            public readonly object? __bound__;
            public readonly PyTuple<object?> __constraints__;
            public readonly bool __covariant__, __contravariant__, __infer_variance__;

            public TypeVar(string name, [PyPackedArguments] object?[] constraints,
                [Optional, DefaultParameterValue(null)] object? bound,
                [Optional, DefaultParameterValue(false)] bool covariant,
                [Optional, DefaultParameterValue(false)] bool contravariant,
                [Optional, DefaultParameterValue(false)] bool infer_variance,
                [Optional] object? @default, [PyKeywordArguments, Optional] IDictionary<string, object?>? __kwargs)
                : base(name, @default, Variance(covariant, contravariant, infer_variance))
            {
                if (__kwargs?.Count > 0) throw new InvalidCastException($"'{__kwargs.Keys.First()}' is an invalid keyword argument for typevar()");
                if (constraints.Length == 1) throw new InvalidCastException("A single constraint is not allowed");
                if (constraints.Length > 0 && bound != null) throw new InvalidCastException("Constraints cannot be combined with bound=...");
                __bound__ = ForwardReference(bound);
                __constraints__ = new PyTuple<object?>(constraints.Select(ForwardReference));
                (__covariant__, __contravariant__, __infer_variance__) = (covariant, contravariant, infer_variance);
            }

            public object? __typing_subst__(object? argument) => ForwardReference(argument) ?? typeof(types.NoneType);
        }

        internal sealed class ParamSpec : TypeParameter
        {
            public readonly object? __bound__;
            public readonly bool __covariant__, __contravariant__, __infer_variance__;
            public ParamSpecArgs args => new(this);
            public ParamSpecKwargs kwargs => new(this);

            public ParamSpec(string name, [Optional, DefaultParameterValue(null)] object? bound,
                [Optional, DefaultParameterValue(false)] bool covariant, [Optional, DefaultParameterValue(false)] bool contravariant,
                [Optional, DefaultParameterValue(false)] bool infer_variance, [Optional] object? @default)
                : base(name, @default, Variance(covariant, contravariant, infer_variance))
            {
                __bound__ = ForwardReference(bound);
                (__covariant__, __contravariant__, __infer_variance__) = (covariant, contravariant, infer_variance);
            }
        }

        internal sealed class ParamSpecArgs(object? origin) : IPyRepr
        {
            public readonly object? __origin__ = origin;
            public string __repr__() => (__origin__ is ParamSpec parameter ? parameter.__name__ : Builtins.repr(__origin__)) + ".args";
            public override string ToString() => __repr__();
            public override bool Equals(object? other) => other is ParamSpecArgs args && Builtins.eq(__origin__, args.__origin__);
            public override int GetHashCode() => throw new InvalidCastException("unhashable type: 'ParamSpecArgs'");
        }

        internal sealed class ParamSpecKwargs(object? origin) : IPyRepr
        {
            public readonly object? __origin__ = origin;
            public string __repr__() => (__origin__ is ParamSpec parameter ? parameter.__name__ : Builtins.repr(__origin__)) + ".kwargs";
            public override string ToString() => __repr__();
            public override bool Equals(object? other) => other is ParamSpecKwargs args && Builtins.eq(__origin__, args.__origin__);
            public override int GetHashCode() => throw new InvalidCastException("unhashable type: 'ParamSpecKwargs'");
        }

        internal sealed class TypeVarTuple(string name, [Optional] object? @default) : TypeParameter(name, @default, "~")
        {
            public PyIterator<object?> __iter__()
            {
                object typingModule = SourceImport.ImportModule("typing");
                return Builtins.iter<object?>([Builtins.__getitem((object?)Builtins.getattr(typingModule, "Unpack"), this)]);
            }
        }

        internal class Generic
        {
            protected Generic() { }

            static PyTuple<object?>? _parameters;
            static readonly PyClosure InitializeSubclass = new("__init_subclass__", (args, keywords) =>
            {
                if (args.Length != 1 || args[0] is not Type cls)
                    throw new InvalidCastException("Generic.__init_subclass__() takes no positional arguments");
                if (cls == typeof(Generic))
                {
                    if (keywords?.Count > 0)
                        throw new InvalidCastException("Generic.__init_subclass__() takes no keyword arguments");
                    _parameters = PyTuple<object?>.Empty;
                    return null;
                }
                if (sys_.modules.TryGetValue("typing", out object? typing) &&
                    (object?)Builtins.getattr(typing, "_generic_init_subclass", null) is object initialize)
                    return Builtins.__invoke_kw(initialize, args, keywords);
                var parent = new builtins.super(typeof(Generic), cls);
                if (parent.TryResolve("__init_subclass__", out var hook)) Builtins.__invoke_kw(hook, [], keywords);
                if ((object?)Builtins.getattr(cls, "__type_params__", null) is not PyTuple<object?> parameters)
                    throw new InvalidCastException("Cannot inherit from plain Generic");
                Builtins.setattr(cls, "__parameters__", parameters);
                return null;
            });

            public static types.MethodType __init_subclass__ => new(InitializeSubclass, typeof(Generic));

            public static PyTuple<object?> __parameters__ =>
                _parameters ?? throw new MissingMemberException("type object 'Generic' has no attribute '__parameters__'");

            internal static bool TryGetParameters(out object? value)
            {
                value = _parameters;
                return value != null;
            }

            public static types.GenericAlias __class_getitem__(object? args) => new(typeof(Generic), args);
        }

        internal sealed class TypeAliasType : IPyRepr
        {
            public readonly string __name__;
            public readonly string? __module__;
            public readonly object? __value__;
            public readonly PyTuple<object?> __type_params__;
            public readonly PyTuple<object?> __parameters__;

            public TypeAliasType(string name, object? value, [Optional] object? type_params)
            {
                __name__ = name ?? throw new InvalidCastException("type alias name must be a string");
                __module__ = sys_._getframemodulename();
                __value__ = value;
                if (ReferenceEquals(type_params, Type.Missing)) __type_params__ = new PyTuple<object?>(Array.Empty<object?>());
                else if (type_params is ITuple tuple)
                    __type_params__ = new PyTuple<object?>(Enumerable.Range(0, tuple.Length).Select(i => tuple[i]));
                else throw new InvalidCastException("type_params must be a tuple");
                foreach (object? parameter in __type_params__)
                    if (parameter is not TypeParameter) throw new InvalidCastException($"Expected a type param, got {Builtins.repr(parameter)}");
                __parameters__ = __type_params__;
            }

            public types.GenericAlias __getitem__(object? args)
            {
                if (__type_params__.Count == 0) throw new InvalidCastException("Only generic type aliases are subscriptable");
                return new types.GenericAlias(this, args);
            }

            public string __repr__() => __name__;
            public override string ToString() => __repr__();
        }

        internal static PyTuple<object?> CollectParameters(IEnumerable<object?> arguments)
        {
            var result = new List<object?>();
            foreach (object? argument in arguments)
            {
                if (argument is TypeParameter parameter) Add(parameter);
                else if (argument is types.GenericAlias alias)
                    foreach (var nested in alias.__parameters__) Add(nested);
                else if (argument is types.UnionType union)
                    foreach (var nested in union.__parameters__) Add(nested);
            }
            return new PyTuple<object?>(result);

            void Add(object? parameter)
            {
                if (!result.Contains(parameter)) result.Add(parameter);
            }
        }
    }
}
