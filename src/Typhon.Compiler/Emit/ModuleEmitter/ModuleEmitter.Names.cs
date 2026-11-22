// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    // Names: Python names to C# names and types (renames, shadowing, the runtime's helpers), and resolving type names.
    internal sealed partial class ModuleEmitter
    {
        static readonly HashSet<string> CSharpKeywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
            "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
            "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };

        internal static string Esc(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

        static readonly Dictionary<string, string> ExceptionMap = new Dictionary<string, string>
        {
            ["BaseException"] = "global::System.Exception",
            ["ValueError"] = "global::System.ArgumentException",
            ["TypeError"] = "global::System.InvalidCastException",
            ["KeyError"] = "global::System.Collections.Generic.KeyNotFoundException",
            ["IndexError"] = "global::System.ArgumentOutOfRangeException",
            ["ZeroDivisionError"] = "global::System.DivideByZeroException",
            ["NotImplementedError"] = "global::System.NotImplementedException",
            ["RuntimeError"] = "global::System.InvalidOperationException",
            ["AttributeError"] = "global::System.MissingMemberException",
            ["FileNotFoundError"] = "global::System.IO.FileNotFoundException",
            ["OSError"] = "global::System.IO.IOException",
            ["IOError"] = "global::System.IO.IOException",
            ["OverflowError"] = "global::System.OverflowException",
            ["ArithmeticError"] = "global::System.ArithmeticException",
            ["TimeoutError"] = "global::System.TimeoutException",
            ["CancelledError"] = "global::System.OperationCanceledException",
            ["PermissionError"] = "global::System.UnauthorizedAccessException",
            ["StopIteration"] = "global::Typhon.Runtime.StopIteration",
            ["GeneratorExit"] = "global::Typhon.Runtime.GeneratorExit",
            ["SystemExit"] = "global::Typhon.Runtime.SystemExit",
            ["KeyboardInterrupt"] = "global::Typhon.Runtime.KeyboardInterrupt",
            ["AssertionError"] = "global::Typhon.Runtime.AssertionError",
            // the other builtin exceptions (Runtime/Types/Exceptions.cs)
            ["Exception"] = "global::System.Exception",
            ["EOFError"] = "global::System.IO.EndOfStreamException",
            ["MemoryError"] = "global::System.OutOfMemoryException",
            ["EnvironmentError"] = "global::System.IO.IOException",
            ["LookupError"] = "global::Typhon.Runtime.LookupError",
            ["ImportError"] = "global::Typhon.Runtime.ImportError",
            ["ModuleNotFoundError"] = "global::Typhon.Runtime.ModuleNotFoundError",
            ["ImportCycleError"] = "global::Typhon.Runtime.ImportCycleError",
            ["SyntaxError"] = "global::Typhon.Runtime.SyntaxError",
            ["_IncompleteInputError"] = "global::Typhon.Runtime._IncompleteInputError",
            ["IndentationError"] = "global::Typhon.Runtime.IndentationError",
            ["TabError"] = "global::Typhon.Runtime.TabError",
            ["NameError"] = "global::Typhon.Runtime.NameError",
            ["UnboundLocalError"] = "global::Typhon.Runtime.UnboundLocalError",
            ["BufferError"] = "global::Typhon.Runtime.BufferError",
            ["ReferenceError"] = "global::Typhon.Runtime.ReferenceError",
            ["SystemError"] = "global::Typhon.Runtime.SystemError",
            ["StopAsyncIteration"] = "global::Typhon.Runtime.StopAsyncIteration",
            ["RecursionError"] = "global::Typhon.Runtime.RecursionError",
            ["PythonFinalizationError"] = "global::Typhon.Runtime.PythonFinalizationError",
            ["FloatingPointError"] = "global::Typhon.Runtime.FloatingPointError",
            ["UnicodeError"] = "global::Typhon.Runtime.UnicodeError",
            ["UnicodeDecodeError"] = "global::Typhon.Runtime.UnicodeDecodeError",
            ["UnicodeEncodeError"] = "global::Typhon.Runtime.UnicodeEncodeError",
            ["UnicodeTranslateError"] = "global::Typhon.Runtime.UnicodeTranslateError",
            ["BaseExceptionGroup"] = "global::Typhon.Runtime.BaseExceptionGroup",
            ["ExceptionGroup"] = "global::Typhon.Runtime.ExceptionGroup",
            ["BlockingIOError"] = "global::Typhon.Runtime.BlockingIOError",
            ["ChildProcessError"] = "global::Typhon.Runtime.ChildProcessError",
            ["ConnectionError"] = "global::Typhon.Runtime.ConnectionError",
            ["BrokenPipeError"] = "global::Typhon.Runtime.BrokenPipeError",
            ["ConnectionAbortedError"] = "global::Typhon.Runtime.ConnectionAbortedError",
            ["ConnectionRefusedError"] = "global::Typhon.Runtime.ConnectionRefusedError",
            ["ConnectionResetError"] = "global::Typhon.Runtime.ConnectionResetError",
            ["FileExistsError"] = "global::Typhon.Runtime.FileExistsError",
            ["InterruptedError"] = "global::Typhon.Runtime.InterruptedError",
            ["IsADirectoryError"] = "global::Typhon.Runtime.IsADirectoryError",
            ["NotADirectoryError"] = "global::Typhon.Runtime.NotADirectoryError",
            ["ProcessLookupError"] = "global::Typhon.Runtime.ProcessLookupError",
            ["Warning"] = "global::Typhon.Runtime.Warning",
            ["UserWarning"] = "global::Typhon.Runtime.UserWarning",
            ["DeprecationWarning"] = "global::Typhon.Runtime.DeprecationWarning",
            ["PendingDeprecationWarning"] = "global::Typhon.Runtime.PendingDeprecationWarning",
            ["SyntaxWarning"] = "global::Typhon.Runtime.SyntaxWarning",
            ["RuntimeWarning"] = "global::Typhon.Runtime.RuntimeWarning",
            ["FutureWarning"] = "global::Typhon.Runtime.FutureWarning",
            ["ImportWarning"] = "global::Typhon.Runtime.ImportWarning",
            ["UnicodeWarning"] = "global::Typhon.Runtime.UnicodeWarning",
            ["BytesWarning"] = "global::Typhon.Runtime.BytesWarning",
            ["ResourceWarning"] = "global::Typhon.Runtime.ResourceWarning",
            ["EncodingWarning"] = "global::Typhon.Runtime.EncodingWarning",
        };
        static readonly Dictionary<string, string> ExceptionMetaMap = ExceptionMap.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("global::", ""));

        /// <summary>When catching, some Python exceptions correspond to several unrelated .NET exceptions.</summary>
        static readonly Dictionary<string, string[]> CatchMap = new Dictionary<string, string[]>
        {
            // ArgumentOutOfRangeException derives from ArgumentException but is Python's IndexError
            ["ValueError"] = new[] { "(global::System.ArgumentException and not global::System.ArgumentOutOfRangeException)", "global::System.FormatException", "global::Typhon.Stdlib.io.UnsupportedOperation" },
            ["IndexError"] = new[] { "global::System.ArgumentOutOfRangeException", "global::System.IndexOutOfRangeException" },
            ["TypeError"] = new[] { "global::System.InvalidCastException", "global::Microsoft.CSharp.RuntimeBinder.RuntimeBinderException" },
            ["ArithmeticError"] = new[] { "global::System.ArithmeticException" },
            ["LookupError"] = new[] { "global::System.Collections.Generic.KeyNotFoundException", "global::System.ArgumentOutOfRangeException", "global::System.IndexOutOfRangeException" },
        };

        /// <summary>The .NET types an `except X` clause should catch.</summary>
        List<string> CatchTypes(Expr type, Node at)
        {
            var result = new List<string>();
            foreach (var t in type is TupleExpr tup ? tup.Elts : new List<Expr> { type })
            {
                if (t is NameExpr n && !IsShadowed(n.Id) && CatchMap.TryGetValue(n.Id, out var many)) result.AddRange(many);
                else result.Add(MapType(t, at));
            }
            return result;
        }

        bool IsLocal(string name)
        {
            foreach (var set in _lambdaParams) if (set.ContainsKey(name)) return true;
            if (_renames.ContainsKey(name)) return true;
            for (var s = _scope; s != null; s = s.Parent)
            {
                if (s.Params.Contains(name)) return true;
                if (s.Locals != null && (s.Locals.AllLocals.Contains(name) || s.Locals.LocalFunctions.Contains(name))) return true;
            }
            return false;
        }

        bool HasSelfInScope()
        {
            for (var s = _scope; s != null; s = s.Parent) if (s.HasSelf) return true;
            return false;
        }

        bool IsClassArgument(string name)
        {
            for (var s = _scope; s != null; s = s.Parent)
            {
                if (s.ClassArgument == name) return true;
                if (s.Params.Contains(name) || s.Locals?.AllLocals.Contains(name) == true) return false;
            }
            return false;
        }

        /// <summary>A module-level or imported name that is NOT a type (function / variable).</summary>
        bool IsValueName(string name)
        {
            if (_m.Functions.Contains(name) || _m.Globals.Contains(name)) return true;
            if (_importedFromModules.TryGetValue(name, out var imp)) return !imp.Key.Classes.ContainsKey(imp.Value);
            foreach (var w in _wildcardModules) if (w.Functions.Contains(name) || w.Globals.Contains(name)) return true;
            return false;
        }

        bool IsShadowed(string name) => IsLocal(name) || IsValueName(name) || _m.RuntimeBindings.Contains(name) ||
            _m.Classes.ContainsKey(name) || _importedFromModules.ContainsKey(name);

        /// <summary>A Typhon runtime helper (mod, pow, slice, str, ...): qualified when a Python name in scope hides it (a parameter called 'mod').</summary>
        string Rt(string helper) => IsShadowed(helper) || _renames.ContainsKey(helper) || BuiltinClashes(helper) ? "global::Typhon.Runtime.Builtins." + helper : helper;

        /// <summary>
        /// Names imported from Typhon standard modules (from math import sqrt / *): the module's C# type. A call to such a name
        /// is qualified with it, and Python's builtin of the same name (pow) with Builtins, since 'using static' of the module
        /// brings all of its members into scope, not only the imported ones.
        /// </summary>
        readonly Dictionary<string, string> _stdlibImported = new Dictionary<string, string>();
        readonly List<string> _stdlibStar = new List<string>();
        readonly Dictionary<string, bool> _clashes = new Dictionary<string, bool>();

        static string CsTypeName(string meta) => "global::" + string.Join(".", meta.Replace('+', '.').Split('.').Select(Esc));

        string StdlibOwner(string name)
        {
            if (_stdlibImported.TryGetValue(name, out var meta)) return meta;
            foreach (var star in _stdlibStar) if (_r.GetMembers(star, name).Count > 0) return star;
            return null;
        }

        bool BuiltinClashes(string name)
        {
            if (_staticTypes.Count == 0 || StdlibOwner(name) != null) return false;
            if (!_clashes.TryGetValue(name, out bool clash))
                _clashes[name] = clash = _staticTypes.Any(st => st.StartsWith("Typhon.Stdlib.") && _r.GetMembers(st, name).Count > 0) &&
                                         _r.GetMembers("Typhon.Runtime.Builtins", name).Count > 0;
            return clash;
        }

        string EmitName(NameExpr n)
        {
            string id = n.Id;
            foreach (var scope in _lambdaParams)
                if (scope.TryGetValue(id, out var lambdaParameter)) return lambdaParameter;
            if (_renames.TryGetValue(id, out var renamed)) return Esc(renamed);
            if (IsLocal(id)) return Esc(id);
            if (_m.RuntimeCodeMode != null && _m.RuntimeBindings.Contains(id))
                return $"__typhon_execution.{(_scopeKey == RuntimeCodeScopeKey ? "Local" : "Global")}[{CsString(id)}]";
            if (id == "self" && HasSelfInScope()) return "this";
            if (id == "__name__") return CsString(_m.Name);
            if (id == "__package__" && !IsShadowed(id)) return _m.PackageName.Length == 0 ? "null" : CsString(_m.PackageName);
            if (id == "__file__" && !IsShadowed(id)) return CsString(System.IO.Path.GetFullPath(_path));
            if (id == "globals" && !IsShadowed(id))
            {
                string args = NewTmp("globalsArgs"), kw = NewTmp("globalsKw");
                return $"new global::Typhon.Runtime.PyClosure(\"globals\", ({args}, {kw}) => " +
                    $"({args}.Length == 0 && ({kw} == null || {kw}.Count == 0)) ? __typhon_globals() : " +
                    "throw new global::System.InvalidCastException(\"globals() takes no arguments\"))";
            }
            if (PackageImportedMember(id) is string packageMember) return packageMember;
            if (_qualifiedRuntimeModuleImports.Contains(id) && !IsShadowed(id) && _dotnetAliases.TryGetValue(id, out var runtimeModule))
                return CsTypeName(runtimeModule);
            if (ExceptionMap.TryGetValue(id, out var ex) && !IsShadowed(id)) return ex;
            if (!IsShadowed(id))
            {
                if (StdlibOwner(id) is string owner && _r.GetMembers(owner, id).Count > 0) return CsTypeName(owner) + "." + Esc(id);
                if (BuiltinClashes(id)) return "global::Typhon.Runtime.Builtins." + Esc(id);
                // An optional module that failed to import left its global name unbound.
                if (_m.Ast.FailedModuleImports.Contains(id) && !_moduleFieldNames.Contains(id) && !_moduleAliases.ContainsKey(id) &&
                    !_dotnetAliases.ContainsKey(id) && !_typingOnlyNames.Contains(id))
                    return $"{Rt("__unbound_import")}({CsString(id)})";
            }
            return Esc(id);
        }


        IEnumerable<string> NamespaceChain()
        {
            var ns = _m.Namespace;
            while (!string.IsNullOrEmpty(ns))
            {
                yield return ns;
                int dot = ns.LastIndexOf('.');
                ns = dot < 0 ? null : ns.Substring(0, dot);
            }
        }

        ModuleInfo ModuleRef(string dotted)
        {
            if (IsLocal(dotted.Split('.')[0])) return null;
            if (_moduleAliases.TryGetValue(dotted, out var m)) return m;
            int dot = dotted.LastIndexOf('.');
            if (dot > 0 && ModuleRef(dotted.Substring(0, dot)) is ModuleInfo parent && IsProjectPackageModule(parent))
            {
                var (owner, member) = ProjectExport(parent, dotted.Substring(dot + 1));
                return member == null ? owner : null;
            }
            return _p.FindModule(dotted);
        }

        /// <summary>Does the dotted name denote a type? (Python has no 'new', so calls need this to become constructions.)</summary>
        bool TryResolveType(string dotted, int arity, out string meta, out ClassDef pyClass)
        {
            meta = null; pyClass = null;
            var parts = dotted.Split('.');
            string first = parts[0];
            if (IsLocal(first) || first == "self") return false;
            string sfx = arity > 0 ? "`" + arity : "";

            if (parts.Length == 1)
            {
                if (_cls != null && _cls.Name == first) { pyClass = _cls.Def; return true; }
                if (_cls != null && _cls.TypeParams.Contains(first)) return false;
                if (IsValueName(first)) return false;
                if (_m.Classes.TryGetValue(first, out pyClass)) return true;
                if (_cls != null && _cls.Def.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == first) is ClassDef nested) { pyClass = nested; return true; }
                if (_importedFromModules.TryGetValue(first, out var imp)) return imp.Key.Classes.TryGetValue(imp.Value, out pyClass);
                foreach (var w in _wildcardModules) if (w.Classes.TryGetValue(first, out pyClass)) return true;
                if (_importedTypes.TryGetValue(first, out var full) && _r.TypeExists(full + sfx)) { meta = full + sfx; return true; }
                if (ExceptionMetaMap.TryGetValue(first, out var exMeta)) { meta = exMeta; return true; }
            }
            else
            {
                string prefix = string.Join(".", parts, 0, parts.Length - 1);
                var mod = ModuleRef(prefix);
                if (mod != null)
                {
                    var (owner, member) = ProjectExport(mod, parts[parts.Length - 1]);
                    return member != null && owner.Classes.TryGetValue(member, out pyClass);
                }
            }

            string expanded = dotted;
            if (_dotnetAliases.TryGetValue(first, out var aliasTarget)) expanded = aliasTarget + dotted.Substring(first.Length);
            foreach (var cand in Candidates(expanded))
            {
                if (_r.TypeExists(cand + sfx)) { meta = cand + sfx; return true; }
                // A module can contain several levels of nested types (unittest.case._SubTest).
                // Roslyn metadata separates enclosing type names with '+', not '.'.
                for (int dot = cand.LastIndexOf('.'); dot > 0; dot = cand.LastIndexOf('.', dot - 1))
                {
                    string nestedName = cand.Substring(0, dot) + "+" + cand.Substring(dot + 1).Replace('.', '+');
                    if (_r.TypeExists(nestedName + sfx)) { meta = nestedName + sfx; return true; }
                }
            }
            return false;
        }

        IEnumerable<string> Candidates(string dotted)
        {
            yield return dotted;
            foreach (var ns in _usingNamespaces) yield return ns + "." + dotted;
            foreach (var ns in NamespaceChain()) yield return ns + "." + dotted;
            foreach (var t in _staticTypes) yield return t + "+" + dotted;
        }

        static readonly HashSet<string> BuiltinConversionNames = new HashSet<string> { "int", "float", "complex", "str", "bool", "list", "dict", "set", "tuple", "bytes", "bytearray", "object", "frozenset" };

        const string PyBytesType = "global::Typhon.Runtime.PyBytes", PyByteArrayType = "global::Typhon.Runtime.PyByteArray";
        static bool IsBytesType(string t) => t == PyBytesType || t == PyByteArrayType;

        static readonly Dictionary<string, string> PrimitiveTypes = new Dictionary<string, string>
        {
            ["int"] = "long", ["float"] = "double", ["str"] = "string", ["bool"] = "bool", ["object"] = "object", ["type"] = "global::System.Type",
            ["complex"] = "global::System.Numerics.Complex",
            ["bytes"] = "global::Typhon.Runtime.PyBytes", ["bytearray"] = "global::Typhon.Runtime.PyByteArray",
            ["property"] = "global::Typhon.Stdlib.builtins.property", ["staticmethod"] = "global::Typhon.Stdlib.builtins.staticmethod",
            ["classmethod"] = "global::Typhon.Stdlib.builtins.classmethod",
            ["Any"] = "dynamic", ["typing.Any"] = "dynamic",
            ["float32"] = "float", ["float64"] = "double", ["int8"] = "sbyte", ["int16"] = "short", ["int32"] = "int", ["int64"] = "long",
            ["uint8"] = "byte", ["uint16"] = "ushort", ["uint32"] = "uint", ["uint64"] = "ulong",
            ["long"] = "long", ["double"] = "double", ["decimal"] = "decimal", ["char"] = "char", ["byte"] = "byte", ["sbyte"] = "sbyte",
            ["short"] = "short", ["ushort"] = "ushort", ["uint"] = "uint", ["ulong"] = "ulong", ["nint"] = "nint", ["nuint"] = "nuint",
            ["list"] = "List<dynamic>", ["dict"] = "OrderedDictionary<dynamic, dynamic>", ["set"] = "HashSet<dynamic>",
            ["tuple"] = "global::System.Runtime.CompilerServices.ITuple", ["frozenset"] = "IReadOnlySet<dynamic>",
            ["Callable"] = "Delegate", ["Iterator"] = "IEnumerable<dynamic>", ["Iterable"] = "IEnumerable<dynamic>",
            ["range"] = "global::Typhon.Runtime.PyRange",
            ["slice"] = "global::Typhon.Runtime.PySlice",
        };

        static readonly HashSet<string> GenericAliases = new HashSet<string>
        {
            "list", "List", "dict", "Dict", "set", "Set", "frozenset", "FrozenSet", "tuple", "Tuple", "Optional", "Union", "Callable",
            "Iterator", "Iterable", "Generator", "AsyncIterator", "AsyncIterable", "AsyncGenerator", "Awaitable", "Coroutine",
            "Sequence", "MutableSequence", "Mapping", "MutableMapping", "Type", "type", "ClassVar", "Final", "Annotated", "Array",
        };

        static string StripTypingPrefix(string dotted)
        {
            foreach (var p in new[] { "typing.", "collections.abc.", "t." })
                if (dotted != null && dotted.StartsWith(p)) return dotted.Substring(p.Length);
            return dotted;
        }

        /// <summary>The type a name stands for when it is a type alias of this module or imported from another Typhon module.</summary>
        Expr TypeAlias(string name)
        {
            if (IsLocal(name)) return null;
            if (_m.TypeAliases.TryGetValue(name, out var local)) return local;
            if (_importedFromModules.TryGetValue(name, out var imp) && imp.Key.TypeAliases.TryGetValue(imp.Value, out var imported)) return imported;
            return null;
        }

        /// <summary>Python classes of the standard library imported by name (Fraction, Path) → their Typhon runtime type.</summary>
        string StdlibClassType(string name)
        {
            if (IsShadowed(name) && !_importedFromModules.ContainsKey(name)) return null;
            if (name == "Fraction" && _staticTypes.Contains("Typhon.Stdlib.fractions")) return "global::Typhon.Runtime.Fraction";
            if (name == "Path" && _staticTypes.Contains("Typhon.Stdlib.pathlib")) return "global::Typhon.Runtime.Path";
            return StdlibNestedType(name);
        }

        /// <summary>
        /// A class nested in a Typhon standard module (date, datetime.date, re.Pattern): its full C# name, which the inference
        /// and member signatures use too (and which keeps a function taking one internal, like the class).
        /// </summary>
        string StdlibNestedType(string dotted) =>
            TryResolveType(dotted, 0, out var meta, out var pyClass) && pyClass == null && meta != null && meta.StartsWith("Typhon.Stdlib.") && meta.Contains('+')
                ? CsTypeName(meta) : null;

        bool IsTypeParamInScope(string name)
        {
            if (_cls != null && _cls.TypeParams.Contains(name)) return true;
            return _currentFuncTypeParams.Contains(name);
        }

        readonly HashSet<string> _currentFuncTypeParams = new HashSet<string>();

        /// <summary>
        /// A .NET type whose attributes are bound at run time (a System.Dynamic.DynamicObject such as argparse.Namespace): an
        /// annotation naming it is dynamic, so attribute access on the annotated value stays late-bound as in Python.
        /// </summary>
        bool IsLateBoundDotNetType(string dotted) =>
            TryResolveType(dotted, 0, out var meta, out var pyClass) && pyClass == null && meta != null && _r.HasMember(meta, "TryGetMember");

        /// <summary>
        /// A type of a Typhon standard module (argparse.ArgumentParser, logging.Logger, re.Pattern) as fully qualified C#, so
        /// that a function, field or property using it is internal like the runtime's own types (see SurfaceVisibility).
        /// </summary>
        static string StdlibTypeCode(string meta, ClassDef pyClass) =>
            pyClass == null && meta != null && meta.StartsWith("Typhon.Stdlib.") && !meta.Contains('`')
                ? "global::" + string.Join(".", meta.Replace('+', '.').Split('.').Select(Esc)) : null;

        /// <summary>Maps a Python type annotation to a C# type.</summary>
        string MapType(Expr e, Node at, bool isReturn = false)
        {
            switch (e)
            {
                case null: return "dynamic";
                case ConstExpr c when c.Kind == ConstKind.None: return isReturn ? "void" : "object";
                case ConstExpr c when c.Kind == ConstKind.Str:
                    try { return MapType(Parser.ParseExpression(c.Str, c.Line, c.Col), at, isReturn); }
                    catch (SyntaxError) { Error(at, $"invalid type annotation '{c.Str}'"); return "dynamic"; }
                case NameExpr n:
                {
                    if (n.Id == "None") return isReturn ? "void" : "object";
                    if (n.Id == "Self" && _cls != null) return _cls.CsSelfType;
                    if (IsTypeParamInScope(n.Id)) return Esc(n.Id);
                    if (TypeAlias(n.Id) is Expr aliased) return MapType(aliased, at, isReturn);
                    if (StdlibClassType(n.Id) is string stdlibType) return stdlibType;
                    if (n.Id == "NDArray" && !IsShadowed("NDArray") && _staticTypes.Contains("Typhon.Stdlib.numpy+typing")) return "global::Typhon.Stdlib.numpy.ndarray";
                    if (_big && n.Id == "int" && !IsShadowed("int")) return IntT;
                    if (!IsShadowed(n.Id) && PrimitiveTypes.TryGetValue(n.Id, out var prim)) return prim;
                    if (ExceptionMap.TryGetValue(n.Id, out var ex) && !IsShadowed(n.Id)) return ex;
                    if (IsLateBoundDotNetType(n.Id)) return "dynamic";
                    if (TryResolveType(n.Id, 0, out var stdMeta, out var stdPy) && StdlibTypeCode(stdMeta, stdPy) is string stdName) return stdName;
                    return PackageImportedMember(n.Id) ?? Esc(n.Id);
                }
                case AttributeExpr a:
                {
                    string dotted = DottedName(a);
                    if (dotted == null) { Error(at, "invalid type annotation"); return "dynamic"; }
                    string stripped = StripTypingPrefix(dotted);
                    if (stripped != dotted) return MapType(new NameExpr { Id = stripped, Line = a.Line, Col = a.Col }, at, isReturn);
                    if (IsLateBoundDotNetType(dotted)) return "dynamic";
                    if (TryResolveType(dotted, 0, out var stdMeta, out var stdPy) && StdlibTypeCode(stdMeta, stdPy) is string stdName) return stdName;
                    return (stdPy == null ? null : ProjectPackageMember(dotted)) ?? StdlibNestedType(dotted) ?? string.Join(".", dotted.Split('.').Select(Esc));
                }
                case SubscriptExpr s:
                {
                    var args = s.Index is TupleExpr t ? t.Elts : new List<Expr> { s.Index };
                    string baseName = StripTypingPrefix(DottedName(s.Target));
                    if (baseName == null) { Error(at, "invalid generic type annotation"); return "dynamic"; }
                    string A(int i) => MapType(args[i], at);
                    switch (baseName)
                    {
                        case "NDArray": case "npt.NDArray": case "numpy.typing.NDArray": return "global::Typhon.Stdlib.numpy.ndarray"; // numpy.typing
                        case "Counter": case "collections.Counter": return $"global::Typhon.Runtime.Counter<{A(0)}>";
                        case "deque": case "Deque": case "collections.deque": return $"global::Typhon.Runtime.deque<{A(0)}>";
                        case "defaultdict": case "DefaultDict": case "collections.defaultdict": return $"global::Typhon.Runtime.defaultdict<{A(0)}, {A(1)}>";
                        case "OrderedDict": case "collections.OrderedDict": return $"global::Typhon.Runtime.PyOrderedDict<{A(0)}, {A(1)}>";
                        case "list": case "List": return $"List<{A(0)}>";
                        case "dict": case "Dict": return $"OrderedDictionary<{A(0)}, {A(1)}>"; // insertion-ordered, like Python's dict
                        case "set": case "Set": return $"HashSet<{A(0)}>";
                        case "frozenset": case "FrozenSet": return $"IReadOnlySet<{A(0)}>";
                        case "Sequence": return $"IReadOnlyList<{A(0)}>";
                        case "MutableSequence": return $"IList<{A(0)}>";
                        case "Mapping": return $"IReadOnlyDictionary<{A(0)}, {A(1)}>";
                        case "MutableMapping": return $"IDictionary<{A(0)}, {A(1)}>";
                        case "Iterator": case "Iterable": case "Generator": return $"IEnumerable<{A(0)}>";
                        case "AsyncIterator": case "AsyncIterable": case "AsyncGenerator": return $"IAsyncEnumerable<{A(0)}>";
                        case "Awaitable": case "Coroutine": return $"Task<{A(args.Count - 1)}>";
                        case "Type": case "type": return "global::System.Type";
                        case "ClassVar": case "Final": case "Annotated": return A(0);
                        case "Array": return $"{A(0)}[]";
                        case "Optional": return Nullable(A(0));
                        case "Union": return MapUnion(args, at);
                        case "tuple": case "Tuple":
                            if (args.Count == 2 && args[1] is ConstExpr el && el.Kind == ConstKind.Ellipsis) return $"IReadOnlyList<{A(0)}>";
                            if (args.Count == 1) return $"ValueTuple<{A(0)}>";
                            return "(" + string.Join(", ", args.Select(x => MapType(x, at))) + ")";
                        case "Callable":
                        {
                            var ps = args.Count > 0 && args[0] is ListExpr pl ? pl.Elts.Select(x => MapType(x, at)).ToList() : new List<string>();
                            string ret = args.Count > 1 ? MapType(args[1], at, isReturn: true) : "void";
                            if (ret == "void") return ps.Count == 0 ? "Action" : $"Action<{string.Join(", ", ps)}>";
                            ps.Add(ret);
                            return $"Func<{string.Join(", ", ps)}>";
                        }
                    }
                    string b = MapType(s.Target, at);
                    return $"{b}<{string.Join(", ", args.Select(x => MapType(x, at)))}>";
                }
                case BinOpExpr bin when bin.Op == "|":
                {
                    var members = new List<Expr>();
                    void Flatten(Expr x) { if (x is BinOpExpr bx && bx.Op == "|") { Flatten(bx.Left); Flatten(bx.Right); } else members.Add(x); }
                    Flatten(bin);
                    return MapUnion(members, at);
                }
                default:
                    Error(at, "unsupported type annotation");
                    return "dynamic";
            }
        }

        static string Nullable(string t) => t.EndsWith("?") || t == "dynamic" || t == "object" ? t : t + "?";

        string MapUnion(List<Expr> members, Node at)
        {
            bool hasNone = members.Any(m => (m is ConstExpr c && c.Kind == ConstKind.None) || (m is NameExpr n && n.Id == "None"));
            var rest = members.Where(m => !((m is ConstExpr c && c.Kind == ConstKind.None) || (m is NameExpr n && n.Id == "None"))).Select(m => MapType(m, at)).Distinct().ToList();
            // int | float: an int is acceptable wherever a float is (PEP 484's numeric tower)
            if (rest.Count == 2 && rest.Contains("double") && rest.Any(IsIntegral)) rest = new List<string> { "double" };
            if (rest.Count == 1) return hasNone ? Nullable(rest[0]) : rest[0];
            // TODO(.NET 11 / C# 15): lower to a canonical (sorted) generated union type instead of object.
            Report(at, "TY1002", Severity.Info, $"union type ({string.Join(" | ", rest)}) is represented as 'object' in this version; narrow it with isinstance()");
            return "object";
        }

        bool IsTypeish(Expr e)
        {
            switch (e)
            {
                case ConstExpr c: return c.Kind == ConstKind.None || c.Kind == ConstKind.Str;
                case NameExpr n: return PrimitiveTypes.ContainsKey(n.Id) || n.Id == "None" || IsTypeParamInScope(n.Id) || TypeAlias(n.Id) != null || TryResolveType(n.Id, 0, out _, out _);
                case AttributeExpr a: { var d = DottedName(a); return d != null && TryResolveType(d, 0, out _, out _); }
                case SubscriptExpr s:
                {
                    string bn = StripTypingPrefix(DottedName(s.Target));
                    if (bn == null) return false;
                    var args = s.Index is TupleExpr t ? t.Elts : new List<Expr> { s.Index };
                    if (GenericAliases.Contains(bn)) return true;
                    return TryResolveType(bn, args.Count, out _, out _) && args.All(IsTypeish);
                }
                case BinOpExpr b when b.Op == "|": return IsTypeish(b.Left) && IsTypeish(b.Right);
                default: return false;
            }
        }

        /// <summary>If the expression names a type, returns the C# code for it (used for 'Foo(...)' → 'new Foo(...)').</summary>
        bool TryTypeExpr(Expr e, out string code)
        {
            code = null;
            switch (e)
            {
                case NameExpr n:
                    if (BuiltinConversionNames.Contains(n.Id) && !IsShadowed(n.Id)) return false;
                    if (!TryResolveType(n.Id, 0, out var nameMeta, out var namePy)) return false;
                    code = ExceptionMap.TryGetValue(n.Id, out var ex) && !IsShadowed(n.Id) ? ex : (_cls != null && _cls.Name == n.Id ? _cls.CsSelfType : PackageImportedMember(n.Id) ?? StdlibTypeCode(nameMeta, namePy) ?? StdlibNestedType(n.Id) ?? Esc(n.Id));
                    return true;
                case AttributeExpr a:
                {
                    string dotted = DottedName(a);
                    if (dotted == null || !TryResolveType(dotted, 0, out var attrMeta, out var attrPy)) return false;
                    code = (attrPy == null ? null : ProjectPackageMember(dotted)) ?? StdlibTypeCode(attrMeta, attrPy) ?? StdlibNestedType(dotted) ?? string.Join(".", dotted.Split('.').Select(Esc));
                    return true;
                }
                case SubscriptExpr s:
                {
                    var args = s.Index is TupleExpr t ? t.Elts : new List<Expr> { s.Index };
                    if (!args.All(IsTypeish)) return false;
                    string bn = StripTypingPrefix(DottedName(s.Target));
                    if (bn == null) return false;
                    if (GenericAliases.Contains(bn) || TryResolveType(bn, args.Count, out _, out _)) { code = MapType(s, s); return true; }
                    return false;
                }
                default: return false;
            }
        }
    }
}
