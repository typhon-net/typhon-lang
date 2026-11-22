// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System.Collections.Generic;
    using Typhon.Runtime;

    internal static class dis_
    {
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
