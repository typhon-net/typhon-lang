// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Retain Python's module name.

namespace Typhon.Stdlib
{
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Threading;
    using Typhon.Runtime;

    internal static partial class socket_
    {
        internal partial class socket
        {
            readonly object _fileLifetime = new object();
            int _openFiles;
            bool _closeRequested;

            public dynamic makefile(string mode = "r", long buffering = -1, string? encoding = null,
                string? errors = null, string? newline = null)
            {
                foreach (char flag in mode)
                    if (flag is not ('r' or 'w' or 'b'))
                        throw new ArgumentException($"invalid mode {Builtins.repr(mode)} (only r, w, b allowed)");
                bool writing = mode.Contains('w');
                bool reading = mode.Contains('r') || !writing;
                bool binary = mode.Contains('b');
                if (buffering == 0 && !binary) throw new ArgumentException("unbuffered streams must be binary");
                int capacity = buffering < 0 ? (int)io.DEFAULT_BUFFER_SIZE : checked((int)buffering);
                if (capacity == 0) return OpenFile(reading, writing, capacity);

                io.IOBase buffered = reading && writing ? OpenPair(capacity) :
                    reading ? new io.BufferedReader(OpenFile(true, false, capacity), capacity) :
                    new io.BufferedWriter(OpenFile(false, true, capacity), capacity);
                if (binary) return buffered;
                try { return new io.TextIOWrapper(buffered, encoding, errors, newline) { mode = mode }; }
                catch { buffered.close(); throw; }
            }

            io.BufferedRWPair OpenPair(int capacity)
            {
                var input = OpenFile(true, false, capacity);
                try { return new io.BufferedRWPair(input, OpenFile(false, true, capacity), capacity); }
                catch { input.close(); throw; }
            }

            io.FileIO OpenFile(bool reading, bool writing, int capacity)
            {
                long handle;
                lock (_fileLifetime)
                {
                    handle = fileno();
                    if (handle == -1) throw new PyOSError(errno.EBADF, "Bad file descriptor");
                    _openFiles++;
                }
                var stream = new SocketFileStream(this, reading, writing);
                try
                {
                    Stream data = capacity == 0 ? stream : new BufferedStream(stream, capacity);
                    string mode = (reading ? "r" : "") + (writing ? "w" : "") + "b";
                    return new io.FileIO(data, handle, mode, closefd: true);
                }
                catch { stream.Dispose(); throw; }
            }

            void ReleaseFile()
            {
                lock (_fileLifetime)
                {
                    _openFiles--;
                    if (_openFiles == 0 && _closeRequested) _handle.Dispose();
                }
            }

            sealed class SocketFileStream(socket owner, bool reading, bool writing) : Stream
            {
                int _disposed;
                bool _readTimedOut;

                public override bool CanRead => _disposed == 0 && reading;
                public override bool CanWrite => _disposed == 0 && writing;
                public override bool CanSeek => false;
                public override long Length => throw io.Unsupported("seek");
                public override long Position { get => throw io.Unsupported("seek"); set => throw io.Unsupported("seek"); }
                public override long Seek(long offset, SeekOrigin origin) => throw io.Unsupported("seek");
                public override void SetLength(long value) => throw io.Unsupported("truncate");
                public override void Flush() { }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    if (!CanRead) throw io.Unsupported("not readable");
                    if (_readTimedOut) throw new PyOSError("cannot read from timed out object");
                    if (count == 0) return 0;
                    try { return owner.Run(s => s.Receive(buffer, offset, count, SocketFlags.None)); }
                    catch (TimeoutException) { _readTimedOut = true; throw; }
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    if (!CanWrite) throw io.Unsupported("not writable");
                    owner.Run(s =>
                    {
                        while (count > 0)
                        {
                            int sent = s.Send(buffer, offset, count, SocketFlags.None);
                            if (sent == 0) throw new BrokenPipeError(errno.EPIPE, "Broken pipe");
                            offset += sent;
                            count -= sent;
                        }
                    });
                }

                void Release()
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ReleaseFile();
                }

                ~SocketFileStream() => Release();

                protected override void Dispose(bool disposing)
                {
                    Release();
                    if (disposing) GC.SuppressFinalize(this);
                    base.Dispose(disposing);
                }
            }
        }
    }
}
