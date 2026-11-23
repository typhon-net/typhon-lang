// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Keep the Python module name.

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Typhon.Runtime;

    /// <summary>The CPython marshal wire format for ordinary data objects, including profiler stats.</summary>
    internal static class marshal
    {
        public const long version = 6;

        public static PyBytes dumps(object? value, long version = marshal.version, bool allow_code = true)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            Write(writer, value, version, allow_code, depth: 0);
            return new PyBytes(output.ToArray());
        }

        public static void dump(object? value, object file, long version = marshal.version, bool allow_code = true) =>
            Builtins.__invoke((object?)Builtins.getattr(file, "write"), [dumps(value, version, allow_code)]);

        /// <summary>Read one value from a binary file, leaving it positioned at the start of the next value.</summary>
        public static dynamic? load(object file, bool allow_code = true) =>
            file is io.IOBase { __stream: Stream input } ? Read(input, allow_code)
            : throw new InvalidCastException("marshal.load() expected a binary file");

        public static dynamic? loads(object bytes, bool allow_code = true)
        {
            using var input = new MemoryStream(ByteOps.From(bytes), writable: false);
            return Read(input, allow_code);
        }

        static object? Read(Stream input, bool allowCode)
        {
            using var binary = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            return new Reader(binary, allowCode).Item();
        }

        /// <summary>Bytecode and metadata preserved from a CPython marshal record.</summary>
        internal sealed class MarshalledCode(
            string name, string filename, int firstLine, int flags, string qualifiedName,
            int arguments, int positional, int keywordOnly, int stackSize,
            PyBytes bytecode, PyTuple<object?> constants, PyTuple<object?> names,
            PyTuple<object?> localNames, PyBytes localKinds, PyBytes lines, PyBytes exceptions)
            : types.CodeType(name, filename, firstLine, flags, qualifiedName,
                (arguments, positional, keywordOnly), NamesWithKind(localNames, localKinds, 0x20))
        {
            public readonly long co_stacksize = stackSize;
            public readonly PyBytes co_code = bytecode;
            public readonly PyTuple<object?> co_consts = constants;
            public readonly PyTuple<object?> co_names = names;
            public readonly PyBytes co_linetable = lines;
            public readonly PyBytes co_exceptiontable = exceptions;
            internal readonly PyTuple<object?> LocalNames = localNames;
            internal readonly PyBytes LocalKinds = localKinds;
            public readonly PyTuple<string> co_cellvars = NamesWithKind(localNames, localKinds, 0x40);
            public override PyTuple<string> co_freevars { get; } = NamesWithKind(localNames, localKinds, 0x80);

            // CPython location table encoding
            // https://github.com/python/cpython/blob/main/InternalDocs/code_objects.md#format-of-the-locations-table
            readonly record struct Location(int Size, long? Line, long? EndLine, long? Column, long? EndColumn);
            public PyBytes _co_code_adaptive => co_code;

            public string _varname_from_oparg(long argument) =>
                argument >= 0 && argument < LocalNames.Count ? (string)LocalNames[(int)argument]! :
                throw new IndexOutOfRangeException("tuple index out of range");

            IEnumerable<Location> Locations()
            {
                byte[] table = co_linetable.Data;
                int index = 0;
                long line = co_firstlineno;
                while (index < table.Length)
                {
                    byte marker = table[index++];
                    if ((marker & 128) == 0) throw BadLocations();
                    int size = (marker & 7) + 1;
                    int kind = (marker >> 3) & 15;
                    if (kind < 10)
                    {
                        int columns = Payload(table, ref index);
                        long column = kind * 8L + (columns >> 4);
                        yield return new(size, line, line, column, column + (columns & 15));
                    }
                    else if (kind <= 12)
                    {
                        line += kind - 10;
                        long column = Payload(table, ref index);
                        long endColumn = Payload(table, ref index);
                        yield return new(size, line, line, column, endColumn);
                    }
                    else if (kind == 15)
                    {
                        yield return new(size, null, null, null, null);
                    }
                    else
                    {
                        long delta = LocationNumber(table, ref index);
                        line += (delta & 1) == 0 ? delta >> 1 : -(delta >> 1);
                        if (kind == 13)
                        {
                            yield return new(size, line, line, null, null);
                        }
                        else
                        {
                            long endLine = line + LocationNumber(table, ref index);
                            long column = LocationNumber(table, ref index);
                            long endColumn = LocationNumber(table, ref index);
                            yield return new(size, line, endLine, column == 0 ? null : column - 1,
                                endColumn == 0 ? null : endColumn - 1);
                        }
                    }
                }
            }

            static ArgumentException BadLocations() => new("bad marshal data (invalid code location table)");

            static byte Payload(byte[] table, ref int index)
            {
                if (index >= table.Length || (table[index] & 128) != 0) throw BadLocations();
                return table[index++];
            }

            static long LocationNumber(byte[] table, ref int index)
            {
                long result = 0;
                for (int shift = 0; shift <= 30; shift += 6)
                {
                    byte part = Payload(table, ref index);
                    long bits = part & 63;
                    if (shift == 30 && bits > 3) throw BadLocations();
                    result |= bits << shift;
                    if ((part & 64) == 0) return result;
                }
                throw BadLocations();
            }

            public override IEnumerable<(long, long, long?)> co_lines()
            {
                long start = 0, end = 0;
                long? line = null;
                foreach (var location in Locations())
                {
                    if (location.Line != line && end > start)
                    {
                        yield return (start, end, line);
                        start = end;
                    }
                    end += location.Size * 2L;
                    line = location.Line;
                }
                if (end > start) yield return (start, end, line);
            }

            public override PyIterator<(long?, long?, long?, long?)> co_positions() => Builtins.iter(Positions());

            IEnumerable<(long?, long?, long?, long?)> Positions()
            {
                foreach (var location in Locations())
                    for (int instruction = 0; instruction < location.Size; instruction++)
                        yield return (location.Line, location.EndLine, location.Column, location.EndColumn);
            }

            static PyTuple<string> NamesWithKind(PyTuple<object?> names, PyBytes kinds, int mask)
            {
                var result = new List<string>();
                for (int i = 0; i < names.Count; i++)
                    if ((kinds.Data[i] & mask) != 0) result.Add((string)names[i]!);
                return new PyTuple<string>(result);
            }
        }

        sealed class Reader(BinaryReader binary, bool allowCode)
        {
            static readonly object Terminator = new();
            readonly List<object?> references = new();

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
                    '{' => Mapping(reference, depth, frozen: false),
                    '}' => Mapping(reference, depth, frozen: true),
                    '(' => Tuple(binary.ReadInt32(), reference, depth),
                    ')' => Tuple(binary.ReadByte(), reference, depth),
                    '[' => List(reference, depth),
                    '<' => Set(reference, depth, frozen: false),
                    '>' => Set(reference, depth, frozen: true),
                    ':' => Remember(reference, new PySlice(Child(depth), Child(depth), Child(depth))),
                    'c' => Remember(reference, CodeObject(depth)),
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
                '.' => Builtins.Ellipsis,
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
                _ => throw InvalidData(),
            };

            MarshalledCode CodeObject(int depth)
            {
                if (!allowCode) throw new ArgumentException("unmarshalling code objects is disallowed");
                T Field<T>() where T : class => Child(depth) as T ?? throw InvalidData();

                int arguments = binary.ReadInt32();
                int positional = binary.ReadInt32();
                int keywords = binary.ReadInt32();
                int stackSize = binary.ReadInt32();
                int flags = binary.ReadInt32();
                var code = Field<PyBytes>();
                var constants = Field<PyTuple<object?>>();
                var names = Field<PyTuple<object?>>();
                var localNames = Field<PyTuple<object?>>();
                var localKinds = Field<PyBytes>();
                var filename = Field<string>();
                var name = Field<string>();
                var qualifiedName = Field<string>();
                int firstLine = binary.ReadInt32();
                var lines = Field<PyBytes>();
                var exceptions = Field<PyBytes>();
                if (arguments < 0 || positional < 0 || positional > arguments || keywords < 0 || stackSize < 0 ||
                    (code.Data.Length & 1) != 0 || localNames.Count != localKinds.Data.Length)
                    throw InvalidData();
                foreach (object? local in localNames)
                    if (local is not string) throw InvalidData();
                return new MarshalledCode(name, filename, firstLine, flags, qualifiedName,
                    arguments, positional, keywords, stackSize, code, constants,
                    names, localNames, localKinds, lines, exceptions);
            }

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

            object Mapping(int reference, int depth, bool frozen)
            {
                var map = Builtins.__newdict<object, object?>();
                if (!frozen) Remember(reference, map);
                while (true)
                {
                    object? key = Value(depth + 1);
                    if (ReferenceEquals(key, Terminator))
                        return frozen ? Remember(reference, Builtins.frozendict(map))! : map;
                    if (key == null) throw new ArgumentException("None dictionary keys are unsupported");
                    map[key] = Child(depth);
                }
            }

            object List(int reference, int depth)
            {
                int size = binary.ReadInt32();
                if (size < 0) throw InvalidData();
                var list = new List<object?>();
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

        static char? SetTag(Type type)
        {
            if (!type.IsGenericType) return null;
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(frozenset<>)) return '>';
            return definition == typeof(HashSet<>) ? '<' : null;
        }

        static void Write(BinaryWriter writer, object? value, long version, bool allowCode, int depth)
        {
            if (depth > 500) throw new ArgumentException("object too deeply nested to marshal");
            void Type(char tag) => writer.Write((byte)tag);
            void Blob(char tag, byte[] bytes) { Type(tag); writer.Write(bytes.Length); writer.Write(bytes); }
            void Item(object? child) => Write(writer, child, version, allowCode, depth + 1);
            void Float(double number)
            {
                if (version >= 2) { writer.Write(number); return; }
                var bytes = Encoding.ASCII.GetBytes(number.ToString("G17", CultureInfo.InvariantCulture).ToLowerInvariant());
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
            }
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
                case types.EllipsisType: Type('.'); break;
                case int number: Integral(number); break;
                case long number: Integral(number); break;
                case BigInteger number: Integral(number); break;
                case double number:
                    Type(version >= 2 ? 'g' : 'f');
                    Float(number);
                    break;
                case Complex number:
                    Type(version >= 2 ? 'y' : 'x');
                    Float(number.Real);
                    Float(number.Imaginary);
                    break;
                case string text: Blob('u', Encoding.UTF8.GetBytes(text)); break;
                case PyBytes bytes: Blob('s', bytes.Data); break;
                case PySlice slice when version >= 5:
                    Type(':');
                    Item(slice.start);
                    Item(slice.stop);
                    Item(slice.step);
                    break;
                case types.CodeType when !allowCode: throw new ArgumentException("marshalling code objects is disallowed");
                case MarshalledCode code:
                    Type('c');
                    writer.Write((int)code.co_argcount);
                    writer.Write((int)code.co_posonlyargcount);
                    writer.Write((int)code.co_kwonlyargcount);
                    writer.Write((int)code.co_stacksize);
                    writer.Write((int)code.co_flags);
                    Item(code.co_code);
                    Item(code.co_consts);
                    Item(code.co_names);
                    Item(code.LocalNames);
                    Item(code.LocalKinds);
                    Item(code.co_filename);
                    Item(code.co_name);
                    Item(code.co_qualname);
                    writer.Write((int)code.co_firstlineno);
                    Item(code.co_linetable);
                    Item(code.co_exceptiontable);
                    break;
                case ITuple tuple:
                    Type('('); writer.Write(tuple.Length);
                    for (int i = 0; i < tuple.Length; i++) Item(tuple[i]);
                    break;
                case IPyTuple tuple:
                    Type('('); writer.Write(tuple.Count);
                    for (int i = 0; i < tuple.Count; i++) Item(tuple[i]);
                    break;
                case IDictionary entries:
                    bool frozen = Builtins.__type_of(entries) == typeof(frozendict<object, object>);
                    if (frozen && version < 6) throw new ArgumentException("unmarshallable object");
                    Type(frozen ? '}' : '{');
                    var iterator = entries.GetEnumerator();
                    while (iterator.MoveNext()) { Item(iterator.Key); Item(iterator.Value); }
                    Type('0');
                    break;
                case IList list:
                    Type('['); writer.Write(list.Count);
                    foreach (var child in list) Item(child);
                    break;
                case IEnumerable sequence when SetTag(sequence.GetType()) is char tag:
                    Type(tag);
                    object?[] members = sequence.Cast<object?>().ToArray();
                    writer.Write(members.Length);
                    foreach (var member in members) Item(member);
                    break;
                default: throw new ArgumentException("unmarshallable object");
            }
        }
    }
}
