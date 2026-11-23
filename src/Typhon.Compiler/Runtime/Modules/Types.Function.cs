// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    internal static partial class types
    {
        internal class FunctionType : PyFunction
        {
            readonly PyFunction _function = null!;
            readonly string[] _names = Array.Empty<string>();
            readonly object?[] _defaults = Array.Empty<object?>();
            readonly int _positional, _positionalOnly;
            readonly bool _varargs, _kwargs;
            public override IDictionary? __globals__ { get; }
            public override ITuple? __defaults__ { get; }
            public override IDictionary? __kwdefaults__ { get; }

            protected FunctionType() : base("<lambda>") { }

            public FunctionType(object? code, object? globals, string? name = null, object? argdefs = null,
                object? closure = null, object? kwdefaults = null) : base(name ?? (code as types.CodeType)?.co_name ?? "<function>")
            {
                _code = code as RuntimeFunctionCode ?? throw new InvalidCastException("function() requires a Typhon function code object");
                __globals__ = globals as IDictionary ?? throw new InvalidCastException("function() argument 'globals' must be dict");
                __defaults__ = argdefs == null ? null : argdefs as ITuple ?? throw new InvalidCastException("arg 4 (defaults) must be None or tuple");
                if (closure != null)
                {
                    if (closure is not ITuple cells) throw new InvalidCastException("arg 5 (closure) must be tuple");
                    if (cells.Length != 0) throw new ArgumentException($"{__name__} requires closure of length 0, not {cells.Length}");
                }
                __kwdefaults__ = kwdefaults == null ? null : kwdefaults as IDictionary ?? throw new InvalidCastException("kwdefaults must be a dict");
                __qualname__ = _code.co_qualname;
                __module__ = __globals__.Contains("__name__") ? (__globals__["__name__"] as string)! : null!;

                _function = ((RuntimeFunctionCode)_code).Bind(__globals__);
                _signature = _function._signature;
                __doc__ = _function.__doc__;
                var parameters = _signature!.Invoke().Arguments;
                _varargs = parameters.Any(p => p.kind == inspect._ParameterKind.VAR_POSITIONAL);
                _kwargs = parameters.Any(p => p.kind == inspect._ParameterKind.VAR_KEYWORD);
                _positionalOnly = parameters.Count(p => p.kind == inspect._ParameterKind.POSITIONAL_ONLY);
                _positional = _positionalOnly + parameters.Count(p => p.kind == inspect._ParameterKind.POSITIONAL_OR_KEYWORD);
                _names = parameters.Where(p => p.kind is not (inspect._ParameterKind.VAR_POSITIONAL or inspect._ParameterKind.VAR_KEYWORD))
                    .Select(p => p.name).ToArray();
                _defaults = Enumerable.Repeat<object?>(PyClosure.RequiredArgument, _names.Length).ToArray();
                if (__defaults__ is ITuple positionalDefaults)
                {
                    int used = Math.Min(positionalDefaults.Length, _positional);
                    for (int i = 1; i <= used; i++) _defaults[_positional - i] = positionalDefaults[positionalDefaults.Length - i];
                }
            }

            public override object? __call__(object?[] args) => __call_kw__(args, null);

            public override object? __call_kw__(object?[] args, IDictionary<string, object?>? kw)
            {
                var defaults = _defaults;
                if (__kwdefaults__ != null && _positional < _names.Length)
                {
                    defaults = (object?[])_defaults.Clone();
                    for (int i = _positional; i < _names.Length; i++)
                        if (__kwdefaults__.Contains(_names[i])) defaults[i] = __kwdefaults__[_names[i]];
                }
                var bound = PyClosure.BindArguments(__name__, _names, defaults, _positional, _positionalOnly,
                    args, kw, _varargs, _kwargs, out var keywords);
                if (_positional < _names.Length)
                {
                    keywords ??= new Dictionary<string, object?>();
                    for (int i = _positional; i < _names.Length; i++) keywords[_names[i]] = bound[i];
                }
                return _function.__call_kw__([.. bound.Take(_positional), .. bound.Skip(_names.Length)], keywords);
            }
        }
    }
}
