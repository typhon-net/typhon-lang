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
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class importlib
    {
        internal static class _bootstrap_external
        {
            public static Type SourceFileLoader => typeof(machinery.SourceFileLoader);
            public static Type SourcelessFileLoader => typeof(machinery.SourcelessFileLoader);

            public static long _classify_pyc(object data, string name, object exc_details) =>
                Classify(ByteOps.From(data), name, exc_details);

            public static PyBytes _code_to_timestamp_pyc(types.CodeType code, object? mtime = null, object? source_size = null)
            {
                var result = BytecodeFile(code, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), PackTimestampWord(mtime));
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), PackTimestampWord(source_size));
                return new PyBytes(result);
            }

            static uint PackTimestampWord(object? value) => value == null ? 0 : (uint)(Builtins.__bigint(value) & uint.MaxValue);

            public static PyBytes _code_to_hash_pyc(types.CodeType code, object source_hash, bool @checked = true)
            {
                var hash = ByteOps.Buffer(source_hash);
                if (hash.Length != 8) throw new ArgumentException("a source hash must contain eight bytes");
                var result = BytecodeFile(code, @checked ? 3u : 1u);
                hash.CopyTo(result.AsSpan(8, 8));
                return new PyBytes(result);
            }

            static byte[] BytecodeFile(types.CodeType code, uint flags)
            {
                byte[] contents = code is RuntimeCode source ? source.CpythonBytecode : marshal.dumps(code).Data;
                var header = new byte[16 + contents.Length];
                util.MAGIC_NUMBER.Data.CopyTo(header, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), flags);
                contents.CopyTo(header, 16);
                return header;
            }

            static readonly Lazy<string?> CpythonCompiler = new(FindCpythonCompiler);

            internal static string? CacheTag => CpythonCompiler.Value == null ? null :
                $"cpython-{sys_.version_info.major}{sys_.version_info.minor}";

            static string? FindCpythonCompiler()
            {
                string? explicitPath = Environment.GetEnvironmentVariable("TYPHON_CPYTHON");
                string[] candidates = !string.IsNullOrEmpty(explicitPath) ? [explicitPath] :
                    OperatingSystem.IsWindows() ? ["python", "py"] : ["python3", "python"];
                const string magic = "import importlib.util, sys; sys.stdout.buffer.write(importlib.util.MAGIC_NUMBER)";
                foreach (string executable in candidates)
                {
                    try
                    {
                        var result = subprocess.run(new[] { executable, "-I", "-c", magic }, capture_output: true, timeout: 10);
                        if (result.returncode == 0 && result.stdout is PyBytes bytes && bytes.Data.AsSpan().SequenceEqual(util.MAGIC_NUMBER.Data))
                            return executable;
                    }
                    catch (FileNotFoundException) { }
                    catch (subprocess.TimeoutExpired) { }
                }
                return null;
            }

            internal static byte[] CompileCpythonCode(RuntimeCode code)
            {
                const string script = "import importlib.util, marshal, sys\n" +
                    "source = sys.stdin.buffer.read().decode('utf-8')\n" +
                    "module = sys.argv[5] if len(sys.argv) > 5 else None\n" +
                    "body = compile(source, sys.argv[1], sys.argv[2], int(sys.argv[3]), True, int(sys.argv[4]), module=module)\n" +
                    "sys.stdout.buffer.write(importlib.util.MAGIC_NUMBER + marshal.dumps(body))\n";
                string executable = CpythonCompiler.Value ??
                    throw new NotImplementedException("CPython bytecode requires a matching CPython interpreter");
                var command = new List<string> { executable, "-I", "-c", script, code.co_filename, code.Mode,
                    (code.co_flags & 0x1fe0000L).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    code.Optimize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (code.Module is string module) command.Add(module);
                var result = subprocess.run(command, input: new PyBytes(Encoding.UTF8.GetBytes(code.Source)), capture_output: true);
                if (result.returncode != 0)
                    throw new SyntaxError(ByteOps.Decode(((PyBytes)result.stderr!).Data, "utf-8", "replace").TrimEnd());
                var output = ((PyBytes)result.stdout!).Data;
                if (!output.AsSpan().StartsWith(util.MAGIC_NUMBER.Data))
                    throw new ImportError("the CPython bytecode compiler must use the same bytecode format as Typhon's importlib");
                return output[4..];
            }

            public static long _calc_mode(object path)
            {
                long mode;
                try { mode = os_.stat(path).st_mode; }
                catch (IOException) { mode = 0x1b6; }
                catch (UnauthorizedAccessException) { mode = 0x1b6; }
                return mode | 0x80;
            }

            public static void _write_atomic(object path, object data, long mode = 0x1b6)
            {
                string output = os_.__path(path);
                string temporary = $"{output}.{Guid.NewGuid():N}";
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = (UnixFileMode)(mode & 0x1ff);
                bool created = false;
                try
                {
                    using (var stream = new FileStream(temporary, options))
                    {
                        created = true;
                        stream.Write(ByteOps.Buffer(data));
                    }
                    File.Move(temporary, output, overwrite: true);
                }
                finally
                {
                    if (created) File.Delete(temporary);
                }
            }

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

                public types.CodeType get_code(string target)
                {
                    byte[] data = get_data(get_filename(target)).Data;
                    _bootstrap_external.Classify(data, target);
                    return (object?)marshal.loads(new PyBytes(data[16..])) as types.CodeType
                        ?? throw new ImportError("non-code object in bytecode file", fullname);
                }

                public object? create_module(ModuleSpec spec) => null;
                public void exec_module(object module) =>
                    throw new NotSupportedException("CPython bytecode files cannot be executed by Typhon");
            }
        }
    }
}
