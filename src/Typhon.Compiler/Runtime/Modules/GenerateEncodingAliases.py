# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

"""Generate codec alias data with the CPython version whose names Typhon exposes."""

from encodings.aliases import aliases
from itertools import groupby
from operator import itemgetter
from pathlib import Path
from platform import python_version


rows = "\n".join(
    "                " + " ".join((codec, *(name for name, _ in group)))
    for codec, group in groupby(aliases.items(), key=itemgetter(1))
)

Path(__file__).with_name("EncodingAliases.Generated.cs").write_text(f'''// Derived from CPython {python_version()} Lib/encodings/aliases.py.
// See CPython-LICENSE.txt for the complete upstream license and copyright notices.
// Regenerate with GenerateEncodingAliases.py using the specified CPython version.

namespace Typhon.Runtime
{{
    using System;
    using System.Collections.Generic;

    internal static class EncodingAliasData
    {{
        internal static OrderedDictionary<string, string> Load()
        {{
            const string rows = """
{rows}
                """;
            var aliases = new OrderedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string row in rows.Split('\\n', StringSplitOptions.RemoveEmptyEntries))
            {{
                string[] names = row.Trim().Split(' ');
                for (int i = 1; i < names.Length; i++) aliases.Add(names[i], names[0]);
            }}
            return aliases;
        }}
    }}
}}
''', encoding="utf-8", newline="\n")
