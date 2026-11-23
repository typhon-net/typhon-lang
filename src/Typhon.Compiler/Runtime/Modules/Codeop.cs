// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// The completeness checks follow CPython Lib/codeop.py.
// See CPython-LICENSE.txt for the upstream license.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Linq;
    using System.Numerics;
    using Typhon.Runtime;

    internal static class codeop
    {
        public const long PyCF_DONT_IMPLY_DEDENT = 0x200;
        public const long PyCF_ONLY_AST = ast.PyCF_ONLY_AST;
        public const long PyCF_ALLOW_INCOMPLETE_INPUT = 0x4000;
        const long IncompleteFlags = PyCF_DONT_IMPLY_DEDENT | PyCF_ALLOW_INCOMPLETE_INPUT;
        static readonly long FutureFlags = __future__.all_feature_names
            .Select(name => ((__future__._Feature)typeof(__future__).GetField(name)!.GetValue(null)!).compiler_flag)
            .Aggregate(0L, (flags, value) => flags | value);

        public static dynamic? compile_command(string source, string filename = "<input>", string symbol = "single", long flags = 0) =>
            MaybeCompile((text, incomplete) => (object?)Builtins.compile(text, filename, symbol,
                incomplete ? flags | IncompleteFlags : flags), source, symbol);

        public static dynamic? compile_command(string source, string filename, string symbol, BigInteger flags) =>
            compile_command(source, filename, symbol, checked((long)flags));

        static object? MaybeCompile(Func<string, bool, object?> compile, string source, string symbol)
        {
            if (symbol != "eval" && source.Split('\n').All(line => line.TrimStart() is var trimmed &&
                (trimmed.Length == 0 || trimmed.StartsWith('#'))))
                source = "pass";

            var warningScope = new warnings.catch_warnings();
            warningScope.__enter__();
            try
            {
                warnings.simplefilter("ignore", (typeof(SyntaxWarning), typeof(DeprecationWarning)));
                try { _ = compile(source, true); }
                catch (SyntaxError)
                {
                    try
                    {
                        _ = compile(source + "\n", true);
                        return null;
                    }
                    catch (_IncompleteInputError) { return null; }
                    catch (SyntaxError) { }
                }
            }
            finally { warningScope.__exit__(null, null, null); }

            return compile(source, false);
        }

        internal class Compile
        {
            public long flags = IncompleteFlags;

            public dynamic __call__(string source, string filename, string symbol, long flags = 0, bool incomplete_input = true)
            {
                long active = flags | this.flags;
                if (!incomplete_input) active &= ~IncompleteFlags;
                object result = (object)Builtins.compile(source, filename, symbol, active, dont_inherit: true);
                if (result is RuntimeCode code) this.flags |= code.co_flags & FutureFlags;
                return result;
            }

            public dynamic __call__(string source, string filename, string symbol, BigInteger flags, bool incomplete_input = true) =>
                __call__(source, filename, symbol, checked((long)flags), incomplete_input);
        }

        internal class CommandCompiler
        {
            public Compile compiler = new();

            public dynamic? __call__(string source, string filename = "<input>", string symbol = "single") =>
                MaybeCompile((text, incomplete) => (object?)compiler.__call__(text, filename, symbol, incomplete_input: incomplete),
                    source, symbol);
        }
    }
}
