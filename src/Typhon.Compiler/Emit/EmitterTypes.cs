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
    internal sealed class CodeWriter
    {
        readonly StringBuilder _sb = new StringBuilder();
        int _indent;
        public void Line(string s = "") { if (s.Length > 0) _sb.Append(' ', _indent * 4); _sb.Append(s).Append('\n'); }
        public void Open() { Line("{"); _indent++; }
        public void Close(string suffix = "") { _indent--; Line("}" + suffix); }
        public void Raw(string s) { _sb.Append(s).Append('\n'); }
        public int Indent { get => _indent; set => _indent = value; }
        public override string ToString() => _sb.ToString();
    }

    /// <summary>Per-function emission state.</summary>
    internal sealed class Scope
    {
        public Scope Parent;
        public LocalsResult Locals;
        public readonly HashSet<string> Params = new HashSet<string>();
        public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
        public string ReturnType;        // element type for generators, awaited type for async; "void" for none
        public bool IsGenerator;
        /// <summary>A generator that receives values (x = yield v) or returns one: a PyGenerator whose body reads them.</summary>
        public bool ReceivesValues;
        public bool IsAsync;
        public bool HasSelf;             // instance method: 'self' → 'this'
        public string ClsName;           // classmethod: 'cls' → class name
        public bool IsFunctionBoundary = true;
        /// <summary>Parameters that stand for a function (def f(x, bisect=bisect) never passed): name → that function.</summary>
        public readonly Dictionary<string, Expr> Aliases = new Dictionary<string, Expr>();
    }

    internal sealed class ClassContext
    {
        public ClassDef Def;
        public string Name;
        public string CsSelfType;        // Name<T,...>
        public bool IsInterface;
        public bool IsSealed;
        public readonly HashSet<string> Members = new HashSet<string>();
        public readonly Dictionary<string, string> FieldTypes = new Dictionary<string, string>();
        public readonly HashSet<string> TypeParams = new HashSet<string>();
        public string IterElementType;
        public readonly Dictionary<string, string> MethodReturnTypes = new Dictionary<string, string>();
        public bool NoInitDone;
    }
}
