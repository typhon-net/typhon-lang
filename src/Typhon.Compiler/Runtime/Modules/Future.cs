// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System.Collections.Generic;
    using Typhon.Runtime;
    using Release = System.ValueTuple<long, long, long, string, long>;

    internal static class __future__
    {
        public static readonly List<string> all_feature_names =
        [
            "nested_scopes", "generators", "division", "absolute_import", "with_statement",
            "print_function", "unicode_literals", "barry_as_FLUFL", "generator_stop", "annotations",
        ];

        public static readonly _Feature nested_scopes = new _Feature((2, 1, 0, "beta", 1), (2, 2, 0, "alpha", 0), 0x0010);
        public static readonly _Feature generators = new _Feature((2, 2, 0, "alpha", 1), (2, 3, 0, "final", 0), 0);
        public static readonly _Feature division = new _Feature((2, 2, 0, "alpha", 2), (3, 0, 0, "alpha", 0), 0x20000);
        public static readonly _Feature absolute_import = new _Feature((2, 5, 0, "alpha", 1), (3, 0, 0, "alpha", 0), 0x40000);
        public static readonly _Feature with_statement = new _Feature((2, 5, 0, "alpha", 1), (2, 6, 0, "alpha", 0), 0x80000);
        public static readonly _Feature print_function = new _Feature((2, 6, 0, "alpha", 2), (3, 0, 0, "alpha", 0), 0x100000);
        public static readonly _Feature unicode_literals = new _Feature((2, 6, 0, "alpha", 2), (3, 0, 0, "alpha", 0), 0x200000);
        public static readonly _Feature barry_as_FLUFL = new _Feature((3, 1, 0, "alpha", 2), (4, 0, 0, "alpha", 0), 0x400000);
        public static readonly _Feature generator_stop = new _Feature((3, 5, 0, "beta", 1), (3, 7, 0, "alpha", 0), 0x800000);
        public static readonly _Feature annotations = new _Feature((3, 7, 0, "beta", 1), null, 0x1000000);

        internal sealed class _Feature : IPyRepr
        {
            public Release optional;
            public Release? mandatory;
            public long compiler_flag;
            public _Feature(Release optionalRelease, Release? mandatoryRelease, long compilerFlag)
            { optional = optionalRelease; mandatory = mandatoryRelease; compiler_flag = compilerFlag; }

            public Release getOptionalRelease() => optional;
            public Release? getMandatoryRelease() => mandatory;
            public string __repr__() => "_Feature" + Builtins.repr((optional, mandatory, compiler_flag));
            public override string ToString() => __repr__();
        }
    }
}
