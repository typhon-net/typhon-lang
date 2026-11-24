// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Retain Python's module names.

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.IO;
    using System.IO.Compression;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static class gzip
    {
        public sealed class BadGzipFile : PyOSError { public BadGzipFile(string message) : base(message) { } }

        public static PyBytes compress(object data, [Optional, DefaultParameterValue(6L)] object? compresslevel,
            [PyKeywordOnly, Optional, DefaultParameterValue(0L)] object? mtime)
        {
            PyBytes result = zlib.compress(data, compresslevel, wbits: 31);
            if (mtime == null) mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint timestamp = Builtins.truthy((object?)Builtins.le((object)0L, mtime)) &&
                Builtins.truthy((object?)Builtins.lt(mtime, (object)(1L << 32)))
                ? (uint)Builtins.@int(mtime) : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(result.Data.AsSpan(4, 4), timestamp);
            result.Data[9] = 255;
            return result;
        }

        public static PyBytes decompress(object? data)
        {
            if (data == null) return PyBytes.Empty;
            ReadOnlySpan<byte> input = ByteOps.Buffer(data);
            if (input.IsEmpty) return PyBytes.Empty;
            using var output = new MemoryStream();
            Span<byte> buffer = stackalloc byte[8192];
            while (!input.IsEmpty)
            {
                input = ReadHeader(input);
                int start = (int)output.Length;
                using var decoder = new DeflateDecoder();
                OperationStatus status;
                do
                {
                    status = decoder.Decompress(input, buffer, out int consumed, out int written);
                    if (status == OperationStatus.InvalidData) throw new zlib.error("Error -3 while decompressing data");
                    if (status == OperationStatus.NeedMoreData) throw Truncated();
                    input = input[consumed..];
                    output.Write(buffer[..written]);
                } while (status == OperationStatus.DestinationTooSmall);

                Require(input, 8);
                var member = output.GetBuffer().AsSpan(start, (int)output.Length - start);
                if (BinaryPrimitives.ReadUInt32LittleEndian(input) != binascii.Crc32(member))
                    throw new BadGzipFile("CRC check failed");
                if (BinaryPrimitives.ReadUInt32LittleEndian(input[4..]) != (uint)member.Length)
                    throw new BadGzipFile("Incorrect length of data produced");
                input = input[8..].TrimStart((byte)0);
            }
            return new PyBytes(output.ToArray());
        }

        static ReadOnlySpan<byte> ReadHeader(ReadOnlySpan<byte> input)
        {
            if (input.Length < 2 || input[0] != 0x1f || input[1] != 0x8b)
                throw new BadGzipFile($"Not a gzipped file ({ByteOps.Repr(input[..Math.Min(2, input.Length)])})");
            Require(input, 10);
            if (input[2] != 8) throw new BadGzipFile("Unknown compression method");
            const int HeaderCrc = 2, Extra = 4, Name = 8, Comment = 16;
            byte flags = input[3];
            var body = input[10..];
            if ((flags & Extra) != 0)
            {
                Require(body, 2);
                int size = BinaryPrimitives.ReadUInt16LittleEndian(body) + 2;
                Require(body, size);
                body = body[size..];
            }
            if ((flags & Name) != 0) body = SkipTerminatedString(body);
            if ((flags & Comment) != 0) body = SkipTerminatedString(body);
            if ((flags & HeaderCrc) != 0)
            {
                Require(body, 2);
                uint actual = binascii.Crc32(input[..(input.Length - body.Length)]) & 0xffff;
                uint expected = BinaryPrimitives.ReadUInt16LittleEndian(body);
                if (actual != expected)
                    throw new BadGzipFile($"Corrupted gzip header. Checksums do not match: {actual:x4} != {expected:x4}");
                body = body[2..];
            }
            return body;
        }

        static ReadOnlySpan<byte> SkipTerminatedString(ReadOnlySpan<byte> input)
        {
            int end = input.IndexOf((byte)0);
            return end < 0 ? ReadOnlySpan<byte>.Empty : input[(end + 1)..];
        }

        static void Require(ReadOnlySpan<byte> input, int count)
        {
            if (input.Length < count) throw Truncated();
        }

        static EndOfStreamException Truncated() =>
            new("Compressed file ended before the end-of-stream marker was reached");

        public static dynamic open(object filename, string mode = "rb", long compresslevel = 9,
            string? encoding = null, string? errors = null, string? newline = null)
        {
            if (mode is not ("r" or "rb" or "rt")) throw io.Unsupported("gzip mode " + mode);
            bool text = mode == "rt";
            if (!text && (encoding != null || errors != null || newline != null))
                throw new ArgumentException("binary mode doesn't take an encoding, errors, or newline argument");
            var (stream, name) = CompressedInput.Open(filename, raw => new GZipStream(raw, CompressionMode.Decompress));
            var binary = new PyBinaryFile(stream, name, "rb", closefd: true);
            if (!text) return binary;
            try { return new io.TextIOWrapper(binary, encoding, errors, newline); }
            catch { binary.close(); throw; }
        }
    }

    internal static class bz2
    {
        public static dynamic open(object filename, string mode = "rb", long compresslevel = 9,
            string? encoding = null, string? errors = null, string? newline = null)
        {
            bool text = mode.EndsWith("t", StringComparison.Ordinal);
            if (!text && (encoding != null || errors != null || newline != null))
                throw new ArgumentException("binary mode doesn't take an encoding, errors, or newline argument");
            var binary = new BZ2File(filename, text ? mode[..^1] : mode, compresslevel);
            if (!text) return binary;
            try { return new io.TextIOWrapper(binary, encoding, errors, newline); }
            catch { binary.close(); throw; }
        }

        public static PyBytes compress(object data, long compresslevel = 9)
        {
            using var target = new MemoryStream();
            using (var writer = new BZip2OutputStream(target, compresslevel, leaveOpen: true))
                writer.Write(ByteOps.Buffer(data));
            return new PyBytes(target.ToArray());
        }

        public static PyBytes decompress(object data)
        {
            var input = ByteOps.Buffer(data);
            if (input.Length == 0) return new PyBytes(Array.Empty<byte>());
            using var source = new MemoryStream(input, writable: false);
            using var reader = new BZip2Stream(source);
            using var output = new MemoryStream();
            try { reader.CopyTo(output); }
            catch (EndOfStreamException)
            {
                throw new ArgumentException("Compressed data ended before the end-of-stream marker was reached");
            }
            return new PyBytes(output.ToArray());
        }

        internal sealed class BZ2Compressor
        {
            readonly MemoryStream _output = new();
            readonly BZip2OutputStream _writer;
            bool _flushed;

            public BZ2Compressor(long compresslevel = 9) =>
                _writer = new BZip2OutputStream(_output, compresslevel, leaveOpen: true);

            public PyBytes compress(object data)
            {
                Check();
                _writer.Write(ByteOps.Buffer(data));
                _writer.Flush();
                return Drain();
            }

            public PyBytes flush()
            {
                Check();
                _flushed = true;
                _writer.Dispose();
                return Drain();
            }

            void Check()
            {
                if (_flushed) throw new ArgumentException("Compressor has been flushed");
            }

            PyBytes Drain()
            {
                var output = new PyBytes(_output.ToArray());
                _output.SetLength(0);
                return output;
            }
        }

        internal sealed class BZ2Decompressor
        {
            readonly MemoryStream _input = new();
            readonly BZip2Stream _reader;

            public bool eof { get; private set; }
            public bool needs_input { get; private set; } = true;
            public PyBytes unused_data { get; private set; } = new PyBytes(Array.Empty<byte>());

            public BZ2Decompressor() => _reader = new BZip2Stream(_input, singleMember: true);

            public PyBytes decompress(object data, long max_length = -1)
            {
                if (eof) throw new EndOfStreamException("End of stream already reached");
                byte[] added = ByteOps.Buffer(data);
                if (added.Length != 0)
                {
                    long start = _input.Position;
                    _input.Position = _input.Length;
                    _input.Write(added);
                    _input.Position = start;
                }
                if (max_length == 0)
                {
                    needs_input = !_reader.HasBufferedOutput && _input.Position == _input.Length;
                    return new PyBytes(Array.Empty<byte>());
                }

                using var output = new MemoryStream();
                Span<byte> buffer = stackalloc byte[8192];
                long remaining = max_length < 0 ? long.MaxValue : max_length;
                bool? available;
                while ((available = _reader.PrepareAvailable()) == true && remaining != 0)
                {
                    int count = _reader.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
                    output.Write(buffer[..count]);
                    remaining -= count;
                }
                eof = available == false;
                needs_input = available == null;
                var tail = _input.GetBuffer().AsSpan((int)_input.Position, (int)(_input.Length - _input.Position));
                if (eof)
                {
                    unused_data = new PyBytes(tail.ToArray());
                    _reader.Dispose();
                }
                else if (_input.Position != 0)
                {
                    // Drop complete blocks already held by the reader. Its bit buffer
                    // retains any extra bits from the last compressed byte.
                    tail.CopyTo(_input.GetBuffer());
                    _input.SetLength(tail.Length);
                    _input.Position = 0;
                }
                return new PyBytes(output.ToArray());
            }
        }

        internal sealed class BZ2File : PyBinaryFile
        {
            public BZ2File(object filename, string mode = "r", long compresslevel = 9)
                : this(Open(filename, mode, compresslevel)) { }

            BZ2File((Stream Stream, string Name, string Mode) file)
                : base(file.Stream, file.Name, file.Mode, closefd: true) { }

            static (Stream, string, string) Open(object filename, string mode, long compresslevel)
            {
                if (mode is "r" or "rb")
                {
                    var input = CompressedInput.Open(filename, raw => new BZip2Stream(raw));
                    return (input.Stream, input.Name, "rb");
                }
                if (mode is not ("w" or "wb" or "x" or "xb" or "a" or "ab"))
                    throw new ArgumentException("Invalid mode: " + mode);
                if (compresslevel < 1 || compresslevel > 9) throw new ArgumentException("compresslevel must be between 1 and 9");
                string binaryMode = mode[0] + "b";
                bool fileLike = Builtins.hasattr(filename, "write");
                string name = fileLike ? (object?)Builtins.getattr(filename, "name", null) as string ?? "" : os_.__path(filename);
                Stream output = fileLike ? new PyObjectStream(filename, leaveOpen: true) : StreamOps.OpenFile(name, binaryMode);
                try { return (new BZip2OutputStream(output, compresslevel), name, binaryMode); }
                catch { output.Dispose(); throw; }
            }
        }
    }
}

namespace Typhon.Runtime
{
    using System;
    using System.IO;

    internal static class CompressedInput
    {
        public static (Stream Stream, string Name) Open(object source, Func<Stream, Stream> wrap)
        {
            bool fileLike = Builtins.hasattr(source, "read");
            string name = fileLike ? (object?)Builtins.getattr(source, "name", null) as string ?? "" : Stdlib.os_.__path(source);
            Stream input = fileLike ? new PyObjectStream(source, leaveOpen: true) : StreamOps.OpenFile(name, "rb");
            try { return (wrap(input), name); }
            catch { input.Dispose(); throw; }
        }
    }
}
