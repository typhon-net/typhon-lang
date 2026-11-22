// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Retain Python's module names.

namespace Typhon.Stdlib
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using Typhon.Runtime;

    internal static class gzip
    {
        public static dynamic open(object filename, string mode = "rb", long compresslevel = 9,
            string? encoding = null, string? errors = null, string? newline = null)
        {
            if (mode is not ("r" or "rb" or "rt")) throw io.Unsupported("gzip mode " + mode);
            bool text = mode == "rt";
            if (!text && (encoding != null || errors != null || newline != null))
                throw new ArgumentException("binary mode doesn't take an encoding, errors, or newline argument");
            string path = os_.__path(filename);
            var stream = CompressedInput.Open(path, raw => new GZipStream(raw, CompressionMode.Decompress));
            var binary = new PyBinaryFile(stream, path, "rb", closefd: true);
            if (!text) return binary;
            try { return new io.TextIOWrapper(binary, encoding, errors, newline); }
            catch { binary.close(); throw; }
        }
    }

    internal static class bz2
    {
        internal sealed class BZ2File : PyBinaryFile
        {
            public BZ2File(object filename, string mode = "r", long compresslevel = 9)
                : this(os_.__path(filename), mode) { }

            BZ2File(string path, string mode) : base(Open(path, mode), path, "rb", closefd: true) { }

            static Stream Open(string path, string mode)
            {
                if (mode is not ("r" or "rb")) throw io.Unsupported("bzip2 mode " + mode);
                return CompressedInput.Open(path, raw => new BZip2Stream(raw));
            }
        }
    }
}

namespace Typhon.Runtime
{
    using System;
    using System.IO;

    internal static class CompressedInput
    {
        public static Stream Open(string path, Func<Stream, Stream> wrap)
        {
            var file = StreamOps.OpenFile(path, "rb");
            try { return wrap(file); }
            catch { file.Dispose(); throw; }
        }
    }
}
