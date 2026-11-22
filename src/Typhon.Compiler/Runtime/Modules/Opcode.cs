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

        sealed record Instruction(byte Flags, StackCount? Pops = null, StackCount? Pushes = null)
        {
            internal Instruction(byte flags, int pops, int pushes) : this(flags, new StackCount(pops), new StackCount(pushes)) { }
        }

        static readonly Instruction?[] Instructions =
        [
            new(0x00, 0, 0), // 0 CACHE
            new(0x00, 3, 1), // 1 BINARY_SLICE
            new(0x00, 2, 1), // 2 BUILD_TEMPLATE
            new(0x20), // 3 BINARY_OP_INPLACE_ADD_UNICODE
            new(0x00, 4, 1), // 4 CALL_FUNCTION_EX
            new(0x00, 2, 2), // 5 CHECK_EG_MATCH
            new(0x00, 2, 2), // 6 CHECK_EXC_MATCH
            new(0x00, 4, 3), // 7 CLEANUP_THROW
            new(0x00, 2, 0), // 8 DELETE_SUBSCR
            new(0x00, 1, 0), // 9 END_FOR
            new(0x00, 3, 1), // 10 END_SEND
            new(0x00, 1, 0), // 11 EXIT_INIT_CHECK
            new(0x00, 1, 1), // 12 FORMAT_SIMPLE
            new(0x00, 2, 1), // 13 FORMAT_WITH_SPEC
            new(0x00, 1, 1), // 14 GET_AITER
            new(0x00, 1, 2), // 15 GET_ANEXT
            new(0x00, 1, 2), // 16 GET_LEN
            new(0x00, 0, 0), // 17 RESERVED
            new(0x00, 1, 0), // 18 INTERPRETER_EXIT
            new(0x00, 0, 1), // 19 LOAD_BUILD_CLASS
            new(0x00, 0, 1), // 20 LOAD_LOCALS
            new(0x00, 1, 1), // 21 MAKE_FUNCTION
            new(0x00, 2, 3), // 22 MATCH_KEYS
            new(0x00, 1, 2), // 23 MATCH_MAPPING
            new(0x00, 1, 2), // 24 MATCH_SEQUENCE
            new(0x00, 0, 0), // 25 NOP
            new(0x00, 0, 0), // 26 NOT_TAKEN
            new(0x00, 1, 0), // 27 POP_EXCEPT
            new(0x00, 2, 0), // 28 POP_ITER
            new(0x00, 1, 0), // 29 POP_TOP
            new(0x00, 1, 2), // 30 PUSH_EXC_INFO
            new(0x00, 0, 1), // 31 PUSH_NULL
            new(0x00, 0, 1), // 32 RETURN_GENERATOR
            new(0x00, 1, 1), // 33 RETURN_VALUE
            new(0x00, 0, 0), // 34 SETUP_ANNOTATIONS
            new(0x00, 4, 0), // 35 STORE_SLICE
            new(0x00, 3, 0), // 36 STORE_SUBSCR
            new(0x00, 1, 1), // 37 TO_BOOL
            new(0x00, 1, 1), // 38 UNARY_INVERT
            new(0x00, 1, 1), // 39 UNARY_NEGATIVE
            new(0x00, 1, 1), // 40 UNARY_NOT
            new(0x00, 5, 6), // 41 HAVE_ARGUMENT
            new(0x01, 2, 1), // 42 BINARY_OP
            new(0x01, new StackCount(2, ArgumentRule.LowBit), new StackCount(1)), // 43 BUILD_INTERPOLATION
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 44 BUILD_LIST
            new(0x01, new StackCount(0, ArgumentRule.Double), new StackCount(1)), // 45 BUILD_MAP
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 46 BUILD_SET
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 47 BUILD_SLICE
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 48 BUILD_STRING
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1)), // 49 BUILD_TUPLE
            new(0x01, new StackCount(2, ArgumentRule.Whole), new StackCount(1)), // 50 CALL
            new(0x01, 1, 1), // 51 CALL_INTRINSIC_1
            new(0x01, 2, 1), // 52 CALL_INTRINSIC_2
            new(0x01, new StackCount(3, ArgumentRule.Whole), new StackCount(1)), // 53 CALL_KW
            new(0x01, 2, 1), // 54 COMPARE_OP
            new(0x01, 2, 1), // 55 CONTAINS_OP
            new(0x01, 1, 1), // 56 CONVERT_VALUE
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(1, ArgumentRule.Whole)), // 57 COPY
            new(0x01, 0, 0), // 58 COPY_FREE_VARS
            new(0x05, 1, 0), // 59 DELETE_ATTR
            new(0x11, 0, 0), // 60 DELETE_DEREF
            new(0x21, 0, 0), // 61 DELETE_FAST
            new(0x05, 0, 0), // 62 DELETE_GLOBAL
            new(0x05, 0, 0), // 63 DELETE_NAME
            new(0x01, new StackCount(4, ArgumentRule.Whole), new StackCount(3, ArgumentRule.Whole)), // 64 DICT_MERGE
            new(0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 65 DICT_UPDATE
            new(0x09, 2, 0), // 66 END_ASYNC_FOR
            new(0x01, 0, 0), // 67 EXTENDED_ARG
            new(0x09, 2, 3), // 68 FOR_ITER
            new(0x01, 1, 1), // 69 GET_AWAITABLE
            new(0x01, 1, 2), // 70 GET_ITER
            new(0x05, 1, 2), // 71 IMPORT_FROM
            new(0x05, 2, 1), // 72 IMPORT_NAME
            new(0x01, 2, 1), // 73 IS_OP
            new(0x09, 0, 0), // 74 JUMP_BACKWARD
            new(0x09, 0, 0), // 75 JUMP_BACKWARD_NO_INTERRUPT
            new(0x09, 0, 0), // 76 JUMP_FORWARD
            new(0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 77 LIST_APPEND
            new(0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 78 LIST_EXTEND
            new(0x05, new StackCount(1), new StackCount(1, ArgumentRule.LowBit)), // 79 LOAD_ATTR
            new(0x01, 0, 1), // 80 LOAD_COMMON_CONSTANT
            new(0x03, 0, 1), // 81 LOAD_CONST
            new(0x21, 0, 1), // 82 LOAD_DEREF
            new(0x21, 0, 1), // 83 LOAD_FAST
            new(0x21, 0, 1), // 84 LOAD_FAST_AND_CLEAR
            new(0x21, 0, 1), // 85 LOAD_FAST_BORROW
            new(0x21, 0, 2), // 86 LOAD_FAST_BORROW_LOAD_FAST_BORROW
            new(0x21, 0, 1), // 87 LOAD_FAST_CHECK
            new(0x21, 0, 2), // 88 LOAD_FAST_LOAD_FAST
            new(0x11, 1, 1), // 89 LOAD_FROM_DICT_OR_DEREF
            new(0x05, 1, 1), // 90 LOAD_FROM_DICT_OR_GLOBALS
            new(0x05, new StackCount(0), new StackCount(1, ArgumentRule.LowBit)), // 91 LOAD_GLOBAL
            new(0x05, 0, 1), // 92 LOAD_NAME
            new(0x01, 0, 1), // 93 LOAD_SMALL_INT
            new(0x01, 1, 2), // 94 LOAD_SPECIAL
            new(0x05, new StackCount(3), new StackCount(1, ArgumentRule.LowBit)), // 95 LOAD_SUPER_ATTR
            new(0x11, 0, 0), // 96 MAKE_CELL
            new(0x01, new StackCount(2, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 97 MAP_ADD
            new(0x01, 3, 1), // 98 MATCH_CLASS
            new(0x09, 1, 0), // 99 POP_JUMP_IF_FALSE
            new(0x09, 1, 0), // 100 POP_JUMP_IF_NONE
            new(0x09, 1, 0), // 101 POP_JUMP_IF_NOT_NONE
            new(0x09, 1, 0), // 102 POP_JUMP_IF_TRUE
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(0)), // 103 RAISE_VARARGS
            new(0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 104 RERAISE
            new(0x09, 3, 3), // 105 SEND
            new(0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 106 SET_ADD
            new(0x01, 2, 1), // 107 SET_FUNCTION_ATTRIBUTE
            new(0x01, new StackCount(1, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 108 SET_UPDATE
            new(0x05, 2, 0), // 109 STORE_ATTR
            new(0x11, 1, 0), // 110 STORE_DEREF
            new(0x21, 1, 0), // 111 STORE_FAST
            new(0x21, 1, 1), // 112 STORE_FAST_LOAD_FAST
            new(0x21, 2, 0), // 113 STORE_FAST_STORE_FAST
            new(0x05, 1, 0), // 114 STORE_GLOBAL
            new(0x05, 1, 0), // 115 STORE_NAME
            new(0x01, new StackCount(0, ArgumentRule.Whole), new StackCount(0, ArgumentRule.Whole)), // 116 SWAP
            new(0x01, new StackCount(1), new StackCount(1, ArgumentRule.Split)), // 117 UNPACK_EX
            new(0x01, new StackCount(1), new StackCount(0, ArgumentRule.Whole)), // 118 UNPACK_SEQUENCE
            new(0x01, 1, 1), // 119 YIELD_VALUE
            null, // 120
            null, // 121
            null, // 122
            null, // 123
            null, // 124
            null, // 125
            null, // 126
            null, // 127
            new(0x01, 0, 0), // 128 RESUME
            new(0x00), // 129 MIN_SPECIALIZED_OPCODE
            new(0x00), // 130 BINARY_OP_ADD_INT
            new(0x00), // 131 BINARY_OP_ADD_UNICODE
            new(0x00), // 132 BINARY_OP_EXTEND
            new(0x00), // 133 BINARY_OP_MULTIPLY_FLOAT
            new(0x00), // 134 BINARY_OP_MULTIPLY_INT
            new(0x00), // 135 BINARY_OP_SUBSCR_DICT
            new(0x00), // 136 BINARY_OP_SUBSCR_GETITEM
            new(0x00), // 137 BINARY_OP_SUBSCR_LIST_INT
            new(0x00), // 138 BINARY_OP_SUBSCR_LIST_SLICE
            new(0x00), // 139 BINARY_OP_SUBSCR_STR_INT
            new(0x00), // 140 BINARY_OP_SUBSCR_TUPLE_INT
            new(0x00), // 141 BINARY_OP_SUBSCR_USTR_INT
            new(0x00), // 142 BINARY_OP_SUBTRACT_FLOAT
            new(0x00), // 143 BINARY_OP_SUBTRACT_INT
            new(0x01), // 144 CALL_ALLOC_AND_ENTER_INIT
            new(0x01), // 145 CALL_BOUND_METHOD_EXACT_ARGS
            new(0x01), // 146 CALL_BOUND_METHOD_GENERAL
            new(0x01), // 147 CALL_BUILTIN_CLASS
            new(0x01), // 148 CALL_BUILTIN_FAST
            new(0x01), // 149 CALL_BUILTIN_FAST_WITH_KEYWORDS
            new(0x01), // 150 CALL_BUILTIN_O
            new(0x00), // 151 CALL_EX_NON_PY_GENERAL
            new(0x01), // 152 CALL_EX_PY
            new(0x00), // 153 CALL_ISINSTANCE
            new(0x01), // 154 CALL_KW_BOUND_METHOD
            new(0x01), // 155 CALL_KW_NON_PY
            new(0x01), // 156 CALL_KW_PY
            new(0x00), // 157 CALL_LEN
            new(0x01), // 158 CALL_LIST_APPEND
            new(0x01), // 159 CALL_METHOD_DESCRIPTOR_FAST
            new(0x01), // 160 CALL_METHOD_DESCRIPTOR_FAST_WITH_KEYWORDS
            new(0x01), // 161 CALL_METHOD_DESCRIPTOR_NOARGS
            new(0x01), // 162 CALL_METHOD_DESCRIPTOR_O
            new(0x01), // 163 CALL_NON_PY_GENERAL
            new(0x01), // 164 CALL_PY_EXACT_ARGS
            new(0x01), // 165 CALL_PY_GENERAL
            new(0x01), // 166 CALL_STR_1
            new(0x01), // 167 CALL_TUPLE_1
            new(0x01), // 168 CALL_TYPE_1
            new(0x01), // 169 COMPARE_OP_FLOAT
            new(0x01), // 170 COMPARE_OP_INT
            new(0x01), // 171 COMPARE_OP_STR
            new(0x01), // 172 CONTAINS_OP_DICT
            new(0x01), // 173 CONTAINS_OP_SET
            new(0x01), // 174 FOR_ITER_GEN
            new(0x09), // 175 FOR_ITER_LIST
            new(0x09), // 176 FOR_ITER_RANGE
            new(0x09), // 177 FOR_ITER_TUPLE
            new(0x09), // 178 FOR_ITER_VIRTUAL
            new(0x00), // 179 GET_ITER_SELF
            new(0x00), // 180 GET_ITER_VIRTUAL
            new(0x09), // 181 JUMP_BACKWARD_JIT
            new(0x09), // 182 JUMP_BACKWARD_NO_JIT
            new(0x01), // 183 LOAD_ATTR_CLASS
            new(0x01), // 184 LOAD_ATTR_CLASS_WITH_METACLASS_CHECK
            new(0x05), // 185 LOAD_ATTR_GETATTRIBUTE_OVERRIDDEN
            new(0x01), // 186 LOAD_ATTR_INSTANCE_VALUE
            new(0x01), // 187 LOAD_ATTR_METHOD_LAZY_DICT
            new(0x01), // 188 LOAD_ATTR_METHOD_NO_DICT
            new(0x01), // 189 LOAD_ATTR_METHOD_WITH_VALUES
            new(0x01), // 190 LOAD_ATTR_MODULE
            new(0x01), // 191 LOAD_ATTR_NONDESCRIPTOR_NO_DICT
            new(0x01), // 192 LOAD_ATTR_NONDESCRIPTOR_WITH_VALUES
            new(0x01), // 193 LOAD_ATTR_PROPERTY
            new(0x01), // 194 LOAD_ATTR_SLOT
            new(0x05), // 195 LOAD_ATTR_WITH_HINT
            new(0x01), // 196 LOAD_GLOBAL_BUILTIN
            new(0x01), // 197 LOAD_GLOBAL_MODULE
            new(0x05), // 198 LOAD_SUPER_ATTR_ATTR
            new(0x05), // 199 LOAD_SUPER_ATTR_METHOD
            new(0x00), // 200 RESUME_CHECK
            new(0x01), // 201 RESUME_CHECK_JIT
            new(0x09), // 202 SEND_ASYNC_GEN
            new(0x01), // 203 SEND_GEN
            new(0x09), // 204 SEND_VIRTUAL
            new(0x00), // 205 STORE_ATTR_INSTANCE_VALUE
            new(0x00), // 206 STORE_ATTR_SLOT
            new(0x05), // 207 STORE_ATTR_WITH_HINT
            new(0x00), // 208 STORE_SUBSCR_DICT
            new(0x00), // 209 STORE_SUBSCR_LIST_INT
            new(0x00), // 210 TO_BOOL_ALWAYS_TRUE
            new(0x00), // 211 TO_BOOL_BOOL
            new(0x00), // 212 TO_BOOL_INT
            new(0x00), // 213 TO_BOOL_LIST
            new(0x00), // 214 TO_BOOL_NONE
            new(0x00), // 215 TO_BOOL_STR
            new(0x01), // 216 UNPACK_SEQUENCE_LIST
            new(0x01), // 217 UNPACK_SEQUENCE_TUPLE
            new(0x01), // 218 UNPACK_SEQUENCE_TWO_TUPLE
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
            new(0x00, 3, 2), // 233 MIN_INSTRUMENTED_OPCODE
            new(0x00, 2, 0), // 234 INSTRUMENTED_POP_ITER
            new(0x00, 3, 1), // 235 INSTRUMENTED_END_SEND
            new(0x09, 2, 3), // 236 INSTRUMENTED_FOR_ITER
            new(0x00, 0, 0), // 237 INSTRUMENTED_INSTRUCTION
            new(0x01, 0, 0), // 238 INSTRUMENTED_JUMP_FORWARD
            new(0x00, 0, 0), // 239 INSTRUMENTED_NOT_TAKEN
            new(0x01, 1, 0), // 240 INSTRUMENTED_POP_JUMP_IF_TRUE
            new(0x01, 1, 0), // 241 INSTRUMENTED_POP_JUMP_IF_FALSE
            new(0x01, 1, 0), // 242 INSTRUMENTED_POP_JUMP_IF_NONE
            new(0x01, 1, 0), // 243 INSTRUMENTED_POP_JUMP_IF_NOT_NONE
            new(0x01, 0, 0), // 244 INSTRUMENTED_RESUME
            new(0x00, 1, 1), // 245 INSTRUMENTED_RETURN_VALUE
            new(0x01, 1, 1), // 246 INSTRUMENTED_YIELD_VALUE
            new(0x09, 2, 0), // 247 INSTRUMENTED_END_ASYNC_FOR
            new(0x05, new StackCount(3), new StackCount(1, ArgumentRule.LowBit)), // 248 INSTRUMENTED_LOAD_SUPER_ATTR
            new(0x01, new StackCount(2, ArgumentRule.Whole), new StackCount(1)), // 249 INSTRUMENTED_CALL
            new(0x01, new StackCount(3, ArgumentRule.Whole), new StackCount(1)), // 250 INSTRUMENTED_CALL_KW
            new(0x00, 4, 1), // 251 INSTRUMENTED_CALL_FUNCTION_EX
            new(0x01, 0, 0), // 252 INSTRUMENTED_JUMP_BACKWARD
            new(0x00, 0, 0), // 253 INSTRUMENTED_LINE
            new(0x01, 0, 0), // 254 ENTER_EXECUTOR
            new(0x01, 0, 0), // 255 TRACE_RECORD
            new(0x00, 0, 0), // 256 ANNOTATIONS_PLACEHOLDER
            new(0x09, 0, 0), // 257 JUMP
            new(0x09, 1, 1), // 258 JUMP_IF_FALSE
            new(0x09, 1, 1), // 259 JUMP_IF_TRUE
            new(0x09, 0, 0), // 260 JUMP_NO_INTERRUPT
            new(0x21, 0, 1), // 261 LOAD_CLOSURE
            new(0x00, 0, 0), // 262 POP_BLOCK
            new(0x41, 0, 2), // 263 SETUP_CLEANUP
            new(0x41, 0, 1), // 264 SETUP_FINALLY
            new(0x41, 0, 1), // 265 SETUP_WITH
            new(0x21, 1, 0), // 266 STORE_FAST_MAYBE_NULL
        ];

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
            if (code is not sys_.code)
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
