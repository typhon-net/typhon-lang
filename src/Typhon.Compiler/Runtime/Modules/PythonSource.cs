// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Typhon.Runtime;

    internal static class tokenize
    {
        static readonly Regex Cookie = new Regex(@"\A[ \t\f]*#.*?coding[=:][ \t]*([-\w.]+)", RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
        static readonly Regex CommentOrBlank = new Regex(@"\A[ \t\f]*(?:#|[\r\n]|\z)", RegexOptions.CultureInvariant);
        static readonly byte[] Utf8Bom = { 0xef, 0xbb, 0xbf };

        public static PyFile open(object filename)
        {
            string path = os_.__path(filename);
            var stream = StreamOps.OpenFile(path, "r");
            try
            {
                string encoding = SourceEncoding(stream);
                stream.Position = 0;
                return new PyFile(stream, path, "r", encoding, null, null, true);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal static string SourceEncoding(Stream stream)
        {
            byte[] first = ReadLine(stream);
            bool bom = first.AsSpan().StartsWith(Utf8Bom);
            if (bom) first = first[Utf8Bom.Length..];
            string header = Encoding.Latin1.GetString(first);
            string? cookie = DeclaredEncoding(header);
            byte[] second = Array.Empty<byte>();
            if (cookie == null && first.Length != 0 && CommentOrBlank.IsMatch(header))
            {
                second = ReadLine(stream);
                cookie = DeclaredEncoding(Encoding.Latin1.GetString(second));
            }
            string codec = cookie ?? "utf-8";
            if (bom && codec != "utf-8") throw new SyntaxError("encoding problem: utf-8");
            if (first.Contains((byte)0) || second.Contains((byte)0))
                throw new SyntaxError("source code cannot contain null bytes");
            Encoding encoding;
            try { encoding = StrictEncoding(codec); }
            catch (ArgumentException) { throw new SyntaxError($"unknown encoding: {codec}"); }
            try
            {
                encoding.GetCharCount(first);
                encoding.GetCharCount(second);
            }
            catch (DecoderFallbackException) { throw new SyntaxError("invalid or missing encoding declaration"); }
            return bom ? "utf-8-sig" : codec;
        }

        internal static Encoding StrictEncoding(string name)
        {
            var encoding = (Encoding)ByteOps.EncodingOf(name, "strict").Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            return encoding;
        }

        static string? DeclaredEncoding(string line)
        {
            var match = Cookie.Match(line);
            if (!match.Success) return null;
            string name = match.Groups[1].Value;
            string normalized = name.ToLowerInvariant().Replace('_', '-');
            if (normalized == "utf-8" || normalized.StartsWith("utf-8-", StringComparison.Ordinal)) return "utf-8";
            foreach (string alias in new[] { "latin-1", "iso-8859-1", "iso-latin-1" })
                if (normalized == alias || normalized.StartsWith(alias + "-", StringComparison.Ordinal)) return "iso-8859-1";
            return name;
        }

        static byte[] ReadLine(Stream stream)
        {
            using var line = new MemoryStream();
            for (int b; (b = stream.ReadByte()) != -1;)
            {
                line.WriteByte((byte)b);
                if (b == '\n') break;
            }
            return line.ToArray();
        }
    }

    internal static class importlib
    {
        internal static class util
        {
            public static string decode_source(PyBytes source_bytes)
            {
                byte[] bytes = source_bytes.Data;
                using var stream = new MemoryStream(bytes, writable: false);
                string codec = tokenize.SourceEncoding(stream);
                int start = codec == "utf-8-sig" ? 3 : 0;
                try
                {
                    string text = tokenize.StrictEncoding(start == 0 ? codec : "utf-8").GetString(bytes, start, bytes.Length - start);
                    return text.Replace("\r\n", "\n").Replace('\r', '\n');
                }
                catch (DecoderFallbackException e)
                {
                    int index = e.Index + start;
                    throw new UnicodeDecodeError(codec, source_bytes, index, index + e.BytesUnknown.Length, "invalid byte sequence");
                }
            }
        }
    }
}
