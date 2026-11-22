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
        public RuntimeCode(string source, string filename, string mode, long flags) : base("<module>", filename, 1, flags)
        {
            Source = source;
            Mode = mode;
        }
    }

    internal static partial class Builtins
    {
        public static RuntimeCode compile(object? source, object? filename = null, object? mode = null, long flags = 0, bool dont_inherit = false, long optimize = -1, object? module = null)
        {
            if (module != null && module is not string)
                throw new InvalidCastException($"compile() argument 'module' must be str or None, not {PyTypeName(module.GetType())}");
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
            if (flags < 0 || (flags & ~0x1fe4210L) != 0)
                throw new ArgumentException("compile() flags are unsupported");
            if (optimize is < -1 or > 2) throw new ArgumentException("compile(): invalid optimize value");
            var validate = AppContext.GetData("Typhon.CheckRuntimeSyntax") as
                Func<string, string, string, long, (string? Error, int Line, int Column, int Kind, long Flags)>
                ?? throw new NotSupportedException("compile() requires the Typhon runtime compiler host");
            var result = validate(text, path, kind, flags);
            if (result.Error is string message)
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                string? line = result.Line > 0 && result.Line <= lines.Length ? lines[result.Line - 1] : null;
                var details = (path, (long)result.Line, (long)result.Column, line);
                throw result.Kind switch
                {
                    1 => new _IncompleteInputError(message, details),
                    2 => new IndentationError(message, details),
                    _ => new SyntaxError(message, details),
                };
            }
            return new RuntimeCode(text, path, kind, result.Flags);
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
                Func<string, string, string, long, IDictionary, IDictionary, bool, (object? Value, string? Error)>
                ?? throw new NotSupportedException("exec() and eval() require the Typhon runtime compiler host");
            var result = run(code.Source, code.co_filename, code.Mode, code.co_flags, globalNames, localNames, PyProfiling.Active);
            return result.Error != null ? throw new SyntaxError(result.Error) : result.Value;
        }

        static readonly object MissingImportAttribute = new object();

        public static dynamic? __runtime_relative_import(IDictionary globals, string module, long level, string member)
        {
            if (globals["__package__"] is not string package || package.Length == 0)
                throw new ImportError("attempted relative import with no known parent package");
            string absolute = SourceImport.ResolveRelative(module, package, checked((int)level));
            object imported = SourceImport.ImportModule(absolute);
            object? value = getattr(imported, member, MissingImportAttribute);
            if (!ReferenceEquals(value, MissingImportAttribute)) return value;
            if (hasattr(imported, "__path__"))
            {
                string child = absolute + "." + member;
                try { return SourceImport.ImportModule(child); }
                catch (ModuleNotFoundError error) when (Equals(error.name, child)) { }
            }
            throw new ImportError($"cannot import name '{member}' from '{absolute}'", absolute);
        }

        public static void __display_runtime_result(object? value)
        {
            if (value != null) sys_.displayhook(value);
        }
    }
}
