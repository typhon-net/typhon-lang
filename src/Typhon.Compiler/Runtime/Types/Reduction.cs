// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    internal static partial class Builtins
    {
        public static object? __object_reduce(object value) => DefaultReduction(value, 0);

        public static object? __object_reduce_ex(object value, object? protocol)
        {
            int level = checked((int)__index_value(protocol));
            Type type = __type_of(value);
            object? classReducer = getattr(type, "__reduce__");
            if (!ReferenceEquals(classReducer, __object_method("__object_reduce")))
                return __invoke((object?)getattr(value, "__reduce__"), []);
            return DefaultReduction(value, level);
        }

        static object? CopyregCallable(string name)
        {
            if (Typhon.Stdlib.sys_.modules.TryGetValue("copyreg", out var module) &&
                TryGetOptionalAttr((object?)module, name, out var member))
                return member;
            return PyModule.Of(typeof(Typhon.Stdlib.copyreg), "copyreg").ReadGlobal(name);
        }

        static object? DefaultReduction(object value, int protocol)
        {
            if (protocol < 2)
                return (object?)__invoke(CopyregCallable("_reduce_ex"), [value, (long)protocol]);

            object? arguments = null;
            IDictionary? keywords = null;
            if (TryGetOptionalAttr(value, "__getnewargs_ex__", out var getExtended))
            {
                object? result = __invoke(getExtended, []);
                if (result is not ITuple pair || pair.Length != 2 || pair[0] is not ITuple || pair[1] is not IDictionary mapping)
                    throw new InvalidCastException("__getnewargs_ex__ should return a tuple of arguments and a dict of keyword arguments");
                arguments = pair[0];
                keywords = mapping;
            }
            else if (TryGetOptionalAttr(value, "__getnewargs__", out var getArguments))
            {
                arguments = __invoke(getArguments, []);
                if (arguments is not ITuple) throw new InvalidCastException("__getnewargs__ should return a tuple");
            }
            else if (AsInt(value, out long integer))
                arguments = new PyTuple<object?>([integer]);
            else if (value is BigInteger or string)
                arguments = new PyTuple<object?>([value]);

            Type type = __type_of(value);
            bool withKeywords = keywords is { Count: > 0 };
            object? constructor = CopyregCallable(withKeywords ? "__newobj_ex__" : "__newobj__");
            object?[] args = withKeywords ? [type, arguments, keywords] :
                arguments == null ? [type] : [type, .. __iter_dyn(arguments)];
            object? state = __invoke((object?)getattr(value, "__getstate__"), []);
            object? listItems = value is IList and not IDictionary ? iter(value) : null;
            object? dictItems = value is IDictionary ? iter(__invoke((object?)getattr(value, "items"), [])) : null;
            return (constructor, new PyTuple<object?>(args), state, listItems, dictItems);
        }

        internal static object LegacyReduction(object value)
        {
            Type type = __type_of(value);
            Type basis = PyMetaclasses.MethodOrder(type).FirstOrDefault(candidate => candidate is not PyLocalDataClass &&
                !candidate.IsDefined(typeof(PySourceAttribute), false)) ?? typeof(object);
            if (basis == type && basis != typeof(object))
                throw new InvalidCastException($"cannot pickle {repr(PyTypeName(type))} object");
            object? baseState = basis == typeof(object) ? null : __invoke(basis, [value]);
            var args = new PyTuple<object?>([type, basis, baseState]);
            if (ReferenceEquals((object?)getattr(type, "__getstate__"), __object_method("__object_getstate")) &&
                truthy((object?)getattr(value, "__slots__", null)))
                throw new InvalidCastException("a class that defines __slots__ without defining __getstate__ cannot be pickled");
            object? state = __invoke((object?)getattr(value, "__getstate__"), []);
            object? constructor = CopyregCallable("_reconstructor");
            return truthy(state) ? (object)(constructor, args, state) : (constructor, args);
        }
    }
}
