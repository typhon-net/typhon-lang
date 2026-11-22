// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.IO;

    /// <summary>A streaming reader for ordinary bzip2 members. Each block is decoded and checked before serving bytes.</summary>
    internal sealed class BZip2Stream : Stream
    {
        const ulong BlockMarker = 0x314159265359, EndMarker = 0x177245385090;
        const int GroupSize = 50, MaxSelectors = 18002, MaxCodeLength = 20;
        static readonly uint[] CrcTable = BuildCrcTable();
        readonly Stream _source;
        byte[] _block = Array.Empty<byte>();
        int _offset, _blockSize, _bits;
        uint _bitBuffer, _streamCrc;
        long _position;
        bool _finished, _disposed;

        public BZip2Stream(Stream source)
        {
            _source = source;
            ReadHeader(first: true);
        }

        bool ReadHeader(bool first)
        {
            _bits = 0; // Each concatenated member starts on a byte boundary.
            int next = _source.ReadByte();
            if (next < 0 && !first) return false;
            foreach (byte expected in "BZh"u8)
            {
                if (next < 0) throw Truncated();
                if (next != expected)
                {
                    if (!first) return false;
                    throw Invalid("invalid stream header");
                }
                next = _source.ReadByte();
            }
            if (next < 0) throw Truncated();
            int level = next - '0';
            if (level is < 1 or > 9)
            {
                if (!first) return false;
                throw Invalid("invalid block size");
            }
            _blockSize = level * 100000;
            _streamCrc = 0;
            return true;
        }

        int Bits(int count)
        {
            while (_bits < count)
            {
                int next = _source.ReadByte();
                if (next < 0) throw Truncated();
                _bitBuffer = (_bitBuffer << 8) | (uint)next;
                _bits += 8;
            }
            _bits -= count;
            return (int)((_bitBuffer >> _bits) & ((1u << count) - 1));
        }

        uint Checksum() => ((uint)Bits(16) << 16) | (uint)Bits(16);

        bool ReadBlock()
        {
            for (;;)
            {
                ulong marker = ((ulong)Bits(24) << 24) | (uint)Bits(24);
                uint expectedCrc = Checksum();
                if (marker == BlockMarker)
                {
                    if (Bits(1) != 0) throw new NotSupportedException("Randomized bzip2 blocks are not supported");
                    int origin = Bits(24);
                    byte[] encoded = new byte[_blockSize];
                    int size = DecodeBlock(encoded);
                    if (origin >= size) throw Invalid("invalid block origin");
                    _block = ExpandBlock(encoded, size, origin, expectedCrc);
                    _offset = 0;
                    _streamCrc = ((_streamCrc << 1) | (_streamCrc >> 31)) ^ expectedCrc;
                    return true;
                }
                if (marker != EndMarker || expectedCrc != _streamCrc) throw Invalid("invalid end-of-stream marker or checksum");
                if (!ReadHeader(first: false)) return false;
            }
        }

        int DecodeBlock(byte[] output)
        {
            byte[] alphabet = new byte[256];
            int used = 0, groupsUsed = Bits(16);
            for (int group = 0; group < 16; group++)
            {
                if ((groupsUsed & (1 << (15 - group))) == 0) continue;
                int mask = Bits(16);
                for (int slot = 0; slot < 16; slot++)
                    if ((mask & (1 << (15 - slot))) != 0) alphabet[used++] = (byte)(16 * group + slot);
            }
            int groupCount = Bits(3), selectorCount = Bits(15);
            if (used == 0 || groupCount is < 2 or > 6 || selectorCount is < 1 or > MaxSelectors)
                throw Invalid("invalid Huffman group count");
            byte[] groups = new byte[groupCount];
            for (int i = 0; i < groupCount; i++) groups[i] = (byte)i;
            byte[] selectors = new byte[selectorCount];
            for (int i = 0; i < selectorCount; i++)
            {
                int position = 0;
                while (Bits(1) != 0)
                    if (++position >= groupCount) throw Invalid("invalid Huffman selector");
                selectors[i] = MoveToFront(groups, position);
            }

            var codes = new HuffmanCode[groupCount];
            for (int group = 0; group < groupCount; group++)
            {
                int length = Bits(5);
                int[] lengths = new int[used + 2];
                for (int symbol = 0; symbol < lengths.Length; symbol++)
                {
                    while (Bits(1) != 0)
                    {
                        length += Bits(1) == 0 ? 1 : -1;
                        if (length is < 1 or > MaxCodeLength) throw Invalid("invalid Huffman code length");
                    }
                    if (length is < 1 or > MaxCodeLength) throw Invalid("invalid Huffman code length");
                    lengths[symbol] = length;
                }
                codes[group] = new HuffmanCode(lengths);
            }

            int cursor = 0, remaining = 0, written = 0;
            HuffmanCode code = codes[0];
            int NextSymbol()
            {
                if (remaining == 0)
                {
                    if (cursor == selectors.Length) throw Invalid("too few Huffman selectors");
                    code = codes[selectors[cursor++]];
                    remaining = GroupSize;
                }
                remaining--;
                return code.Read(this);
            }

            int current = NextSymbol();
            while (current != used + 1)
            {
                if (current is 0 or 1)
                {
                    int run = 0, weight = 1;
                    do
                    {
                        if (weight > output.Length) throw Invalid("run exceeds the block size");
                        run += weight * (current + 1);
                        weight <<= 1;
                        current = NextSymbol();
                    } while (current is 0 or 1);
                    if (run > output.Length - written) throw Invalid("run exceeds the block size");
                    output.AsSpan(written, run).Fill(alphabet[0]);
                    written += run;
                }
                else
                {
                    if (written == output.Length) throw Invalid("block exceeds its declared size");
                    output[written++] = MoveToFront(alphabet, current - 1);
                    current = NextSymbol();
                }
            }
            return written;
        }

        static byte MoveToFront(byte[] items, int position)
        {
            byte value = items[position];
            Array.Copy(items, 0, items, 1, position);
            items[0] = value;
            return value;
        }

        static byte[] ExpandBlock(byte[] encoded, int size, int origin, uint expectedCrc)
        {
            // Invert the Burrows-Wheeler transform, then reverse the initial byte run encoding.
            int[] counts = new int[256];
            for (int i = 0; i < size; i++) counts[encoded[i]]++;
            int total = 0;
            for (int i = 0; i < counts.Length; i++) { int count = counts[i]; counts[i] = total; total += count; }
            int[] next = new int[size];
            for (int i = 0; i < size; i++) next[counts[encoded[i]]++] = i;

            using var result = new MemoryStream(size);
            uint crc = uint.MaxValue;
            int position = next[origin], previous = -1, run = 0;
            for (int i = 0; i < size; i++)
            {
                byte value = encoded[position];
                position = next[position];
                if (run == 4)
                {
                    for (int copies = 0; copies < value; copies++) Emit((byte)previous);
                    previous = -1;
                    run = 0;
                }
                else
                {
                    Emit(value);
                    run = value == previous ? run + 1 : 1;
                    previous = value;
                }
            }
            if (run == 4 || ~crc != expectedCrc) throw Invalid("invalid block checksum or run");
            return result.ToArray();

            void Emit(byte value)
            {
                result.WriteByte(value);
                crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ value];
            }
        }

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i << 24;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc << 1) ^ ((crc & 0x80000000) == 0 ? 0u : 0x04c11db7u);
                table[i] = crc;
            }
            return table;
        }

        sealed class HuffmanCode
        {
            readonly int[] _first = new int[MaxCodeLength + 1], _counts = new int[MaxCodeLength + 1],
                           _offsets = new int[MaxCodeLength + 1], _symbols;

            public HuffmanCode(int[] lengths)
            {
                foreach (int length in lengths) _counts[length]++;
                _symbols = new int[lengths.Length];
                int first = 0, offset = 0;
                for (int length = 1; length <= MaxCodeLength; length++)
                {
                    _first[length] = first;
                    _offsets[length] = offset;
                    for (int symbol = 0; symbol < lengths.Length; symbol++)
                        if (lengths[symbol] == length) _symbols[offset++] = symbol;
                    if (first + _counts[length] > 1 << length) throw Invalid("overloaded Huffman code");
                    first = (first + _counts[length]) << 1;
                }
            }

            public int Read(BZip2Stream reader)
            {
                int value = 0;
                for (int length = 1; length <= MaxCodeLength; length++)
                {
                    value = (value << 1) | reader.Bits(1);
                    int index = value - _first[length];
                    if (index >= 0 && index < _counts[length]) return _symbols[_offsets[length] + index];
                }
                throw Invalid("invalid Huffman code");
            }
        }

        static EndOfStreamException Truncated() =>
            new EndOfStreamException("Compressed file ended before the end-of-stream marker was reached");

        static IOException Invalid(string reason) => new IOException("Invalid bzip2 data: " + reason);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty || _finished) return 0;
            if (_offset == _block.Length && !ReadBlock()) { _finished = true; return 0; }
            int count = Math.Min(buffer.Length, _block.Length - _offset);
            _block.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            _position += count;
            return count;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing) _source.Dispose();
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
