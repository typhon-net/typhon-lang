// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Match the Python module names.

namespace Typhon.Stdlib
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static class posix
    {
        public static long getpid() => os_.getpid();
        public static long getuid() => os_.getuid();
        public static long getgid() => os_.getgid();
        public static os_.stat_result stat(object path, object? dir_fd = null, bool follow_symlinks = true) =>
            os_.stat(path, dir_fd, follow_symlinks);
        public static os_.stat_result lstat(object path, object? dir_fd = null) => os_.lstat(path, dir_fd);
        public static os_.stat_result fstat(long fd) => os_.fstat(fd);
    }

    internal static class nt
    {
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpace(string path, out ulong available, out ulong total, out ulong free);

        public static (long, long) _getdiskusage(object path)
        {
            string directory = os_.__path(path);
            if (!GetDiskFreeSpace(directory, out _, out ulong total, out ulong free))
                throw _winapi.FileError(Marshal.GetLastPInvokeError(), directory);
            return (checked((long)total), checked((long)free));
        }
    }

    internal static class _winapi
    {
        public const long COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x00000008;
        public const long COPY_FILE_COPY_SYMLINK = 0x00000800;
        public const long ERROR_ACCESS_DENIED = 5;
        public const long ERROR_PRIVILEGE_NOT_HELD = 1314;

        [StructLayout(LayoutKind.Sequential)]
        struct CopyParameters
        {
            public uint Size, Flags;
            public IntPtr Cancel, Callback, Context;
        }

        [DllImport("kernel32.dll", EntryPoint = "CopyFile2", CharSet = CharSet.Unicode)]
        static extern int CopyFileNative(string source, string target, ref CopyParameters settings);

        [DllImport("kernel32.dll", EntryPoint = "NeedCurrentDirectoryForExePathW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NeedSearchCurrentDirectory(string executable);

        public static void CopyFile2(object existing_file_name, object new_file_name, long flags)
        {
            string source = os_.__path(existing_file_name);
            string target = os_.__path(new_file_name);
            var settings = new CopyParameters { Size = (uint)Marshal.SizeOf<CopyParameters>(), Flags = checked((uint)flags) };
            int result = CopyFileNative(source, target, ref settings);
            if (result < 0) throw FileError(result & 0xffff, source, target);
        }

        public static bool NeedCurrentDirectoryForExePath(string exe_name) => NeedSearchCurrentDirectory(exe_name);

        internal static Exception FileError(int code, string filename, string? filename2 = null) =>
            OsErrors.WinError(code, new Win32Exception(code).Message, filename, filename2);
    }
}
