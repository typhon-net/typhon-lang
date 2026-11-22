// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using Typhon.Stdlib;

    internal sealed class ExecutionScope
    {
        public IDictionary Globals { get; }
        public IDictionary Locals { get; }
        public Bindings Global { get; }
        public Bindings Local { get; }

        public ExecutionScope(IDictionary globals, IDictionary locals)
        {
            Globals = globals;
            Locals = locals;
            Global = new Bindings(globals, globals);
            Local = new Bindings(locals, globals);
        }

        public void Export(string name, object? value) => Locals[name] = value;

        internal sealed class Bindings
        {
            readonly IDictionary _names, _globals;
            public Bindings(IDictionary names, IDictionary globals) { _names = names; _globals = globals; }

            public dynamic? this[string name]
            {
                get
                {
                    if (_names.Contains(name)) return _names[name];
                    if (!ReferenceEquals(_names, _globals) && _globals.Contains(name)) return _globals[name];
                    throw new NameError($"name '{name}' is not defined");
                }
                set => _names[name] = value;
            }
        }
    }

    internal sealed class RuntimeCode : sys_.code
    {
        public string Source { get; }
        public string Mode { get; }
        public RuntimeCode(string source, string filename, string mode) : base("<module>", filename, 1)
        {
            Source = source;
            Mode = mode;
        }
    }

    internal static partial class Builtins
    {
        public static RuntimeCode compile(object? source, object? filename = null, object? mode = null, long flags = 0, bool dont_inherit = false, long optimize = -1)
        {
            string text = source switch
            {
                string s => s,
                PyBytes b => importlib.util.decode_source(b),
                _ => throw new InvalidCastException("compile() arg 1 must be a string or bytes"),
            };
            string path = filename == null ? "<string>" : os_.__path(filename);
            string kind = mode as string ?? "exec";
            if (kind is not ("exec" or "eval" or "single"))
                throw new ArgumentException("compile() mode must be 'exec', 'eval' or 'single'");
            if (flags < 0 || (flags & ~0x1fe0010L) != 0)
                throw new ArgumentException("compile() flags are unsupported");
            if (optimize is < -1 or > 2) throw new ArgumentException("compile(): invalid optimize value");
            var validate = AppContext.GetData("Typhon.CheckRuntimeSyntax") as Func<string, string, string, string?>
                ?? throw new NotSupportedException("compile() requires the Typhon runtime compiler host");
            if (validate(text, path, kind) is string message) throw new SyntaxError(message);
            return new RuntimeCode(text, path, kind);
        }

        public static void exec(object? source, object? globals = null, object? locals = null) =>
            ExecuteCode(source, globals, locals, "exec");

        public static dynamic? eval(object? source, object? globals = null, object? locals = null) =>
            ExecuteCode(source is string text ? text.Trim(' ', '\t') : source, globals, locals, "eval");

        static object? ExecuteCode(object? source, object? globals, object? locals, string defaultMode)
        {
            var code = source as RuntimeCode ?? compile(source, "<string>", defaultMode);
            var globalNames = globals == null ? new OrderedDictionary<string, object?>() :
                globals as IDictionary ?? throw new InvalidCastException("globals must be a dictionary");
            var localNames = locals == null ? globalNames :
                locals as IDictionary ?? throw new InvalidCastException("locals must be a mapping");
            var run = AppContext.GetData("Typhon.RunRuntimeCode") as
                Func<string, string, string, IDictionary, IDictionary, (object? Value, string? Error)>
                ?? throw new NotSupportedException("exec() and eval() require the Typhon runtime compiler host");
            var result = run(code.Source, code.co_filename, code.Mode, globalNames, localNames);
            return result.Error != null ? throw new SyntaxError(result.Error) : result.Value;
        }

        public static void __display_runtime_result(object? value)
        {
            if (value != null) sys_.displayhook(value);
        }
    }
}
