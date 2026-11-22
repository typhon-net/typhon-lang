// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using Typhon.Runtime;

    internal static partial class zipfile
    {
        /// <summary>A read-only resource path in a ZIP archive.</summary>
        internal sealed class Path
        {
            readonly string _archive;
            public string at { get; }

            public Path(object root, string at = "")
            {
                _archive = os_.__path(root);
                this.at = at;
            }

            public string name => at.Length == 0 ? System.IO.Path.GetFileName(_archive) : at.TrimEnd('/').Split('/')[^1];
            public bool exists() => Inspect(zip => zip.GetEntry(at) != null ||
                at.EndsWith('/') && zip.Entries.Any(entry => entry.FullName.StartsWith(at, StringComparison.Ordinal)));
            public bool is_dir() => at.Length == 0 || at.EndsWith('/');
            public bool is_file() => !is_dir() && exists();

            T Inspect<T>(Func<ZipArchive, T> operation)
            {
                using var stream = File.OpenRead(_archive);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                return operation(archive);
            }

            public IEnumerable<Path> iterdir()
            {
                if (!is_dir()) throw new ArgumentException("Can't listdir a file");
                return Inspect(zip =>
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var children = new List<Path>();
                    foreach (var entry in zip.Entries)
                    {
                        string full = entry.FullName;
                        if (!full.StartsWith(at, StringComparison.Ordinal) || full.Length == at.Length) continue;
                        int slash = full.IndexOf('/', at.Length);
                        string child = slash < 0 ? full : full[..(slash + 1)];
                        if (names.Add(child)) children.Add(new Path(_archive, child));
                    }
                    return children;
                });
            }

            public Path joinpath(params string[] descendants)
            {
                if (descendants.Length == 0) return this;
                string member = at;
                foreach (string descendant in descendants)
                    member = member.Length == 0 || descendant.StartsWith('/') ? descendant : member.TrimEnd('/') + "/" + descendant;
                string directory = member.TrimEnd('/') + "/";
                return Inspect(zip => !member.EndsWith('/') && zip.GetEntry(member) == null &&
                    zip.Entries.Any(entry => entry.FullName.StartsWith(directory, StringComparison.Ordinal))
                    ? new Path(_archive, directory) : new Path(_archive, member));
            }

            public static Path operator /(Path path, string child) => path.joinpath(child);
            public PyBytes read_bytes()
            {
                if (is_dir()) throw new IsADirectoryError(ToString());
                return Inspect(zip =>
                {
                    using var input = (zip.GetEntry(at) ?? throw new FileNotFoundException(ToString())).Open();
                    using var output = new MemoryStream();
                    input.CopyTo(output);
                    return new PyBytes(output.ToArray());
                });
            }

            public string read_text(string? encoding = null, string? errors = null)
            {
                using var reader = new io.TextIOWrapper(new io.BytesIO(read_bytes()), encoding, errors);
                return reader.read();
            }

            public dynamic open(string mode = "r", string? encoding = null, string? errors = null, string? newline = null)
            {
                if (mode is not ("r" or "rb")) throw new ArgumentException($"unsupported ZIP resource mode {mode}");
                if (mode == "rb" && (encoding != null || errors != null || newline != null))
                    throw new ArgumentException("binary mode doesn't take encoding, errors, or newline arguments");
                var binary = new io.BytesIO(read_bytes());
                return mode == "rb" ? binary : new io.TextIOWrapper(binary, encoding, errors, newline);
            }

            public override string ToString() => System.IO.Path.Combine(_archive, at.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }
    }
}
