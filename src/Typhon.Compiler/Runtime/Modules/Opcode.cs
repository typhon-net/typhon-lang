// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using Typhon.Runtime;

    // Bytecode metadata measured against the CPython 3.15.2 _opcode API.
    // See CPython-LICENSE.txt for upstream attribution and license terms.
    // The Typhon compiler emits CLR instructions and does not run CPython's adaptive interpreter.
    internal static class _opcode
    {
        public const long ENABLE_SPECIALIZATION = 0;

        const byte Argument = 1, Constant = 2, Name = 4, Jump = 8, Free = 16, Local = 32, ExceptionHandler = 64;

        enum ArgumentRule { None, Whole, Double, LowBit, Split }

        readonly record struct StackCount(int Count, ArgumentRule Rule = ArgumentRule.None)
        {
            internal int For(int argument) => unchecked(Count + (Rule switch
            {
                ArgumentRule.Whole => argument,
                ArgumentRule.Double => argument * 2,
                ArgumentRule.LowBit => argument & 1,
                ArgumentRule.Split => (argument & 255) + (argument >> 8),
                _ => 0,
            }));
        }

        sealed record Instruction(string Name, byte Flags, StackCount? Pops = null, StackCount? Pushes = null)
        {
            internal Instruction(string name, byte flags, int pops, int pushes) : this(name, flags, new StackCount(pops), new StackCount(pushes)) { }
        }

        static readonly Instruction?[] Instructions =
        [
            new("CACHE", 0x00, 0, 0), // 0
            new("BINARY_SLICE", 0x00, 3, 1), // 1
            new("BUILD_TEMPLATE", 0x00, 2, 1), // 2
            new("BINARY_OP_INPLACE_ADD_UNICODE", 0x20), // 3
            new("CALL_FUNCTION_EX", 0x00, 4, 1), // 4
            new("CHECK_EG_MATCH", 0x00, 2, 2), // 5
            new("CHECK_EXC_MATCH", 0x00, 2, 2), // 6
            new("CLEANUP_THROW", 0x00, 4, 3), // 7
            new("DELETE_SUBSCR", 0x00, 2, 0), // 8
            new("END_FOR", 0x00, 1, 0), // 9
            new("END_SEND", 0x00, 3, 1), // 10
            new("EXIT_INIT_CHECK", 0x00, 1, 0), // 11
            new("FORMAT_SIMPLE", 0x00, 1, 1), // 12
            new("FORMAT_WITH_SPEC", 0x00, 2, 1), // 13
            new("GET_AITER", 0x00, 1, 1), // 14
            new("GET_ANEXT", 0x00, 1, 2), // 15
            new("GET_LEN", 0x00, 1, 2), // 16
            new("RESERVED", 0x00, 0, 0), // 17
            new("INTERPRETER_EXIT", 0x00, 1, 0), // 18
            new("LOAD_BUILD_CLASS", 0x00, 0, 1), // 19
            new("LOAD_LOCALS", 0x00, 0, 1), // 20
            new("MAKE_FUNCTION", 0x00, 1, 1), // 21
            new("MATCH_KEYS", 0x00, 2, 3), // 22
            new("MATCH_MAPPING", 0x00, 1, 2), // 23
            new("MATCH_SEQUENCE", 0x00, 1, 2), // 24
            new("NOP", 0x00, 0, 0), // 25
            new("NOT_TAKEN", 0x00, 0, 0), // 26
            new("POP_EXCEPT", 0x00, 1, 0), // 27
            new("POP_ITER", 0x00, 2, 0), // 28
            new("POP_TOP", 0x00, 1, 0), // 29
            new("PUSH_EXC_INFO", 0x00, 1, 2), // 30
            new("PUSH_NULL", 0x00, 0, 1), // 31
            new("RETURN_GENERATOR", 0x00, 0, 1), // 32
            new("RETURN_VALUE", 0x00, 1, 1), // 33
            new("SETUP_ANNOTATIONS", 0x00, 0, 0), // 34
            new("STORE_SLICE", 0x00, 4, 0), // 35
            new("STORE_SUBSCR", 0x00, 3, 0), // 36
            new("TO_BOOL", 0x00, 1, 1), // 37
            new("UNARY_INVERT", 0x00, 1, 1), // 38
            new("UNARY_NEGATIVE", 0x00, 1, 1), // 39
            new("UNARY_NOT", 0x00, 1, 1), // 40
            new("WITH_EXCEPT_START", 0x00, 5, 6), // 41
            new("BINARY_OP", 0x01, 2, 1), // 42
            new("BUILD_INTERPOLATION", 0x01, new StackCount(2, ArgumentRule.LowBit), new StackCount(1)), // 43
            new("BUILD_LIST", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 44
            new("BUILD_MAP", 0x01, new StackCount(0, ArgumentRule.Double), new StackCount(1)), // 45
            new("BUILD_SET", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 46
            new("BUILD_SLICE", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 47
            new("BUILD_STRING", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 48
            new("BUILD_TUPLE", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 49
            new("CALL", 0x01, new StackCount(2, ArgumentRule.Whole), new StackCount(1)), // 50
            new("CALL_INTRINSIC_1", 0x01, 1, 1), // 51
            new("CALL_INTRINSIC_2", 0x01, 2, 1), // 52
            new("CALL_KW", 0x01, new StackCount(3, ArgumentRule.Whole), new StackCount(1)), // 53
            new("COMPARE_OP", 0x01, 2, 1), // 54
            new("CONTAINS_OP", 0x01, 2, 1), // 55
            new("CONVERT_VALUE", 0x01, 1, 1), // 56
            new("COPY", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1, ArgumentRule.Whole)), // 57
            new("COPY_FREE_VARS", 0x01, 0, 0), // 58
            new("DELETE_ATTR", 0x05, 1, 0), // 59
            new("DELETE_DEREF", 0x11, 0, 0), // 60
            new("DELETE_FAST", 0x21, 0, 0), // 61
            new("DELETE_GLOBAL", 0x05, 0, 0), // 62
            new("DELETE_NAME", 0x05, 0, 0), // 63
            new("DICT_MERGE", 0x01, new StackCount(4, ArgumentRule.Whole), new StackCount(3, ArgumentRule.Whole)), // 64
            new("DICT_UPDATE", 0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 65
            new("END_ASYNC_FOR", 0x09, 2, 0), // 66
            new("EXTENDED_ARG", 0x01, 0, 0), // 67
            new("FOR_ITER", 0x09, 2, 3), // 68
            new("GET_AWAITABLE", 0x01, 1, 1), // 69
            new("GET_ITER", 0x01, 1, 2), // 70
            new("IMPORT_FROM", 0x05, 1, 2), // 71
            new("IMPORT_NAME", 0x05, 2, 1), // 72
            new("IS_OP", 0x01, 2, 1), // 73
            new("JUMP_BACKWARD", 0x09, 0, 0), // 74
            new("JUMP_BACKWARD_NO_INTERRUPT", 0x09, 0, 0), // 75
            new("JUMP_FORWARD", 0x09, 0, 0), // 76
            new("LIST_APPEND", 0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 77
            new("LIST_EXTEND", 0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 78
            new("LOAD_ATTR", 0x05, new StackCount(1), new StackCount(1, ArgumentRule.LowBit)), // 79
            new("LOAD_COMMON_CONSTANT", 0x01, 0, 1), // 80
            new("LOAD_CONST", 0x03, 0, 1), // 81
            new("LOAD_DEREF", 0x21, 0, 1), // 82
            new("LOAD_FAST", 0x21, 0, 1), // 83
            new("LOAD_FAST_AND_CLEAR", 0x21, 0, 1), // 84
            new("LOAD_FAST_BORROW", 0x21, 0, 1), // 85
            new("LOAD_FAST_BORROW_LOAD_FAST_BORROW", 0x21, 0, 2), // 86
            new("LOAD_FAST_CHECK", 0x21, 0, 1), // 87
            new("LOAD_FAST_LOAD_FAST", 0x21, 0, 2), // 88
            new("LOAD_FROM_DICT_OR_DEREF", 0x11, 1, 1), // 89
            new("LOAD_FROM_DICT_OR_GLOBALS", 0x05, 1, 1), // 90
            new("LOAD_GLOBAL", 0x05, new StackCount(0), new StackCount(1, ArgumentRule.LowBit)), // 91
            new("LOAD_NAME", 0x05, 0, 1), // 92
            new("LOAD_SMALL_INT", 0x01, 0, 1), // 93
            new("LOAD_SPECIAL", 0x01, 1, 2), // 94
            new("LOAD_SUPER_ATTR", 0x05, new StackCount(3), new StackCount(1, ArgumentRule.LowBit)), // 95
            new("MAKE_CELL", 0x11, 0, 0), // 96
            new("MAP_ADD", 0x01, new StackCount(2, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 97
            new("MATCH_CLASS", 0x01, 3, 1), // 98
            new("POP_JUMP_IF_FALSE", 0x09, 1, 0), // 99
            new("POP_JUMP_IF_NONE", 0x09, 1, 0), // 100
            new("POP_JUMP_IF_NOT_NONE", 0x09, 1, 0), // 101
            new("POP_JUMP_IF_TRUE", 0x09, 1, 0), // 102
            new("RAISE_VARARGS", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(0)), // 103
            new("RERAISE", 0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 104
            new("SEND", 0x09, 3, 3), // 105
            new("SET_ADD", 0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 106
            new("SET_FUNCTION_ATTRIBUTE", 0x01, 2, 1), // 107
            new("SET_UPDATE", 0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 108
            new("STORE_ATTR", 0x05, 2, 0), // 109
            new("STORE_DEREF", 0x11, 1, 0), // 110
            new("STORE_FAST", 0x21, 1, 0), // 111
            new("STORE_FAST_LOAD_FAST", 0x21, 1, 1), // 112
            new("STORE_FAST_STORE_FAST", 0x21, 2, 0), // 113
            new("STORE_GLOBAL", 0x05, 1, 0), // 114
            new("STORE_NAME", 0x05, 1, 0), // 115
            new("SWAP", 0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 116
            new("UNPACK_EX", 0x01, new StackCount(1), new StackCount(1, ArgumentRule.Split)), // 117
            new("UNPACK_SEQUENCE", 0x01, new StackCount(1), new StackCount(0, ArgumentRule.Whole)), // 118
            new("YIELD_VALUE", 0x01, 1, 1), // 119
            null, // 120
            null, // 121
            null, // 122
            null, // 123
            null, // 124
            null, // 125
            null, // 126
            null, // 127
            new("RESUME", 0x01, 0, 0), // 128
            new("BINARY_OP_ADD_FLOAT", 0x00), // 129
            new("BINARY_OP_ADD_INT", 0x00), // 130
            new("BINARY_OP_ADD_UNICODE", 0x00), // 131
            new("BINARY_OP_EXTEND", 0x00), // 132
            new("BINARY_OP_MULTIPLY_FLOAT", 0x00), // 133
            new("BINARY_OP_MULTIPLY_INT", 0x00), // 134
            new("BINARY_OP_SUBSCR_DICT", 0x00), // 135
            new("BINARY_OP_SUBSCR_GETITEM", 0x00), // 136
            new("BINARY_OP_SUBSCR_LIST_INT", 0x00), // 137
            new("BINARY_OP_SUBSCR_LIST_SLICE", 0x00), // 138
            new("BINARY_OP_SUBSCR_STR_INT", 0x00), // 139
            new("BINARY_OP_SUBSCR_TUPLE_INT", 0x00), // 140
            new("BINARY_OP_SUBSCR_USTR_INT", 0x00), // 141
            new("BINARY_OP_SUBTRACT_FLOAT", 0x00), // 142
            new("BINARY_OP_SUBTRACT_INT", 0x00), // 143
            new("CALL_ALLOC_AND_ENTER_INIT", 0x01), // 144
            new("CALL_BOUND_METHOD_EXACT_ARGS", 0x01), // 145
            new("CALL_BOUND_METHOD_GENERAL", 0x01), // 146
            new("CALL_BUILTIN_CLASS", 0x01), // 147
            new("CALL_BUILTIN_FAST", 0x01), // 148
            new("CALL_BUILTIN_FAST_WITH_KEYWORDS", 0x01), // 149
            new("CALL_BUILTIN_O", 0x01), // 150
            new("CALL_EX_NON_PY_GENERAL", 0x00), // 151
            new("CALL_EX_PY", 0x01), // 152
            new("CALL_ISINSTANCE", 0x00), // 153
            new("CALL_KW_BOUND_METHOD", 0x01), // 154
            new("CALL_KW_NON_PY", 0x01), // 155
            new("CALL_KW_PY", 0x01), // 156
            new("CALL_LEN", 0x00), // 157
            new("CALL_LIST_APPEND", 0x01), // 158
            new("CALL_METHOD_DESCRIPTOR_FAST", 0x01), // 159
            new("CALL_METHOD_DESCRIPTOR_FAST_WITH_KEYWORDS", 0x01), // 160
            new("CALL_METHOD_DESCRIPTOR_NOARGS", 0x01), // 161
            new("CALL_METHOD_DESCRIPTOR_O", 0x01), // 162
            new("CALL_NON_PY_GENERAL", 0x01), // 163
            new("CALL_PY_EXACT_ARGS", 0x01), // 164
            new("CALL_PY_GENERAL", 0x01), // 165
            new("CALL_STR_1", 0x01), // 166
            new("CALL_TUPLE_1", 0x01), // 167
            new("CALL_TYPE_1", 0x01), // 168
            new("COMPARE_OP_FLOAT", 0x01), // 169
            new("COMPARE_OP_INT", 0x01), // 170
            new("COMPARE_OP_STR", 0x01), // 171
            new("CONTAINS_OP_DICT", 0x01), // 172
            new("CONTAINS_OP_SET", 0x01), // 173
            new("FOR_ITER_GEN", 0x01), // 174
            new("FOR_ITER_LIST", 0x09), // 175
            new("FOR_ITER_RANGE", 0x09), // 176
            new("FOR_ITER_TUPLE", 0x09), // 177
            new("FOR_ITER_VIRTUAL", 0x09), // 178
            new("GET_ITER_SELF", 0x00), // 179
            new("GET_ITER_VIRTUAL", 0x00), // 180
            new("JUMP_BACKWARD_JIT", 0x09), // 181
            new("JUMP_BACKWARD_NO_JIT", 0x09), // 182
            new("LOAD_ATTR_CLASS", 0x01), // 183
            new("LOAD_ATTR_CLASS_WITH_METACLASS_CHECK", 0x01), // 184
            new("LOAD_ATTR_GETATTRIBUTE_OVERRIDDEN", 0x05), // 185
            new("LOAD_ATTR_INSTANCE_VALUE", 0x01), // 186
            new("LOAD_ATTR_METHOD_LAZY_DICT", 0x01), // 187
            new("LOAD_ATTR_METHOD_NO_DICT", 0x01), // 188
            new("LOAD_ATTR_METHOD_WITH_VALUES", 0x01), // 189
            new("LOAD_ATTR_MODULE", 0x01), // 190
            new("LOAD_ATTR_NONDESCRIPTOR_NO_DICT", 0x01), // 191
            new("LOAD_ATTR_NONDESCRIPTOR_WITH_VALUES", 0x01), // 192
            new("LOAD_ATTR_PROPERTY", 0x01), // 193
            new("LOAD_ATTR_SLOT", 0x01), // 194
            new("LOAD_ATTR_WITH_HINT", 0x05), // 195
            new("LOAD_GLOBAL_BUILTIN", 0x01), // 196
            new("LOAD_GLOBAL_MODULE", 0x01), // 197
            new("LOAD_SUPER_ATTR_ATTR", 0x05), // 198
            new("LOAD_SUPER_ATTR_METHOD", 0x05), // 199
            new("RESUME_CHECK", 0x00), // 200
            new("RESUME_CHECK_JIT", 0x01), // 201
            new("SEND_ASYNC_GEN", 0x09), // 202
            new("SEND_GEN", 0x01), // 203
            new("SEND_VIRTUAL", 0x09), // 204
            new("STORE_ATTR_INSTANCE_VALUE", 0x00), // 205
            new("STORE_ATTR_SLOT", 0x00), // 206
            new("STORE_ATTR_WITH_HINT", 0x05), // 207
            new("STORE_SUBSCR_DICT", 0x00), // 208
            new("STORE_SUBSCR_LIST_INT", 0x00), // 209
            new("TO_BOOL_ALWAYS_TRUE", 0x00), // 210
            new("TO_BOOL_BOOL", 0x00), // 211
            new("TO_BOOL_INT", 0x00), // 212
            new("TO_BOOL_LIST", 0x00), // 213
            new("TO_BOOL_NONE", 0x00), // 214
            new("TO_BOOL_STR", 0x00), // 215
            new("UNPACK_SEQUENCE_LIST", 0x01), // 216
            new("UNPACK_SEQUENCE_TUPLE", 0x01), // 217
            new("UNPACK_SEQUENCE_TWO_TUPLE", 0x01), // 218
            null, // 219
            null, // 220
            null, // 221
            null, // 222
            null, // 223
            null, // 224
            null, // 225
            null, // 226
            null, // 227
            null, // 228
            null, // 229
            null, // 230
            null, // 231
            null, // 232
            new("INSTRUMENTED_END_FOR", 0x00, 3, 2), // 233
            new("INSTRUMENTED_POP_ITER", 0x00, 2, 0), // 234
            new("INSTRUMENTED_END_SEND", 0x00, 3, 1), // 235
            new("INSTRUMENTED_FOR_ITER", 0x09, 2, 3), // 236
            new("INSTRUMENTED_INSTRUCTION", 0x00, 0, 0), // 237
            new("INSTRUMENTED_JUMP_FORWARD", 0x01, 0, 0), // 238
            new("INSTRUMENTED_NOT_TAKEN", 0x00, 0, 0), // 239
            new("INSTRUMENTED_POP_JUMP_IF_TRUE", 0x01, 1, 0), // 240
            new("INSTRUMENTED_POP_JUMP_IF_FALSE", 0x01, 1, 0), // 241
            new("INSTRUMENTED_POP_JUMP_IF_NONE", 0x01, 1, 0), // 242
            new("INSTRUMENTED_POP_JUMP_IF_NOT_NONE", 0x01, 1, 0), // 243
            new("INSTRUMENTED_RESUME", 0x01, 0, 0), // 244
            new("INSTRUMENTED_RETURN_VALUE", 0x00, 1, 1), // 245
            new("INSTRUMENTED_YIELD_VALUE", 0x01, 1, 1), // 246
            new("INSTRUMENTED_END_ASYNC_FOR", 0x09, 2, 0), // 247
            new("INSTRUMENTED_LOAD_SUPER_ATTR", 0x05, new StackCount(3), new StackCount(1, ArgumentRule.LowBit)), // 248
            new("INSTRUMENTED_CALL", 0x01, new StackCount(2, ArgumentRule.Whole), new StackCount(1)), // 249
            new("INSTRUMENTED_CALL_KW", 0x01, new StackCount(3, ArgumentRule.Whole), new StackCount(1)), // 250
            new("INSTRUMENTED_CALL_FUNCTION_EX", 0x00, 4, 1), // 251
            new("INSTRUMENTED_JUMP_BACKWARD", 0x01, 0, 0), // 252
            new("INSTRUMENTED_LINE", 0x00, 0, 0), // 253
            new("ENTER_EXECUTOR", 0x01, 0, 0), // 254
            new("TRACE_RECORD", 0x01, 0, 0), // 255
            new("ANNOTATIONS_PLACEHOLDER", 0x00, 0, 0), // 256
            new("JUMP", 0x09, 0, 0), // 257
            new("JUMP_IF_FALSE", 0x09, 1, 1), // 258
            new("JUMP_IF_TRUE", 0x09, 1, 1), // 259
            new("JUMP_NO_INTERRUPT", 0x09, 0, 0), // 260
            new("LOAD_CLOSURE", 0x21, 0, 1), // 261
            new("POP_BLOCK", 0x00, 0, 0), // 262
            new("SETUP_CLEANUP", 0x41, 0, 2), // 263
            new("SETUP_FINALLY", 0x41, 0, 1), // 264
            new("SETUP_WITH", 0x41, 0, 1), // 265
            new("STORE_FAST_MAYBE_NULL", 0x21, 1, 0), // 266
        ];

        internal static string InstructionName(int code) =>
            Instructions[code]?.Name ?? throw new ArgumentOutOfRangeException(nameof(code));

        static long Integer(object? value)
        {
            var index = Builtins.__index_value(value);
            if (index is long number) return number;
            var integer = (BigInteger)index;
            if (integer < long.MinValue || integer > long.MaxValue)
                throw new OverflowException("Python int too large to convert to C long");
            return (long)integer;
        }

        static int IntArgument(object? value)
        {
            long number = Integer(value);
            if (number < int.MinValue || number > int.MaxValue)
                throw new OverflowException("Python int too large to convert to C int");
            return (int)number;
        }

        static Instruction? Lookup(object? opcode)
        {
            int number = IntArgument(opcode);
            return number >= 0 && number < Instructions.Length ? Instructions[number] : null;
        }

        static bool Has(object? opcode, byte flag) => Lookup(opcode) is Instruction info && (info.Flags & flag) != 0;

        public static bool is_valid(object? opcode) => Lookup(opcode) != null;
        public static bool has_arg(object? opcode) => Has(opcode, Argument);
        public static bool has_const(object? opcode) => Has(opcode, Constant);
        public static bool has_name(object? opcode) => Has(opcode, Name);
        public static bool has_jump(object? opcode) => Has(opcode, Jump);
        public static bool has_free(object? opcode) => Has(opcode, Free);
        public static bool has_local(object? opcode) => Has(opcode, Local);
        public static bool has_exc(object? opcode) => Has(opcode, ExceptionHandler);

        public static long stack_effect(object? opcode, object? oparg = null, object? jump = null)
        {
            var info = Lookup(opcode);
            int argument = oparg == null ? 0 : unchecked((int)Integer(oparg));
            if (jump is not (null or bool)) throw new ArgumentException("stack_effect: jump must be False, True or None");
            if (info is not { Pops: StackCount pops, Pushes: StackCount pushes })
                throw new ArgumentException("invalid opcode or oparg");
            int popped = pops.For(argument);
            int pushed = pushes.For(argument);
            if (popped < 0 || pushed < 0) throw new ArgumentException("invalid opcode or oparg");
            return unchecked(pushed - popped);
        }

        public static object get_executor(object? code, object? offset)
        {
            _ = IntArgument(offset);
            if (code is not types.CodeType)
                throw new InvalidCastException($"expected a code object, not '{Builtins.PyTypeName(Builtins.__type_of(code))}'");
            throw new ArgumentException("no executor at given byte offset");
        }

        public static object? get_specialization_stats() => null;

        public static List<string> get_intrinsic1_descs() =>
            ["INTRINSIC_1_INVALID", "INTRINSIC_PRINT", "INTRINSIC_IMPORT_STAR", "INTRINSIC_STOPITERATION_ERROR", "INTRINSIC_ASYNC_GEN_WRAP", "INTRINSIC_UNARY_POSITIVE", "INTRINSIC_LIST_TO_TUPLE", "INTRINSIC_TYPEVAR", "INTRINSIC_PARAMSPEC", "INTRINSIC_TYPEVARTUPLE", "INTRINSIC_SUBSCRIPT_GENERIC", "INTRINSIC_TYPEALIAS"];

        public static List<string> get_intrinsic2_descs() =>
            ["INTRINSIC_2_INVALID", "INTRINSIC_PREP_RERAISE_STAR", "INTRINSIC_TYPEVAR_WITH_BOUND", "INTRINSIC_TYPEVAR_WITH_CONSTRAINTS", "INTRINSIC_SET_FUNCTION_TYPE_PARAMS", "INTRINSIC_SET_TYPEPARAM_DEFAULT"];

        public static List<string> get_special_method_names() =>
            ["__enter__", "__exit__", "__aenter__", "__aexit__"];

        public static List<(string, string)> get_nb_ops() =>
        [
            ("NB_ADD", "+"),
            ("NB_AND", "&"),
            ("NB_FLOOR_DIVIDE", "//"),
            ("NB_LSHIFT", "<<"),
            ("NB_MATRIX_MULTIPLY", "@"),
            ("NB_MULTIPLY", "*"),
            ("NB_REMAINDER", "%"),
            ("NB_OR", "|"),
            ("NB_POWER", "**"),
            ("NB_RSHIFT", ">>"),
            ("NB_SUBTRACT", "-"),
            ("NB_TRUE_DIVIDE", "/"),
            ("NB_XOR", "^"),
            ("NB_INPLACE_ADD", "+="),
            ("NB_INPLACE_AND", "&="),
            ("NB_INPLACE_FLOOR_DIVIDE", "//="),
            ("NB_INPLACE_LSHIFT", "<<="),
            ("NB_INPLACE_MATRIX_MULTIPLY", "@="),
            ("NB_INPLACE_MULTIPLY", "*="),
            ("NB_INPLACE_REMAINDER", "%="),
            ("NB_INPLACE_OR", "|="),
            ("NB_INPLACE_POWER", "**="),
            ("NB_INPLACE_RSHIFT", ">>="),
            ("NB_INPLACE_SUBTRACT", "-="),
            ("NB_INPLACE_TRUE_DIVIDE", "/="),
            ("NB_INPLACE_XOR", "^="),
            ("NB_SUBSCR", "[]")
        ];
    }
}
