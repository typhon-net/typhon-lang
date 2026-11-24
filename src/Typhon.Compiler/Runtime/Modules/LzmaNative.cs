// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Reflection;
    using System.Runtime.InteropServices;

    // The liblzma C ABI. Native compression state stays in this library.
    internal static class LzmaNative
    {
        const string Library = "typhon-liblzma";

        static readonly Lazy<IntPtr> Handle = new(Load);
        internal static bool IsAvailable => Handle.Value != IntPtr.Zero;

        static LzmaNative() => NativeLibrary.SetDllImportResolver(typeof(LzmaNative).Assembly,
            (name, _, _) => name == Library ? Handle.Value : IntPtr.Zero);

        static IntPtr Load()
        {
            string[] names = OperatingSystem.IsWindows() ? ["liblzma.dll", "liblzma-5.dll"]
                : OperatingSystem.IsMacOS() ? ["liblzma.5.dylib", "liblzma.dylib"]
                : ["liblzma.so.5", "liblzma.so"];
            Assembly assembly = typeof(LzmaNative).Assembly;
            string? configured = Environment.GetEnvironmentVariable("TYPHON_LIBLZMA");
            if (configured != null && System.IO.Path.IsPathFullyQualified(configured) &&
                NativeLibrary.TryLoad(configured, assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories, out IntPtr selected))
                return selected;
            foreach (string name in names)
                if (NativeLibrary.TryLoad(name, assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out IntPtr result))
                    return result;
            return IntPtr.Zero;
        }

        internal static void RequireLibrary()
        {
            if (!IsAvailable) throw new Typhon.Runtime.ImportError("liblzma is unavailable", "_lzma");
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Stream
        {
            internal IntPtr NextInput;
            internal nuint AvailableInput;
            ulong _totalInput;
            internal IntPtr NextOutput;
            internal nuint AvailableOutput;
            ulong _totalOutput;
            IntPtr _allocator, _internal, _reservedPointer1, _reservedPointer2, _reservedPointer3, _reservedPointer4;
            ulong _seekPosition, _reservedInteger2;
            nuint _reservedInteger3, _reservedInteger4;
            int _reservedEnumeration1, _reservedEnumeration2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Filter
        {
            internal ulong Id;
            internal IntPtr Options;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LzmaOptions
        {
            internal uint DictionarySize;
            IntPtr _presetDictionary;
            uint _presetDictionarySize;
            internal uint LiteralContextBits, LiteralPositionBits, PositionBits, Mode, NiceLength, MatchFinder, Depth;
            uint _extendedFlags, _extendedSizeLow, _extendedSizeHigh;
            uint _reservedInteger4, _reservedInteger5, _reservedInteger6, _reservedInteger7, _reservedInteger8;
            int _reservedEnumeration1, _reservedEnumeration2, _reservedEnumeration3, _reservedEnumeration4;
            IntPtr _reservedPointer1, _reservedPointer2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DeltaOptions
        {
            int _type;
            internal uint Distance;
            uint _reservedInteger1, _reservedInteger2, _reservedInteger3, _reservedInteger4;
            IntPtr _reservedPointer1, _reservedPointer2;
        }

        internal enum Result
        {
            Ok, StreamEnd, NoCheck, UnsupportedCheck, GetCheck, MemoryError, MemoryLimit,
            FormatError, OptionsError, DataError, BufferError, ProgramError
        }

        internal enum Action { Run = 0, Finish = 3 }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_easy_encoder(ref Stream stream, uint preset, uint check);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_stream_encoder(ref Stream stream, [In] Filter[] filters, uint check);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_alone_encoder(ref Stream stream, in LzmaOptions options);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_raw_encoder(ref Stream stream, [In] Filter[] filters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_auto_decoder(ref Stream stream, ulong limit, uint flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_stream_decoder(ref Stream stream, ulong limit, uint flags);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_alone_decoder(ref Stream stream, ulong limit);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_raw_decoder(ref Stream stream, [In] Filter[] filters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_code(ref Stream stream, Action action);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void lzma_end(ref Stream stream);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lzma_get_check(ref Stream stream);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool lzma_check_is_supported(uint check);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool lzma_lzma_preset(out LzmaOptions options, uint preset);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_properties_size(out uint size, in Filter filter);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_properties_encode(in Filter filter, [Out] byte[] properties);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern Result lzma_properties_decode(ref Filter filter, IntPtr allocator, byte[] properties, nuint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void lzma_filters_free([In, Out] Filter[] filters, IntPtr allocator);
    }
}
