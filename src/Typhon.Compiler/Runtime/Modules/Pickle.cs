// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers.Binary;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Typhon.Runtime;

    /// <summary>CPython pickle data objects in protocol 4 and 5.</summary>
    internal static class _pickle
    {
        public sealed class PickleBuffer
        {
            readonly global::memoryview _view;
            bool _released;

            public PickleBuffer(object? buffer) => _view = new global::memoryview(buffer);

            internal global::memoryview View => _released
                ? throw new ArgumentException("operation forbidden on released PickleBuffer object")
                : _view;

            public global::memoryview raw()
            {
                var view = View;
                if (!view.contiguous)
                    throw new BufferError("cannot extract raw buffer from non-contiguous buffer");
                return view.cast("B");
            }

            public void release()
            {
                _view.release();
                _released = true;
            }
        }
    }

    internal static partial class pickle
    {
        public static readonly Type PickleBuffer = typeof(_pickle.PickleBuffer);
        public const long DEFAULT_PROTOCOL = 5;
        public const long HIGHEST_PROTOCOL = 5;

        public class PickleError(string message) : Exception(message) { }
        public class PicklingError(string message) : PickleError(message) { }
        public class UnpicklingError(string message) : PickleError(message) { }

        public static PyBytes dumps(object? obj, long? protocol = null)
        {
            long version = protocol is null or < 0 ? DEFAULT_PROTOCOL : protocol.Value;
            if (version is < 4 or > HIGHEST_PROTOCOL)
                throw new ArgumentException($"unsupported pickle protocol: {version}");
            using var stream = new MemoryStream();
            using var binary = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            binary.Write((byte)0x80); // PROTO
            binary.Write((byte)version);
            new Pickler(binary, version).Write(obj, 0);
            binary.Write((byte)'.'); // STOP
            return new PyBytes(stream.ToArray());
        }

        public static void dump(object? obj, object file, long? protocol = null) =>
            Builtins.__invoke((object?)Builtins.getattr(file, "write"), [dumps(obj, protocol)]);

        public static dynamic? loads(object data)
        {
            using var stream = new MemoryStream(ByteOps.Buffer(data), writable: false);
            return Read(stream);
        }

        public static dynamic? load(object file) =>
            file is io.IOBase { __stream: Stream stream } && (file is io.BufferedIOBase or io.RawIOBase)
                ? Read(stream) : throw new InvalidCastException("pickle.load() expects a binary file");

        static object? Read(Stream stream)
        {
            using var binary = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            return new Unpickler(binary).Read();
        }

        sealed class Pickler(BinaryWriter binary, long protocol)
        {
            readonly Dictionary<object, int> memo = new(ReferenceEqualityComparer.Instance);
            readonly HashSet<object> tuples = new(ReferenceEqualityComparer.Instance);

            void Op(char operation) => binary.Write((byte)operation);
            void Op(int operation) => binary.Write((byte)operation);

            void Remember(object value)
            {
                memo.Add(value, memo.Count);
                Op(0x94); // MEMOIZE
            }

            public void Write(object? value, int depth)
            {
                if (depth > 1000) throw new PicklingError("maximum pickle nesting exceeded");
                if (value != null && memo.TryGetValue(value, out int id))
                {
                    if (id < 256) { Op('h'); binary.Write((byte)id); }
                    else { Op('j'); binary.Write(id); }
                    return;
                }
                switch (value)
                {
                    case null: Op('N'); break;
                    case bool flag: Op(flag ? 0x88 : 0x89); break;
                    case BigInteger integer: Integer(integer); break;
                    case byte or sbyte or short or ushort or int or uint or long:
                        Integer(Convert.ToInt64(value)); break;
                    case ulong integer: Integer(integer); break;
                    case double or float:
                        Op('G');
                        binary.Write(BinaryPrimitives.ReverseEndianness(BitConverter.DoubleToInt64Bits(Convert.ToDouble(value))));
                        break;
                    case string text:
                        Block(Encoding.UTF8.GetBytes(text), 0x8c, 'X');
                        Remember(text);
                        break;
                    case PyBytes bytes:
                        Block(bytes.Data, 'C', 'B');
                        Remember(bytes);
                        break;
                    case PyByteArray array when protocol >= 5:
                        var data = ByteOps.Buffer(array);
                        Op(0x96); // BYTEARRAY8
                        binary.Write((long)data.Length);
                        binary.Write(data);
                        Remember(array);
                        break;
                    case IDictionary dictionary:
                        Op('}');
                        Remember(dictionary);
                        if (dictionary.Count != 0)
                        {
                            Op('(');
                            foreach (DictionaryEntry entry in dictionary)
                            {
                                Write(entry.Key, depth + 1);
                                Write(entry.Value, depth + 1);
                            }
                            Op('u');
                        }
                        break;
                    case ITuple tuple:
                        Tuple(tuple, depth);
                        break;
                    case IList list:
                        Op(']');
                        Remember(list);
                        if (list.Count != 0)
                        {
                            Op('(');
                            foreach (object? item in list) Write(item, depth + 1);
                            Op('e');
                        }
                        break;
                    default:
                        throw new PicklingError($"unsupported pickle value of type '{Builtins.PyTypeName(value.GetType())}'");
                }
            }

            void Integer(BigInteger value)
            {
                if (value >= 0 && value < 256) { Op('K'); binary.Write((byte)value); }
                else if (value >= 0 && value < 65536) { Op('M'); binary.Write((ushort)value); }
                else if (value >= int.MinValue && value <= int.MaxValue) { Op('J'); binary.Write((int)value); }
                else
                {
                    byte[] bytes = value.ToByteArray();
                    Block(bytes, 0x8a, 0x8b);
                }
            }

            void Block(byte[] bytes, int small, int large)
            {
                if (bytes.Length < 256) { Op(small); binary.Write((byte)bytes.Length); }
                else { Op(large); binary.Write(bytes.Length); }
                binary.Write(bytes);
            }

            void Tuple(ITuple tuple, int depth)
            {
                if (tuple.Length == 0) { Op(')'); return; }
                if (!tuples.Add(tuple)) throw new PicklingError("recursive tuple cannot be written");
                if (tuple.Length > 3) Op('(');
                for (int index = 0; index < tuple.Length; index++) Write(tuple[index], depth + 1);
                Op(tuple.Length < 4 ? 0x84 + tuple.Length : 't');
                tuples.Remove(tuple);
                Remember(tuple);
            }
        }

        sealed class Unpickler(BinaryReader binary)
        {
            static readonly object Marker = new();
            readonly List<object?> stack = new();
            readonly Dictionary<int, object?> memo = new();

            object? Peek() => stack.Count > 0 ? stack[^1] : throw new UnpicklingError("unpickling stack underflow");
            object? Pop()
            {
                object? value = Peek();
                stack.RemoveAt(stack.Count - 1);
                return value;
            }
            object?[] PopItems(int count)
            {
                int start = stack.Count - count;
                if (start < 0) throw new UnpicklingError("unpickling stack underflow");
                var values = stack.GetRange(start, count).ToArray();
                stack.RemoveRange(start, count);
                return values;
            }
            object?[] PopMarked()
            {
                int marker = stack.LastIndexOf(Marker);
                if (marker < 0) throw new UnpicklingError("could not find MARK");
                var parts = PopItems(stack.Count - marker - 1);
                stack.RemoveAt(marker);
                return parts;
            }

            byte[] Bytes(long count)
            {
                if (count < 0 || count > Array.MaxLength ||
                    binary.BaseStream.CanSeek && count > binary.BaseStream.Length - binary.BaseStream.Position)
                    throw new UnpicklingError("pickle data was truncated");
                byte[] bytes = binary.ReadBytes((int)count);
                if (bytes.Length != count) throw new UnpicklingError("pickle data was truncated");
                return bytes;
            }
            object? GetMemo(int id) => memo.TryGetValue(id, out var value)
                ? value : throw new UnpicklingError($"memo value not found at index {id}");

            public object? Read()
            {
                try
                {
                    while (true)
                    {
                        int next = binary.BaseStream.ReadByte();
                        switch (next)
                        {
                            case -1: throw new EndOfStreamException("Ran out of input");
                            case 0x80:
                                byte protocol = binary.ReadByte();
                                if (protocol > HIGHEST_PROTOCOL) throw new ArgumentException($"unsupported pickle protocol: {protocol}");
                                break;
                            case 0x95: // FRAME
                                long frame = binary.ReadInt64();
                                if (frame < 0 || binary.BaseStream.CanSeek && frame > binary.BaseStream.Length - binary.BaseStream.Position)
                                    throw new UnpicklingError("pickle data was truncated");
                                break;
                            case '.': return Pop();
                            case '(': stack.Add(Marker); break;
                            case 'N': stack.Add(null); break;
                            case 0x88: stack.Add(true); break;
                            case 0x89: stack.Add(false); break;
                            case 'K': stack.Add((long)binary.ReadByte()); break;
                            case 'M': stack.Add((long)binary.ReadUInt16()); break;
                            case 'J': stack.Add((long)binary.ReadInt32()); break;
                            case 0x8a: stack.Add(DecodeInteger(Bytes(binary.ReadByte()))); break;
                            case 0x8b: stack.Add(DecodeInteger(Bytes(binary.ReadInt32()))); break;
                            case 'G': stack.Add(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReverseEndianness(binary.ReadInt64()))); break;
                            case 'C': stack.Add(new PyBytes(Bytes(binary.ReadByte()))); break;
                            case 'B': stack.Add(new PyBytes(Bytes(binary.ReadInt32()))); break;
                            case 0x8e: stack.Add(new PyBytes(Bytes(binary.ReadInt64()))); break;
                            case 0x96: stack.Add(new PyByteArray(Bytes(binary.ReadInt64()))); break;
                            case 0x8c: stack.Add(Encoding.UTF8.GetString(Bytes(binary.ReadByte()))); break;
                            case 'X': stack.Add(Encoding.UTF8.GetString(Bytes(binary.ReadInt32()))); break;
                            case 0x8d: stack.Add(Encoding.UTF8.GetString(Bytes(binary.ReadInt64()))); break;
                            case ']': stack.Add(new List<object?>()); break;
                            case 'a':
                                object? value = Pop();
                                AsList(Peek()).Add(value);
                                break;
                            case 'e':
                                var parts = PopMarked();
                                AsList(Peek()).AddRange(parts);
                                break;
                            case '}': stack.Add(Builtins.__newdict<object, object?>()); break;
                            case 's':
                                object? item = Pop(), key = Pop();
                                Put(Peek(), key, item);
                                break;
                            case 'u':
                                var pairs = PopMarked();
                                if ((pairs.Length & 1) != 0) throw new UnpicklingError("odd number of items for SETITEMS");
                                object? dictionary = Peek();
                                for (int index = 0; index < pairs.Length; index += 2) Put(dictionary, pairs[index], pairs[index + 1]);
                                break;
                            case ')': stack.Add(new PyTuple<object?>(Array.Empty<object?>())); break;
                            case 't': stack.Add(new PyTuple<object?>(PopMarked())); break;
                            case 0x85: case 0x86: case 0x87:
                                stack.Add(new PyTuple<object?>(PopItems(next - 0x84)));
                                break;
                            case 0x94: memo[memo.Count] = Peek(); break;
                            case 'q': memo[binary.ReadByte()] = Peek(); break;
                            case 'r': memo[binary.ReadInt32()] = Peek(); break;
                            case 'h': stack.Add(GetMemo(binary.ReadByte())); break;
                            case 'j': stack.Add(GetMemo(binary.ReadInt32())); break;
                            default: throw new UnpicklingError($"unsupported pickle opcode 0x{next:x2}");
                        }
                    }
                }
                catch (EndOfStreamException)
                {
                    if (stack.Count == 0) throw;
                    throw new UnpicklingError("pickle data was truncated");
                }
            }

            static List<object?> AsList(object? value) => value as List<object?> ??
                throw new UnpicklingError("APPENDS requires a list");

            static void Put(object? dictionary, object? key, object? value)
            {
                if (dictionary is not IDictionary) throw new UnpicklingError("SETITEMS requires a dictionary");
                Builtins.__setitem(dictionary, key, value);
            }
        }
    }
}
