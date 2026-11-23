// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;
    using static LzmaNative;

    internal static class _lzma
    {
        public const long FORMAT_AUTO = 0, FORMAT_XZ = 1, FORMAT_ALONE = 2, FORMAT_RAW = 3;
        public const long CHECK_NONE = 0, CHECK_CRC32 = 1, CHECK_CRC64 = 4, CHECK_SHA256 = 10,
            CHECK_ID_MAX = 15, CHECK_UNKNOWN = 16;
        public const long FILTER_LZMA1 = 0x4000000000000001, FILTER_LZMA2 = 0x21, FILTER_DELTA = 3,
            FILTER_X86 = 4, FILTER_POWERPC = 5, FILTER_IA64 = 6, FILTER_ARM = 7, FILTER_ARMTHUMB = 8, FILTER_SPARC = 9;
        public const long MF_HC3 = 3, MF_HC4 = 4, MF_BT2 = 18, MF_BT3 = 19, MF_BT4 = 20,
            MODE_FAST = 1, MODE_NORMAL = 2, PRESET_DEFAULT = 6, PRESET_EXTREME = 1L << 31;

        public sealed class LZMAError : PyExceptionBase
        {
            public LZMAError(string message) : base(message) { }
        }

        public static bool is_check_supported(long check)
        {
            RequireLibrary();
            return check is >= 0 and <= CHECK_ID_MAX && lzma_check_is_supported((uint)check);
        }

        public sealed class LZMACompressor
        {
            readonly Coder _coder = new();
            bool _flushed;

            public LZMACompressor(long format = FORMAT_XZ, long check = -1, object? preset = null, object? filters = null)
            {
                if (format != FORMAT_XZ && check is not (-1 or CHECK_NONE))
                    throw new ArgumentException("Integrity checks are only supported by FORMAT_XZ");
                if (preset != null && filters != null)
                    throw new ArgumentException("Cannot specify both preset and filter chain");
                RequireLibrary();
                using var chain = new FilterChain(filters);
                uint level = preset == null ? (uint)PRESET_DEFAULT : checked((uint)Unsigned(preset));
                uint checksum = (uint)(check == -1 ? CHECK_CRC64 : check);
                Result result;
                switch (format)
                {
                    case FORMAT_XZ:
                        result = filters == null
                            ? lzma_easy_encoder(ref _coder.State, level, checksum)
                            : lzma_stream_encoder(ref _coder.State, chain.Filters, checksum);
                        break;
                    case FORMAT_ALONE:
                        LzmaOptions options;
                        if (filters == null)
                            options = Preset(level);
                        else
                        {
                            if (chain.Filters.Length != 2 || chain.Filters[0].Id != (ulong)FILTER_LZMA1)
                                throw new ArgumentException("Invalid filter chain for FORMAT_ALONE - must be a single LZMA1 filter");
                            options = Marshal.PtrToStructure<LzmaOptions>(chain.Filters[0].Options);
                        }
                        result = lzma_alone_encoder(ref _coder.State, in options);
                        break;
                    case FORMAT_RAW:
                        if (filters == null) throw new ArgumentException("Must specify filters for FORMAT_RAW");
                        result = lzma_raw_encoder(ref _coder.State, chain.Filters);
                        break;
                    default: throw new ArgumentException($"Invalid container format: {format}");
                }
                _coder.Initialize(result);
            }

            public PyBytes compress(object data)
            {
                lock (_coder)
                {
                    Check();
                    return _coder.Process(ByteOps.Buffer(data), -1, LzmaNative.Action.Run).Data;
                }
            }

            public PyBytes flush()
            {
                lock (_coder)
                {
                    Check();
                    _flushed = true;
                    try { return _coder.Process([], -1, LzmaNative.Action.Finish).Data; }
                    finally { _coder.Close(); }
                }
            }

            void Check()
            {
                if (_flushed) throw new ArgumentException("Compressor has been flushed");
            }
        }

        public sealed class LZMADecompressor
        {
            readonly Coder _coder = new();
            byte[] _pending = [];

            public long check { get; private set; }
            public bool eof { get; private set; }
            public bool needs_input { get; private set; } = true;
            public PyBytes unused_data { get; private set; } = new([]);

            public LZMADecompressor(long format = FORMAT_AUTO, object? memlimit = null, object? filters = null)
            {
                if (format == FORMAT_RAW && memlimit != null)
                    throw new ArgumentException("Cannot specify memory limit with FORMAT_RAW");
                if (format != FORMAT_RAW && filters != null)
                    throw new ArgumentException("Cannot specify filters except with FORMAT_RAW");
                if (format == FORMAT_RAW && filters == null)
                    throw new ArgumentException("Must specify filters for FORMAT_RAW");
                RequireLibrary();
                using var chain = new FilterChain(filters);
                ulong limit = memlimit == null ? ulong.MaxValue : Unsigned(memlimit);
                const uint TellAnyCheck = 4;
                _coder.Initialize(format switch
                {
                    FORMAT_AUTO => lzma_auto_decoder(ref _coder.State, limit, TellAnyCheck),
                    FORMAT_XZ => lzma_stream_decoder(ref _coder.State, limit, TellAnyCheck),
                    FORMAT_ALONE => lzma_alone_decoder(ref _coder.State, limit),
                    FORMAT_RAW => lzma_raw_decoder(ref _coder.State, chain.Filters),
                    _ => throw new ArgumentException($"Invalid container format: {format}"),
                });
                check = format is FORMAT_ALONE or FORMAT_RAW ? CHECK_NONE : CHECK_UNKNOWN;
            }

            public PyBytes decompress(object data, long max_length = -1)
            {
                lock (_coder)
                {
                    if (eof) throw new EndOfStreamException("Already at end of stream");
                    byte[] added = ByteOps.Buffer(data);
                    byte[] input = _pending.Length == 0 ? added : added.Length == 0 ? _pending : [.. _pending, .. added];
                    var result = _coder.Process(input, max_length, LzmaNative.Action.Run);
                    if (_coder.Check is int checksum) check = checksum;
                    eof = result.End;
                    var remaining = input.AsSpan(result.Consumed);
                    if (eof)
                    {
                        _pending = [];
                        unused_data = new PyBytes(remaining.ToArray());
                        needs_input = false;
                        _coder.Close();
                    }
                    else
                    {
                        _pending = remaining.ToArray();
                        needs_input = _pending.Length == 0 && !result.OutputLimited;
                    }
                    return result.Data;
                }
            }
        }

        readonly record struct Chunk(PyBytes Data, int Consumed, bool End, bool OutputLimited);

        sealed class Coder
        {
            internal LzmaNative.Stream State;
            internal int? Check;
            bool _initialized;

            ~Coder() => Close();

            internal void Initialize(Result result)
            {
                _initialized = result == Result.Ok;
                Validate(result);
            }

            internal void Close()
            {
                if (!_initialized) return;
                _initialized = false;
                lzma_end(ref State);
                GC.SuppressFinalize(this);
            }

            internal Chunk Process(byte[] input, long maxLength, LzmaNative.Action action)
            {
                using var output = new MemoryStream();
                byte[] buffer = new byte[8192];
                var pinnedInput = GCHandle.Alloc(input, GCHandleType.Pinned);
                var pinnedOutput = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    State.NextInput = pinnedInput.AddrOfPinnedObject();
                    State.AvailableInput = (nuint)input.Length;
                    long remaining = maxLength < 0 ? long.MaxValue : maxLength;
                    while (true)
                    {
                        State.NextOutput = pinnedOutput.AddrOfPinnedObject();
                        int capacity = (int)Math.Min(buffer.Length, remaining);
                        State.AvailableOutput = (nuint)capacity;
                        nuint before = State.AvailableInput;
                        Result result = lzma_code(ref State, action);
                        int written = capacity - (int)State.AvailableOutput;
                        int consumed = input.Length - (int)State.AvailableInput;
                        output.Write(buffer, 0, written);
                        remaining -= written;
                        if (result is Result.GetCheck or Result.NoCheck or Result.UnsupportedCheck)
                        {
                            Check = lzma_get_check(ref State);
                            continue;
                        }
                        if (result == Result.StreamEnd)
                            return new Chunk(new PyBytes(output.ToArray()), consumed, true, false);
                        if (result != Result.BufferError || action == LzmaNative.Action.Finish) Validate(result);
                        if (remaining == 0 || action == LzmaNative.Action.Run &&
                            (State.AvailableInput == 0 && written != capacity || before == State.AvailableInput && written == 0))
                            return new Chunk(new PyBytes(output.ToArray()), consumed, false, remaining == 0);
                    }
                }
                finally
                {
                    State.NextInput = State.NextOutput = IntPtr.Zero;
                    State.AvailableInput = State.AvailableOutput = 0;
                    pinnedOutput.Free();
                    pinnedInput.Free();
                }
            }
        }

        public static PyBytes _encode_filter_properties(object filter)
        {
            RequireLibrary();
            using var chain = new FilterChain(new[] { filter });
            ref Filter item = ref chain.Filters[0];
            Validate(lzma_properties_size(out uint size, in item));
            byte[] output = new byte[size];
            if (size != 0) Validate(lzma_properties_encode(in item, output));
            return new PyBytes(output);
        }

        public static Dictionary<string, long> _decode_filter_properties(long filter_id, object encoded_props)
        {
            RequireLibrary();
            byte[] data = ByteOps.Buffer(encoded_props);
            Filter[] items = [new() { Id = (ulong)filter_id }, EndFilter];
            Validate(lzma_properties_decode(ref items[0], IntPtr.Zero, data, (nuint)data.Length));
            try
            {
                var result = new Dictionary<string, long> { ["id"] = filter_id };
                IntPtr options = items[0].Options;
                switch (filter_id)
                {
                    case FILTER_LZMA1:
                    case FILTER_LZMA2:
                        var lzma = Marshal.PtrToStructure<LzmaOptions>(options);
                        if (filter_id == FILTER_LZMA1)
                        {
                            result["lc"] = lzma.LiteralContextBits;
                            result["lp"] = lzma.LiteralPositionBits;
                            result["pb"] = lzma.PositionBits;
                        }
                        result["dict_size"] = lzma.DictionarySize;
                        break;
                    case FILTER_DELTA:
                        result["dist"] = Marshal.PtrToStructure<DeltaOptions>(options).Distance;
                        break;
                    default:
                        if (options != IntPtr.Zero) result["start_offset"] = (uint)Marshal.ReadInt32(options);
                        break;
                }
                return result;
            }
            finally { lzma_filters_free(items, IntPtr.Zero); }
        }

        static Filter EndFilter => new() { Id = ulong.MaxValue };

        sealed class FilterChain : IDisposable
        {
            internal readonly Filter[] Filters;

            internal FilterChain(object? value)
            {
                var filters = new List<Filter>(5);
                try
                {
                    if (value != null)
                    {
                        foreach (object? specification in Builtins.__iter_dyn(value))
                        {
                            if (filters.Count == 4) throw new ArgumentException("Too many filters - liblzma supports a maximum of 4");
                            filters.Add(ParseFilter(specification));
                        }
                    }
                    filters.Add(EndFilter);
                    Filters = filters.ToArray();
                }
                catch
                {
                    foreach (Filter filter in filters) Marshal.FreeHGlobal(filter.Options);
                    throw;
                }
            }

            public void Dispose()
            {
                foreach (Filter filter in Filters) Marshal.FreeHGlobal(filter.Options);
            }
        }

        static Filter ParseFilter(object? specification)
        {
            if (specification is not IDictionary options)
                throw new InvalidCastException("Filter specifier must be a dict");
            if (!options.Contains("id"))
                throw new ArgumentException("Filter specifier must have an \"id\" entry");
            ulong id = Unsigned(options["id"]);
            uint Get(string name, uint fallback) => options.Contains(name) ? checked((uint)Unsigned(options[name])) : fallback;
            void ValidateKeys(string filter, params string[] names)
            {
                var allowed = new HashSet<string>(names, StringComparer.Ordinal) { "id" };
                foreach (object key in options.Keys)
                    if (key is not string name || !allowed.Contains(name))
                        throw new ArgumentException($"Invalid filter specifier for {filter} filter");
            }
            switch (id)
            {
                case (ulong)FILTER_LZMA1:
                case (ulong)FILTER_LZMA2:
                    ValidateKeys("LZMA", "preset", "dict_size", "lc", "lp", "pb", "mode", "nice_len", "mf", "depth");
                    var lzma = Preset(Get("preset", (uint)PRESET_DEFAULT));
                    lzma.DictionarySize = Get("dict_size", lzma.DictionarySize);
                    lzma.LiteralContextBits = Get("lc", lzma.LiteralContextBits);
                    lzma.LiteralPositionBits = Get("lp", lzma.LiteralPositionBits);
                    lzma.PositionBits = Get("pb", lzma.PositionBits);
                    lzma.Mode = Get("mode", lzma.Mode);
                    lzma.NiceLength = Get("nice_len", lzma.NiceLength);
                    lzma.MatchFinder = Get("mf", lzma.MatchFinder);
                    lzma.Depth = Get("depth", lzma.Depth);
                    return Allocate(id, lzma);
                case (ulong)FILTER_DELTA:
                    ValidateKeys("delta", "dist");
                    return Allocate(id, new DeltaOptions { Distance = Get("dist", 1) });
                case >= (ulong)FILTER_X86 and <= (ulong)FILTER_SPARC:
                    ValidateKeys("BCJ", "start_offset");
                    return Allocate(id, Get("start_offset", 0));
                default: throw new ArgumentException("Invalid filter ID");
            }
        }

        static Filter Allocate<T>(ulong id, T options) where T : struct
        {
            IntPtr address = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(options, address, false);
            return new Filter { Id = id, Options = address };
        }

        static LzmaOptions Preset(uint preset)
        {
            if (lzma_lzma_preset(out var options, preset)) throw new LZMAError("Invalid or unsupported options");
            return options;
        }

        static ulong Unsigned(object? input)
        {
            object number = Builtins.__index_value(input);
            BigInteger integer = number is long narrow ? new BigInteger(narrow) : (BigInteger)number;
            if (integer.Sign < 0) throw new ArgumentException("Cannot convert negative int");
            return checked((ulong)integer);
        }

        static void Validate(Result result)
        {
            if (result == Result.Ok) return;
            if (result == Result.MemoryError) throw new OutOfMemoryException();
            throw new LZMAError(result switch
            {
                Result.UnsupportedCheck => "Unsupported integrity check",
                Result.MemoryLimit => "Memory usage limit exceeded",
                Result.FormatError => "Input format not supported by decoder",
                Result.OptionsError => "Invalid or unsupported options",
                Result.DataError => "Corrupt input data",
                Result.BufferError => "Insufficient buffer space",
                _ => "Internal error",
            });
        }
    }
}
