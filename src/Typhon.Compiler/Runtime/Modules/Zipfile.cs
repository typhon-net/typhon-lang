// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Retain Python's module names.

namespace Typhon.Stdlib
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using Typhon.Runtime;

    internal static class zipfile
    {
        public const long ZIP_STORED = 0;
        public const long ZIP_DEFLATED = 8;

        /// <summary>Writing regular files, directories and byte or text entries to an archive.</summary>
        internal sealed class ZipFile : IDisposable
        {
            readonly ZipArchive _archive;
            readonly Stream _stream;
            readonly Stream? _ownedStream;
            readonly long _compression;
            bool _closed;

            public ZipFile(object file, string mode = "r", long compression = ZIP_STORED)
            {
                if (mode != "w") throw new NotSupportedException($"ZipFile mode '{mode}' is not supported");
                Compression(compression);
                _compression = compression;

                Stream stream;
                if (file is io.IOBase pythonFile)
                {
                    pythonFile._checkWritable();
                    stream = StreamOps.StreamOf(pythonFile);
                }
                else if (file is Stream || Builtins.hasattr(file, "write"))
                    stream = StreamOps.StreamOf(file);
                else
                    stream = _ownedStream = StreamOps.OpenFile(os_.__path(file), "wb");

                _stream = stream;
                try { _archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8); }
                catch { _ownedStream?.Dispose(); throw; }
            }

            static CompressionLevel Compression(long method) => method switch
            {
                ZIP_STORED => CompressionLevel.NoCompression,
                ZIP_DEFLATED => CompressionLevel.Optimal,
                _ => throw new NotImplementedException("That compression method is not supported"),
            };

            void CheckOpen()
            {
                if (_closed) throw new ArgumentException("Attempt to use ZIP archive that was already closed");
            }

            static string PortableName(string name)
            {
                if (OperatingSystem.IsWindows()) name = name.Replace('\\', '/');
                int nul = name.IndexOf('\0');
                return nul < 0 ? name : name[..nul];
            }

            public void write(object filename, string? arcname = null, long? compress_type = null)
            {
                CheckOpen();
                string source = os_.__path(filename);
                string name = arcname ?? source;
                string root = System.IO.Path.GetPathRoot(name) ?? "";
                name = PortableName(name[root.Length..]).TrimStart('/');
                var file = os_.stat(source);
                bool directory = stat.S_ISDIR(file.st_mode);
                if (directory) name = name.TrimEnd('/') + "/";
                var timestamp = new DateTimeOffset(File.GetLastWriteTime(source));
                if (timestamp.Year < 1980) throw new ArgumentException("ZIP does not support timestamps before 1980");
                var compression = directory ? CompressionLevel.NoCompression : Compression(compress_type ?? _compression);

                using var input = directory ? null : File.OpenRead(source);
                var entry = _archive.CreateEntry(name, compression);
                entry.LastWriteTime = timestamp;
                entry.ExternalAttributes = unchecked((int)((file.st_mode & 0xffff) << 16)) | (directory ? 0x10 : 0);
                if (input != null)
                {
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }

            public void writestr(string arcname, object data, long? compress_type = null)
            {
                CheckOpen();
                var bytes = data is string text ? Encoding.UTF8.GetBytes(text) : ByteOps.From(data);
                var entry = _archive.CreateEntry(PortableName(arcname), Compression(compress_type ?? _compression));
                using var output = entry.Open();
                output.Write(bytes);
            }

            public ZipFile __enter__() { CheckOpen(); return this; }

            public bool __exit__(object? type, object? value, object? traceback)
            {
                close();
                return false;
            }

            public void close()
            {
                if (_closed) return;
                _closed = true;
                try
                {
                    _archive.Dispose();
                    _stream.Flush();
                }
                finally { _ownedStream?.Dispose(); }
            }

            public void Dispose() => close();
        }
    }
}
