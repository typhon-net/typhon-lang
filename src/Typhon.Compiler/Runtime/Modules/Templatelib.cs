// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static partial class string_
    {
        /// <summary>string.templatelib: the values of template strings, t"..." (PEP 750, Python 3.14).</summary>
        internal static class templatelib
        {
            /// <summary>
            /// A t-string's value: its literal parts (always one more than the interpolations, possibly empty) and its
            /// interpolations, unformatted: what to do with them is up to the code that receives the template.
            /// </summary>
            internal sealed class Template : IEnumerable<object?>, IPyRepr
            {
                public readonly string[] strings;
                public readonly Interpolation[] interpolations;

                /// <summary>Template(*args): strings and interpolations in any order; adjacent strings are joined.</summary>
                public Template(params object?[] args)
                {
                    var ss = new List<string>(); var ins = new List<Interpolation>();
                    var cur = new System.Text.StringBuilder();
                    foreach (var a in args)
                    {
                        if (a is string s) cur.Append(s);
                        else if (a is Interpolation i) { ss.Add(cur.ToString()); cur.Clear(); ins.Add(i); }
                        else throw new InvalidCastException($"Template.__new__ *args need to be of type 'str' or 'Interpolation', got {Builtins.__name_of(a?.GetType())}");
                    }
                    ss.Add(cur.ToString());
                    strings = ss.ToArray(); interpolations = ins.ToArray();
                }

                /// <summary>The interpolations' values, in order.</summary>
                public object?[] values => interpolations.Select(i => i.value).ToArray();

                /// <summary>The non-empty strings and the interpolations, in order.</summary>
                public IEnumerator<object?> GetEnumerator()
                {
                    for (int i = 0; i < strings.Length; i++)
                    {
                        if (strings[i].Length > 0) yield return strings[i];
                        if (i < interpolations.Length) yield return interpolations[i];
                    }
                }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                public Template __add__(Template other)
                {
                    var parts = new List<object?>(this);
                    parts.AddRange(other);
                    return new Template(parts.ToArray());
                }
                public static Template operator +(Template a, Template b) => a.__add__(b);

                public string __repr__() =>
                    $"Template(strings={Builtins.repr(strings)}, interpolations={Builtins.repr(interpolations)})";
                public override string ToString() => __repr__();
            }

            /// <summary>One {expression!conversion:format_spec} of a t-string: its value and how it was written.</summary>
            internal sealed class Interpolation : IPyRepr
            {
                public readonly object? value;
                public readonly string expression;
                public readonly string? conversion;
                public readonly string format_spec;
                public Interpolation(object? value, string expression = "", string? conversion = null, string format_spec = "")
                {
                    if (conversion != null && conversion != "s" && conversion != "r" && conversion != "a")
                        throw new ArgumentException("Interpolation() argument 'conversion' must be one of 's', 'a' or 'r'");
                    this.value = value; this.expression = expression; this.conversion = conversion; this.format_spec = format_spec;
                }
                public string __repr__() =>
                    $"Interpolation({Builtins.repr(value)}, {Builtins.repr(expression)}, {(conversion == null ? "None" : Builtins.repr(conversion))}, {Builtins.repr(format_spec)})";
                public override string ToString() => __repr__();
            }
        }
    }
}
