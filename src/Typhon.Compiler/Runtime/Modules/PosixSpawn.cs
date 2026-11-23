// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using Microsoft.Win32.SafeHandles;
    using Typhon.Runtime;

    internal static class UnixSpawn
    {
        internal const int Open = 0, Close = 1, Duplicate = 2;
        const int CloseFrom = 3, ChangeDirectory = 4;
        const short ResetIds = 0x01, ProcessGroup = 0x02, SignalDefaults = 0x04, SignalMask = 0x08,
            SchedulerParameters = 0x10, SchedulerPolicy = 0x20, Session = 0x80;

        // Linux glibc layouts from <spawn.h>. The libc functions own their contents.
        [StructLayout(LayoutKind.Sequential, Size = 80)]
        struct FileActions { IntPtr alignment; }
        [StructLayout(LayoutKind.Sequential, Size = 336)]
        struct Attributes { long alignment; }
        [StructLayout(LayoutKind.Sequential, Size = 128)]
        struct Signals { long alignment; }

        [DllImport("libc")] static extern int posix_spawn_file_actions_init(ref FileActions actions);
        [DllImport("libc")] static extern int posix_spawn_file_actions_destroy(ref FileActions actions);
        [DllImport("libc")] static extern int posix_spawn_file_actions_addclose(ref FileActions actions, int descriptor);
        [DllImport("libc")] static extern int posix_spawn_file_actions_adddup2(ref FileActions actions, int descriptor, int target);
        [DllImport("libc")] static extern int posix_spawn_file_actions_addopen(ref FileActions actions, int descriptor, IntPtr path, int flags, uint mode);
        [DllImport("libc")] static extern int posix_spawn_file_actions_addclosefrom_np(ref FileActions actions, int descriptor);
        [DllImport("libc")] static extern int posix_spawn_file_actions_addchdir_np(ref FileActions actions, IntPtr path);

        [DllImport("libc")] static extern int posix_spawnattr_init(ref Attributes attributes);
        [DllImport("libc")] static extern int posix_spawnattr_destroy(ref Attributes attributes);
        [DllImport("libc")] static extern int posix_spawnattr_setflags(ref Attributes attributes, short flags);
        [DllImport("libc")] static extern int posix_spawnattr_setpgroup(ref Attributes attributes, int group);
        [DllImport("libc")] static extern int posix_spawnattr_setsigmask(ref Attributes attributes, ref Signals signals);
        [DllImport("libc")] static extern int posix_spawnattr_setsigdefault(ref Attributes attributes, ref Signals signals);
        [DllImport("libc")] static extern int posix_spawnattr_setschedparam(ref Attributes attributes, ref int priority);
        [DllImport("libc")] static extern int posix_spawnattr_setschedpolicy(ref Attributes attributes, int policy);
        [DllImport("libc", SetLastError = true)] static extern int sigemptyset(ref Signals signals);
        [DllImport("libc", SetLastError = true)] static extern int sigaddset(ref Signals signals, int signal);

        [DllImport("libc")] static extern int posix_spawn(out int pid, IntPtr path, ref FileActions actions,
            ref Attributes attributes, IntPtr arguments, IntPtr environment);
        [DllImport("libc")] static extern int posix_spawnp(out int pid, IntPtr path, ref FileActions actions,
            ref Attributes attributes, IntPtr arguments, IntPtr environment);

        static void Check(int status)
        {
            if (status != 0) throw OsErrors.Errno(status);
        }

        static int NativeInteger(object? value)
        {
            object integer = Builtins.__index_value(value);
            return integer is long small ? checked((int)small) : checked((int)(System.Numerics.BigInteger)integer);
        }

        internal static long Start(bool search, object path, object argv, object? env, object? fileActions,
            object? setpgroup, bool resetids, bool setsid, object? setsigmask, object? setsigdef, object? scheduler)
        {
            return StartCore(search, [os_.fsencode(path).Data], argv, EnvironmentEntries(env), fileActions,
                setpgroup, resetids, setsid, setsigmask, setsigdef, scheduler);
        }

        static long StartCore(bool search, IEnumerable<byte[]> paths, object argv, IEnumerable<byte[]> env, object? fileActions,
            object? setpgroup, bool resetids, bool setsid, object? setsigmask, object? setsigdef, object? scheduler,
            bool internalActions = false)
        {
            if (!OperatingSystem.IsLinux())
                throw new NotSupportedException("POSIX process creation is unavailable on this platform");

            var arguments = Builtins.__iter_dyn(argv).Select(value => os_.fsencode((object)value).Data).ToArray();
            if (arguments.Length == 0) throw new ArgumentException("posix_spawn: argv must not be empty");
            using var buffers = new NativeStrings();
            IntPtr argumentVector = buffers.Vector(arguments);
            IntPtr environment = buffers.Vector(env);
            FileActions actions = default;
            Attributes attributes = default;
            Check(posix_spawn_file_actions_init(ref actions));
            try
            {
                AddFileActions(ref actions, fileActions, buffers, internalActions);
                Check(posix_spawnattr_init(ref attributes));
                try
                {
                    SetAttributes(ref attributes, setpgroup, resetids, setsid, setsigmask, setsigdef, scheduler);
                    Exception? failure = null;
                    foreach (byte[] executable in paths)
                    {
                        IntPtr executablePath = buffers.String(executable);
                        int result = search
                            ? posix_spawnp(out int pid, executablePath, ref actions, ref attributes, argumentVector, environment)
                            : posix_spawn(out pid, executablePath, ref actions, ref attributes, argumentVector, environment);
                        if (result == 0) return pid;
                        if (failure == null || failure is FileNotFoundException && result is not (2 or 20))
                            failure = OsErrors.Errno(result, os_.fsdecode(new PyBytes(executable)));
                    }
                    throw failure ?? new ArgumentException("exec requires at least one executable path");
                }
                finally { posix_spawnattr_destroy(ref attributes); }
            }
            finally { posix_spawn_file_actions_destroy(ref actions); }
        }

        internal static long StartChild(object argv, object executables, bool closeFiles, object descriptorsToKeep, object? cwd,
            object? environment, long input, long parentInput, long parentOutput, long output, long parentError, long error,
            long errorRead, long errorWrite, bool restoreSignals, bool newSession, long group)
        {
            var actions = new List<object>();
            var temporaryDescriptors = new List<SafeFileHandle>();
            try
            {
                long[] streams = [input, output, error];
                for (int descriptor = 0; descriptor < streams.Length; descriptor++)
                {
                    long source = streams[descriptor];
                    if (source is >= 0 and <= 2 && source != descriptor)
                    {
                        var temporary = os_.DuplicateLinuxHandle(checked((int)source));
                        temporaryDescriptors.Add(temporary);
                        streams[descriptor] = (long)temporary.DangerousGetHandle();
                    }
                }

                for (int descriptor = 0; descriptor < streams.Length; descriptor++)
                    if (streams[descriptor] >= 0)
                        actions.Add((Duplicate, streams[descriptor], descriptor));

                var closed = new HashSet<int>();
                foreach (long descriptor in new[] { parentInput, parentOutput, parentError, errorRead })
                {
                    if (descriptor >= 3 && closed.Add(checked((int)descriptor)))
                        actions.Add((Close, descriptor));
                }
                if (cwd != null) actions.Add((ChangeDirectory, cwd));

                var keep = Builtins.__iter_dyn(descriptorsToKeep).Cast<object?>().Select(NativeInteger)
                    .Where(descriptor => descriptor != errorWrite).ToHashSet();
                foreach (int descriptor in keep)
                    actions.Add((Duplicate, descriptor, descriptor));
                if (closeFiles)
                {
                    int firstClosed = Math.Max(3, checked(keep.DefaultIfEmpty(2).Max() + 1));
                    if (firstClosed > 3)
                        foreach (string path in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
                            if (int.TryParse(System.IO.Path.GetFileName(path), out int descriptor) && descriptor >= 3 &&
                                descriptor < firstClosed && !keep.Contains(descriptor) && closed.Add(descriptor))
                                actions.Add((Close, descriptor));
                    actions.Add((CloseFrom, firstClosed));
                }

                IEnumerable<byte[]> paths = Builtins.__iter_dyn(executables).Select(value => os_.fsencode((object)value).Data);
                IEnumerable<byte[]> variables = environment == null ? EnvironmentEntries(null) :
                    Builtins.__iter_dyn(environment).Select(value => os_.fsencode((object)value).Data);
                return StartCore(false, paths, argv, variables, actions, group < 0 ? null : group,
                    resetids: false, setsid: newSession, setsigmask: null,
                    setsigdef: restoreSignals ? new[] { 13, 25 } : null, scheduler: null, internalActions: true);
            }
            finally
            {
                foreach (var descriptor in temporaryDescriptors) descriptor.Dispose();
            }
        }

        static void AddFileActions(ref FileActions actions, object? fileActions, NativeStrings buffers, bool internalActions)
        {
            if (fileActions == null) return;
            foreach (object item in Builtins.__iter_dyn(fileActions))
            {
                object?[] fields = Builtins.__iter_dyn(item).Cast<object?>().ToArray();
                int operation = fields.Length == 0 ? -1 : NativeInteger(fields[0]);
                int status = (operation, fields.Length) switch
                {
                    (Close, 2) => posix_spawn_file_actions_addclose(ref actions, NativeInteger(fields[1])),
                    (Duplicate, 3) => posix_spawn_file_actions_adddup2(ref actions, NativeInteger(fields[1]), NativeInteger(fields[2])),
                    (Open, 5) => posix_spawn_file_actions_addopen(ref actions, NativeInteger(fields[1]),
                        buffers.String(os_.fsencode(fields[2]!).Data), NativeInteger(fields[3]), checked((uint)NativeInteger(fields[4]))),
                    (CloseFrom, 2) when internalActions => posix_spawn_file_actions_addclosefrom_np(ref actions, NativeInteger(fields[1])),
                    (ChangeDirectory, 2) when internalActions =>
                        posix_spawn_file_actions_addchdir_np(ref actions, buffers.String(os_.fsencode(fields[1]!).Data)),
                    _ => throw new ArgumentException("invalid file_actions entry"),
                };
                Check(status);
            }
        }

        static void SetAttributes(ref Attributes attributes, object? group, bool reset, bool session,
            object? mask, object? defaults, object? scheduler)
        {
            short flags = 0;
            if (reset) flags |= ResetIds;
            if (session) flags |= Session;
            if (group != null)
            {
                Check(posix_spawnattr_setpgroup(ref attributes, NativeInteger(group)));
                flags |= ProcessGroup;
            }
            if (mask != null)
            {
                var signals = ReadSignals(mask);
                Check(posix_spawnattr_setsigmask(ref attributes, ref signals));
                flags |= SignalMask;
            }
            if (defaults != null)
            {
                var signals = ReadSignals(defaults);
                Check(posix_spawnattr_setsigdefault(ref attributes, ref signals));
                flags |= SignalDefaults;
            }
            if (scheduler != null)
            {
                var fields = Builtins.__iter_dyn(scheduler).Cast<object?>().ToArray();
                if (fields.Length != 2) throw new ArgumentException("a scheduler tuple must have two items");
                int priority = NativeInteger(Builtins.getattr(fields[1], "sched_priority"));
                Check(posix_spawnattr_setschedparam(ref attributes, ref priority));
                flags |= SchedulerParameters;
                if (fields[0] != null)
                {
                    Check(posix_spawnattr_setschedpolicy(ref attributes, NativeInteger(fields[0])));
                    flags |= SchedulerPolicy;
                }
            }
            Check(posix_spawnattr_setflags(ref attributes, flags));
        }

        static Signals ReadSignals(object values)
        {
            Signals signals = default;
            if (sigemptyset(ref signals) != 0) throw OsErrors.Errno(Marshal.GetLastPInvokeError());
            foreach (object item in Builtins.__iter_dyn(values))
                if (sigaddset(ref signals, NativeInteger(item)) != 0)
                    throw OsErrors.Errno(Marshal.GetLastPInvokeError());
            return signals;
        }

        static IEnumerable<byte[]> EnvironmentEntries(object? env)
        {
            if (env == null)
            {
                foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                    yield return EnvironmentEntry(item.Key, item.Value);
                yield break;
            }
            object? items = Builtins.__invoke((object?)Builtins.getattr(env, "items"), []);
            foreach (object item in Builtins.__iter_dyn(items))
            {
                var pair = Builtins.__iter_dyn(item).Cast<object?>().ToArray();
                if (pair.Length != 2) throw new ArgumentException("environment entry must contain a key and value");
                yield return EnvironmentEntry(pair[0], pair[1]);
            }
        }

        static byte[] EnvironmentEntry(object? key, object? value)
        {
            byte[] name = os_.fsencode(key!).Data;
            if (Array.IndexOf(name, (byte)'=') >= 0) throw new ArgumentException("illegal environment variable name");
            return [.. name, (byte)'=', .. os_.fsencode(value!).Data];
        }

        sealed class NativeStrings : IDisposable
        {
            readonly List<IntPtr> allocated = [];

            IntPtr Allocate(int count)
            {
                IntPtr storage = Marshal.AllocHGlobal(count);
                allocated.Add(storage);
                return storage;
            }

            internal IntPtr String(byte[] value)
            {
                if (Array.IndexOf(value, (byte)0) >= 0) throw new ArgumentException("embedded null byte");
                IntPtr storage = Allocate(checked(value.Length + 1));
                Marshal.Copy(value, 0, storage, value.Length);
                Marshal.WriteByte(storage, value.Length, 0);
                return storage;
            }

            internal IntPtr Vector(IEnumerable<byte[]> values)
            {
                IntPtr[] strings = values.Select(String).ToArray();
                IntPtr storage = Allocate(checked((strings.Length + 1) * IntPtr.Size));
                Marshal.Copy(strings, 0, storage, strings.Length);
                Marshal.WriteIntPtr(storage, strings.Length * IntPtr.Size, IntPtr.Zero);
                return storage;
            }

            public void Dispose()
            {
                foreach (IntPtr storage in allocated) Marshal.FreeHGlobal(storage);
                allocated.Clear();
            }
        }
    }

    internal static class _posixsubprocess
    {
        public static long fork_exec(object args, object executable_list, bool close_fds, object pass_fds,
            object? cwd, object? env, long p2cread, long p2cwrite, long c2pread, long c2pwrite,
            long errread, long errwrite, long errpipe_read, long errpipe_write, bool restore_signals,
            bool start_new_session, long process_group, object? gid, object? gids, object? uid, long umask, object? preexec_fn)
        {
            if (preexec_fn != null)
                throw new NotSupportedException("preexec_fn cannot run in a child created by posix_spawn");
            if (gid != null || gids != null || uid != null || umask >= 0)
                throw new NotSupportedException("child filesystem mode and identity changes require a native launch helper");
            return UnixSpawn.StartChild(args, executable_list, close_fds, pass_fds, cwd, env, p2cread, p2cwrite,
                c2pread, c2pwrite, errread, errwrite, errpipe_read, errpipe_write, restore_signals, start_new_session,
                process_group);
        }
    }
}
