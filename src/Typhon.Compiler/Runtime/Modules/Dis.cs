// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static class dis_
    {
        // Python 3.15 code flags, in the order exposed by Lib/dis.py. See CPython-LICENSE.txt.
        public static readonly OrderedDictionary<long, string> COMPILER_FLAG_NAMES = new()
        {
            [0x1] = "OPTIMIZED",
            [0x2] = "NEWLOCALS",
            [0x4] = "VARARGS",
            [0x8] = "VARKEYWORDS",
            [0x10] = "NESTED",
            [0x20] = "GENERATOR",
            [0x40] = "NOFREE",
            [0x80] = "COROUTINE",
            [0x100] = "ITERABLE_COROUTINE",
            [0x200] = "ASYNC_GENERATOR",
            [0x4000000] = "HAS_DOCSTRING",
            [0x8000000] = "METHOD",
        };

        public static IEnumerable<(object?, object?)> findlinestarts(object code)
        {
            object? previous = false;
            object? table = Builtins.__invoke((object?)Builtins.getattr(code, "co_lines"), []);
            foreach (object? entry in Builtins.__iter_dyn(table))
            {
                var position = Builtins.__unpack_items(Builtins.__iter_dyn(entry), 3);
                object? line = position[2];
                if (Builtins.__same_object(line, previous)) continue;
                previous = line;
                yield return (position[0], line);
            }
        }

        public static PyIterator<object?> _find_store_names(object co) => Builtins.iter(StoreNames(co));

        public static PyIterator<(object?, object?, object?)> _find_imports(object co) => Builtins.iter(Imports(co));

        // CPython 3.15.2 Lib/dis.py scans the public wordcode, skipping inline caches and EXTENDED_ARG.
        // See CPython-LICENSE.txt for the upstream license.
        static readonly long StoreName = opcode.opmap["STORE_NAME"], StoreGlobal = opcode.opmap["STORE_GLOBAL"];
        static readonly long ImportName = opcode.opmap["IMPORT_NAME"], LoadSmallInt = opcode.opmap["LOAD_SMALL_INT"];
        static readonly long LoadCommonConstant = opcode.opmap["LOAD_COMMON_CONSTANT"];
        static readonly object Unknown = new();
        static readonly Dictionary<long, long> DeoptimizedOpcodes = opcode._specializations
            .SelectMany(family => family.Value.Select(member => (Member: opcode._specialized_opmap[member], Base: opcode.opmap[family.Key])))
            .ToDictionary(entry => entry.Member, entry => entry.Base);

        readonly record struct Instruction(long Op, int Argument);

        static IEnumerable<Instruction> Instructions(object code)
        {
            byte[] bytes = ByteOps.From((object?)Builtins.getattr(code, "co_code"));
            int extended = 0;
            for (int offset = 0; offset < bytes.Length;)
            {
                long op = bytes[offset];
                long baseOp = DeoptimizedOpcodes.GetValueOrDefault(op, op);
                int argument = _opcode.has_arg(baseOp) ? extended | bytes[offset + 1] : 0;
                offset += 2 * (1 + (int)opcode._inline_cache_entries.GetValueOrDefault(opcode.opname[(int)baseOp]));
                if (baseOp == opcode.EXTENDED_ARG)
                {
                    extended = argument << 8;
                    continue;
                }
                extended = 0;
                yield return new(op, argument);
            }
        }

        static IEnumerable<object?> StoreNames(object code)
        {
            object? names = Builtins.getattr(code, "co_names");
            foreach (var instruction in Instructions(code))
                if (instruction.Op == StoreName || instruction.Op == StoreGlobal)
                    yield return Builtins.__getitem(names, (long)instruction.Argument);
        }

        static IEnumerable<(object?, object?, object?)> Imports(object code)
        {
            object? constants = Builtins.getattr(code, "co_consts");
            object? names = Builtins.getattr(code, "co_names");
            Instruction? level = null, fromList = null;
            foreach (var instruction in Instructions(code))
            {
                if (instruction.Op == ImportName && level is { } left && fromList is { } right &&
                    (IsConstant(left.Op) || left.Op == LoadSmallInt) && IsConstant(right.Op))
                {
                    // IMPORT_NAME stores the module's name index above the lazy/eager bits.
                    yield return (Builtins.__getitem(names, (long)(instruction.Argument >> 2)),
                        ResolveConstant(left, constants), ResolveConstant(right, constants));
                }
                level = fromList;
                fromList = instruction;
            }
        }

        static bool IsConstant(long op) => op == LoadCommonConstant || _opcode.has_const(op);

        static object? ResolveConstant(Instruction instruction, object? constants)
        {
            if (instruction.Op == LoadSmallInt) return (long)instruction.Argument;
            if (instruction.Op == LoadCommonConstant)
                return instruction.Argument is >= 7 and <= 11 ? opcode._common_constants[instruction.Argument] : Unknown;
            return constants == null ? Unknown : Builtins.__getitem(constants, (long)instruction.Argument);
        }
    }
}
