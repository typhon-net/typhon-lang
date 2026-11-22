// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    internal static partial class types
    {
        /// <summary>The same union class is exposed as types.UnionType and typing.Union.</summary>
        internal sealed class UnionType : IPyRepr
        {
            public readonly PyTuple<object?> __args__;
            public readonly PyTuple<object?> __parameters__ = new([]);
            public Type __origin__ => typeof(UnionType);

            UnionType(List<object?> members) => __args__ = new PyTuple<object?>(members.ToArray());

            internal static bool IsOperand(object? value) => value is Type or UnionType or GenericAlias;

            internal static object Combine(object? left, object? right)
            {
                if (!(IsOperand(left) || IsOperand(right)) ||
                    left != null && !IsOperand(left) || right != null && !IsOperand(right))
                {
                    string Name(object? value) => value == null ? "NoneType" : value is Type ? "type" : Builtins.PyTypeName(value.GetType());
                    throw new InvalidCastException($"unsupported operand type(s) for |: '{Name(left)}' and '{Name(right)}'");
                }
                return Create([left, right]);
            }

            internal static object Subscribe(object? arguments) =>
                Create(arguments switch
                {
                    IPyTuple tuple => Enumerable.Range(0, tuple.Count).Select(i => tuple[i]),
                    ITuple tuple => Enumerable.Range(0, tuple.Length).Select(i => tuple[i]),
                    _ => new[] { arguments },
                });

            static object Create(IEnumerable<object?> arguments)
            {
                var members = new List<object?>();
                var unique = new HashSet<object?>();
                void Add(object? member)
                {
                    if (member is UnionType union)
                    {
                        foreach (var item in union.__args__) Add(item);
                        return;
                    }
                    member ??= typeof(NoneType);
                    if (member is not Type && member is not GenericAlias)
                        throw new InvalidCastException($"Union requires types. Got {Builtins.repr(member)}.");
                    if (unique.Add(member)) members.Add(member);
                }
                foreach (var argument in arguments) Add(argument);
                return members.Count switch
                {
                    0 => throw new InvalidCastException("Cannot take a Union of no types."),
                    1 => members[0]!,
                    _ => new UnionType(members),
                };
            }

            public override bool Equals(object? obj) =>
                obj is UnionType other && new HashSet<object?>(__args__).SetEquals(other.__args__);

            public override int GetHashCode()
            {
                int hash = 0;
                foreach (var type in __args__) hash ^= type!.GetHashCode();
                return hash;
            }

            static string TypeName(object? member)
            {
                if (ReferenceEquals(member, typeof(NoneType))) return "None";
                return member is Type type ? Builtins.PythonQualifiedTypeName(type) : Builtins.repr(member);
            }

            public string __repr__() => string.Join(" | ", __args__.Select(TypeName));
            public override string ToString() => __repr__();
        }
    }
}

namespace Typhon.Runtime
{
    internal static partial class Builtins
    {
        public static dynamic __union__(object? left, object? right) => Typhon.Stdlib.types.UnionType.Combine(left, right);
    }
}
