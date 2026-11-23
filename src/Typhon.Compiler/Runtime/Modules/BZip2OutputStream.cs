// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>Encodes bzip2 members using a block-sized input buffer.</summary>
    internal sealed class BZip2OutputStream : Stream
    {
        const int GroupSize = 50, MaxCodeLength = 20;
        readonly Stream _destination;
        readonly byte[] _block;
        readonly byte[] _output = new byte[8192];
        readonly int _level;
        readonly bool _leaveOpen;
        int _length, _runLength, _bits, _outputLength;
        byte _runByte;
        uint _blockCrc = uint.MaxValue, _streamCrc, _bitBuffer;
        long _position;
        bool _started, _finished;

        public BZip2OutputStream(Stream destination, long level, bool leaveOpen = false)
        {
            if (level < 1 || level > 9) throw new ArgumentException("compresslevel must be between 1 and 9");
            _destination = destination;
            _level = (int)level;
            _block = new byte[_level * 100000];
            _leaveOpen = leaveOpen;
        }

        void Start()
        {
            if (_started) return;
            _destination.Write("BZh"u8);
            _destination.WriteByte((byte)('0' + _level));
            _started = true;
        }

        void Bits(uint value, int count)
        {
            _bitBuffer = (_bitBuffer << count) | value;
            _bits += count;
            while (_bits >= 8)
            {
                _bits -= 8;
                _output[_outputLength++] = (byte)(_bitBuffer >> _bits);
                if (_outputLength == _output.Length) WriteBufferedBytes();
            }
        }

        void Marker(ulong marker, uint crc)
        {
            Bits((uint)(marker >> 24), 24);
            Bits((uint)(marker & 0xffffff), 24);
            Bits(crc >> 16, 16);
            Bits(crc & 0xffff, 16);
        }

        void StoreRun()
        {
            if (_runLength == 0) return;
            int copies = Math.Min(4, _runLength);
            int size = _runLength >= 4 ? copies + 1 : copies;
            if (size > _block.Length - _length) EncodeBlock();
            _block.AsSpan(_length, copies).Fill(_runByte);
            _length += copies;
            if (_runLength >= 4) _block[_length++] = (byte)(_runLength - copies);
            for (int index = 0; index < _runLength; index++)
                _blockCrc = BZip2Stream.UpdateCrc(_blockCrc, _runByte);
            _runLength = 0;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            foreach (byte value in buffer)
            {
                if (_runLength == 255 || _runLength != 0 && value != _runByte) StoreRun();
                _runByte = value;
                _runLength++;
            }
            _position += buffer.Length;
        }

        // Stable counting sorts of cyclic prefixes build the Burrows-Wheeler transform.
        int[] SortedRotations()
        {
            int size = _length;
            int[] order = new int[size], buffer = new int[size];
            int[] ranks = new int[size], nextRanks = new int[size];
            int[] counts = new int[Math.Max(256, size)];
            for (int index = 0; index < size; index++) counts[_block[index]]++;
            Offsets(counts, 256);
            for (int index = 0; index < size; index++) order[counts[_block[index]]++] = index;

            int rankCount = 1;
            for (int index = 1; index < size; index++)
            {
                if (_block[order[index]] != _block[order[index - 1]]) rankCount++;
                ranks[order[index]] = rankCount - 1;
            }

            for (int width = 1; width < size && rankCount < size; width *= 2)
            {
                Array.Clear(counts, 0, rankCount);
                foreach (int rotation in order) counts[ranks[rotation]]++;
                Offsets(counts, rankCount);
                foreach (int rotation in order)
                {
                    int shifted = rotation - width;
                    if (shifted < 0) shifted += size;
                    buffer[counts[ranks[shifted]]++] = shifted;
                }

                int distinct = 1;
                nextRanks[buffer[0]] = 0;
                for (int index = 1; index < size; index++)
                {
                    int previous = buffer[index - 1], current = buffer[index];
                    int previousEnd = previous + width, currentEnd = current + width;
                    if (previousEnd >= size) previousEnd -= size;
                    if (currentEnd >= size) currentEnd -= size;
                    if (ranks[previous] != ranks[current] || ranks[previousEnd] != ranks[currentEnd]) distinct++;
                    nextRanks[current] = distinct - 1;
                }
                (order, buffer) = (buffer, order);
                (ranks, nextRanks) = (nextRanks, ranks);
                rankCount = distinct;
            }
            return order;
        }

        static void Offsets(int[] counts, int length)
        {
            int offset = 0;
            for (int index = 0; index < length; index++)
            {
                int count = counts[index];
                counts[index] = offset;
                offset += count;
            }
        }

        void EncodeBlock()
        {
            Start();
            uint crc = ~_blockCrc;
            Marker(0x314159265359, crc);
            _streamCrc = ((_streamCrc << 1) | (_streamCrc >> 31)) ^ crc;
            Bits(0, 1); // No randomized blocks.
            int[] rotations = SortedRotations();
            Bits((uint)Array.IndexOf(rotations, 0), 24);

            uint[] map = new uint[16];
            for (int index = 0; index < _length; index++)
            {
                byte value = _block[index];
                map[value / 16] |= 1u << (15 - value % 16);
            }
            uint groups = 0;
            for (int group = 0; group < map.Length; group++)
                if (map[group] != 0) groups |= 1u << (15 - group);
            Bits(groups, 16);
            var alphabet = new List<byte>(256);
            for (int group = 0; group < map.Length; group++)
            {
                if (map[group] == 0) continue;
                Bits(map[group], 16);
                for (int bit = 0; bit < 16; bit++)
                    if ((map[group] & (1u << (15 - bit))) != 0) alphabet.Add((byte)(group * 16 + bit));
            }

            byte[] front = alphabet.ToArray();
            var symbols = new List<int>(_length + 1);
            int run = 0;
            foreach (int rotation in rotations)
            {
                byte value = _block[rotation == 0 ? _length - 1 : rotation - 1];
                int index = Array.IndexOf(front, value);
                if (index == 0) { run++; continue; }
                FlushZeros();
                BZip2Stream.MoveToFront(front, index);
                symbols.Add(index + 1);
            }
            FlushZeros();
            symbols.Add(front.Length + 1);
            WriteSymbols(symbols, front.Length + 2);
            _length = 0;
            _blockCrc = uint.MaxValue;

            void FlushZeros()
            {
                while (run != 0)
                {
                    run--;
                    symbols.Add(run & 1);
                    run >>= 1;
                }
            }
        }

        void WriteSymbols(List<int> symbols, int alphabetSize)
        {
            int[] weights = new int[alphabetSize];
            Array.Fill(weights, 1);
            foreach (int symbol in symbols) weights[symbol]++;
            int[] lengths = CodeLengths(weights);

            // The format requires at least two tables. A single distribution serves every group here.
            Bits(2, 3);
            int selectors = (symbols.Count + GroupSize - 1) / GroupSize;
            Bits((uint)selectors, 15);
            for (int group = 0; group < selectors; group++) Bits(0, 1);
            for (int table = 0; table < 2; table++)
            {
                int current = lengths[0];
                Bits((uint)current, 5);
                foreach (int length in lengths)
                {
                    while (current != length)
                    {
                        bool increase = current < length;
                        Bits(increase ? 2u : 3u, 2);
                        current += increase ? 1 : -1;
                    }
                    Bits(0, 1);
                }
            }

            uint[] codes = new uint[alphabetSize];
            uint next = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                next <<= 1;
                for (int symbol = 0; symbol < alphabetSize; symbol++)
                    if (lengths[symbol] == length) codes[symbol] = next++;
            }
            foreach (int symbol in symbols) Bits(codes[symbol], lengths[symbol]);
        }

        static int[] CodeLengths(int[] weights)
        {
            int[] parents = new int[2 * weights.Length - 1];
            int[] lengths = new int[weights.Length];
            var queue = new PriorityQueue<int, (int Weight, int Index)>();
            while (true)
            {
                Array.Fill(parents, -1);
                for (int index = 0; index < weights.Length; index++) queue.Enqueue(index, (weights[index], index));
                int parent = weights.Length;
                while (queue.Count > 1)
                {
                    queue.TryDequeue(out int left, out var leftPriority);
                    queue.TryDequeue(out int right, out var rightPriority);
                    parents[left] = parents[right] = parent;
                    queue.Enqueue(parent, (leftPriority.Weight + rightPriority.Weight, parent));
                    parent++;
                }
                queue.Clear();
                int longest = 0;
                for (int index = 0; index < lengths.Length; index++)
                {
                    int length = 0;
                    for (int node = index; parents[node] >= 0; node = parents[node]) length++;
                    lengths[index] = length;
                    longest = Math.Max(longest, length);
                }
                if (longest <= MaxCodeLength) return lengths;
                for (int index = 0; index < weights.Length; index++) weights[index] = 1 + weights[index] / 2;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_finished;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        void WriteBufferedBytes()
        {
            if (_outputLength == 0) return;
            _destination.Write(_output.AsSpan(0, _outputLength));
            _outputLength = 0;
        }

        public override void Flush()
        {
            WriteBufferedBytes();
            _destination.Flush();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_finished)
            {
                try
                {
                    Start();
                    StoreRun();
                    if (_length > 0) EncodeBlock();
                    Marker(0x177245385090, _streamCrc);
                    if (_bits != 0) Bits(0, 8 - _bits);
                    Flush();
                }
                finally
                {
                    _finished = true;
                    if (!_leaveOpen) _destination.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
