// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers.Binary;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class pickle
    {
        static PyBytes Opcode(byte value) => new(new[] { value });

        // The opcode bytes are also consumed by Python's pickletools.
        public static readonly PyBytes INT = Opcode(0x49);
        public static readonly PyBytes BININT = Opcode(0x4a);
        public static readonly PyBytes BININT1 = Opcode(0x4b);
        public static readonly PyBytes BININT2 = Opcode(0x4d);
        public static readonly PyBytes LONG = Opcode(0x4c);
        public static readonly PyBytes LONG1 = Opcode(0x8a);
        public static readonly PyBytes LONG4 = Opcode(0x8b);
        public static readonly PyBytes STRING = Opcode(0x53);
        public static readonly PyBytes BINSTRING = Opcode(0x54);
        public static readonly PyBytes SHORT_BINSTRING = Opcode(0x55);
        public static readonly PyBytes BINBYTES = Opcode(0x42);
        public static readonly PyBytes SHORT_BINBYTES = Opcode(0x43);
        public static readonly PyBytes BINBYTES8 = Opcode(0x8e);
        public static readonly PyBytes BYTEARRAY8 = Opcode(0x96);
        public static readonly PyBytes NEXT_BUFFER = Opcode(0x97);
        public static readonly PyBytes READONLY_BUFFER = Opcode(0x98);
        public static readonly PyBytes NONE = Opcode(0x4e);
        public static readonly PyBytes NEWTRUE = Opcode(0x88);
        public static readonly PyBytes NEWFALSE = Opcode(0x89);
        public static readonly PyBytes UNICODE = Opcode(0x56);
        public static readonly PyBytes SHORT_BINUNICODE = Opcode(0x8c);
        public static readonly PyBytes BINUNICODE = Opcode(0x58);
        public static readonly PyBytes BINUNICODE8 = Opcode(0x8d);
        public static readonly PyBytes FLOAT = Opcode(0x46);
        public static readonly PyBytes BINFLOAT = Opcode(0x47);
        public static readonly PyBytes EMPTY_LIST = Opcode(0x5d);
        public static readonly PyBytes APPEND = Opcode(0x61);
        public static readonly PyBytes APPENDS = Opcode(0x65);
        public static readonly PyBytes LIST = Opcode(0x6c);
        public static readonly PyBytes EMPTY_TUPLE = Opcode(0x29);
        public static readonly PyBytes TUPLE = Opcode(0x74);
        public static readonly PyBytes TUPLE1 = Opcode(0x85);
        public static readonly PyBytes TUPLE2 = Opcode(0x86);
        public static readonly PyBytes TUPLE3 = Opcode(0x87);
        public static readonly PyBytes EMPTY_DICT = Opcode(0x7d);
        public static readonly PyBytes DICT = Opcode(0x64);
        public static readonly PyBytes SETITEM = Opcode(0x73);
        public static readonly PyBytes SETITEMS = Opcode(0x75);
        public static readonly PyBytes EMPTY_SET = Opcode(0x8f);
        public static readonly PyBytes ADDITEMS = Opcode(0x90);
        public static readonly PyBytes FROZENSET = Opcode(0x91);
        public static readonly PyBytes POP = Opcode(0x30);
        public static readonly PyBytes DUP = Opcode(0x32);
        public static readonly PyBytes MARK = Opcode(0x28);
        public static readonly PyBytes POP_MARK = Opcode(0x31);
        public static readonly PyBytes GET = Opcode(0x67);
        public static readonly PyBytes BINGET = Opcode(0x68);
        public static readonly PyBytes LONG_BINGET = Opcode(0x6a);
        public static readonly PyBytes PUT = Opcode(0x70);
        public static readonly PyBytes BINPUT = Opcode(0x71);
        public static readonly PyBytes LONG_BINPUT = Opcode(0x72);
        public static readonly PyBytes MEMOIZE = Opcode(0x94);
        public static readonly PyBytes EXT1 = Opcode(0x82);
        public static readonly PyBytes EXT2 = Opcode(0x83);
        public static readonly PyBytes EXT4 = Opcode(0x84);
        public static readonly PyBytes GLOBAL = Opcode(0x63);
        public static readonly PyBytes STACK_GLOBAL = Opcode(0x93);
        public static readonly PyBytes REDUCE = Opcode(0x52);
        public static readonly PyBytes BUILD = Opcode(0x62);
        public static readonly PyBytes INST = Opcode(0x69);
        public static readonly PyBytes OBJ = Opcode(0x6f);
        public static readonly PyBytes NEWOBJ = Opcode(0x81);
        public static readonly PyBytes NEWOBJ_EX = Opcode(0x92);
        public static readonly PyBytes PROTO = Opcode(0x80);
        public static readonly PyBytes STOP = Opcode(0x2e);
        public static readonly PyBytes FRAME = Opcode(0x95);
        public static readonly PyBytes PERSID = Opcode(0x50);
        public static readonly PyBytes BINPERSID = Opcode(0x51);

        public static readonly PyTuple<Type> bytes_types = new(new[] { typeof(PyBytes), typeof(PyByteArray) });
        public static readonly PyTuple<string> __all__ = new(typeof(pickle)
            .GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(PyBytes))
            .Select(field => field.Name).Concat(new[] { "dump", "dumps", "load", "loads", "PickleError", "PicklingError",
                "UnpicklingError", "DEFAULT_PROTOCOL", "HIGHEST_PROTOCOL" }));

        public static dynamic decode_long(object data) => DecodeInteger(ByteOps.Buffer(data));

        static object DecodeInteger(byte[] bytes)
        {
            var integer = new BigInteger(bytes);
            return integer >= long.MinValue && integer <= long.MaxValue ? (object)(long)integer : integer;
        }

        internal sealed class _Pickler
        {
            readonly long protocol;
            public readonly _Framer framer;

            public _Pickler(object file, long protocol = DEFAULT_PROTOCOL)
            {
                this.protocol = protocol < 0 ? HIGHEST_PROTOCOL : protocol;
                if (this.protocol > HIGHEST_PROTOCOL) throw new ArgumentException("pickle protocol must be <= " + HIGHEST_PROTOCOL);
                framer = new _Framer((object?)Builtins.getattr(file, "write"));
            }

            public void write(object data) => framer.write(data);

            public PyBytes put(long index) => protocol >= 4 ? MEMOIZE : Memo(index, 'p', BINPUT, LONG_BINPUT);
            public PyBytes get(long index) => Memo(index, 'g', BINGET, LONG_BINGET);

            PyBytes Memo(long index, char text, PyBytes shortOpcode, PyBytes largeOpcode)
            {
                if (index < 0 || index > uint.MaxValue) throw new OverflowException("pickle memo index out of range");
                if (protocol == 0)
                    return new PyBytes(Encoding.ASCII.GetBytes(text + index.ToString(CultureInfo.InvariantCulture) + "\n"));
                if (index < 256) return new PyBytes(new[] { shortOpcode.Data[0], (byte)index });
                var bytes = new byte[5];
                bytes[0] = largeOpcode.Data[0];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)index);
                return new PyBytes(bytes);
            }
        }

        internal sealed class _Framer
        {
            public readonly long _FRAME_SIZE_TARGET = 64 * 1024;
            const int FrameSizeMin = 4;
            readonly object? _writer;
            MemoryStream? _frame;

            public _Framer(object? writer) => _writer = writer;

            public void start_framing()
            {
                _frame?.Dispose();
                _frame = new MemoryStream();
            }

            public void end_framing()
            {
                commit_frame(force: true);
                _frame?.Dispose();
                _frame = null;
            }

            public void commit_frame(bool force = false)
            {
                if (_frame == null || _frame.Length == 0 || !force && _frame.Length < _FRAME_SIZE_TARGET) return;
                if (_frame.Length >= FrameSizeMin)
                {
                    var header = new byte[9];
                    header[0] = FRAME.Data[0];
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(1), _frame.Length);
                    file_write(new PyBytes(header));
                }
                file_write(new PyBytes(_frame.ToArray()));
                _frame.SetLength(0);
                _frame.Position = 0;
            }

            public void write(object data)
            {
                if (_frame == null) file_write(data);
                else _frame.Write(ByteOps.Buffer(data));
            }

            public void file_write(object data) => Builtins.__invoke(_writer, [data]);
        }
    }
}
