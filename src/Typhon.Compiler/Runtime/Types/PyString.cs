// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;

    /// <summary>The immutable text payload held alongside a compiled str subclass's Python fields.</summary>
    internal class PyString : IPyLen, IPyRepr, IEnumerable<string>, IComparable
    {
        internal string Text { get; private set; } = "";

        internal static PyString Allocate(Type cls, string text)
        {
            if (!typeof(PyString).IsAssignableFrom(cls))
                throw new InvalidCastException("str.__new__ requires a string subtype");
            var value = (PyString)RuntimeHelpers.GetUninitializedObject(cls);
            value.Text = text;
            return value;
        }

        public static object __new__(Type cls) => Allocate(cls, "");
        public static object __new__(Type cls, object? value) => Allocate(cls, Builtins.str(value));
        public static object __new__(Type cls, object? value, string encoding, string errors = "strict") =>
            Allocate(cls, Builtins.str(value, encoding, errors));

        public string this[long index] => Builtins.__getitem(Text, index)!;
        public string this[PySlice slice] => Builtins.__getitem(Text, slice)!;
        public long __len__() => Builtins.len(Text);
        public IEnumerator<string> __iter__() => GetEnumerator();
        public IEnumerator<string> GetEnumerator() => Builtins.__iter_dyn(Text).Cast<string>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static implicit operator string(PyString value) => value.Text;
        public override string ToString() => Text;
        public string __repr__() => Builtins.repr(Text);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);
        public override bool Equals(object? other) => other switch
        {
            PyString text => Text == text.Text,
            PyStrEnum member => Text == member.value,
            string text => Text == text,
            _ => false,
        };
        public int CompareTo(object? other) => other switch
        {
            null => 1,
            PyString text => string.CompareOrdinal(Text, text.Text),
            PyStrEnum member => string.CompareOrdinal(Text, member.value),
            string text => string.CompareOrdinal(Text, text),
            _ => throw new InvalidCastException("strings can only be compared with strings"),
        };
    }
}
