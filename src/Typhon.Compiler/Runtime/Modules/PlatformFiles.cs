// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Match the Python module names.

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static class posix
    {
        public static readonly string[] _have_functions =
            ["HAVE_FSTATAT", "HAVE_LSTAT", "HAVE_MKDIRAT", "HAVE_OPENAT", "HAVE_UNLINKAT", "HAVE_FDOPENDIR"];
        public static readonly long O_NONBLOCK = os_.O_NONBLOCK;

        public static readonly OrderedDictionary<PyBytes, PyBytes> environ = CreateEnviron();
        public static OrderedDictionary<PyBytes, PyBytes> _create_environ() => CreateEnviron();

        static OrderedDictionary<PyBytes, PyBytes> CreateEnviron()
        {
            var result = new OrderedDictionary<PyBytes, PyBytes>();
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                result[os_.fsencode((string)item.Key)] = os_.fsencode((string)item.Value!);
            return result;
        }
        public static long getpid() => os_.getpid();
        public static long open(object path, long flags, long mode = 0x1ff, object? dir_fd = null) =>
            os_.open(os_.__path(path), flags, mode, dir_fd);
        public static void putenv(object key, object value) => os_.putenv(os_.fsdecode(key), os_.fsdecode(value));
        public static void unsetenv(object key) => os_.unsetenv(os_.fsdecode(key));
        public static void mkdir(object path, long mode = 0x1ff, object? dir_fd = null) =>
            os_.mkdir(os_.__path(path), mode, dir_fd);
        public static void rmdir(object path, object? dir_fd = null) =>
            os_.rmdir(os_.__path(path), dir_fd);
        public static void rename(object src, object dst) => os_.rename(os_.__path(src), os_.__path(dst));
        public static object scandir(object? path = null) => os_.ScandirPath(path);
        public static void close(long fd) => os_.close(fd);
        public static (long, long) pipe() => os_.pipe();
        public static PyBytes read(long fd, long n) => os_.read(fd, n);
        public static long write(long fd, object data) => os_.write(fd, data);
        public static long cpu_count() => os_.cpu_count();
        public static void _exit(long n) => os_._exit(n);
        public const long O_RDONLY = os_.O_RDONLY;
        public static long waitstatus_to_exitcode(object? status) => os_.waitstatus_to_exitcode(status);

        public const long POSIX_SPAWN_OPEN = UnixSpawn.Open, POSIX_SPAWN_CLOSE = UnixSpawn.Close, POSIX_SPAWN_DUP2 = UnixSpawn.Duplicate;

        public static long posix_spawn([PyPositionalOnly] object path, [PyPositionalOnly] object argv, [PyPositionalOnly] object? env,
            [PyKeywordOnly] object? file_actions = null, [PyKeywordOnly] object? setpgroup = null,
            [PyKeywordOnly] bool resetids = false, [PyKeywordOnly] bool setsid = false,
            [PyKeywordOnly] object? setsigmask = null, [PyKeywordOnly] object? setsigdef = null, [PyKeywordOnly] object? scheduler = null) =>
            UnixSpawn.Start(false, path, argv, env, file_actions, setpgroup, resetids, setsid, setsigmask, setsigdef, scheduler);

        public static long posix_spawnp([PyPositionalOnly] object path, [PyPositionalOnly] object argv, [PyPositionalOnly] object? env,
            [PyKeywordOnly] object? file_actions = null, [PyKeywordOnly] object? setpgroup = null,
            [PyKeywordOnly] bool resetids = false, [PyKeywordOnly] bool setsid = false,
            [PyKeywordOnly] object? setsigmask = null, [PyKeywordOnly] object? setsigdef = null, [PyKeywordOnly] object? scheduler = null) =>
            UnixSpawn.Start(true, path, argv, env, file_actions, setpgroup, resetids, setsid, setsigmask, setsigdef, scheduler);

        public const long WNOHANG = os_.WNOHANG;
        public static (long, long) waitpid(long pid, long options) => os_.waitpid(pid, options);
        public static bool WIFSTOPPED(object? status) => os_.WIFSTOPPED(status);
        public static long WSTOPSIG(object? status) => os_.WSTOPSIG(status);

        public static long getuid() => os_.getuid();
        public static long getgid() => os_.getgid();
        public static os_.stat_result stat(object path, object? dir_fd = null, bool follow_symlinks = true) =>
            os_.stat(path, dir_fd, follow_symlinks);
        public static os_.stat_result lstat(object path, object? dir_fd = null) => os_.lstat(path, dir_fd);
        public static os_.stat_result fstat(long fd) => os_.fstat(fd);
    }

    internal static class nt
    {
        public static readonly string[] _have_functions = ["MS_WINDOWS"];

        public static os_.stat_result stat(object path, object? dir_fd = null, bool follow_symlinks = true) =>
            os_.stat(path, dir_fd, follow_symlinks);
        public static os_.stat_result lstat(object path, object? dir_fd = null) => os_.lstat(path, dir_fd);
        public static os_.stat_result fstat(long fd) => os_.fstat(fd);

        public static readonly OrderedDictionary<string, string> environ = CreateEnviron();
        public static OrderedDictionary<string, string> _create_environ() => CreateEnviron();

        static OrderedDictionary<string, string> CreateEnviron()
        {
            var result = new OrderedDictionary<string, string>();
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                result[((string)item.Key).ToUpperInvariant()] = (string)item.Value!;
            return result;
        }
        public static long getpid() => os_.getpid();
        public static long open(object path, long flags, long mode = 0x1ff, object? dir_fd = null) =>
            os_.open(os_.__path(path), flags, mode, dir_fd);
        public static void putenv(object key, object value) => os_.putenv(os_.fsdecode(key), os_.fsdecode(value));
        public static void unsetenv(object key) => os_.unsetenv(os_.fsdecode(key));
        public static void mkdir(object path, long mode = 0x1ff, object? dir_fd = null) =>
            os_.mkdir(os_.__path(path), mode, dir_fd);
        public static void rmdir(object path, object? dir_fd = null) =>
            os_.rmdir(os_.__path(path), dir_fd);
        public static void rename(object src, object dst) => os_.rename(os_.__path(src), os_.__path(dst));
        public static object scandir(object? path = null) => os_.ScandirPath(path);
        public static void close(long fd) => os_.close(fd);
        public static (long, long) pipe() => os_.pipe();
        public static PyBytes read(long fd, long n) => os_.read(fd, n);
        public static long write(long fd, object data) => os_.write(fd, data);
        public static long cpu_count() => os_.cpu_count();
        public static void _exit(long n) => os_._exit(n);
        public const long O_RDONLY = os_.O_RDONLY;
        public static long waitstatus_to_exitcode(object? status) => os_.waitstatus_to_exitcode(status);

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
