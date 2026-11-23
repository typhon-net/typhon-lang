// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using Microsoft.Win32.SafeHandles;
    using Typhon.Runtime;

    internal static class termios
    {
        public const long IGNBRK = 1, BRKINT = 2, IGNPAR = 4, PARMRK = 8, INPCK = 16, ISTRIP = 32,
            INLCR = 64, IGNCR = 128, ICRNL = 256, IXON = 1024, IXANY = 2048, IXOFF = 4096;
        public const long OPOST = 1, PARENB = 256, CSIZE = 48, CS8 = 48;
        public const long ISIG = 1, ICANON = 2, ECHO = 8, ECHOE = 16, ECHOK = 32, ECHONL = 64,
            NOFLSH = 128, TOSTOP = 256, IEXTEN = 32768;
        public const long VINTR = 0, VQUIT = 1, VERASE = 2, VKILL = 3, VEOF = 4, VTIME = 5, VMIN = 6,
            VSWTC = 7, VSTART = 8, VSTOP = 9, VSUSP = 10, VEOL = 11, VREPRINT = 12, VDISCARD = 13,
            VWERASE = 14, VLNEXT = 15, VEOL2 = 16, NCCS = 32;
        public const long TCSANOW = 0, TCSADRAIN = 1, TCSAFLUSH = 2, TCIFLUSH = 0, TCOFLUSH = 1, TCIOFLUSH = 2;
        public const long TCOOFF = 0, TCOON = 1, TCIOFF = 2, TCION = 3, B0 = 0, B9600 = 13, B19200 = 14,
            B38400 = 15, B57600 = 4097, B115200 = 4098;

        public sealed class error : PyExceptionBase
        {
            public error(int errno) : base((long)errno, OsErrors.StrError(errno)) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LinuxAttributes
        {
            public uint Input, Output, Control, Local;
            public byte LineDiscipline;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = (int)NCCS)]
            public byte[] Characters;
            public uint InputSpeed, OutputSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowSize
        {
            public ushort Rows, Columns, PixelWidth, PixelHeight;
        }

        [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
        static extern int ReadAttributes(int fd, ref LinuxAttributes attributes);
        [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
        static extern int WriteAttributes(int fd, int when, ref LinuxAttributes attributes);
        [DllImport("libc", EntryPoint = "cfgetispeed")]
        static extern uint ReadInputSpeed(ref LinuxAttributes attributes);
        [DllImport("libc", EntryPoint = "cfgetospeed")]
        static extern uint ReadOutputSpeed(ref LinuxAttributes attributes);
        [DllImport("libc", EntryPoint = "cfsetispeed", SetLastError = true)]
        static extern int WriteInputSpeed(ref LinuxAttributes attributes, uint speed);
        [DllImport("libc", EntryPoint = "cfsetospeed", SetLastError = true)]
        static extern int WriteOutputSpeed(ref LinuxAttributes attributes, uint speed);
        [DllImport("libc", EntryPoint = "tcdrain", SetLastError = true)]
        static extern int Drain(int fd);
        [DllImport("libc", EntryPoint = "tcflush", SetLastError = true)]
        static extern int Flush(int fd, int queue);
        [DllImport("libc", EntryPoint = "tcflow", SetLastError = true)]
        static extern int Flow(int fd, int action);
        [DllImport("libc", EntryPoint = "tcsendbreak", SetLastError = true)]
        static extern int SendBreak(int fd, int duration);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        static extern int ControlWindow(int fd, nuint request, ref WindowSize size);

        internal static bool Available => OperatingSystem.IsLinux() &&
            RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

        static void Check(int result)
        {
            if (result < 0) throw new error(Marshal.GetLastPInvokeError());
        }

        static T WithTerminal<T>(object? descriptor, Func<int, T> operate)
        {
            if (!Available) throw new ImportError("No module named 'termios'");
            object? value = descriptor;
            if (value is not (long or int or bool or System.Numerics.BigInteger))
            {
                var fileno = Builtins.getattr(value, "fileno", null);
                if (fileno == null) throw new InvalidCastException("argument must be an int, or have a fileno() method.");
                value = Builtins.__invoke((object)fileno, []);
                if (value is not (long or int or bool or System.Numerics.BigInteger))
                    throw new InvalidCastException("fileno() returned a non-integer");
            }
            long fd = Builtins.@int(value);
            if (fd < 0) throw new ArgumentException($"file descriptor cannot be a negative integer ({fd})");
            return os_.WithUnixDescriptor(checked((int)fd), operate, () => new error(9));
        }

        static LinuxAttributes Read(int fd)
        {
            var settings = new LinuxAttributes { Characters = new byte[NCCS] };
            Check(ReadAttributes(fd, ref settings));
            return settings;
        }

        public static List<dynamic> tcgetattr(object fd) => WithTerminal<List<dynamic>>(fd, descriptor =>
        {
            var settings = Read(descriptor);
            var control = new List<dynamic>(settings.Characters.Length);
            for (int i = 0; i < settings.Characters.Length; i++)
            {
                byte value = settings.Characters[i];
                if ((settings.Local & ICANON) == 0 && i is (int)VMIN or (int)VTIME) control.Add((long)value);
                else control.Add(new PyBytes(new[] { value }));
            }
            return [(long)settings.Input, (long)settings.Output, (long)settings.Control, (long)settings.Local,
                (long)ReadInputSpeed(ref settings), (long)ReadOutputSpeed(ref settings), control];
        });

        public static object? tcsetattr(object fd, long when, object attributes) => WithTerminal<object?>(fd, descriptor =>
        {
            if (attributes is not IList { Count: 7 } mode)
                throw new InvalidCastException("tcsetattr, arg 3: must be 7 element list");
            if (mode[6] is not IList { Count: (int)NCCS } control)
                throw new InvalidCastException($"tcsetattr: attributes[6] must be {NCCS} element list");
            var settings = Read(descriptor);
            settings.Input = checked((uint)Builtins.@int(mode[0]));
            settings.Output = checked((uint)Builtins.@int(mode[1]));
            settings.Control = checked((uint)Builtins.@int(mode[2]));
            settings.Local = checked((uint)Builtins.@int(mode[3]));
            Check(WriteInputSpeed(ref settings, checked((uint)Builtins.@int(mode[4]))));
            Check(WriteOutputSpeed(ref settings, checked((uint)Builtins.@int(mode[5]))));
            for (int i = 0; i < settings.Characters.Length; i++)
                settings.Characters[i] = control[i] switch
                {
                    PyBytes { Count: 1 } character => character.Data[0],
                    long value => unchecked((byte)value),
                    int value => unchecked((byte)value),
                    _ => throw new InvalidCastException("tcsetattr: elements of attributes[6] must be characters or integers"),
                };
            Check(WriteAttributes(descriptor, checked((int)when), ref settings));
            return null;
        });

        static object? TerminalAction(object fd, Func<int, int> action) => WithTerminal<object?>(fd, descriptor =>
        {
            Check(action(descriptor));
            return null;
        });

        public static object? tcdrain(object fd) => TerminalAction(fd, Drain);
        public static object? tcflush(object fd, long queue) => TerminalAction(fd, descriptor => Flush(descriptor, checked((int)queue)));
        public static object? tcflow(object fd, long action) => TerminalAction(fd, descriptor => Flow(descriptor, checked((int)action)));
        public static object? tcsendbreak(object fd, long duration) => TerminalAction(fd, descriptor => SendBreak(descriptor, checked((int)duration)));

        public static (long, long) tcgetwinsize(object fd) => WithTerminal(fd, descriptor =>
        {
            var size = new WindowSize();
            Check(ControlWindow(descriptor, 0x5413, ref size));
            return ((long)size.Rows, (long)size.Columns);
        });

        public static object? tcsetwinsize(object fd, (long Rows, long Columns) winsize) => WithTerminal<object?>(fd, descriptor =>
        {
            if (winsize.Rows is < 0 or > ushort.MaxValue || winsize.Columns is < 0 or > ushort.MaxValue)
                throw new OverflowException("winsize value(s) out of range.");
            var size = new WindowSize();
            Check(ControlWindow(descriptor, 0x5413, ref size));
            size.Rows = (ushort)winsize.Rows;
            size.Columns = (ushort)winsize.Columns;
            Check(ControlWindow(descriptor, 0x5414, ref size));
            return null;
        });
    }

    internal static class msvcrt
    {
        [DllImport("ucrtbase.dll", EntryPoint = "_getwch", CallingConvention = CallingConvention.Cdecl)]
        static extern ushort GetWideChar();
        [DllImport("ucrtbase.dll", EntryPoint = "_putwch", CallingConvention = CallingConvention.Cdecl)]
        static extern ushort PutWideChar(ushort character);

        public static string getwch() => ((char)GetWideChar()).ToString();
        public static object? putwch(string value)
        {
            if (value.Length != 1) throw new InvalidCastException("putwch() argument must be a unicode character");
            PutWideChar(value[0]);
            return null;
        }
    }

    internal static partial class os_
    {
        const int LinuxGetFileFlags = 3;
        const int LinuxSetFileFlags = 4;
        const int LinuxDuplicateCloseOnExec = 1030;

        [DllImport("libc", EntryPoint = "openpty", SetLastError = true)]
        static extern int LibcOpenPty(out int master, out int slave, IntPtr name, IntPtr attributes, IntPtr size);
        [DllImport("libutil.so.1", EntryPoint = "openpty", SetLastError = true)]
        static extern int LibutilOpenPty(out int master, out int slave, IntPtr name, IntPtr attributes, IntPtr size);
        [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
        static extern int NativePipe([Out] int[] descriptors);
        [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)]
        static extern int NativePipeWithFlags([Out] int[] descriptors, int flags);
        [DllImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateWindowsPipe(out SafeFileHandle read, out SafeFileHandle write,
            IntPtr securityAttributes, uint bufferSize);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        static extern int ConfigureDescriptor(int descriptor, int command, int value);
        [DllImport("libc", EntryPoint = "isatty", SetLastError = true)]
        static extern int NativeIsatty(int descriptor);
        [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
        static extern int NativeWaitPid(int pid, out int status, int options);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        static extern nint NativeRead(int descriptor, [Out] byte[] buffer, nuint count);
        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        static extern nint NativeWrite(int descriptor, byte[] buffer, nuint count);

        public const long WNOHANG = 1;

        static BigInteger WaitStatusIndex(object? status)
        {
            object index = Builtins.__index_value(status);
            return index is BigInteger wide ? wide : new BigInteger((long)index);
        }

        static int UnixWaitStatus(object? status)
        {
            BigInteger integer = WaitStatusIndex(status);
            if (integer < int.MinValue || integer > int.MaxValue)
                throw new OverflowException("Python int too large to convert to C int");
            return (int)integer;
        }

        static bool IsStopped(int status) => (status & 0xff) == 0x7f;
        static int StatusCode(int status) => (status & 0xff00) >> 8;
        public static bool WIFSTOPPED(object? status) => IsStopped(UnixWaitStatus(status));
        public static long WSTOPSIG(object? status) => StatusCode(UnixWaitStatus(status));

        public static long waitstatus_to_exitcode(object? status)
        {
            if (OperatingSystem.IsWindows())
            {
                BigInteger windowsStatus = WaitStatusIndex(status);
                if (windowsStatus < 0 || windowsStatus > ulong.MaxValue)
                    throw new OverflowException("Python int too large to convert to C unsigned long long");
                ulong exit = (ulong)windowsStatus >> 8;
                if (exit > uint.MaxValue) throw new ArgumentException($"invalid exit code: {exit}");
                return (long)exit;
            }

            int unixStatus = UnixWaitStatus(status);
            int signal = unixStatus & 0x7f;
            if (signal == 0) return StatusCode(unixStatus);
            if (signal < 0x7f) return -signal;
            if (IsStopped(unixStatus))
                throw new ArgumentException($"process stopped by delivery of signal {StatusCode(unixStatus)}");
            throw new ArgumentException($"invalid wait status: {unixStatus}");
        }

        public static (long, long) waitpid(long pid, long options)
        {
            if (OperatingSystem.IsWindows()) throw new IOException("POSIX waitpid is unavailable on this platform");
            int child = NativeWaitPid(checked((int)pid), out int status, checked((int)options));
            if (child < 0) throw OsErrors.Errno(Marshal.GetLastPInvokeError());
            return ((long)child, (long)status);
        }


        internal static T WithUnixDescriptor<T>(long descriptor, Func<int, T> operation, Func<Exception> badDescriptor)
        {
            Stream stream;
            try { stream = Fd(descriptor).Stream; }
            catch (IOException) { throw badDescriptor(); }
            return WithUnixDescriptor(descriptor, stream, operation, badDescriptor);
        }

        static T WithUnixDescriptor<T>(long descriptor, Stream stream, Func<int, T> operation, Func<Exception> badDescriptor)
        {
            if (descriptor is >= 0 and <= 2 && stream is not FileStream) return operation((int)descriptor);
            SafeFileHandle handle;
            try { handle = DescriptorHandle(stream) ?? throw badDescriptor(); }
            catch (ObjectDisposedException) { throw badDescriptor(); }
            bool referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                return operation((int)handle.DangerousGetHandle());
            }
            catch (ObjectDisposedException) { throw badDescriptor(); }
            finally { if (referenced) handle.DangerousRelease(); }
        }

        static int LinuxFileStatus(int descriptor)
        {
            int flags = ConfigureDescriptor(descriptor, LinuxGetFileFlags, 0);
            if (flags < 0) throw OsErrors.Errno(Marshal.GetLastPInvokeError());
            return flags;
        }

        static void SetLinuxBlocking(int descriptor, bool blocking)
        {
            int flags = LinuxFileStatus(descriptor);
            int mode = (int)O_NONBLOCK;
            int updated = blocking ? flags & ~mode : flags | mode;
            if (updated != flags && ConfigureDescriptor(descriptor, LinuxSetFileFlags, updated) < 0)
                throw OsErrors.Errno(Marshal.GetLastPInvokeError());
        }

        static int TransferLinuxDescriptor(long descriptor, Stream stream, byte[] buffer, bool write) =>
            WithUnixDescriptor(descriptor, stream, native =>
            {
                while (true)
                {
                    nint count = write ? NativeWrite(native, buffer, (nuint)buffer.Length) : NativeRead(native, buffer, (nuint)buffer.Length);
                    if (count >= 0) return checked((int)count);
                    int error = Marshal.GetLastPInvokeError();
                    if (error == errno.EINTR) continue;
                    if (error == errno.EAGAIN) throw new BlockingIOError((long)error, OsErrors.StrError(error));
                    throw OsErrors.Errno(error);
                }
            }, OsErrors.BadFd);

        static FileStream DuplicateLinuxDescriptor(long descriptor, Stream stream)
        {
            var access = stream.CanRead && stream.CanWrite ? FileAccess.ReadWrite : stream.CanRead ? FileAccess.Read : FileAccess.Write;
            return WithUnixDescriptor(descriptor, stream, native => DuplicateLinuxFile(native, access), OsErrors.BadFd);
        }

        internal static SafeFileHandle DuplicateLinuxHandle(int descriptor)
        {
            int duplicate = ConfigureDescriptor(descriptor, LinuxDuplicateCloseOnExec, 3);
            if (duplicate < 0) throw OsErrors.Errno(Marshal.GetLastPInvokeError());
            return new SafeFileHandle((IntPtr)duplicate, ownsHandle: true);
        }

        static FileStream DuplicateLinuxFile(int descriptor, FileAccess access)
        {
            var handle = DuplicateLinuxHandle(descriptor);
            try { return new FileStream(handle, access, bufferSize: 1); }
            catch { handle.Dispose(); throw; }
        }

        internal static FileStream OpenTerminalInput() => DuplicateLinuxFile(0, FileAccess.Read);

        public static (long, long) openpty()
        {
            if (!termios.Available) throw new IOException("openpty is unavailable on this platform");
            int result, master, slave;
            try { result = LibcOpenPty(out master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
            catch (EntryPointNotFoundException) { result = LibutilOpenPty(out master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
            if (result < 0) throw OsErrors.Errno(Marshal.GetLastPInvokeError());

            return RegisterUnixFilePair(master, FileAccess.ReadWrite, slave, FileAccess.ReadWrite, closeOnExec: false);
        }

        public static (long, long) pipe()
        {
            if (OperatingSystem.IsWindows())
            {
                if (!CreateWindowsPipe(out var read, out var write, IntPtr.Zero, 0))
                {
                    int error = Marshal.GetLastPInvokeError();
                    read?.Dispose();
                    write?.Dispose();
                    throw OsErrors.WinError(error, new System.ComponentModel.Win32Exception(error).Message, filename: null);
                }
                return RegisterFilePair(read, FileAccess.Read, write, FileAccess.Write);
            }

            var descriptors = new int[2];
            bool closeOnExec = OperatingSystem.IsLinux();
            int result = closeOnExec ? NativePipeWithFlags(descriptors, (int)O_CLOEXEC) : NativePipe(descriptors);
            if (result < 0) throw OsErrors.Errno(Marshal.GetLastPInvokeError());
            return RegisterUnixFilePair(descriptors[0], FileAccess.Read, descriptors[1], FileAccess.Write, closeOnExec);
        }

        static (long, long) RegisterUnixFilePair(int first, FileAccess firstAccess, int second, FileAccess secondAccess, bool closeOnExec)
        {
            var firstHandle = new SafeFileHandle((IntPtr)first, ownsHandle: true);
            var secondHandle = new SafeFileHandle((IntPtr)second, ownsHandle: true);
            // F_SETFD with FD_CLOEXEC keeps the descriptors out of spawned processes.
            if (!closeOnExec && (ConfigureDescriptor(first, 2, 1) < 0 || ConfigureDescriptor(second, 2, 1) < 0))
            {
                int error = Marshal.GetLastPInvokeError();
                firstHandle.Dispose();
                secondHandle.Dispose();
                throw OsErrors.Errno(error);
            }
            return RegisterFilePair(firstHandle, firstAccess, secondHandle, secondAccess);
        }

        static (long, long) RegisterFilePair(SafeFileHandle firstHandle, FileAccess firstAccess,
            SafeFileHandle secondHandle, FileAccess secondAccess)
        {
            FileStream? first = null;
            FileStream? second = null;
            long? descriptor = null;
            try
            {
                first = new FileStream(firstHandle, firstAccess, bufferSize: 1);
                second = new FileStream(secondHandle, secondAccess, bufferSize: 1);
                descriptor = __register(first);
                return (descriptor.Value, __register(second));
            }
            catch
            {
                if (descriptor is long fd) close(fd);
                else first?.Dispose();
                second?.Dispose();
                firstHandle.Dispose();
                secondHandle.Dispose();
                throw;
            }
        }
    }
}

namespace Typhon.Runtime
{
    internal static partial class Builtins
    {
        public static void __import_platform_module(string name)
        {
            bool available = name switch
            {
                "termios" => Typhon.Stdlib.termios.Available,
                "msvcrt" or "nt" or "_winapi" => System.OperatingSystem.IsWindows(),
                "posix" => !System.OperatingSystem.IsWindows(),
                "_posixsubprocess" => System.OperatingSystem.IsLinux(),
                _ => true,
            };
            if (!available) throw new ModuleNotFoundError($"No module named '{name}'");
        }
    }
}
