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

        /// <summary>Read one value from a binary file, leaving it positioned at the start of the next value.</summary>
        public static dynamic? load(object file, bool allow_code = true) =>
            file is io.IOBase { __stream: Stream input } ? Read(input)
            : throw new InvalidCastException("marshal.load() expected a binary file");

        public static dynamic? loads(object bytes, bool allow_code = true)
        {
            using var input = new MemoryStream(ByteOps.From(bytes), writable: false);
            return Read(input);
        }

        static object? Read(Stream input)
        {
            using var binary = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            return new Reader(binary).Item();
        }

        sealed class Reader(BinaryReader binary)
        {
            static readonly object Terminator = new();
            readonly System.Collections.Generic.List<object?> references = new();

            public object? Item()
            {
                object? value = Value(0);
                if (ReferenceEquals(value, Terminator)) throw InvalidData();
                return value;
            }

            static ArgumentException InvalidData() => new("bad marshal data");

            object? Remember(int index, object? value)
            {
                if (index >= 0) references[index] = value;
                return value;
            }

            object? Value(int depth)
            {
                if (depth > 500) throw new ArgumentException("object too deeply nested to unmarshal");
                byte type = binary.ReadByte();
                int reference = -1;
                if ((type & 0x80) != 0)
                {
                    reference = references.Count;
                    references.Add(Terminator);
                    type &= 0x7f;
                }

                return (char)type switch
                {
                    '0' => Terminator,
                    'r' => Reference(),
                    '{' => Mapping(reference, depth),
                    '(' => Tuple(binary.ReadInt32(), reference, depth),
                    ')' => Tuple(binary.ReadByte(), reference, depth),
                    '[' => List(reference, depth),
                    '<' => Set(reference, depth, frozen: false),
                    '>' => Set(reference, depth, frozen: true),
                    _ => Remember(reference, Scalar((char)type)),
                };
            }

            object? Reference()
            {
                int index = binary.ReadInt32();
                if ((uint)index >= (uint)references.Count || ReferenceEquals(references[index], Terminator)) throw InvalidData();
                return references[index];
            }

            object? Scalar(char type) => type switch
            {
                'N' => null,
                'T' => true,
                'F' => false,
                'i' => (long)binary.ReadInt32(),
                'I' => binary.ReadInt64(),
                'l' => Long(),
                'g' => binary.ReadDouble(),
                'f' => TextFloat(),
                'y' => new Complex(binary.ReadDouble(), binary.ReadDouble()),
                'x' => new Complex(TextFloat(), TextFloat()),
                's' => new PyBytes(Bytes(binary.ReadInt32())),
                'u' or 't' => Encoding.UTF8.GetString(Bytes(binary.ReadInt32())),
                'a' or 'A' => Encoding.ASCII.GetString(Bytes(binary.ReadInt32())),
                'z' or 'Z' => Encoding.ASCII.GetString(Bytes(binary.ReadByte())),
                'c' => throw new ArgumentException("marshal code objects are unsupported"),
                _ => throw InvalidData(),
            };

            double TextFloat() => Builtins.@float(Encoding.ASCII.GetString(Bytes(binary.ReadByte())));

            byte[] Bytes(int size)
            {
                if (size < 0) throw InvalidData();
                var stream = binary.BaseStream;
                if (stream.CanSeek && size > stream.Length - stream.Position) throw new EndOfStreamException();
                var bytes = binary.ReadBytes(size);
                return bytes.Length == size ? bytes : throw new EndOfStreamException();
            }

            object Long()
            {
                int count = binary.ReadInt32();
                if (count == int.MinValue) throw InvalidData();
                BigInteger value = BigInteger.Zero;
                for (int i = 0; i < Math.Abs(count); i++)
                {
                    ushort digit = binary.ReadUInt16();
                    if (digit >= 0x8000) throw InvalidData();
                    value += (BigInteger)digit << checked(i * 15);
                }
                if (count < 0) value = -value;
                return value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;
            }

            object? Child(int depth)
            {
                object? value = Value(depth + 1);
                return ReferenceEquals(value, Terminator) ? throw InvalidData() : value;
            }

            object Tuple(int size, int reference, int depth)
            {
                if (size < 0) throw InvalidData();
                var stream = binary.BaseStream;
                if (stream.CanSeek && size > stream.Length - stream.Position) throw new EndOfStreamException();
                var values = new object?[size];
                var tuple = new PyTuple<object?>(values);
                Remember(reference, tuple);
                for (int i = 0; i < size; i++) values[i] = Child(depth);
                return tuple;
            }

            object Mapping(int reference, int depth)
            {
                var map = Builtins.__newdict<object, object?>();
                Remember(reference, map);
                while (true)
                {
                    object? key = Value(depth + 1);
                    if (ReferenceEquals(key, Terminator)) return map;
                    if (key == null) throw new ArgumentException("None dictionary keys are unsupported");
                    map[key] = Child(depth);
                }
            }

            object List(int reference, int depth)
            {
                int size = binary.ReadInt32();
                if (size < 0) throw InvalidData();
                var list = new System.Collections.Generic.List<object?>();
                Remember(reference, list);
                for (int i = 0; i < size; i++) list.Add(Child(depth));
                return list;
            }

            object Set(int reference, int depth, bool frozen)
            {
                int size = binary.ReadInt32();
                if (size < 0) throw InvalidData();
                var set = Builtins.__newset<object?>();
                if (!frozen) Remember(reference, set);
                for (int i = 0; i < size; i++) set.Add(Child(depth));
                return frozen ? Remember(reference, Builtins.frozenset(set))! : set;
            }
        }

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
