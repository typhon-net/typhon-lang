// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using Typhon.Runtime;

    internal static class _frozen_importlib
    {
        public static importlib.machinery.ModuleSpec ModuleSpec(string name, object? loader, string? origin = null,
            object? loader_state = null, bool? is_package = null) =>
            new(name, loader, origin, loader_state, is_package);

        public static importlib.machinery.ModuleSpec spec_from_loader(string name, object? loader,
            string? origin = null, bool? is_package = null)
        {
            if (origin == null && Builtins.getattr(loader, "get_filename", null) is object filename)
            {
                try { origin = os_.__path((object?)Builtins.__invoke(filename, [name])); }
                catch (ImportError) { }
                if (origin != null)
                {
                    object? locations = is_package switch { true => new List<string>(), false => null, _ => Type.Missing };
                    return importlib.util.spec_from_file_location(name, origin, loader, locations)!;
                }
            }
            if (is_package == null && Builtins.getattr(loader, "is_package", null) is object check)
            {
                try { is_package = Builtins.truthy((object?)Builtins.__invoke(check, [name])); }
                catch (ImportError) { }
            }
            return new(name, loader, origin, is_package: is_package);
        }

        public static void _verbose_message(string message,
            [PyPackedArguments] object?[]? arguments = null, [PyKeywordOnly] long verbosity = 1)
        {
            if (sys_.flags.verbose < verbosity) return;
            if (message.Length == 0 || message[0] is not ('#' or 'i')) message = "# " + message;
            Builtins.print_kw([Builtins.str_format(message, arguments ?? [])], file: (object?)sys_.stderr);
        }
    }

    internal static class _frozen_importlib_external
    {
        internal class _LoaderBasics
        {
            public virtual string get_filename(string fullname) =>
                throw new MissingMemberException("loader must define get_filename()");

            public virtual bool is_package(string fullname)
            {
                string file = _path_split(get_filename(fullname)).Item2;
                int suffix = file.LastIndexOf('.');
                return SourceImport.Leaf(fullname) != "__init__" &&
                    (suffix < 0 ? file : file[..suffix]) == "__init__";
            }

            public virtual dynamic? get_code(string fullname) =>
                throw new MissingMemberException("loader must define get_code()");

            public virtual object? create_module(importlib.machinery.ModuleSpec spec) => null;

            public virtual void exec_module(object module)
            {
                string fullname = (string)Builtins.getattr(module, "__name__");
                object? code = get_code(fullname);
                if (code == null)
                    throw new ImportError($"cannot load module {Builtins.repr(fullname)} when get_code() returns None");
                Builtins.exec(code, (object?)Builtins.getattr(module, "__dict__"));
            }
        }

        public static readonly string path_sep = os_.sep;
        public static readonly string path_separators = OperatingSystem.IsWindows() ? "\\/" : "/";

        public static os_.stat_result _path_stat(object path) => os_.stat(path);

        public static (string, string) _path_split(string path)
        {
            int separator = path.LastIndexOfAny(path_separators.ToCharArray());
            return separator < 0 ? ("", path) : (path[..separator], path[(separator + 1)..]);
        }

        public static string _path_join([PyPackedArguments] object?[]? path_parts = null)
        {
            string[] parts = (path_parts ?? []).Select(part => (string)part!).ToArray();
            if (parts.Length == 0) return "";
            if (OperatingSystem.IsWindows()) return os_.path.__join_paths(parts);
            return string.Join(path_sep, parts.Where(part => part.Length != 0).Select(part => part.TrimEnd('/')));
        }

        public static long _unpack_uint16(object data) => BinaryPrimitives.ReadUInt16LittleEndian(ByteOps.Buffer(data));
        public static long _unpack_uint32(object data) => BinaryPrimitives.ReadUInt32LittleEndian(ByteOps.Buffer(data));
        public static BigInteger _unpack_uint64(object data) => BinaryPrimitives.ReadUInt64LittleEndian(ByteOps.Buffer(data));

        public static long _classify_pyc(object data, string name, object exc_details) =>
            importlib._bootstrap_external._classify_pyc(data, name, exc_details);

        public static void _validate_hash_pyc(object data, object source_hash, string name, object exc_details)
        {
            var header = ByteOps.Buffer(data);
            if (header.Length >= 16 && header.AsSpan(8, 8).SequenceEqual(ByteOps.Buffer(source_hash))) return;
            throw new ImportError($"hash in bytecode doesn't match hash of source {Builtins.repr(name)}", name,
                (object?)Builtins.getattr(exc_details, "get", null) is object lookup
                    ? Builtins.__invoke(lookup, ["path"]) : null);
        }
    }

    internal static class _imp
    {
        public static string check_hash_based_pycs = "default";

        public static PyBytes source_hash(long key, object source)
        {
            ReadOnlySpan<byte> input = ByteOps.Buffer(source);
            ulong seed = unchecked((ulong)key);
            ulong a = 0x736f6d6570736575UL ^ seed;
            ulong b = 0x646f72616e646f6dUL;
            ulong c = 0x6c7967656e657261UL ^ seed;
            ulong d = 0x7465646279746573UL;
            int offset = 0;
            while (input.Length - offset >= 8)
            {
                Mix(BinaryPrimitives.ReadUInt64LittleEndian(input[offset..]));
                offset += 8;
            }
            ulong tail = (ulong)input.Length << 56;
            for (int i = 0; offset + i < input.Length; i++)
                tail |= (ulong)input[offset + i] << (8 * i);
            Mix(tail);
            c ^= 0xff;
            Round();
            Round();
            Round();
            var hash = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(hash, a ^ b ^ c ^ d);
            return new PyBytes(hash);

            void Mix(ulong message)
            {
                d ^= message;
                Round();
                a ^= message;
            }

            void Round()
            {
                unchecked
                {
                    a += b; c += d;
                    b = BitOperations.RotateLeft(b, 13) ^ a;
                    d = BitOperations.RotateLeft(d, 16) ^ c;
                    a = BitOperations.RotateLeft(a, 32);
                    c += b; a += d;
                    b = BitOperations.RotateLeft(b, 17) ^ c;
                    d = BitOperations.RotateLeft(d, 21) ^ a;
                    c = BitOperations.RotateLeft(c, 32);
                }
            }
        }
    }
}
