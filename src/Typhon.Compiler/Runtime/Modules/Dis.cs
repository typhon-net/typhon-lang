// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System.Collections.Generic;
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
    }
}
