// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Net.Sockets;
    using System.Threading;
    using Typhon.Runtime;

    internal static partial class socket_
    {
        internal partial class socket
        {
            // Attached sockets are kept alive by their wrappers. A detached socket needs
            // a strong reference until its descriptor is adopted or explicitly closed.
            static readonly object DescriptorLock = new();
            static readonly Dictionary<long, WeakReference<Descriptor>> KnownDescriptors = new();
            static readonly Dictionary<long, Descriptor> DetachedDescriptors = new();
            Descriptor? _descriptor;

            sealed class Descriptor(Socket handle)
            {
                internal readonly Socket Handle = handle;
                internal readonly long Number = handle.Handle.ToInt64();
                internal readonly List<WeakReference<socket>> Owners = new();
                internal volatile bool Closed;

                internal void PruneOwners() => Owners.RemoveAll(owner =>
                    !owner.TryGetTarget(out socket? target) || !ReferenceEquals(Volatile.Read(ref target._descriptor), this));
            }

            static PyOSError BadSocketDescriptor() => new(errno.EBADF, "Bad file descriptor");

            Socket RequireHandle()
            {
                var descriptor = Volatile.Read(ref _descriptor);
                return descriptor is { Closed: false } ? descriptor.Handle : throw BadSocketDescriptor();
            }

            static Descriptor FindDescriptor(long number)
            {
                lock (DescriptorLock)
                {
                    if (KnownDescriptors.TryGetValue(number, out var entry) && entry.TryGetTarget(out var socket) && !socket.Closed)
                        return socket;
                    throw BadSocketDescriptor();
                }
            }

            void AttachDescriptor(Descriptor descriptor)
            {
                Descriptor? previous;
                lock (DescriptorLock)
                {
                    if (descriptor.Closed) throw BadSocketDescriptor();
                    if (ReferenceEquals(_descriptor, descriptor)) return;
                    previous = _descriptor;
                    Volatile.Write(ref _descriptor, descriptor);
                    descriptor.PruneOwners();
                    descriptor.Owners.Add(new WeakReference<socket>(this));
                    KnownDescriptors[descriptor.Number] = new WeakReference<Descriptor>(descriptor);
                    DetachedDescriptors.Remove(descriptor.Number);
                }
                if (previous != null) CloseDescriptor(previous);
            }

            static long AdoptableDescriptor(Socket handle)
            {
                var descriptor = new Descriptor(handle);
                lock (DescriptorLock)
                {
                    KnownDescriptors[descriptor.Number] = new WeakReference<Descriptor>(descriptor);
                    DetachedDescriptors.Add(descriptor.Number, descriptor);
                }
                return descriptor.Number;
            }

            static void CloseDescriptor(Descriptor descriptor)
            {
                lock (DescriptorLock)
                {
                    if (descriptor.Closed) return;
                    descriptor.Closed = true;
                    if (KnownDescriptors.TryGetValue(descriptor.Number, out var entry) &&
                        entry.TryGetTarget(out var active) && ReferenceEquals(active, descriptor))
                    {
                        KnownDescriptors.Remove(descriptor.Number);
                        DetachedDescriptors.Remove(descriptor.Number);
                    }
                }
                descriptor.Handle.Dispose();
            }

            void CloseAttachedDescriptor()
            {
                var descriptor = _descriptor;
                Volatile.Write(ref _descriptor, null);
                if (descriptor != null) CloseDescriptor(descriptor);
            }

            internal static void CloseDetached(long number) => CloseDescriptor(FindDescriptor(number));

            public long detach()
            {
                lock (FileLifetime)
                {
                    var descriptor = _descriptor;
                    if (descriptor == null) return -1;
                    Volatile.Write(ref _descriptor, null);
                    _closeRequested = true;
                    lock (DescriptorLock)
                    {
                        descriptor.PruneOwners();
                        if (!descriptor.Closed && descriptor.Owners.Count == 0)
                            DetachedDescriptors[descriptor.Number] = descriptor;
                    }
                    return descriptor.Number;
                }
            }
        }
    }
}
