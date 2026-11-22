// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Sockets;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static class select_
    {
        const int UnixSetSize = 1024;
        const int WindowsSetSize = 512;

        [DllImport("libc", EntryPoint = "select", SetLastError = true)]
        static extern int UnixSelect(int count, [In, Out] nuint[] read, [In, Out] nuint[] write,
            [In, Out] nuint[] error, [In] nint[]? timeout);

        [DllImport("Ws2_32.dll", EntryPoint = "select", ExactSpelling = true, SetLastError = true)]
        static extern int WindowsSelect(int count, [In, Out] nint[] read, [In, Out] nint[] write,
            [In, Out] nint[] error, [In] int[]? timeout);

        public static (List<dynamic>, List<dynamic>, List<dynamic>) select(
            object rlist, object wlist, object xlist, object? timeout = null)
        {
            double? seconds = TimeoutSeconds(timeout);
            var readers = Entries(rlist);
            var writers = Entries(wlist);
            var exceptional = Entries(xlist);
            if (OperatingSystem.IsWindows()) return SelectWindows(readers, writers, exceptional, seconds);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return SelectUnix(readers, writers, exceptional, seconds);
            throw new PlatformNotSupportedException("select is supported on Linux, macOS, and Windows");
        }

        readonly record struct Entry(object Value, long Descriptor);

        static List<Entry> Entries(object sequence)
        {
            var entries = new List<Entry>();
            foreach (object? item in Builtins.__iter_dyn(sequence))
            {
                object? descriptor = item;
                if (!IsInteger(item))
                {
                    object? fileno = Builtins.getattr(item, "fileno", null);
                    if (fileno == null) throw new InvalidCastException("argument must be an int, or have a fileno() method.");
                    descriptor = Builtins.__invoke(fileno, Array.Empty<object?>());
                    if (!IsInteger(descriptor))
                        throw new InvalidCastException($"{Builtins.PyTypeName(item!.GetType())}.fileno() must return an int, not {(descriptor == null ? "NoneType" : Builtins.PyTypeName(descriptor.GetType()))}");
                }
                long fd = descriptor is BigInteger number ? checked((long)number) : Convert.ToInt64(descriptor);
                if (!OperatingSystem.IsWindows()) fd = checked((int)fd);
                if (fd < 0) throw new ArgumentException($"file descriptor cannot be a negative integer ({fd})");
                entries.Add(new Entry(item!, fd));
            }
            return entries;
        }

        static bool IsInteger(object? value) => value is long or int or BigInteger or bool;

        static double? TimeoutSeconds(object? value)
        {
            if (value == null) return null;
            double seconds = value switch
            {
                double real => real,
                float real => real,
                BigInteger integer => (double)integer,
                _ when IsInteger(value) => Convert.ToDouble(value),
                _ => throw new InvalidCastException($"timeout must be a real number or None, not {Builtins.PyTypeName(value.GetType())}"),
            };
            if (double.IsNaN(seconds)) throw new ArgumentException("Invalid value NaN (not a number)");
            if (seconds < 0.0) throw new ArgumentException("timeout must be non-negative");
            if (seconds >= (double)long.MaxValue / 1_000_000_000)
                throw new OverflowException("timestamp out of range for C PyTime_t");
            return seconds;
        }

        static (List<dynamic>, List<dynamic>, List<dynamic>) SelectUnix(
            List<Entry> readers, List<Entry> writers, List<Entry> exceptional, double? seconds)
        {
            var elapsed = Stopwatch.StartNew();
            int highDescriptor = -1;
            int wordBits = IntPtr.Size * 8;
            // Darwin uses 32-bit words for fd_set; packed words have the same layout on little-endian hosts.
            if (OperatingSystem.IsMacOS() && !BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("select on macOS requires a little-endian architecture");

            nuint[] Set(List<Entry> entries)
            {
                var bits = new nuint[UnixSetSize / wordBits];
                foreach (var entry in entries)
                {
                    if (entry.Descriptor >= UnixSetSize) throw new ArgumentException("filedescriptor out of range in select()");
                    int fd = (int)entry.Descriptor;
                    highDescriptor = Math.Max(highDescriptor, fd);
                    bits[fd / wordBits] |= (nuint)1 << (fd % wordBits);
                }
                return bits;
            }

            List<dynamic> Filter(List<Entry> entries, nuint[] bits) => entries
                .Where(e => (bits[e.Descriptor / wordBits] & ((nuint)1 << (int)(e.Descriptor % wordBits))) != 0)
                .Select(e => e.Value).ToList();

            while (true)
            {
                nuint[] r = Set(readers), w = Set(writers), x = Set(exceptional);
                nint[]? timeout = null;
                if (seconds is double limit)
                {
                    long micros = checked((long)Math.Ceiling(Math.Max(0.0, limit - elapsed.Elapsed.TotalSeconds) * 1_000_000));
                    timeout = new[] { checked((nint)(micros / 1_000_000)), (nint)(micros % 1_000_000) };
                }
                if (UnixSelect(highDescriptor + 1, r, w, x, timeout) >= 0)
                    return (Filter(readers, r), Filter(writers, w), Filter(exceptional, x));
                int code = Marshal.GetLastPInvokeError();
                if (code != errno.EINTR) throw new PyOSError((long)code, new Win32Exception(code).Message);
            }
        }

        static (List<dynamic>, List<dynamic>, List<dynamic>) SelectWindows(
            List<Entry> readers, List<Entry> writers, List<Entry> exceptional, double? seconds)
        {
            // Socket's static initialization starts Winsock before the native call.
            _ = Socket.OSSupportsIPv4;
            static nint[] Set(List<Entry> entries)
            {
                var handles = entries.Select(e => e.Descriptor).Distinct().ToArray();
                if (entries.Count > WindowsSetSize) throw new ArgumentException("too many file descriptors in select()");
                var set = new nint[handles.Length + 1];
                set[0] = handles.Length;
                for (int i = 0; i < handles.Length; i++) set[i + 1] = checked((nint)handles[i]);
                return set;
            }

            static List<dynamic> Filter(List<Entry> entries, nint[] set)
            {
                var ready = new HashSet<nint>(set.Skip(1).Take((int)set[0]));
                return entries.Where(e => ready.Contains((nint)e.Descriptor)).Select(e => e.Value).ToList();
            }

            nint[] r = Set(readers), w = Set(writers), x = Set(exceptional);
            int[]? timeout = null;
            if (seconds is double limit)
            {
                long micros = checked((long)Math.Ceiling(limit * 1_000_000));
                timeout = new[] { checked((int)(micros / 1_000_000)), (int)(micros % 1_000_000) };
            }
            if (WindowsSelect(0, r, w, x, timeout) < 0)
            {
                int code = Marshal.GetLastPInvokeError();
                throw new PyOSError((long)code, new SocketException(code).Message, null, (long)code);
            }
            return (Filter(readers, r), Filter(writers, w), Filter(exceptional, x));
        }
    }
}
