// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static class ProcessEnvironment
    {
        internal static IEnumerable<(object? Key, object? Value)> Entries(object mapping)
        {
            object? items = Builtins.__invoke((object?)Builtins.getattr(mapping, "items"), []);
            foreach (object item in Builtins.__iter_dyn(items))
            {
                var pair = Builtins.__iter_dyn(item).Cast<object?>().ToArray();
                if (pair.Length != 2) throw new ArgumentException("environment entry must contain a key and value");
                yield return (pair[0], pair[1]);
            }
        }
    }
}
