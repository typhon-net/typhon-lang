// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using Typhon.Runtime;

    internal static partial class ast
    {
        public static dynamic? literal_eval(object? node_or_string)
        {
            if (node_or_string is string text)
                node_or_string = CompileTree(text.TrimStart(' ', '\t'), "<unknown>", "eval", PyCF_ONLY_AST);
            if (node_or_string is Expression expression)
                node_or_string = expression.Fields.GetValueOrDefault("body");
            return EvaluateLiteral(node_or_string, 0);
        }

        static object? EvaluateLiteral(object? value, int depth)
        {
            if (value is not AST node || depth > 1000) throw InvalidLiteral(value);
            object? Field(string name) => node.Fields.GetValueOrDefault(name);
            List<object?> Items(string name) => Field(name) as List<object?> ?? throw InvalidLiteral(node);
            object? Convert(object? child) => EvaluateLiteral(child, depth + 1);

            switch (node)
            {
                case Constant when node.Fields.ContainsKey("value"):
                    return Field("value");
                case Tuple:
                    return new PyTuple<object?>(Items("elts").Select(Convert));
                case List:
                    return Items("elts").Select(Convert).ToList();
                case Set:
                    var set = Builtins.__newset<object?>();
                    foreach (object? element in Items("elts")) set.Add(Convert(element));
                    return set;
                case Dict:
                    var keys = Items("keys");
                    var values = Items("values");
                    if (keys.Count != values.Count) throw InvalidLiteral(node);
                    var mapping = Builtins.__newdict<object, object?>();
                    for (int index = 0; index < keys.Count; index++)
                        Builtins.__setitem(mapping, Convert(keys[index]), Convert(values[index]));
                    return mapping;
                case Call when Field("func") is Name name && Equals(name.Fields.GetValueOrDefault("id"), "set") &&
                    Items("args").Count == 0 && Items("keywords").Count == 0:
                    return Builtins.__newset<object?>();
                case UnaryOp when Field("op") is UAdd or USub &&
                    Field("operand") is Constant operand && operand.Fields.GetValueOrDefault("value") is object number &&
                    number is long or BigInteger or double or Complex:
                    return Field("op") is UAdd ? number : NegateLiteral(number);
                case BinOp when Field("op") is Add or Sub && Field("left") is Constant or UnaryOp &&
                    Field("right") is Constant right && right.Fields.GetValueOrDefault("value") is Complex imaginary:
                    double real = Convert(Field("left")) switch
                    {
                        long integer => integer,
                        BigInteger integer => (double)integer,
                        double floating => floating,
                        _ => throw InvalidLiteral(node),
                    };
                    return Field("op") is Add ? real + imaginary : real - imaginary;
                default:
                    throw InvalidLiteral(node);
            }
        }

        static object NegateLiteral(object number) => number switch
        {
            long value when value != long.MinValue => -value,
            long value => -(BigInteger)value,
            BigInteger value => -value,
            double value => -value,
            Complex value => -value,
            _ => throw InvalidLiteral(number),
        };

        static ArgumentException InvalidLiteral(object? value)
        {
            string location = value is AST node && node.Attributes.GetValueOrDefault("lineno") is long line && line != 0
                ? $" on line {line}" : "";
            return new ArgumentException($"malformed node or string{location}: {Builtins.repr(value)}");
        }
    }
}
