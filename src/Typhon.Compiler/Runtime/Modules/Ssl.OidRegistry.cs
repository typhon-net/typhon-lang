// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Numerics;
    using Typhon.Runtime;

    internal static partial class _ssl
    {
        public static PyTuple<object?> nid2obj([PyPositionalOnly] object? nid)
        {
            object index = Builtins.__index_value(nid);
            int number;
            try { number = index is long small ? checked((int)small) : checked((int)(BigInteger)index); }
            catch (OverflowException) { throw new OverflowException("Python int too large to convert to C int"); }

            if (number < 0) throw new ArgumentException("NID must be positive.");
            if (number == 0) throw new ArgumentException("Unknown object");
            return OidRegistry.ByNumber.TryGetValue(number, out var entry)
                ? entry.Tuple() : throw new ArgumentException($"unknown NID {number}");
        }

        public static PyTuple<object?> txt2obj(object? txt, object? name = null)
        {
            string text = txt switch
            {
                string value => value,
                PyString value => value.Text,
                _ => throw new InvalidCastException($"txt2obj() argument 'txt' must be str, not {Builtins.PyTypeName(Builtins.__type_of(txt))}"),
            };
            if (text.Contains('\0')) throw new ArgumentException("embedded null character");
            if (Builtins.truthy(name) && OidRegistry.ByName.TryGetValue(text, out var named)) return named.Tuple();
            if (OidRegistry.ByOid.TryGetValue(text, out var exact)) return exact.Tuple();
            string? oid = CanonicalOid(text);
            if (oid == null) throw new ArgumentException($"unknown object '{text}'");
            return OidRegistry.ByOid.TryGetValue(oid, out var entry)
                ? entry.Tuple() : throw new ArgumentException("Unknown object");
        }

        static string? CanonicalOid(string text)
        {
            if (text.Length < 3 || text[0] is < '0' or > '2' || text[1] is not ('.' or ' ')) return null;
            string[] arcs = text.Split('.', ' ');
            int count = arcs[^1].Length == 0 ? arcs.Length - 1 : arcs.Length;
            for (int index = 1; index < count; index++)
            {
                foreach (char digit in arcs[index])
                    if (digit is < '0' or > '9') return null;
                string arc = arcs[index].TrimStart('0');
                arcs[index] = arc.Length == 0 ? "0" : arc;
            }
            if (count < 2 || text[0] != '2' && (arcs[1].Length > 2 || int.Parse(arcs[1], CultureInfo.InvariantCulture) > 39))
                return null;
            return string.Join(".", arcs, 0, count);
        }

        sealed record OidEntry(long Number, string ShortName, string LongName, string? Oid)
        {
            internal PyTuple<object?> Tuple() => new(new object?[] { Number, ShortName, LongName, Oid });
        }

        static class OidRegistry
        {
            internal static readonly Dictionary<int, OidEntry> ByNumber = [];
            internal static readonly Dictionary<string, OidEntry> ByName = new(StringComparer.Ordinal);
            internal static readonly Dictionary<string, OidEntry> ByOid = new(StringComparer.Ordinal);

            static OidRegistry()
            {
                foreach (string row in OidRegistryData.Split('\n'))
                {
                    string[] cells = row.TrimEnd('\r').Split('\t');
                    int number = int.Parse(cells[0], CultureInfo.InvariantCulture);
                    var entry = new OidEntry(number, cells[1], cells[2], cells[3].Length > 0 ? cells[3] : null);
                    ByNumber.Add(number, entry);
                    if (cells[4].Contains('S')) ByName.TryAdd(entry.ShortName, entry);
                    if (cells[4].Contains('L')) ByName.TryAdd(entry.LongName, entry);
                    if (cells[4].Contains('O')) ByOid.Add(entry.Oid!, entry);
                }
            }
        }
    }
}
