// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.ComponentModel;
    using System.Linq;
    using System.Numerics;
    using System.IO;
    using Microsoft.Win32.SafeHandles;
    using Typhon.Runtime;

    internal static partial class _winapi
    {
        public const long CREATE_NEW_CONSOLE = 0x00000010, CREATE_NEW_PROCESS_GROUP = 0x00000200,
            CREATE_NO_WINDOW = 0x08000000, DETACHED_PROCESS = 0x00000008,
            CREATE_DEFAULT_ERROR_MODE = 0x04000000, CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
            ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000, BELOW_NORMAL_PRIORITY_CLASS = 0x00004000,
            HIGH_PRIORITY_CLASS = 0x00000080, IDLE_PRIORITY_CLASS = 0x00000040,
            NORMAL_PRIORITY_CLASS = 0x00000020, REALTIME_PRIORITY_CLASS = 0x00000100;

        public const long STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12,
            SW_HIDE = 0, STARTF_USESTDHANDLES = 0x100, STARTF_USESHOWWINDOW = 1,
            STARTF_FORCEONFEEDBACK = 0x40, STARTF_FORCEOFFFEEDBACK = 0x80,
            FILE_TYPE_CHAR = 2, DUPLICATE_SAME_ACCESS = 2,
            WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258, STILL_ACTIVE = 259, INFINITE = uint.MaxValue;

        static T ProcessOperation<T>(Func<T> operation, string? filename = null)
        {
            try { return operation(); }
            catch (Win32Exception error) { throw FileError(error.NativeErrorCode, filename); }
        }

        static void ProcessOperation(Action operation)
        {
            try { operation(); }
            catch (Win32Exception error) { throw FileError(error.NativeErrorCode, null); }
        }

        public static long GetCurrentProcess() => WindowsProcessNative.GetCurrentProcess().ToInt64();

        public static dynamic? GetStdHandle(long std_handle)
        {
            long result = ProcessOperation(() => WindowsProcessNative.StandardHandle(checked((int)std_handle)).ToInt64());
            return result == 0 ? null : (object)result;
        }

        public static long GetFileType(object handle) => ProcessOperation(() => (long)WindowsProcessNative.FileType(new IntPtr(HandleInteger(handle))));

        public static (long, long) CreatePipe(object? pipe_attrs, long size)
        {
            var (reader, writer) = ProcessOperation(() => WindowsProcessNative.CreatePipe(checked((uint)size)));
            return (reader.ToInt64(), writer.ToInt64());
        }

        public static void CloseHandle(object handle) => ProcessOperation(() => WindowsProcessNative.Close(new IntPtr(HandleInteger(handle))));

        public static long DuplicateHandle(object source_process_handle, object source_handle, object target_process_handle,
            long desired_access, object inherit_handle, long options) =>
            ProcessOperation(() => WindowsProcessNative.Duplicate(new IntPtr(HandleInteger(source_process_handle)), new IntPtr(HandleInteger(source_handle)),
                new IntPtr(HandleInteger(target_process_handle)), checked((uint)desired_access), HandleInteger(inherit_handle) != 0, checked((uint)options)).ToInt64());

        public static long WaitForSingleObject(object handle, long milliseconds) =>
            ProcessOperation(() => (long)WindowsProcessNative.Wait(new IntPtr(HandleInteger(handle)), checked((uint)milliseconds)));

        public static long GetExitCodeProcess(object process) =>
            ProcessOperation(() => (long)WindowsProcessNative.ExitCode(new IntPtr(HandleInteger(process))));

        public static void TerminateProcess(object process, long exit_code) =>
            ProcessOperation(() => WindowsProcessNative.Terminate(new IntPtr(HandleInteger(process)), checked((uint)exit_code)));

        public static (long, long, long, long) CreateProcess(string? application_name, string? command_line,
            object? process_attributes, object? thread_attributes, object inherit_handles, long creation_flags,
            object? env_mapping, string? current_directory, object startup_info)
        {
            var startup = new WindowsProcessNative.StartupInfo
            {
                Flags = checked((uint)StartupInteger(startup_info, "dwFlags")),
                ShowWindow = checked((ushort)StartupInteger(startup_info, "wShowWindow")),
                Input = new IntPtr(StartupInteger(startup_info, "hStdInput")),
                Output = new IntPtr(StartupInteger(startup_info, "hStdOutput")),
                Error = new IntPtr(StartupInteger(startup_info, "hStdError")),
            };
            object? attributes = Builtins.getattr(startup_info, "lpAttributeList", null);
            object? handleList = attributes == null ? null :
                Builtins.__invoke((object?)Builtins.getattr(attributes, "get"), ["handle_list"]);
            IntPtr[] handles = handleList == null ? [] : Builtins.__iter_dyn(handleList).Cast<object?>()
                .Select(handle => new IntPtr(HandleInteger(handle))).Distinct().ToArray();
            string? environment = env_mapping == null ? null : EnvironmentBlock(env_mapping);
            var result = ProcessOperation(() => WindowsProcessNative.Start(application_name, command_line, HandleInteger(inherit_handles) != 0,
                checked((uint)creation_flags), environment, current_directory, startup, handles), application_name);
            return (result.Process.ToInt64(), result.Thread.ToInt64(), result.ProcessId, result.ThreadId);
        }

        static long StartupInteger(object startup, string name)
        {
            object? value = Builtins.getattr(startup, name, null);
            return value == null ? 0 : HandleInteger(value);
        }

        static long HandleInteger(object? value)
        {
            object index = Builtins.__index_value(value);
            return index is BigInteger wide ? checked((long)wide) : (long)index;
        }

        static string EnvironmentBlock(object mapping)
        {
            var entries = ProcessEnvironment.Entries(mapping).Select(pair =>
            {
                if (pair.Key is not string key || pair.Value is not string value)
                    throw new InvalidCastException("environment can only contain strings");
                if (key.Length == 0 || key.IndexOf('=', 1) >= 0 || key.Contains('\0') || value.Contains('\0'))
                    throw new ArgumentException("illegal environment variable name or value");
                return key + "=" + value;
            });
            return string.Join('\0', entries.OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)) + "\0\0";
        }
    }

    internal static partial class os_
    {
        internal static long RegisterWindowsFileHandle(long handle, long flags)
        {
            var access = (flags & 3) switch
            {
                O_WRONLY => FileAccess.Write,
                O_RDWR => FileAccess.ReadWrite,
                // With no explicit access flags the wrapped handle determines which operations are possible.
                _ => FileAccess.ReadWrite,
            };
            var owned = new SafeFileHandle(new IntPtr(handle), ownsHandle: true);
            try { return __register(new FileStream(owned, access, bufferSize: 1)); }
            catch { owned.Dispose(); throw; }
        }

        internal static long WindowsFileHandle(long descriptor)
        {
            if (descriptor is >= 0 and <= 2)
                return (object?)_winapi.GetStdHandle(_winapi.STD_INPUT_HANDLE - descriptor) is long standard ? standard : throw OsErrors.BadFd();
            return DescriptorHandle(__fdstream(descriptor))?.DangerousGetHandle().ToInt64() ?? throw OsErrors.BadFd();
        }
    }
}
