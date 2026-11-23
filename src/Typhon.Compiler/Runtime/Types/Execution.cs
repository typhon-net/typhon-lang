// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Runtime.InteropServices;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Stdlib;

    internal sealed class ExecutionScope
    {
        public IDictionary Globals { get; }
        public IDictionary Locals { get; }
        public object? BuiltinNamespace => Globals.Contains("__builtins__")
            ? Globals["__builtins__"] : PyModule.Of(typeof(builtins), "builtins").__dict__;
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
                    throw new NameError($"name '{name}' is not defined", name: name);
                }
                set => _names[name] = value;
            }
        }
    }

    internal sealed class RuntimeCode : types.CodeType
    {
        public string Source { get; }
        public string Mode { get; }
        public RuntimeCode(string source, string filename, string mode, long flags) : base("<module>", filename, 1, flags)
        {
            Source = source;
            Mode = mode;
        }
    }

    internal sealed class RuntimeFunctionCode(string name, string filename, long line, string source, bool bigInts,
        long flags, (long Positional, long PositionalOnly, long KeywordOnly) arity, string[] variables)
        : types.CodeType(name, filename, line, flags, arity: arity, variables: new PyTuple<string>(variables))
    {
        internal PyFunction Bind(IDictionary globals)
        {
            var bind = AppContext.GetData("Typhon.BindRuntimeFunction") as
                Func<string, string, IDictionary, bool, bool, (object? Function, string? Error)>
                ?? throw new NotSupportedException("types.FunctionType() requires the Typhon runtime compiler host");
            var result = bind(source, co_filename, globals, bigInts, PyProfiling.Active);
            if (result.Error != null) throw new SyntaxError(result.Error);
            return (PyFunction)result.Function!;
        }
    }

    internal static partial class Builtins
    {
        public static dynamic compile(object? source, object? filename, object? mode, System.Numerics.BigInteger flags,
            [Optional, DefaultParameterValue(false)] bool dont_inherit,
            [Optional, DefaultParameterValue(-1L)] object? optimize,
            [PyKeywordOnly, Optional, DefaultParameterValue(null)] object? module,
            [PyKeywordOnly, Optional, DefaultParameterValue(-1L)] object? _feature_version) =>
            CompileSource(source, filename, mode, checked((long)flags), dont_inherit,
                CompileInteger(optimize), module, CompileInteger(_feature_version));

        public static dynamic compile(object? source,
            [Optional, DefaultParameterValue(null)] object? filename,
            [Optional, DefaultParameterValue(null)] object? mode,
            [Optional, DefaultParameterValue(0L)] long flags,
            [Optional, DefaultParameterValue(false)] bool dont_inherit,
            [Optional, DefaultParameterValue(-1L)] object? optimize,
            [PyKeywordOnly, Optional, DefaultParameterValue(null)] object? module,
            [PyKeywordOnly, Optional, DefaultParameterValue(-1L)] object? _feature_version) =>
            CompileSource(source, filename, mode, flags, dont_inherit, CompileInteger(optimize), module, CompileInteger(_feature_version));

        static long CompileInteger(object? value)
        {
            object index = __index_value(value);
            return index is System.Numerics.BigInteger wide ? checked((long)wide) : (long)index;
        }

        static dynamic CompileSource(object? source, object? filename, object? mode, long flags, bool dont_inherit,
            long optimize, object? module, long _feature_version)
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
            long validFlags = 0x1fe4210L | ast.PyCF_ONLY_AST;
            if ((flags & ast.PyCF_ONLY_AST) != 0) validFlags |= ast.PyCF_TYPE_COMMENTS | ast.PyCF_OPTIMIZED_AST;
            if (flags < 0 || (flags & ~validFlags) != 0)
                throw new ArgumentException("compile() flags are unsupported");
            if (optimize is < -1 or > 2) throw new ArgumentException("compile(): invalid optimize value");
            if (_feature_version < int.MinValue || _feature_version > int.MaxValue)
                throw new OverflowException("Python int too large to convert to C int");
            if ((flags & ast.PyCF_ONLY_AST) != 0) return ast.CompileTree(text, path, kind, flags, (int)_feature_version, (int)optimize);
            var validate = AppContext.GetData("Typhon.CheckRuntimeSyntax") as
                Func<string, string, string, long, (string? Error, int Line, int Column, int Kind, long Flags)>
                ?? throw new NotSupportedException("compile() requires the Typhon runtime compiler host");
            var result = validate(text, path, kind, flags);
            if (result.Error is string message)
                throw RuntimeSyntaxError(text, path, message, result.Line, result.Column, result.Kind);
            return new RuntimeCode(text, path, kind, result.Flags);
        }

        internal static SyntaxError RuntimeSyntaxError(string source, string path, string message, int line, int column, int kind)
        {
            var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string? text = line > 0 && line <= lines.Length ? lines[line - 1] : null;
            var details = (path, (long)line, (long)column, text);
            return kind switch
            {
                1 => new _IncompleteInputError(message, details),
                2 => new IndentationError(message, details),
                _ => new SyntaxError(message, details),
            };
        }

        public static void exec(object? source, object? globals = null, object? locals = null, [PyKeywordOnly] object? closure = null)
        {
            if (source is types.CodeType code)
            {
                int count = code.co_freevars.Count;
                if (count == 0 && closure != null)
                    throw new InvalidCastException("cannot use a closure with this code object");
                if (count != 0)
                {
                    if (closure is not System.Runtime.CompilerServices.ITuple cells || cells.Length != count ||
                        Enumerable.Range(0, count).Any(index => cells[index] is not types.CellType))
                        throw new InvalidCastException($"code object requires a closure of exactly length {count}");
                    throw new NotSupportedException("the Typhon runtime cannot execute code objects with free variables");
                }
            }
            else if (closure != null)
                throw new InvalidCastException("closure can only be used when source is a code object");
            ExecuteCode(source, globals, locals, "exec");
        }

        public static dynamic? eval(object? source, object? globals = null, object? locals = null) =>
            ExecuteCode(source is string text ? text.Trim(' ', '\t') : source, globals, locals, "eval");

        static object? ExecuteCode(object? source, object? globals, object? locals, string defaultMode)
        {
            var code = source as RuntimeCode ?? (RuntimeCode)compile(source, "<string>", defaultMode);
            var globalNames = globals == null ? new OrderedDictionary<string, object?>() :
                globals as IDictionary ?? throw new InvalidCastException("globals must be a dictionary");
            var localNames = locals == null ? globalNames :
                locals as IDictionary ?? throw new InvalidCastException("locals must be a mapping");
            var run = AppContext.GetData("Typhon.RunRuntimeCode") as
                Func<string, string, string, long, IDictionary, IDictionary, bool, bool, (object? Value, string? Error)>
                ?? throw new NotSupportedException("exec() and eval() require the Typhon runtime compiler host");
            var result = run(code.Source, code.co_filename, code.Mode, code.co_flags, globalNames, localNames, PyProfiling.Active, PyTracing.Active || PyMonitoring.Active);
            return result.Error != null ? throw new SyntaxError(result.Error) : result.Value;
        }

        static readonly object MissingImportAttribute = new object();

        public static dynamic? __runtime_source_import(IDictionary globals, string module, long level, string? member, bool bindRoot)
        {
            string absolute = module;
            if (level != 0)
            {
                if (globals["__package__"] is not string package || package.Length == 0)
                    throw new ImportError("attempted relative import with no known parent package");
                absolute = SourceImport.ResolveRelative(module, package, checked((int)level));
            }
            object imported = SourceImport.ImportModule(absolute);
            if (member == null)
            {
                int dot = absolute.IndexOf('.');
                return bindRoot && dot >= 0 ? SourceImport.ImportModule(absolute[..dot]) : imported;
            }
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
            if (value != null) __invoke((object?)sys_.displayhook, [value]);
        }
    }
}
