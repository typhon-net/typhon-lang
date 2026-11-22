// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Keep the Python module name.

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Globalization;
    using System.IO;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Typhon.Runtime;

    /// <summary>The CPython marshal wire format for ordinary data objects, including profiler stats.</summary>
    internal static class marshal
    {
        public const long version = 5;

        public static PyBytes dumps(object? value, long version = marshal.version, bool allow_code = true)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            Write(writer, value, version, depth: 0);
            return new PyBytes(output.ToArray());
        }

        public static void dump(object? value, object file, long version = marshal.version, bool allow_code = true) =>
            Builtins.__invoke((object?)Builtins.getattr(file, "write"), [dumps(value, version, allow_code)]);

        static void Write(BinaryWriter writer, object? value, long version, int depth)
        {
            if (depth > 500) throw new ArgumentException("object too deeply nested to marshal");
            void Type(char tag) => writer.Write((byte)tag);
            void Blob(char tag, byte[] bytes) { Type(tag); writer.Write(bytes.Length); writer.Write(bytes); }
            void Item(object? child) => Write(writer, child, version, depth + 1);
            void Integral(BigInteger number)
            {
                if (number >= int.MinValue && number <= int.MaxValue) { Type('i'); writer.Write((int)number); return; }
                Type('l');
                var abs = BigInteger.Abs(number);
                int digits = checked((int)((abs.GetBitLength() + 14) / 15));
                writer.Write(number.Sign * digits);
                for (int i = 0; i < digits; i++) { writer.Write((ushort)(abs & 0x7fff)); abs >>= 15; }
            }

            switch (value)
            {
                case null: Type('N'); break;
                case bool flag: Type(flag ? 'T' : 'F'); break;
                case int number: Integral(number); break;
                case long number: Integral(number); break;
                case BigInteger number: Integral(number); break;
                case double number:
                    if (version >= 2) { Type('g'); writer.Write(number); }
                    else
                    {
                        Type('f');
                        var bytes = Encoding.ASCII.GetBytes(number.ToString("G17", CultureInfo.InvariantCulture).ToLowerInvariant());
                        writer.Write((byte)bytes.Length);
                        writer.Write(bytes);
                    }
                    break;
                case string text: Blob('u', Encoding.UTF8.GetBytes(text)); break;
                case PyBytes bytes: Blob('s', bytes.Data); break;
                case ITuple tuple:
                    Type('('); writer.Write(tuple.Length);
                    for (int i = 0; i < tuple.Length; i++) Item(tuple[i]);
                    break;
                case IPyTuple tuple:
                    Type('('); writer.Write(tuple.Count);
                    for (int i = 0; i < tuple.Count; i++) Item(tuple[i]);
                    break;
                case IDictionary entries:
                    Type('{');
                    var iterator = entries.GetEnumerator();
                    while (iterator.MoveNext()) { Item(iterator.Key); Item(iterator.Value); }
                    Type('0');
                    break;
                case IList list:
                    Type('['); writer.Write(list.Count);
                    foreach (var child in list) Item(child);
                    break;
                default: throw new ArgumentException("unmarshallable object");
            }
        }
    }
}
