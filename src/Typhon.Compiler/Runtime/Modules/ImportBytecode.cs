// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// The header protocol follows CPython Lib/importlib/_bootstrap_external.py.
// See CPython-LICENSE.txt for the upstream license.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using Typhon.Runtime;

    internal static partial class importlib
    {
        internal static class _bootstrap_external
        {
            public static Type SourceFileLoader => typeof(machinery.SourceFileLoader);
            public static Type SourcelessFileLoader => typeof(machinery.SourcelessFileLoader);

            public static long _classify_pyc(object data, string name, object exc_details) =>
                Classify(ByteOps.From(data), name, exc_details);

            internal static long Classify(byte[] data, string name, object? details = null)
            {
                var magic = data.AsSpan(0, Math.Min(4, data.Length));
                ImportError Invalid(string message) => new(message,
                    details == null ? null : (string?)Builtins.__dict_get((System.Collections.IDictionary)details, "name"),
                    details == null ? null : Builtins.__dict_get((System.Collections.IDictionary)details, "path"));
                if (!magic.SequenceEqual(util.MAGIC_NUMBER.Data))
                    throw Invalid($"bad magic number in {Builtins.repr(name)}: {Builtins.repr(new PyBytes(magic.ToArray()))}");
                if (data.Length < 16) throw new EndOfStreamException($"reached EOF while reading pyc header of {Builtins.repr(name)}");
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
                if ((flags & ~3u) != 0) throw Invalid($"invalid flags {flags} in {Builtins.repr(name)}");
                return flags;
            }
        }

        internal static partial class machinery
        {
            internal sealed class SourcelessFileLoader(string fullname, string origin)
            {
                public string name => fullname;
                public string path => origin;

                public string get_filename(string target)
                {
                    if (target != fullname) throw new ImportError($"loader for {fullname} cannot handle {target}", target);
                    return origin;
                }

                public PyBytes get_data(string path) => new(File.ReadAllBytes(path));
                public bool is_package(string target) => System.IO.Path.GetFileName(get_filename(target)) == "__init__.pyc";
                public object? get_source(string target) => null;

                public sys_.code get_code(string target)
                {
                    byte[] data = get_data(get_filename(target)).Data;
                    _bootstrap_external.Classify(data, target);
                    return (object?)marshal.loads(new PyBytes(data[16..])) as sys_.code
                        ?? throw new ImportError("non-code object in bytecode file", fullname);
                }

                public object? create_module(ModuleSpec spec) => null;
                public void exec_module(object module) =>
                    throw new NotSupportedException("CPython bytecode files cannot be executed by Typhon");
            }
        }
    }
}
