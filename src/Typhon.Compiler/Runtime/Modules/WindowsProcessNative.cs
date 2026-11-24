// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.Text;

    internal static class WindowsProcessNative
    {
        const int InsufficientBuffer = 122;
        const uint ExtendedStartupInfo = 0x00080000;
        const uint UnicodeEnvironment = 0x00000400;
        const uint WaitFailed = uint.MaxValue;
        static readonly UIntPtr HandleList = new(0x00020002u);

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal uint Size;
            IntPtr reserved, desktop, title;
            uint x, y, width, height, xChars, yChars, fill;
            internal uint Flags;
            internal ushort ShowWindow;
            ushort reservedBytes;
            IntPtr reservedData;
            internal IntPtr Input, Output, Error;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedInfo
        {
            internal StartupInfo Info;
            internal IntPtr Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessHandles
        {
            internal IntPtr Process, Thread;
            internal uint ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NativeCreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NativeStart(string? application, StringBuilder? commandLine, IntPtr processSecurity,
            IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
            IntPtr environment, string? directory, ref ExtendedInfo info, out ProcessHandles handles);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute,
            IntPtr value, nuint size, IntPtr previous, IntPtr resultSize);

        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "GetStdHandle", SetLastError = true)]
        static extern IntPtr NativeStdHandle(int number);

        [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
        static extern uint NativeFileType(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NativeClose(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NativeDuplicate(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
            uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritable, uint options);

        [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
        static extern uint NativeWait(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NativeExitCode(IntPtr process, out uint code);

        [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool NativeTerminate(IntPtr process, uint code);

        static Win32Exception Error() => new(Marshal.GetLastPInvokeError());

        internal static (IntPtr Reader, IntPtr Writer) CreatePipe(uint size)
        {
            if (!NativeCreatePipe(out IntPtr reader, out IntPtr writer, IntPtr.Zero, size)) throw Error();
            return (reader, writer);
        }

        internal static IntPtr StandardHandle(int number)
        {
            IntPtr handle = NativeStdHandle(number);
            if (handle == new IntPtr(-1)) throw Error();
            return handle;
        }

        internal static uint FileType(IntPtr handle)
        {
            uint type = NativeFileType(handle);
            if (type == 0 && Marshal.GetLastPInvokeError() != 0) throw Error();
            return type;
        }

        internal static void Close(IntPtr handle)
        {
            if (!NativeClose(handle)) throw Error();
        }

        internal static IntPtr Duplicate(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            uint access, bool inheritable, uint options)
        {
            if (!NativeDuplicate(sourceProcess, source, targetProcess, out IntPtr copy, access, inheritable, options))
                throw Error();
            return copy;
        }

        internal static uint Wait(IntPtr handle, uint milliseconds)
        {
            uint result = NativeWait(handle, milliseconds);
            if (result == WaitFailed) throw Error();
            return result;
        }

        internal static uint ExitCode(IntPtr process)
        {
            if (!NativeExitCode(process, out uint code)) throw Error();
            return code;
        }

        internal static void Terminate(IntPtr process, uint code)
        {
            if (!NativeTerminate(process, code)) throw Error();
        }

        internal static ProcessHandles Start(string? application, string? commandLine, bool inheritHandles,
            uint flags, string? environment, string? directory, StartupInfo startup, IntPtr[] inheritableHandles)
        {
            var info = new ExtendedInfo { Info = startup };
            var mutableCommandLine = commandLine == null ? null : new StringBuilder(commandLine);
            info.Info.Size = (uint)Marshal.SizeOf<StartupInfo>();
            IntPtr environmentBlock = IntPtr.Zero, attributes = IntPtr.Zero, handleBuffer = IntPtr.Zero;
            bool attributesInitialized = false;
            try
            {
                if (environment != null)
                {
                    environmentBlock = Marshal.StringToHGlobalUni(environment);
                    flags |= UnicodeEnvironment;
                }
                if (inheritableHandles.Length != 0)
                {
                    nuint size = 0;
                    if (InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size) ||
                        Marshal.GetLastPInvokeError() != InsufficientBuffer) throw Error();
                    attributes = Marshal.AllocHGlobal(checked((nint)size));
                    if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Error();
                    attributesInitialized = true;
                    int handleBytes = checked(inheritableHandles.Length * IntPtr.Size);
                    handleBuffer = Marshal.AllocHGlobal(handleBytes);
                    Marshal.Copy(inheritableHandles, 0, handleBuffer, inheritableHandles.Length);
                    if (!UpdateProcThreadAttribute(attributes, 0, HandleList, handleBuffer, (nuint)handleBytes,
                            IntPtr.Zero, IntPtr.Zero)) throw Error();
                    info.Attributes = attributes;
                    info.Info.Size = (uint)Marshal.SizeOf<ExtendedInfo>();
                    flags |= ExtendedStartupInfo;
                }
                if (!NativeStart(application, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles, flags,
                        environmentBlock, directory, ref info, out ProcessHandles result)) throw Error();
                return result;
            }
            finally
            {
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
                Marshal.FreeHGlobal(handleBuffer);
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }
}
