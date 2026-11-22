// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static partial class ast
    {
        public static string unparse(AST node) => PrintExpression(node);

        static string PrintExpression(AST? node, int parentPrecedence = 0)
        {
            if (node == null) return "";
            object? Field(string name) => node.Fields[name];
            AST? Child(string name) => Field(name) as AST;
            List<object?> Children(string name) => (List<object?>)Field(name)!;
            string Text(string name, int precedence = 0) => PrintExpression(Child(name), precedence);
            IEnumerable<string> Texts(string name, int precedence = 0) => Children(name).Cast<AST>().Select(child => PrintExpression(child, precedence));
            string Sequence(string name) => string.Join(", ", Texts(name));

            string kind = node.GetType().Name;
            int power = 100;
            string text;
            switch (kind)
            {
                case "Expression":
                case "Expr": text = Text(kind == "Expression" ? "body" : "value"); break;
                case "Name": text = (string)Field("id")!; break;
                case "Constant": text = Builtins.repr(Field("value")); break;
                case "Attribute":
                    // Python requires a space between an integer literal and the following period.
                    bool needsSpace = Child("value") is Constant constant && constant.Fields["value"] is long or System.Numerics.BigInteger;
                    text = Text("value", 95) + (needsSpace ? " ." : ".") + (string)Field("attr")!;
                    break;
                case "Subscript":
                    var slice = Child("slice");
                    string index = slice is Tuple ? PrintTupleItems(slice) : PrintExpression(slice);
                    text = Text("value", 95) + "[" + index + "]";
                    break;
                case "Slice":
                    text = Text("lower") + ":" + Text("upper") + (Child("step") == null ? "" : ":" + Text("step"));
                    break;
                case "Tuple": text = "(" + PrintTupleItems(node) + ")"; break;
                case "List": text = "[" + Sequence("elts") + "]"; break;
                case "Set": text = "{" + Sequence("elts") + "}"; break;
                case "Dict":
                    text = "{" + string.Join(", ", Children("keys").Zip(Texts("values"),
                        (key, value) => key == null ? "**" + value : PrintExpression((AST)key) + ": " + value)) + "}";
                    break;
                case "Call":
                    text = Text("func", 95) + "(" + string.Join(", ", Texts("args").Concat(Texts("keywords"))) + ")";
                    break;
                case "keyword": text = (Field("arg") is string name ? name + "=" : "**") + Text("value"); break;
                case "Starred": text = "*" + Text("value", 85); break;
                case "BinOp":
                    var (binary, strength) = OperatorText(Child("op")!);
                    power = strength;
                    text = Text("left", power + (binary == "**" ? 1 : 0)) + " " + binary + " " +
                        Text("right", power + (binary == "**" ? 0 : 1));
                    break;
                case "UnaryOp":
                    var (unary, degree) = OperatorText(Child("op")!);
                    power = degree;
                    text = unary + Text("operand", power);
                    break;
                case "BoolOp":
                    var (boolean, rank) = OperatorText(Child("op")!);
                    power = rank;
                    text = string.Join(" " + boolean + " ", Texts("values", power + 1));
                    break;
                case "Compare":
                    power = 30;
                    text = Text("left", power + 1) + string.Concat(Children("ops").Cast<AST>().Zip(Texts("comparators", power + 1),
                        (op, value) => " " + OperatorText(op).Text + " " + value));
                    break;
                case "IfExp":
                    power = 10;
                    text = Text("body", power + 1) + " if " + Text("test", power + 1) + " else " + Text("orelse", power);
                    break;
                case "NamedExpr":
                    text = "(" + Text("target") + " := " + Text("value") + ")";
                    break;
                case "Await":
                    power = 90;
                    text = "await " + Text("value", power);
                    break;
                case "Yield":
                case "YieldFrom":
                    power = 2;
                    text = kind == "YieldFrom" ? "yield from " + Text("value") : Child("value") == null ? "yield" : "yield " + Text("value");
                    break;
                case "GeneratorExp":
                case "ListComp":
                case "SetComp":
                case "DictComp":
                    string opening = kind == "ListComp" ? "[" : kind == "GeneratorExp" ? "(" : "{";
                    string closing = kind == "ListComp" ? "]" : kind == "GeneratorExp" ? ")" : "}";
                    string element = kind == "DictComp" ? Text("key") + ": " + Text("value") : Text("elt");
                    text = opening + element + string.Concat(Texts("generators")) + closing;
                    break;
                case "comprehension":
                    text = ((long)Field("is_async")! == 0 ? " for " : " async for ") + Text("target") + " in " + Text("iter", 11) +
                        string.Concat(Texts("ifs", 11).Select(test => " if " + test));
                    break;
                default: throw new NotSupportedException($"ast.unparse() does not support {kind} nodes");
            }
            return power < parentPrecedence ? "(" + text + ")" : text;
        }

        static string PrintTupleItems(AST tuple)
        {
            var items = (List<object?>)tuple.Fields["elts"]!;
            string text = string.Join(", ", items.Cast<AST>().Select(child => PrintExpression(child)));
            return items.Count == 1 ? text + "," : text;
        }

        static (string Text, int Power) OperatorText(AST op) => op.GetType().Name switch
        {
            "Or" => ("or", 15), "And" => ("and", 20), "Not" => ("not ", 25),
            "Eq" => ("==", 30), "NotEq" => ("!=", 30), "Lt" => ("<", 30), "LtE" => ("<=", 30),
            "Gt" => (">", 30), "GtE" => (">=", 30), "Is" => ("is", 30), "IsNot" => ("is not", 30),
            "In" => ("in", 30), "NotIn" => ("not in", 30),
            "BitOr" => ("|", 40), "BitXor" => ("^", 45), "BitAnd" => ("&", 50), "LShift" => ("<<", 55), "RShift" => (">>", 55),
            "Add" => ("+", 60), "Sub" => ("-", 60),
            "Mult" => ("*", 70), "MatMult" => ("@", 70), "Div" => ("/", 70), "FloorDiv" => ("//", 70), "Mod" => ("%", 70),
            "UAdd" => ("+", 80), "USub" => ("-", 80), "Invert" => ("~", 80), "Pow" => ("**", 85),
            _ => throw new NotSupportedException(op.GetType().Name),
        };
    }
}
