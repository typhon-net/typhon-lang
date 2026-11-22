// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers;
    using System.IO;
    using System.IO.Compression;
    using Typhon.Runtime;

    internal static class zlib
    {
        public const long DEFLATED = 8;
        public const long MAX_WBITS = 15;
        public const long DEF_MEM_LEVEL = 8;
        public const long DEF_BUF_SIZE = 16384;
        public const long Z_DEFAULT_COMPRESSION = -1;
        public const long Z_DEFAULT_STRATEGY = 0;
        public const long Z_NO_FLUSH = 0;
        public const long Z_SYNC_FLUSH = 2;
        public const long Z_FINISH = 4;

        public sealed class error : Exception { public error(string message) : base(message) { } }

        public static long crc32(object data, long value = 0) => binascii.crc32(data, value);

        public static long adler32(object data, long value = 1)
        {
            const uint modulus = 65521;
            uint seed = unchecked((uint)value);
            uint low = (seed & 0xffff) % modulus;
            uint high = (seed >> 16) % modulus;
            foreach (byte character in ByteOps.Buffer(data))
            {
                low += character;
                if (low >= modulus) low -= modulus;
                high += low;
                if (high >= modulus) high -= modulus;
            }
            return (high << 16) | low;
        }

        internal enum Container { Zlib, Raw, Gzip }

        static (Container, int) CompressionContainer(long bits) => bits switch
        {
            >= 8 and <= MAX_WBITS => (Container.Zlib, (int)bits),
            >= -MAX_WBITS and <= -9 => (Container.Raw, -(int)bits),
            >= 25 and <= MAX_WBITS + 16 => (Container.Gzip, (int)bits - 16),
            _ => throw new ArgumentException("unsupported compression window size"),
        };

        public static CompressObject compressobj(long level = Z_DEFAULT_COMPRESSION, long method = DEFLATED,
            long wbits = MAX_WBITS, long memLevel = DEF_MEM_LEVEL, long strategy = Z_DEFAULT_STRATEGY)
        {
            if (method != DEFLATED || memLevel != DEF_MEM_LEVEL || level is < -1 or > 9 || strategy is < 0 or > 4)
                throw new ArgumentException("Invalid initialization option");
            var (container, window) = CompressionContainer(wbits);
            var options = new ZLibCompressionOptions
            {
                CompressionLevel = (int)level,
                CompressionStrategy = (ZLibCompressionStrategy)strategy,
                WindowLog2 = window,
            };
            return new CompressObject(container, options);
        }

        public static DecompressObject decompressobj(long wbits = MAX_WBITS) => new DecompressObject(wbits switch
        {
            0 or MAX_WBITS => Container.Zlib,
            -MAX_WBITS => Container.Raw,
            MAX_WBITS + 16 => Container.Gzip,
            _ => throw new ArgumentException("unsupported decompression window size"),
        });

        public static PyBytes compress(object data, long level = Z_DEFAULT_COMPRESSION, long wbits = MAX_WBITS)
        {
            using var encoder = compressobj(level: level, wbits: wbits);
            return encoder.Encode(ByteOps.Buffer(data), final: true);
        }

        public static PyBytes decompress(object data, long wbits = MAX_WBITS, long bufsize = DEF_BUF_SIZE)
        {
            if (bufsize < 0) throw new ArgumentException("bufsize must be non-negative");
            using var decoder = decompressobj(wbits);
            PyBytes output = decoder.decompress(data);
            if (!decoder.eof) throw new error("Error -5 while decompressing data: incomplete or truncated stream");
            return output;
        }

        public sealed class CompressObject : IDisposable
        {
            delegate OperationStatus EncodeBlock(ReadOnlySpan<byte> from, Span<byte> to, out int read, out int written, bool final);
            delegate OperationStatus FlushBlock(Span<byte> to, out int written);
            readonly EncodeBlock _encode;
            readonly FlushBlock _flush;
            readonly IDisposable _encoder;
            bool _disposed;

            internal CompressObject(Container container, ZLibCompressionOptions options)
            {
                switch (container)
                {
                    case Container.Raw:
                        var raw = new DeflateEncoder(options);
                        (_encoder, _encode, _flush) = (raw, raw.Compress, raw.Flush);
                        break;
                    case Container.Zlib:
                        var wrapped = new ZLibEncoder(options);
                        (_encoder, _encode, _flush) = (wrapped, wrapped.Compress, wrapped.Flush);
                        break;
                    default:
                        var gzip = new GZipEncoder(options);
                        (_encoder, _encode, _flush) = (gzip, gzip.Compress, gzip.Flush);
                        break;
                }
            }

            public PyBytes compress(object data) => Encode(ByteOps.Buffer(data), final: false);

            internal PyBytes Encode(ReadOnlySpan<byte> input, bool final)
            {
                if (_disposed) throw new error("Error -2 while compressing data: inconsistent stream state");
                using var output = new MemoryStream();
                Span<byte> buffer = stackalloc byte[8192];
                OperationStatus status;
                do
                {
                    status = _encode(input, buffer, out int consumed, out int produced, final);
                    if (status == OperationStatus.InvalidData) throw new error("Error -2 while compressing data");
                    output.Write(buffer[..produced]);
                    input = input[consumed..];
                } while (status == OperationStatus.DestinationTooSmall || !input.IsEmpty);
                if (final) Dispose();
                return new PyBytes(output.ToArray());
            }

            public PyBytes flush(long mode = Z_FINISH)
            {
                if (mode == Z_NO_FLUSH) return PyBytes.Empty;
                if (mode == Z_FINISH) return Encode(ReadOnlySpan<byte>.Empty, final: true);
                if (mode != Z_SYNC_FLUSH) throw new ArgumentException("unsupported compression flush mode");
                if (_disposed) throw new error("Error -2 while flushing: inconsistent stream state");

                using var output = new MemoryStream();
                Span<byte> buffer = stackalloc byte[8192];
                OperationStatus status;
                do
                {
                    status = _flush(buffer, out int produced);
                    if (status == OperationStatus.InvalidData) throw new error("Error -2 while flushing");
                    output.Write(buffer[..produced]);
                } while (status == OperationStatus.DestinationTooSmall);
                return new PyBytes(output.ToArray());
            }

            public void Dispose()
            {
                if (_disposed) return;
                _encoder.Dispose();
                _disposed = true;
            }
        }

        public sealed class DecompressObject : IDisposable
        {
            delegate OperationStatus DecodeBlock(ReadOnlySpan<byte> from, Span<byte> to, out int read, out int written);
            readonly DecodeBlock _decode;
            readonly IDisposable _decoder;
            bool _disposed;
            bool _flushed;

            public bool eof { get; private set; }
            public PyBytes unused_data { get; private set; } = PyBytes.Empty;
            public PyBytes unconsumed_tail { get; private set; } = PyBytes.Empty;

            internal DecompressObject(Container container)
            {
                switch (container)
                {
                    case Container.Raw:
                        var raw = new DeflateDecoder();
                        (_decoder, _decode) = (raw, raw.Decompress);
                        break;
                    case Container.Zlib:
                        var wrapped = new ZLibDecoder();
                        (_decoder, _decode) = (wrapped, wrapped.Decompress);
                        break;
                    default:
                        var gzip = new GZipDecoder();
                        (_decoder, _decode) = (gzip, gzip.Decompress);
                        break;
                }
            }

            public PyBytes decompress(object data, long max_length = 0)
            {
                if (max_length < 0) throw new ArgumentException("max_length must be non-negative");
                if (_flushed || _disposed && !eof)
                    throw new error("Error -2 while decompressing data: inconsistent stream state");
                ReadOnlySpan<byte> input = ByteOps.Buffer(data);
                if (eof)
                {
                    KeepUnused(input);
                    return PyBytes.Empty;
                }
                using var output = new MemoryStream();
                Span<byte> buffer = stackalloc byte[8192];
                long remaining = max_length == 0 ? long.MaxValue : max_length;
                while (remaining > 0)
                {
                    Span<byte> destination = buffer[..(int)Math.Min(remaining, buffer.Length)];
                    OperationStatus status = _decode(input, destination, out int consumed, out int produced);
                    if (status == OperationStatus.InvalidData) throw new error("Error -3 while decompressing data");
                    output.Write(destination[..produced]);
                    remaining -= produced;
                    input = input[consumed..];
                    if (status == OperationStatus.Done)
                    {
                        KeepUnused(input);
                        input = ReadOnlySpan<byte>.Empty;
                        eof = true;
                        Dispose();
                        break;
                    }
                    if (status == OperationStatus.NeedMoreData || consumed == 0 && produced == 0) break;
                }
                unconsumed_tail = input.IsEmpty ? PyBytes.Empty : new PyBytes(input.ToArray());
                return new PyBytes(output.ToArray());
            }

            public PyBytes flush(long length = DEF_BUF_SIZE)
            {
                if (length <= 0) throw new ArgumentException("length must be greater than zero");
                if (_flushed) return PyBytes.Empty;
                PyBytes output = decompress(unconsumed_tail);
                _flushed = eof;
                return output;
            }

            void KeepUnused(ReadOnlySpan<byte> input)
            {
                if (input.IsEmpty) return;
                var combined = new byte[unused_data.Count + input.Length];
                unused_data.Data.CopyTo(combined, 0);
                input.CopyTo(combined.AsSpan(unused_data.Count));
                unused_data = new PyBytes(combined);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _decoder.Dispose();
                _disposed = true;
            }
        }
    }
}
