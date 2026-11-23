// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    // CPython 3.15.2 opcode API and inline-cache layout. See CPython-LICENSE.txt.
    internal static class opcode
    {
        public static readonly List<string> __all__ = ["cmp_op", "stack_effect", "hascompare", "opname", "opmap", "HAVE_ARGUMENT", "EXTENDED_ARG", "hasarg", "hasconst", "hasname", "hasjump", "hasjrel", "hasjabs", "hasfree", "haslocal", "hasexc"];

        public const long HAVE_ARGUMENT = _opcode_metadata.HAVE_ARGUMENT;
        public const long MIN_INSTRUMENTED_OPCODE = _opcode_metadata.MIN_INSTRUMENTED_OPCODE;
        public static readonly frozendict<string, long> opmap = _opcode_metadata.opmap;
        public static readonly frozendict<string, long> _specialized_opmap = _opcode_metadata._specialized_opmap;
        public static readonly frozendict<string, PyTuple<string>> _specializations = _opcode_metadata._specializations;
        public static readonly long EXTENDED_ARG = opmap["EXTENDED_ARG"];
        public static readonly List<string> opname = OpcodeNames();

        public static readonly PyTuple<string> cmp_op = new(new[] { "<", "<=", "==", "!=", ">", ">=" });
        public static readonly List<long> hascompare = [opmap["COMPARE_OP"]];
        public static readonly List<long> hasarg = Filter(_opcode.has_arg);
        public static readonly List<long> hasconst = Filter(_opcode.has_const);
        public static readonly List<long> hasname = Filter(_opcode.has_name);
        public static readonly List<long> hasjump = Filter(_opcode.has_jump);
        public static readonly List<long> hasjrel = hasjump;
        public static readonly List<long> hasjabs = [];
        public static readonly List<long> hasfree = Filter(_opcode.has_free);
        public static readonly List<long> haslocal = Filter(_opcode.has_local);
        public static readonly List<long> hasexc = Filter(_opcode.has_exc);

        public static readonly dynamic stack_effect = PyModule.Of(typeof(_opcode), "_opcode").ImportAttribute("stack_effect")!;
        public static readonly List<string> _intrinsic_1_descs = _opcode.get_intrinsic1_descs();
        public static readonly List<string> _intrinsic_2_descs = _opcode.get_intrinsic2_descs();
        public static readonly List<string> _special_method_names = _opcode.get_special_method_names();
        public static readonly List<(string, string)> _nb_ops = _opcode.get_nb_ops();
        public static readonly List<object?> _common_constants =
        [
            typeof(AssertionError), typeof(NotImplementedException), typeof(ITuple),
            PyModule.Of(typeof(builtins), "builtins").ImportAttribute("all"),
            PyModule.Of(typeof(builtins), "builtins").ImportAttribute("any"),
            typeof(List<object>), typeof(HashSet<object>), null, "", true, false, -1L,
        ];

        public static readonly frozendict<string, frozendict<string, long>> _cache_format = CacheFormats(
        [
            ("LOAD_GLOBAL", [("counter", 1L), ("index", 1L), ("module_keys_version", 1L), ("builtin_keys_version", 1L)]),
            ("BINARY_OP", [("counter", 1L), ("descr", 4L)]),
            ("UNPACK_SEQUENCE", [("counter", 1L)]),
            ("COMPARE_OP", [("counter", 1L)]),
            ("CONTAINS_OP", [("counter", 1L)]),
            ("FOR_ITER", [("counter", 1L)]),
            ("GET_ITER", [("counter", 1L)]),
            ("LOAD_SUPER_ATTR", [("counter", 1L)]),
            ("LOAD_ATTR", [("counter", 1L), ("version", 2L), ("keys_version", 2L), ("descr", 4L)]),
            ("STORE_ATTR", [("counter", 1L), ("version", 2L), ("index", 1L)]),
            ("CALL", [("counter", 1L), ("func_version", 2L)]),
            ("CALL_KW", [("counter", 1L), ("func_version", 2L)]),
            ("CALL_FUNCTION_EX", [("counter", 1L)]),
            ("STORE_SUBSCR", [("counter", 1L)]),
            ("SEND", [("counter", 1L)]),
            ("JUMP_BACKWARD", [("counter", 1L)]),
            ("TO_BOOL", [("counter", 1L), ("version", 2L)]),
            ("POP_JUMP_IF_TRUE", [("counter", 1L)]),
            ("POP_JUMP_IF_FALSE", [("counter", 1L)]),
            ("POP_JUMP_IF_NONE", [("counter", 1L)]),
            ("POP_JUMP_IF_NOT_NONE", [("counter", 1L)]),
            ("RESUME", [("counter", 1L)]),
        ]);
        public static readonly frozendict<string, long> _inline_cache_entries = new(
            _cache_format.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Values.Sum())), StringComparer.Ordinal);

        static List<long> Filter(Func<object?, bool> predicate) =>
            opmap.Values.Where(code => predicate(code)).ToList();

        static List<string> OpcodeNames()
        {
            var result = Enumerable.Range(0, (int)opmap.Values.Max() + 1)
                .Select(code => "<" + code.ToString(CultureInfo.InvariantCulture) + ">").ToList();
            foreach (var (name, code) in opmap.Concat(_specialized_opmap)) result[(int)code] = name;
            return result;
        }

        static frozendict<string, frozendict<string, long>> CacheFormats((string Name, (string Field, long Size)[] Layout)[] formats) =>
            new(formats.Select(format => KeyValuePair.Create(format.Name, new frozendict<string, long>(
                format.Layout.Select(field => KeyValuePair.Create(field.Field, field.Size)), StringComparer.Ordinal))), StringComparer.Ordinal);
    }
}
