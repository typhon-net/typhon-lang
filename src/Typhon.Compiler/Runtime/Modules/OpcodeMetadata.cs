// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    // The ordering and family membership follow CPython 3.15.2's generated
    // Lib/_opcode_metadata.py. See CPython-LICENSE.txt.
    internal static class _opcode_metadata
    {
        public const long HAVE_ARGUMENT = 41;
        public const long MIN_INSTRUMENTED_OPCODE = 233;

        public static readonly frozendict<string, long> opmap = BuildOpcodeMap(
        [
            0, 17, 128, 253, 254, 255, 1, 2, 4, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26,
            27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
            42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56,
            57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71,
            72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86,
            87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101,
            102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116,
            117, 118, 119, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 243, 244,
            245, 246, 247, 248, 249, 250, 251, 252, 256, 257, 258, 259, 260, 261, 262,
            263, 264, 265, 266,
        ]);

        public static readonly frozendict<string, long> _specialized_opmap = BuildOpcodeMap(
        [
            129, 130, 131, 132, 3, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142,
            143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157,
            158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172,
            173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187,
            188, 189, 190, 191, 192, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202,
            203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217,
            218,
        ]);

        public static readonly frozendict<string, PyTuple<string>> _specializations = BuildSpecializations(
        [
            (128, [200, 201]), // RESUME
            (37, [210, 211, 212, 213, 214, 215]), // TO_BOOL
            (42, [134, 130, 143, 133, 129, 142, 131, 137, 138, 140, 139, 141, 135, 136, 3, 132]), // BINARY_OP
            (36, [208, 209]), // STORE_SUBSCR
            (105, [203, 204, 202]), // SEND
            (118, [218, 217, 216]), // UNPACK_SEQUENCE
            (109, [205, 206, 207]), // STORE_ATTR
            (91, [197, 196]), // LOAD_GLOBAL
            (95, [198, 199]), // LOAD_SUPER_ATTR
            (79, [186, 190, 195, 194, 183, 184, 193, 185, 189, 188, 187, 192, 191]), // LOAD_ATTR
            (54, [169, 170, 171]), // COMPARE_OP
            (55, [173, 172]), // CONTAINS_OP
            (74, [182, 181]), // JUMP_BACKWARD
            (70, [179, 180]), // GET_ITER
            (68, [175, 177, 176, 174, 178]), // FOR_ITER
            (50, [145, 164, 168, 166, 167, 147, 150, 148, 149, 157, 153, 158, 162, 160, 161, 159, 144, 165, 146, 163]), // CALL
            (53, [154, 156, 155]), // CALL_KW
            (4, [152, 151]), // CALL_FUNCTION_EX
        ]);

        static frozendict<string, long> BuildOpcodeMap(int[] codes) => new(
            codes.Select(code => KeyValuePair.Create(_opcode.InstructionName(code), (long)code)), StringComparer.Ordinal);

        static frozendict<string, PyTuple<string>> BuildSpecializations((int Opcode, int[] Members)[] families) => new(
            families.Select(family => KeyValuePair.Create(_opcode.InstructionName(family.Opcode),
                new PyTuple<string>(family.Members.Select(_opcode.InstructionName)))),
            StringComparer.Ordinal);
    }
}
