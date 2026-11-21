// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Compiler.Syntax
{
    /// <summary>What the GDScript front end knows about a .gd module beyond its Typhon AST.</summary>
    public sealed class GdScriptModule
    {
        /// <summary>The class the file itself is (named by class_name, else after the file).</summary>
        public ClassDef ScriptClass;
        public bool HasClassName;
        /// <summary>Inner classes and named enums, moved to module level (Typhon's classes live there).</summary>
        public readonly List<ClassDef> InnerClasses = new List<ClassDef>();
        /// <summary>Member variables with @export, per class.</summary>
        public readonly Dictionary<ClassDef, HashSet<string>> Exports = new Dictionary<ClassDef, HashSet<string>>();
        /// <summary>The GDScript classes of this module (the script class and its inner classes).</summary>
        public readonly List<ClassDef> AllClasses = new List<ClassDef>();
        /// <summary>Names declared with const (and enum values), per class: they are never assigned.</summary>
        public readonly Dictionary<ClassDef, HashSet<string>> Constants = new Dictionary<ClassDef, HashSet<string>>();

        internal void AddConstant(ClassDef c, string name)
        {
            if (!Constants.TryGetValue(c, out var set)) Constants[c] = set = new HashSet<string>();
            set.Add(name);
        }
    }

    /// <summary>
    /// Parses GDScript (Godot 4) into Typhon's Python AST, so the rest of the pipeline (inference, C# emission, Roslyn)
    /// is shared. The file is a class: <c>extends</c> gives its base, member <c>var</c>s become fields (initialized in the
    /// constructor, before <c>_init</c>'s body, as GDScript does), <c>func</c>s methods, <c>const</c>s and <c>static var</c>s
    /// class variables, inner classes and enums module-level classes. GDScript's implicit <c>self</c> is resolved here:
    /// a bare name is a local, a member of the class (or of an outer class: constants, enums, classes), a GDScript global,
    /// or else an inherited member of the native base (<c>position</c>, <c>add_child</c>). GDScript's built-in functions
    /// and its Array/String/Dictionary methods are mapped to Python builtins or to the runtime's <c>gdscript</c> module.
    /// </summary>
    public sealed class GdScriptParser
    {
        /// <summary>The name under which a GDScript module sees the runtime's gdscript module.</summary>
        public const string Rt = "__gd";

        // ───────────────────────────── scopes ─────────────────────────────

        enum MemberKind { Var, StaticVar, Func, StaticFunc, Const, Enum, EnumValue, Class, Signal }

        sealed class ClassScope
        {
            public string GdName;      // the name GDScript uses
            public string PyName;      // the Typhon class name (hoisted inner classes may be renamed)
            public ClassScope Outer;
            public ClassScope BaseScope; // an inner class of this file it extends
            public string BaseName;    // what it extends (null: nothing)
            public bool IsScript;
            public readonly Dictionary<string, MemberKind> Members = new Dictionary<string, MemberKind>();
            public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
            public readonly Dictionary<string, ClassScope> Inner = new Dictionary<string, ClassScope>();
            public readonly Dictionary<string, string> Hoisted = new Dictionary<string, string>(); // enum / class → Typhon name
            public ClassDef Def;
        }

        sealed class FuncScope
        {
            public string Name;
            public bool IsStatic;
            public bool IsInit;
            public readonly List<Dictionary<string, string>> Blocks = new List<Dictionary<string, string>>();
            public string Accessor; // inside a property accessor of this member: the bare name is the backing field
        }

        readonly List<Token> _t;
        int _i;
        readonly string _path;
        readonly GdScriptModule _info = new GdScriptModule();
        readonly HashSet<string> _moduleNames = new HashSet<string>();
        ClassScope _cls;
        FuncScope _fn;
        int _tmp;
        /// <summary>The element type of the array a lambda being parsed is passed to (words.sort_custom(func(a, b): ...)).</summary>
        string _lambdaHint;

        GdScriptParser(List<Token> tokens, string path) { _t = tokens; _path = path; }

        /// <summary>Parses a .gd file. <paramref name="isMain"/>: the program's entry module (constructing the script class runs its _init).</summary>
        public static ModuleAst ParseModule(string source, string path, bool isMain, out GdScriptModule info)
        {
            var p = new GdScriptParser(new GdScriptLexer(source).Tokenize(), path);
            var m = p.ParseFile(isMain);
            info = p._info;
            return m;
        }

        static string ScriptNameFromPath(string path)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(path ?? "script");
            var parts = stem.Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
            string name = string.Concat(parts.Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));
            if (name.Length == 0 || !char.IsLetter(name[0])) name = "Script" + name;
            return new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        }

        ModuleAst ParseFile(bool isMain)
        {
            var module = new ModuleAst { Path = _path };
            var first = Cur;
            module.Body.Add(At(new ImportStmt { Names = { new ImportAlias { Name = "Typhon.Runtime.GdScript", AsName = Rt } } }, first));

            // class_name and extends may come after other annotations (@tool, @icon): find them first
            string className = null; string baseName = null;
            int depth = 0;
            for (int k = 0; k < _t.Count - 1; k++)
            {
                var t = _t[k];
                if (t.Kind == TokenKind.Indent || t.Kind == TokenKind.Op && (t.Text == "(" || t.Text == "[" || t.Text == "{")) { depth++; continue; }
                if (t.Kind == TokenKind.Dedent || t.Kind == TokenKind.Op && (t.Text == ")" || t.Text == "]" || t.Text == "}")) { depth--; continue; }
                if (depth != 0 || t.Kind != TokenKind.Name) continue;
                bool statementStart = k == 0 || _t[k - 1].Kind == TokenKind.Newline || _t[k - 1].Kind == TokenKind.Dedent || _t[k - 1].Is(";");
                if (t.Text == "class_name" && statementStart && _t[k + 1].Kind == TokenKind.Name) className = _t[k + 1].Text;
                if (t.Text == "extends" && (statementStart || IsClassNameDecl(k)) && _t[k + 1].Kind == TokenKind.Name) baseName = DottedAt(k + 1);
            }
            var script = new ClassScope { GdName = className ?? ScriptNameFromPath(_path), IsScript = true, BaseName = baseName };
            script.PyName = script.GdName;
            _info.HasClassName = className != null;
            _moduleNames.Add(script.PyName);
            var def = At(new ClassDef { Name = script.PyName }, first);
            if (baseName != null) def.Bases.Add(TypeExprFromDotted(baseName, first));
            script.Def = def;
            _info.ScriptClass = def;
            _info.AllClasses.Add(def);
            module.Body.Add(def);

            ScanMembers(0, script, untilDedent: false);
            _cls = script;
            ParseClassBody(script, untilDedent: false);
            foreach (var inner in _info.InnerClasses) module.Body.Add(inner);

            if (isMain)
            {
                // the entry point: constructing the script runs its member initializers and _init (like `godot -s file.gd`)
                var guard = At(new IfStmt
                {
                    Test = At(new CompareExpr { Left = At(new NameExpr { Id = "__name__" }, first), Ops = { "==" }, Comparators = { At(new ConstExpr { Kind = ConstKind.Str, Str = "__main__" }, first) } }, first),
                }, first);
                guard.Body.Add(At(new ExprStmt { Value = At(new CallExpr { Func = At(new NameExpr { Id = def.Name }, first) }, first) }, first));
                module.Body.Add(guard);
            }
            return module;
        }

        bool IsClassNameDecl(int extendsIndex) => extendsIndex >= 2 && _t[extendsIndex - 2].Is("class_name");

        string DottedAt(int k)
        {
            string s = _t[k].Text;
            while (k + 2 < _t.Count && _t[k + 1].Is(".") && _t[k + 2].Kind == TokenKind.Name) { s += "." + _t[k + 2].Text; k += 2; }
            return s;
        }

        // ───────────────────────────── token helpers ─────────────────────────────

        Token Cur => _t[_i];
        Token Peek(int n = 1) => _t[Math.Min(_i + n, _t.Count - 1)];
        bool IsOp(string op) => Cur.Kind == TokenKind.Op && Cur.Text == op;
        bool IsKw(string kw) => Cur.Kind == TokenKind.Name && Cur.Text == kw;
        bool AcceptOp(string op) { if (IsOp(op)) { _i++; return true; } return false; }
        bool AcceptKw(string kw) { if (IsKw(kw)) { _i++; return true; } return false; }
        Token ExpectOp(string op) { if (!IsOp(op)) throw Err("expected '" + op + "' but found " + Describe(Cur)); return _t[_i++]; }
        void ExpectKw(string kw) { if (!IsKw(kw)) throw Err("expected '" + kw + "' but found " + Describe(Cur)); _i++; }

        string ExpectName()
        {
            if (Cur.Kind != TokenKind.Name) throw Err("expected a name but found " + Describe(Cur));
            return _t[_i++].Text;
        }

        void ExpectNewline()
        {
            AcceptOp(";");
            if (Cur.Kind == TokenKind.Newline) { _i++; return; }
            if (Cur.Kind == TokenKind.EndOfFile || Cur.Kind == TokenKind.Dedent) return;
            throw Err("expected end of line but found " + Describe(Cur));
        }

        static string Describe(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.Newline: return "end of line";
                case TokenKind.Indent: return "an indented block";
                case TokenKind.Dedent: return "end of block";
                case TokenKind.EndOfFile: return "end of file";
                default: return "'" + t.Text + "'";
            }
        }

        SyntaxError Err(string msg) => new SyntaxError(msg, Cur.Line, Cur.Col);
        SyntaxError ErrAt(Node n, string msg) => new SyntaxError(msg, n.Line, n.Col);
        static T At<T>(T node, Token tok) where T : Node { node.Line = tok.Line; node.Col = tok.Col; return node; }
        static T At<T>(T node, Node at) where T : Node { node.Line = at.Line; node.Col = at.Col; return node; }

        /// <summary>A position (a token's or a node's) as a node, for the helpers below.</summary>
        static Node P(object at) => at is Node n ? n : at is Token t ? new PassStmt { Line = t.Line, Col = t.Col } : new PassStmt();
        NameExpr Name(string id, object at) => At(new NameExpr { Id = id }, P(at));
        AttributeExpr Attr(Expr target, string name, object at) => At(new AttributeExpr { Target = target, Name = name }, P(at));
        CallExpr Call(Expr func, object at, params Expr[] args)
        {
            var c = At(new CallExpr { Func = func }, P(at));
            foreach (var a in args) c.Args.Add(new Arg { Value = a });
            return c;
        }
        Expr RtCall(string fn, object at, params Expr[] args) => Call(Attr(Name(Rt, at), fn, at), at, args);
        Expr Self(object at) => Name("self", at);
        ConstExpr Str(string s, object at) => At(new ConstExpr { Kind = ConstKind.Str, Str = s }, P(at));
        ConstExpr Int(long v, object at) => At(new ConstExpr { Kind = ConstKind.Int, Raw = v.ToString(System.Globalization.CultureInfo.InvariantCulture) }, P(at));

        // ───────────────────────────── member pre-scan ─────────────────────────────

        /// <summary>
        /// Collects the members a class body declares (functions may use members declared further down), from the token
        /// after the body's INDENT (or the file start) to the end of the body.
        /// </summary>
        void ScanMembers(int from, ClassScope cls, bool untilDedent)
        {
            int depth = 0, paren = 0;
            bool lineStart = true;
            for (int k = from; k < _t.Count; k++)
            {
                var t = _t[k];
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Kind == TokenKind.Indent) { depth++; lineStart = true; continue; }
                if (t.Kind == TokenKind.Dedent) { depth--; if (depth < 0 && untilDedent) break; lineStart = true; continue; }
                if (t.Kind == TokenKind.Newline) { if (paren == 0) lineStart = true; continue; }
                if (t.Kind == TokenKind.Op && (t.Text == "(" || t.Text == "[" || t.Text == "{")) { paren++; lineStart = false; continue; }
                if (t.Kind == TokenKind.Op && (t.Text == ")" || t.Text == "]" || t.Text == "}")) { paren--; continue; }
                if (!lineStart || depth != 0 || paren != 0) continue;
                // annotations (@export, @onready, @export_range(...)) and 'static' before the declaration
                int j = k;
                while (j < _t.Count && _t[j].Is("@"))
                {
                    j += 2;
                    if (j < _t.Count && _t[j].Is("("))
                    {
                        int pd = 0;
                        do { if (_t[j].Is("(")) pd++; else if (_t[j].Is(")")) pd--; j++; } while (j < _t.Count && pd > 0);
                    }
                    if (j < _t.Count && _t[j].Kind == TokenKind.Newline) { lineStart = true; k = j; goto next; }
                }
                bool isStatic = false;
                if (_t[j].Is("static")) { isStatic = true; j++; }
                if (j + 1 >= _t.Count) break;
                string kw = _t[j].Kind == TokenKind.Name ? _t[j].Text : null;
                var nameTok = _t[j + 1];
                switch (kw)
                {
                    case "var" when nameTok.Kind == TokenKind.Name:
                        cls.Members[nameTok.Text] = isStatic ? MemberKind.StaticVar : MemberKind.Var;
                        cls.Types[nameTok.Text] = ScanDeclaredType(j + 2);
                        break;
                    case "const" when nameTok.Kind == TokenKind.Name:
                        cls.Members[nameTok.Text] = MemberKind.Const;
                        cls.Types[nameTok.Text] = ScanDeclaredType(j + 2);
                        break;
                    case "func" when nameTok.Kind == TokenKind.Name:
                        cls.Members[nameTok.Text] = isStatic ? MemberKind.StaticFunc : MemberKind.Func;
                        break;
                    case "signal" when nameTok.Kind == TokenKind.Name: cls.Members[nameTok.Text] = MemberKind.Signal; break;
                    case "class" when nameTok.Kind == TokenKind.Name: cls.Members[nameTok.Text] = MemberKind.Class; break;
                    case "enum":
                        if (nameTok.Kind == TokenKind.Name) { cls.Members[nameTok.Text] = MemberKind.Enum; break; }
                        // enum { A, B }: its values are constants of the class
                        for (int e = j + 2, bd = 1; e < _t.Count && bd > 0; e++)
                        {
                            if (_t[e].Is("{")) bd++;
                            else if (_t[e].Is("}")) bd--;
                            else if (bd == 1 && _t[e].Kind == TokenKind.Name && (_t[e - 1].Is("{") || _t[e - 1].Is(","))) { cls.Members[_t[e].Text] = MemberKind.EnumValue; cls.Types[_t[e].Text] = "int"; }
                        }
                        break;
                }
                lineStart = false;
            next:;
            }
        }

        /// <summary>The type a declaration gives its name, from the tokens after the name: ": Type", or ":= literal / Type(...)".</summary>
        string ScanDeclaredType(int k)
        {
            if (k >= _t.Count) return null;
            if (_t[k].Is(":") && k + 1 < _t.Count && _t[k + 1].Kind == TokenKind.Name)
            {
                int save = _i;
                _i = k + 1;
                try { return TypeString(ParseTypeRef()); } catch (SyntaxError) { return null; } finally { _i = save; }
            }
            if (_t[k].Is(":=") || _t[k].Is("="))
            {
                var v = k + 1 < _t.Count ? _t[k + 1] : null;
                if (v == null) return null;
                bool negative = v.Is("-") && k + 2 < _t.Count;
                if (negative) v = _t[k + 2];
                if (v.Kind == TokenKind.Number) return IsFloatLiteral(v.Text) ? "float" : "int";
                if (v.Kind == TokenKind.String) return "String";
                if (v.Is("true") || v.Is("false")) return "bool";
                if (v.Is("[")) return "Array";
                if (v.Is("{")) return "Dictionary";
                if (v.Kind == TokenKind.Name && char.IsUpper(v.Text[0]) && k + 2 < _t.Count && (_t[k + 2].Is("(") || _t[k + 2].Is(".") && k + 3 < _t.Count && _t[k + 3].Is("new")))
                    return v.Text;
            }
            return null;
        }

        static bool IsFloatLiteral(string text) =>
            !text.StartsWith("0x") && !text.StartsWith("0X") && !text.StartsWith("0b") && !text.StartsWith("0B") &&
            (text.Contains(".") || text.Contains("e") || text.Contains("E"));

        // ───────────────────────────── class bodies ─────────────────────────────

        sealed class MemberInit { public string Name; public Expr Value; public bool OnReady; public Node At; }

        sealed class Annotation { public string Name; public List<Expr> Args = new List<Expr>(); public Token Tok; }

        void ParseClassBody(ClassScope cls, bool untilDedent)
        {
            var def = cls.Def;
            var inits = new List<MemberInit>();
            FunctionDef init = null, ready = null;
            var accessors = new List<FunctionDef>();
            var pending = new List<Annotation>();
            while (true)
            {
                if (Cur.Kind == TokenKind.EndOfFile) break;
                if (Cur.Kind == TokenKind.Dedent) { if (untilDedent) { _i++; break; } _i++; continue; }
                if (Cur.Kind == TokenKind.Newline || IsOp(";")) { _i++; continue; }
                var tok = Cur;
                if (IsOp("@"))
                {
                    _i++;
                    var a = new Annotation { Name = ExpectName(), Tok = tok };
                    if (AcceptOp("("))
                    {
                        while (!IsOp(")")) { a.Args.Add(ParseExpr()); if (!AcceptOp(",")) break; }
                        ExpectOp(")");
                    }
                    pending.Add(a);
                    continue;
                }
                if (AcceptKw("extends")) { if (cls.IsScript) { SkipTypeOrPath(); ExpectNewline(); } else throw Err("'extends' must come first in a class"); continue; }
                if (AcceptKw("class_name"))
                {
                    ExpectName();
                    if (AcceptKw("extends")) SkipTypeOrPath();
                    ExpectNewline();
                    continue;
                }
                if (IsKw("tool")) { _i++; ExpectNewline(); continue; }
                if (AcceptKw("pass")) { ExpectNewline(); continue; }
                bool isStatic = AcceptKw("static");
                if (IsKw("var")) { ParseMemberVar(cls, isStatic, pending, inits, accessors); pending.Clear(); continue; }
                if (IsKw("const")) { ParseMemberConst(cls); pending.Clear(); continue; }
                if (IsKw("func"))
                {
                    var f = ParseFunc(cls, isStatic);
                    pending.Clear();
                    if (f.Name == "_init" && !isStatic) { init = f; continue; }
                    if (f.Name == "_ready" && !isStatic) ready = f;
                    if (f.Name == "_to_string" && !isStatic) f.Name = "__str__";
                    def.Body.Add(f);
                    continue;
                }
                if (IsKw("signal")) { ParseSignal(cls, inits); pending.Clear(); continue; }
                if (IsKw("enum")) { ParseEnum(cls); pending.Clear(); continue; }
                if (IsKw("class")) { ParseInnerClass(cls); pending.Clear(); continue; }
                if (pending.Count > 0 && Cur.Kind != TokenKind.Name) { pending.Clear(); continue; }
                throw Err("unexpected " + Describe(Cur) + " in a class body (expected var, const, func, signal, enum or class)");
            }

            // the constructor: member initializers in declaration order, then _init's body (GDScript's order)
            var ctor = At(new FunctionDef { Name = "__init__" }, def);
            ctor.Params.Add(At(new Param { Name = "self" }, def));
            var ctorBody = new List<Stmt>();
            if (init != null)
            {
                ctor.Params.AddRange(init.Params.Skip(1));
                ctor.Line = init.Line; ctor.Col = init.Col;
            }
            var initBody = init?.Body ?? new List<Stmt>();
            // an explicit super(...) call stays first
            if (initBody.Count > 0 && IsSuperInitCall(initBody[0])) { ctorBody.Add(initBody[0]); initBody = initBody.Skip(1).ToList(); }
            foreach (var mi in inits.Where(x => !x.OnReady)) ctorBody.Add(At(new AssignStmt { Targets = { Attr(Self(mi.At), mi.Name, mi.At) }, Value = mi.Value }, mi.At));
            ctorBody.AddRange(initBody);
            if (ctorBody.Count > 0 || init != null)
            {
                ctor.Body.AddRange(ctorBody.Count > 0 ? ctorBody : new List<Stmt> { At(new PassStmt(), def) });
                def.Body.Insert(0, ctor);
            }
            // @onready: assigned when the node enters the tree, before _ready's body
            var onReady = inits.Where(x => x.OnReady).ToList();
            if (onReady.Count > 0)
            {
                if (ready == null)
                {
                    ready = At(new FunctionDef { Name = "_ready", Returns = new ConstExpr { Kind = ConstKind.None, Raw = "None" } }, def);
                    ready.Params.Add(At(new Param { Name = "self" }, def));
                    def.Body.Add(ready);
                }
                ready.Body.InsertRange(0, onReady.Select(mi => (Stmt)At(new AssignStmt { Targets = { Attr(Self(mi.At), mi.Name, mi.At) }, Value = mi.Value }, mi.At)));
                if (ready.Body.Count > onReady.Count && ready.Body[onReady.Count] is PassStmt) ready.Body.RemoveAt(onReady.Count);
            }
            def.Body.AddRange(accessors);
            if (def.Body.Count == 0) def.Body.Add(At(new PassStmt(), def));
        }

        static bool IsSuperInitCall(Stmt s) =>
            s is ExprStmt es && es.Value is CallExpr c && c.Func is AttributeExpr a && a.Name == "__init__" && a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super";

        void SkipTypeOrPath()
        {
            if (Cur.Kind == TokenKind.String) { _i++; return; }
            ExpectName();
            while (AcceptOp(".")) ExpectName();
        }

        void ParseMemberVar(ClassScope cls, bool isStatic, List<Annotation> annotations, List<MemberInit> inits, List<FunctionDef> accessors)
        {
            var tok = Cur;
            ExpectKw("var");
            var nameTok = Cur;
            string name = ExpectName();
            Expr typeRef = null; Expr value = null;
            if (AcceptOp(":")) { if (!IsKw("set") && !IsKw("get") && Cur.Kind != TokenKind.Newline) typeRef = ParseTypeRef(); else _i--; }
            if (AcceptOp(":=")) value = ParseExpr();
            else if (AcceptOp("=")) value = ParseExpr();
            string gdType = typeRef != null ? TypeString(typeRef) : value != null ? TypeOf(value) : null;
            if (gdType != null) cls.Types[name] = gdType;
            bool onReady = annotations.Any(a => a.Name == "onready");
            if (annotations.Any(a => a.Name.StartsWith("export")))
            {
                if (!_info.Exports.TryGetValue(cls.Def, out var set)) _info.Exports[cls.Def] = set = new HashSet<string>();
                set.Add(name);
            }
            var annotation = typeRef != null ? MapType(typeRef) : null;
            if (typeRef != null && value != null && NeedsDowncast(value, typeRef)) value = Call(Name("cast", nameTok), nameTok, MapType(typeRef), value);
            // @export var speed := 100.0: an exported member is declared with its type (for Godot's [Export])
            bool exported = annotations.Any(a => a.Name.StartsWith("export"));
            if (annotation == null && exported && !isStatic && gdType != null && !gdType.StartsWith("Array") && !gdType.StartsWith("Dictionary") && !gdType.StartsWith("Packed"))
                annotation = TypeAnnotationFor(gdType, nameTok) ?? (char.IsUpper(gdType[0]) ? MapType(Name(gdType, nameTok)) : null);

            // a property: set(value): / get: accessors (or set = f, get = g)
            FunctionDef getter = null, setter = null;
            if (AcceptOp(":"))
            {
                if (Cur.Kind == TokenKind.Newline)
                {
                    _i++;
                    if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block of set/get");
                    _i++;
                    while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
                    {
                        if (Cur.Kind == TokenKind.Newline) { _i++; continue; }
                        ParseAccessor(cls, name, annotation, ref getter, ref setter);
                        while (AcceptOp(",")) ParseAccessor(cls, name, annotation, ref getter, ref setter);
                    }
                    if (Cur.Kind == TokenKind.Dedent) _i++;
                }
                else
                {
                    do ParseAccessor(cls, name, annotation, ref getter, ref setter); while (AcceptOp(","));
                    ExpectNewline();
                }
            }
            else ExpectNewline();

            var def = cls.Def;
            if (getter != null || setter != null)
            {
                // the value lives in a backing field; inside the accessors the bare name means that field
                string backing = "_" + name + "_value";
                cls.Types[backing] = gdType;
                if (annotation != null) def.Body.Add(At(new AnnAssignStmt { Target = Name(backing, nameTok), Annotation = annotation }, nameTok));
                if (value != null || annotation != null)
                    inits.Add(new MemberInit { Name = backing, Value = value ?? DefaultValue(typeRef, nameTok), OnReady = onReady, At = P(nameTok) });
                if (getter == null)
                {
                    getter = At(new FunctionDef { Name = name, Returns = annotation }, nameTok);
                    getter.Params.Add(At(new Param { Name = "self" }, nameTok));
                    getter.Body.Add(At(new ReturnStmt { Value = Attr(Self(nameTok), backing, nameTok) }, nameTok));
                }
                getter.Decorators.Add(Name("property", nameTok));
                if (getter.Returns == null) getter.Returns = annotation;
                accessors.Add(getter);
                if (setter != null)
                {
                    setter.Decorators.Add(Attr(Name(name, nameTok), "setter", nameTok));
                    accessors.Add(setter);
                }
                return;
            }

            if (isStatic)
            {
                var v = value ?? (typeRef != null ? DefaultValue(typeRef, nameTok) : At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, nameTok));
                if (annotation != null)
                    def.Body.Add(At(new AnnAssignStmt { Target = Name(name, nameTok), Annotation = At(new SubscriptExpr { Target = Name("ClassVar", nameTok), Index = annotation }, nameTok), Value = v }, nameTok));
                else def.Body.Add(At(new AssignStmt { Targets = { Name(name, nameTok) }, Value = v }, nameTok));
                return;
            }
            if (annotation != null) def.Body.Add(At(new AnnAssignStmt { Target = Name(name, nameTok), Annotation = annotation }, nameTok));
            if (value != null || typeRef != null)
                inits.Add(new MemberInit { Name = name, Value = value ?? DefaultValue(typeRef, nameTok), OnReady = onReady, At = P(nameTok) });
            else if (!AssignedInClass(name))
                inits.Add(new MemberInit { Name = name, Value = At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, nameTok), OnReady = onReady, At = P(nameTok) });
        }

        /// <summary>Is the member assigned somewhere in the class's source (then an untyped `var x` needs no None initializer to get a type)?</summary>
        bool AssignedInClass(string name)
        {
            for (int k = _i; k < _t.Count - 1; k++)
                if (_t[k].Kind == TokenKind.Name && _t[k].Text == name && (_t[k + 1].Is("=") || _t[k + 1].Text.Length == 2 && _t[k + 1].Text[1] == '=' && _t[k + 1].Kind == TokenKind.Op && "+-*/%".IndexOf(_t[k + 1].Text[0]) >= 0))
                    return true;
            return false;
        }

        void ParseAccessor(ClassScope cls, string member, Expr annotation, ref FunctionDef getter, ref FunctionDef setter)
        {
            var tok = Cur;
            if (AcceptKw("set"))
            {
                var f = At(new FunctionDef { Name = member, Returns = new ConstExpr { Kind = ConstKind.None, Raw = "None" } }, tok);
                f.Params.Add(At(new Param { Name = "self" }, tok));
                if (AcceptOp("="))
                {
                    // set = _set_x: the setter calls that method
                    string target = ExpectName();
                    f.Params.Add(At(new Param { Name = "value", Annotation = annotation }, tok));
                    f.Body.Add(At(new ExprStmt { Value = Call(Attr(Self(tok), target, tok), tok, Name("value", tok)) }, tok));
                }
                else
                {
                    ExpectOp("(");
                    string pn = ExpectName();
                    if (AcceptOp(":")) ParseTypeRef();
                    ExpectOp(")");
                    f.Params.Add(At(new Param { Name = pn, Annotation = annotation }, tok));
                    ExpectOp(":");
                    var fs = new FuncScope { Name = member, Accessor = member };
                    fs.Blocks.Add(new Dictionary<string, string> { [pn] = annotation != null ? TypeString(annotation) : null });
                    var saved = _fn; _fn = fs;
                    try { f.Body.AddRange(ParseSuite()); } finally { _fn = saved; }
                }
                setter = f;
                return;
            }
            if (AcceptKw("get"))
            {
                var f = At(new FunctionDef { Name = member, Returns = annotation }, tok);
                f.Params.Add(At(new Param { Name = "self" }, tok));
                if (AcceptOp("="))
                {
                    string target = ExpectName();
                    f.Body.Add(At(new ReturnStmt { Value = Call(Attr(Self(tok), target, tok), tok) }, tok));
                }
                else
                {
                    ExpectOp(":");
                    var fs = new FuncScope { Name = member, Accessor = member };
                    fs.Blocks.Add(new Dictionary<string, string>());
                    var saved = _fn; _fn = fs;
                    try { f.Body.AddRange(ParseSuite()); } finally { _fn = saved; }
                }
                getter = f;
                return;
            }
            throw Err("expected 'set' or 'get'");
        }

        void ParseMemberConst(ClassScope cls)
        {
            ExpectKw("const");
            var nameTok = Cur;
            string name = ExpectName();
            Expr typeRef = null;
            if (AcceptOp(":")) typeRef = ParseTypeRef();
            if (!AcceptOp(":=")) ExpectOp("=");
            var value = ParseExpr();
            ExpectNewline();
            string t = typeRef != null ? TypeString(typeRef) : TypeOf(value);
            if (t != null) cls.Types[name] = t;
            if (IsLoad(value)) cls.Types[name] = "Resource";
            _info.AddConstant(cls.Def, name);
            if (typeRef != null)
                cls.Def.Body.Add(At(new AnnAssignStmt { Target = Name(name, nameTok), Annotation = At(new SubscriptExpr { Target = Name("ClassVar", nameTok), Index = MapType(typeRef) }, nameTok), Value = value }, nameTok));
            else cls.Def.Body.Add(At(new AssignStmt { Targets = { Name(name, nameTok) }, Value = value }, nameTok));
        }

        /// <summary>const Scene = preload("res://x.tscn"): a resource.</summary>
        static bool IsLoad(Expr e) =>
            e is CallExpr c && c.Func is AttributeExpr a && a.Target is NameExpr n && n.Id == Rt && a.Name == "load" && c.Args.Count == 1 && c.Args[0].Value is ConstExpr;

        void ParseSignal(ClassScope cls, List<MemberInit> inits)
        {
            ExpectKw("signal");
            var nameTok = Cur;
            string name = ExpectName();
            if (AcceptOp("("))
            {
                while (!IsOp(")")) { ExpectName(); if (AcceptOp(":")) ParseTypeRef(); if (!AcceptOp(",")) break; }
                ExpectOp(")");
            }
            ExpectNewline();
            // a signal is an object with connect / disconnect / emit, created with the instance
            inits.Add(new MemberInit { Name = name, Value = RtCall("Signal", nameTok), At = P(nameTok) });
        }

        void ParseEnum(ClassScope cls)
        {
            var tok = Cur;
            ExpectKw("enum");
            string enumName = Cur.Kind == TokenKind.Name ? ExpectName() : null;
            ExpectOp("{");
            var values = new List<KeyValuePair<Token, Expr>>();
            long next = 0;
            ClassDef holder = null;
            if (enumName != null)
            {
                string py = HoistName(cls, enumName);
                cls.Hoisted[enumName] = py;
                holder = At(new ClassDef { Name = py }, tok);
                cls.Types[enumName] = "int";
            }
            while (!IsOp("}"))
            {
                while (Cur.Kind == TokenKind.Newline) _i++;
                if (IsOp("}")) break;
                var vt = Cur;
                string vn = ExpectName();
                Expr value;
                if (AcceptOp("="))
                {
                    value = ParseExpr();
                    if (ConstIntOf(value) is long cv) next = cv + 1;
                }
                else value = Int(next++, vt);
                var target = holder ?? cls.Def;
                target.Body.Add(At(new AssignStmt { Targets = { Name(vn, vt) }, Value = value }, vt));
                _info.AddConstant(target, vn);
                if (!AcceptOp(",")) break;
            }
            while (Cur.Kind == TokenKind.Newline) _i++;
            ExpectOp("}");
            ExpectNewline();
            if (holder != null)
            {
                if (holder.Body.Count == 0) holder.Body.Add(At(new PassStmt(), tok));
                _info.InnerClasses.Add(holder);
            }
        }

        static long? ConstIntOf(Expr e)
        {
            if (e is ConstExpr c && c.Kind == ConstKind.Int && long.TryParse(c.Raw.Replace("_", ""), out var v)) return v;
            if (e is UnaryExpr u && u.Op == "-" && ConstIntOf(u.Operand) is long n) return -n;
            return null;
        }

        string HoistName(ClassScope cls, string name)
        {
            string py = name;
            if (_moduleNames.Contains(py)) py = cls.PyName + "_" + name;
            while (_moduleNames.Contains(py)) py += "_";
            _moduleNames.Add(py);
            return py;
        }

        void ParseInnerClass(ClassScope outer)
        {
            var tok = Cur;
            ExpectKw("class");
            string name = ExpectName();
            string baseName = null;
            if (AcceptKw("extends"))
            {
                if (Cur.Kind == TokenKind.String) throw Err("extending a script by its path is not supported yet (use its class_name)");
                baseName = ExpectName();
                while (AcceptOp(".")) baseName += "." + ExpectName();
            }
            ExpectOp(":");
            string py = HoistName(outer, name);
            outer.Hoisted[name] = py;
            var scope = new ClassScope { GdName = name, PyName = py, Outer = outer, BaseName = baseName };
            outer.Inner[name] = scope;
            var def = At(new ClassDef { Name = py }, tok);
            scope.Def = def;
            if (baseName != null)
            {
                // a sibling inner class or the script class: its Typhon name; else a native class
                var bs = FindClassScope(outer, baseName);
                if (bs != null) { scope.BaseScope = bs; def.Bases.Add(Name(bs.PyName, tok)); }
                else def.Bases.Add(TypeExprFromDotted(baseName, tok));
            }
            _info.InnerClasses.Add(def);
            _info.AllClasses.Add(def);
            if (Cur.Kind != TokenKind.Newline) throw Err("expected an indented class body");
            _i++;
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented class body");
            _i++;
            ScanMembers(_i, scope, untilDedent: true);
            var saved = _cls; _cls = scope;
            try { ParseClassBody(scope, untilDedent: true); } finally { _cls = saved; }
        }

        ClassScope FindClassScope(ClassScope from, string name)
        {
            for (var c = from; c != null; c = c.Outer)
            {
                if (c.GdName == name) return c;
                if (c.Inner.TryGetValue(name, out var s)) return s;
            }
            return null;
        }

        // ───────────────────────────── functions ─────────────────────────────

        FunctionDef ParseFunc(ClassScope cls, bool isStatic)
        {
            var tok = Cur;
            ExpectKw("func");
            string name = ExpectName();
            var f = At(new FunctionDef { Name = name }, tok);
            var fs = new FuncScope { Name = name, IsStatic = isStatic, IsInit = name == "_init" };
            var paramsBlock = new Dictionary<string, string>();
            fs.Blocks.Add(paramsBlock);
            if (!isStatic) f.Params.Add(At(new Param { Name = "self" }, tok));
            else f.Decorators.Add(Name("staticmethod", tok));
            ExpectOp("(");
            var saved = _fn; _fn = fs;
            try
            {
                while (!IsOp(")"))
                {
                    var pt = Cur;
                    var p = At(new Param { Name = ExpectName() }, pt);
                    string ptype = null;
                    if (AcceptOp(":")) { var tr = ParseTypeRef(); p.Annotation = MapType(tr); ptype = TypeString(tr); }
                    if (AcceptOp(":="))
                    {
                        p.Default = ParseExpr();
                        ptype = TypeOf(p.Default);
                        if (ptype != null && TypeAnnotationFor(ptype, pt) is Expr ann) p.Annotation = ann;
                    }
                    else if (AcceptOp("=")) p.Default = ParseExpr();
                    paramsBlock[p.Name] = ptype;
                    f.Params.Add(p);
                    if (!AcceptOp(",")) break;
                }
                ExpectOp(")");
                if (AcceptOp("->")) f.Returns = MapType(ParseTypeRef());
                ExpectOp(":");
                f.Body.AddRange(ParseSuite());
            }
            finally { _fn = saved; }
            return f;
        }

        /// <summary>A Typhon annotation for a literal's GDScript type (func f(b := 2.0) gives b a float type).</summary>
        Expr TypeAnnotationFor(string gdType, Token at)
        {
            switch (gdType)
            {
                case "int": return Name("int", at);
                case "float": return Name("float", at);
                case "bool": return Name("bool", at);
                case "String": return Name("str", at);
                default: return null;
            }
        }

        List<Stmt> ParseSuite()
        {
            if (Cur.Kind == TokenKind.Newline)
            {
                _i++;
                if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block");
                _i++;
                PushBlock();
                var body = new List<Stmt>();
                try
                {
                    while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
                    {
                        if (Cur.Kind == TokenKind.Newline || IsOp(";")) { _i++; continue; }
                        body.AddRange(ParseStatement());
                    }
                    if (Cur.Kind == TokenKind.Dedent) _i++;
                }
                finally { PopBlock(); }
                if (body.Count == 0) body.Add(At(new PassStmt(), Cur));
                return body;
            }
            // a suite on the same line: if x: return 1
            PushBlock();
            try
            {
                var list = new List<Stmt>(ParseSimpleStatement());
                while (AcceptOp(";"))
                {
                    if (Cur.Kind == TokenKind.Newline || Cur.Kind == TokenKind.EndOfFile) break;
                    list.AddRange(ParseSimpleStatement());
                }
                ExpectNewline();
                return list;
            }
            finally { PopBlock(); }
        }

        void PushBlock() { _fn?.Blocks.Add(new Dictionary<string, string>()); }
        void PopBlock() { if (_fn != null && _fn.Blocks.Count > 1) _fn.Blocks.RemoveAt(_fn.Blocks.Count - 1); }
        void Declare(string name, string type) { if (_fn != null) _fn.Blocks[_fn.Blocks.Count - 1][name] = type; }

        bool LocalType(string name, out string type)
        {
            type = null;
            if (_fn == null) return false;
            for (int k = _fn.Blocks.Count - 1; k >= 0; k--)
                if (_fn.Blocks[k].TryGetValue(name, out type)) return true;
            return false;
        }

        // ───────────────────────────── statements ─────────────────────────────

        List<Stmt> ParseStatement()
        {
            var tok = Cur;
            if (IsKw("if")) return new List<Stmt> { ParseIf() };
            if (IsKw("while"))
            {
                _i++;
                var w = At(new WhileStmt { Test = ParseExpr() }, tok);
                ExpectOp(":");
                w.Body = ParseSuite();
                return new List<Stmt> { w };
            }
            if (IsKw("for")) return new List<Stmt> { ParseFor() };
            if (IsKw("match")) return new List<Stmt> { ParseMatch() };
            var list = new List<Stmt>(ParseSimpleStatement());
            while (AcceptOp(";"))
            {
                if (Cur.Kind == TokenKind.Newline || Cur.Kind == TokenKind.EndOfFile || Cur.Kind == TokenKind.Dedent) break;
                list.AddRange(ParseSimpleStatement());
            }
            ExpectNewline();
            return list;
        }

        Stmt ParseIf()
        {
            var tok = Cur;
            _i++; // if / elif
            var s = At(new IfStmt { Test = ParseExpr() }, tok);
            ExpectOp(":");
            s.Body = ParseSuite();
            while (Cur.Kind == TokenKind.Newline) _i++;
            if (IsKw("elif")) s.OrElse.Add(ParseIf());
            else if (AcceptKw("else")) { ExpectOp(":"); s.OrElse = ParseSuite(); }
            return s;
        }

        Stmt ParseFor()
        {
            var tok = Cur;
            ExpectKw("for");
            var vt = Cur;
            string name = ExpectName();
            string declared = null;
            if (AcceptOp(":")) declared = TypeString(ParseTypeRef());
            ExpectKw("in");
            var iter = ParseExpr();
            ExpectOp(":");
            // for i in 10 / for i in count: an int counts like range(n)
            string it = TypeOf(iter);
            if (it == "int" || it == "float") iter = Call(Name("range", iter), iter, it == "float" ? Call(Name("int", iter), iter, iter) : iter);
            string elem = declared ?? ElementTypeOf(iter);
            var f = At(new ForStmt { Target = Name(name, vt), Iter = iter }, tok);
            PushBlock();
            Declare(name, elem);
            try { f.Body = ParseSuite(); }
            finally { PopBlock(); }
            return f;
        }

        string ElementTypeOf(Expr iter)
        {
            if (iter is CallExpr c && c.Func is NameExpr n && n.Id == "range") return "int";
            string t = TypeOf(iter);
            if (t == null) return null;
            if (t.StartsWith("Array[") && t.EndsWith("]")) return t.Substring(6, t.Length - 7);
            if (t == "String") return "String";
            if (t.StartsWith("Packed") && t.EndsWith("Array")) return PackedElement(t);
            return null;
        }

        static string PackedElement(string packed)
        {
            switch (packed)
            {
                case "PackedInt32Array": case "PackedInt64Array": case "PackedByteArray": return "int";
                case "PackedFloat32Array": case "PackedFloat64Array": return "float";
                case "PackedStringArray": return "String";
                case "PackedVector2Array": return "Vector2";
                case "PackedVector3Array": return "Vector3";
                case "PackedVector4Array": return "Vector4";
                case "PackedColorArray": return "Color";
                default: return null;
            }
        }

        Stmt ParseMatch()
        {
            var tok = Cur;
            ExpectKw("match");
            var subject = ParseExpr();
            ExpectOp(":");
            if (Cur.Kind != TokenKind.Newline) throw Err("expected the match branches on the next lines");
            _i++;
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block of match branches");
            _i++;
            var m = At(new MatchStmt { Subject = subject }, tok);
            while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
            {
                if (Cur.Kind == TokenKind.Newline) { _i++; continue; }
                var ct = Cur;
                PushBlock();
                try
                {
                    var patterns = new List<Pattern> { ParsePattern(subject) };
                    while (AcceptOp(",")) patterns.Add(ParsePattern(subject));
                    var mc = At(new MatchCase { Pattern = patterns.Count == 1 ? patterns[0] : At(new OrPattern { Alternatives = patterns }, ct) }, ct);
                    if (AcceptKw("when")) mc.Guard = ParseExpr();
                    // a dictionary pattern without '..' matches only dictionaries of exactly its keys
                    foreach (var mp in patterns.OfType<MappingPattern>().Where(x => x.Rest == null && IsSimpleSubject(subject)))
                    {
                        var lenCheck = At(new CompareExpr { Left = Call(Name("len", ct), ct, subject), Ops = { "==" }, Comparators = { Int(mp.Keys.Count, ct) } }, ct);
                        mc.Guard = mc.Guard == null ? (Expr)lenCheck : At(new BoolOpExpr { Op = "and", Values = { lenCheck, mc.Guard } }, ct);
                    }
                    foreach (var mp in patterns.OfType<MappingPattern>()) if (mp.Rest == "..") mp.Rest = null;
                    ExpectOp(":");
                    mc.Body = ParseSuite();
                    m.Cases.Add(mc);
                }
                finally { PopBlock(); }
            }
            if (Cur.Kind == TokenKind.Dedent) _i++;
            return m;
        }

        static bool IsSimpleSubject(Expr e) => e is NameExpr || e is AttributeExpr a && IsSimpleSubject(a.Target);

        Pattern ParsePattern(Expr subject)
        {
            var tok = Cur;
            if (AcceptKw("var"))
            {
                string n = ExpectName();
                Declare(n, null);
                return At(new CapturePattern { Name = n }, tok);
            }
            if (Cur.Kind == TokenKind.Name && Cur.Text == "_" ) { _i++; return At(new CapturePattern { Name = null }, tok); }
            if (AcceptOp("["))
            {
                var sp = At(new SequencePattern(), tok);
                while (!IsOp("]"))
                {
                    if (AcceptOp("..")) { sp.StarIndex = sp.Items.Count; sp.Items.Add(At(new CapturePattern { Name = null }, tok)); }
                    else sp.Items.Add(ParsePattern(subject));
                    if (!AcceptOp(",")) break;
                }
                ExpectOp("]");
                return sp;
            }
            if (AcceptOp("{"))
            {
                var mp = At(new MappingPattern(), tok);
                while (Cur.Kind == TokenKind.Newline) _i++;
                while (!IsOp("}"))
                {
                    if (AcceptOp("..")) { mp.Rest = ".."; }
                    else
                    {
                        mp.Keys.Add(ParseExpr());
                        if (AcceptOp(":")) mp.Values.Add(ParsePattern(subject));
                        else mp.Values.Add(At(new CapturePattern { Name = null }, tok));
                    }
                    while (Cur.Kind == TokenKind.Newline) _i++;
                    if (!AcceptOp(",")) break;
                    while (Cur.Kind == TokenKind.Newline) _i++;
                }
                ExpectOp("}");
                return mp;
            }
            // a literal or a constant expression (CONST, Enum.VALUE): compared with ==
            var value = ParseBitOr();
            return At(new ValuePattern { Value = value }, tok);
        }

        List<Stmt> ParseSimpleStatement()
        {
            var tok = Cur;
            if (AcceptKw("pass")) return One(At(new PassStmt(), tok));
            if (AcceptKw("breakpoint")) return One(At(new PassStmt(), tok));
            if (AcceptKw("break")) return One(At(new BreakStmt(), tok));
            if (AcceptKw("continue")) return One(At(new ContinueStmt(), tok));
            if (AcceptKw("return"))
            {
                var r = At(new ReturnStmt(), tok);
                if (Cur.Kind != TokenKind.Newline && Cur.Kind != TokenKind.EndOfFile && Cur.Kind != TokenKind.Dedent && !IsOp(";")) r.Value = ParseExpr();
                return One(r);
            }
            if (IsKw("var") || IsKw("const"))
            {
                _i++;
                var nt = Cur;
                string name = ExpectName();
                Expr typeRef = null, value = null;
                if (AcceptOp(":")) typeRef = ParseTypeRef();
                if (AcceptOp(":=") || AcceptOp("=")) value = ParseExpr();
                string type = typeRef != null ? TypeString(typeRef) : value != null ? TypeOf(value) : null;
                Declare(name, type);
                if (typeRef != null)
                {
                    var v = value ?? DefaultValue(typeRef, nt);
                    if (value != null && NeedsDowncast(value, typeRef)) v = Call(Name("cast", nt), nt, MapType(typeRef), value);
                    return One(At(new AnnAssignStmt { Target = Name(name, nt), Annotation = MapType(typeRef), Value = v }, nt));
                }
                return One(At(new AssignStmt { Targets = { Name(name, nt) }, Value = value ?? At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, nt) }, nt));
            }
            if (IsKw("assert") && Peek().Is("("))
            {
                _i += 2;
                var a = At(new AssertStmt { Test = ParseExpr() }, tok);
                if (AcceptOp(",")) a.Msg = ParseExpr();
                ExpectOp(")");
                return One(a);
            }
            var e = ParseExpr();
            if (IsOp("="))
            {
                _i++;
                var value = ParseExpr();
                if (e is AttributeExpr || e is NameExpr || e is SubscriptExpr) return AssignTo(e, value, tok);
                throw ErrAt(e, "cannot assign to this expression");
            }
            if (Cur.Kind == TokenKind.Op && Cur.Text.Length >= 2 && Cur.Text.EndsWith("=") && Cur.Text != "==" && Cur.Text != "!=" && Cur.Text != "<=" && Cur.Text != ">=" && Cur.Text != ":=")
            {
                string op = Cur.Text.Substring(0, Cur.Text.Length - 1);
                _i++;
                var value = ParseExpr();
                return AugAssign(e, op, value, tok);
            }
            if (e is CallExpr ec && ec.Func is AttributeExpr ea && ea.Target is NameExpr en && en.Id == Rt && ea.Name == "assert_")
                return One(At(new AssertStmt { Test = ec.Args[0].Value, Msg = ec.Args.Count > 1 ? ec.Args[1].Value : null }, tok));
            return One(At(new ExprStmt { Value = e }, tok));
        }

        static List<Stmt> One(Stmt s) => new List<Stmt> { s };

        /// <summary>A typed declaration from a node lookup or instantiation (var l: Label = $Label): GDScript casts implicitly, C# needs a cast.</summary>
        bool NeedsDowncast(Expr value, Expr typeRef)
        {
            string t = TypeString(typeRef);
            if (t == null || IsBuiltinTypeName(t) || GodotStructs.Contains(t) || t.StartsWith("Array") || t.StartsWith("Dictionary") || t.StartsWith("Packed")) return false;
            if (value is CallExpr c && c.Func is AttributeExpr a)
                return a.Name is "get_node" or "get_node_or_null" or "instantiate" or "duplicate" or "get_child" or "get_parent" or "find_child" or "get_owner" or "load" or "get_first_node_in_group";
            return false;
        }

        static bool IsBuiltinTypeName(string t) => t == "int" || t == "float" || t == "bool" || t == "String" || t == "StringName" || t == "NodePath" || t == "Variant";

        static readonly HashSet<string> Components = new HashSet<string> { "x", "y", "z", "w", "r", "g", "b", "a", "h", "s", "v" };

        static readonly HashSet<string> GodotStructs = new HashSet<string>
        {
            "Vector2", "Vector2i", "Vector3", "Vector3i", "Vector4", "Vector4i", "Color", "Rect2", "Rect2i", "Transform2D", "Transform3D",
            "Basis", "Quaternion", "Plane", "AABB", "Projection",
        };

        bool IsLocalName(Expr e) => e is NameExpr n && (n.Id == "self" || LocalType(n.Id, out _));

        /// <summary>
        /// x = v. A field of a value type reached through a property or an element (position.x = 1, points[i].y += 2, self.v.x = 0)
        /// is changed on a copy that is stored back, as GDScript does: C# cannot assign a field of a temporary struct.
        /// </summary>
        List<Stmt> AssignTo(Expr target, Expr value, Token tok)
        {
            if (target is AttributeExpr a && !IsLocalName(a.Target) && !(a.Target is NameExpr) && (Components.Contains(a.Name) || GodotStructs.Contains(TypeOf(a.Target) ?? "")))
            {
                string tmp = "__gd_t" + (_tmp++);
                var list = new List<Stmt>
                {
                    At(new AssignStmt { Targets = { Name(tmp, tok) }, Value = a.Target }, tok),
                    At(new AssignStmt { Targets = { Attr(Name(tmp, tok), a.Name, a) }, Value = value }, tok),
                };
                list.AddRange(AssignTo(a.Target, Name(tmp, tok), tok));
                return list;
            }
            return One(At(new AssignStmt { Targets = { target }, Value = value }, tok));
        }

        List<Stmt> AugAssign(Expr target, string op, Expr value, Token tok)
        {
            // x op= v is x = x op v where the target is cheap to evaluate twice (GDScript arrays: a new array, like a = a + b)
            if (IsSimpleTarget(target))
            {
                if (target is AttributeExpr a && !IsLocalName(a.Target) && !(a.Target is NameExpr) && (Components.Contains(a.Name) || GodotStructs.Contains(TypeOf(a.Target) ?? "")))
                {
                    string tmp = "__gd_t" + (_tmp++);
                    var list = new List<Stmt> { At(new AssignStmt { Targets = { Name(tmp, tok) }, Value = a.Target }, tok) };
                    var field = Attr(Name(tmp, tok), a.Name, a);
                    list.Add(At(new AssignStmt { Targets = { field }, Value = Bin(op, Attr(Name(tmp, tok), a.Name, a), value, tok) }, tok));
                    list.AddRange(AssignTo(a.Target, Name(tmp, tok), tok));
                    return list;
                }
                return One(At(new AssignStmt { Targets = { target }, Value = Bin(op, CloneTarget(target), value, tok) }, tok));
            }
            return One(At(new AugAssignStmt { Target = target, Op = op, Value = value }, tok));
        }

        static bool IsSimpleTarget(Expr e)
        {
            switch (e)
            {
                case NameExpr _: return true;
                case AttributeExpr a: return IsSimpleTarget(a.Target);
                case SubscriptExpr s: return IsSimpleTarget(s.Target) && IsPure(s.Index);
                default: return false;
            }
        }

        static bool IsPure(Expr e)
        {
            switch (e)
            {
                case NameExpr _: case ConstExpr _: return true;
                case AttributeExpr a: return IsPure(a.Target);
                case BinOpExpr b: return IsPure(b.Left) && IsPure(b.Right);
                case UnaryExpr u: return IsPure(u.Operand);
                default: return false;
            }
        }

        static Expr CloneTarget(Expr e)
        {
            switch (e)
            {
                case NameExpr n: return new NameExpr { Id = n.Id, Line = n.Line, Col = n.Col };
                case AttributeExpr a: return new AttributeExpr { Target = CloneTarget(a.Target), Name = a.Name, Line = a.Line, Col = a.Col };
                case SubscriptExpr s: return new SubscriptExpr { Target = CloneTarget(s.Target), Index = CloneTarget(s.Index), Line = s.Line, Col = s.Col };
                case ConstExpr c: return new ConstExpr { Kind = c.Kind, Raw = c.Raw, Str = c.Str, Line = c.Line, Col = c.Col };
                case BinOpExpr b: return new BinOpExpr { Op = b.Op, Left = CloneTarget(b.Left), Right = CloneTarget(b.Right), Line = b.Line, Col = b.Col };
                case UnaryExpr u: return new UnaryExpr { Op = u.Op, Operand = CloneTarget(u.Operand), Line = u.Line, Col = u.Col };
                default: return e;
            }
        }

        BinOpExpr Bin(string op, Expr l, Expr r, object at) => At(new BinOpExpr { Op = op, Left = l, Right = r }, P(at));

        // ───────────────────────────── types ─────────────────────────────

        /// <summary>A type reference: Name, Outer.Inner, Array[T], Dictionary[K, V].</summary>
        Expr ParseTypeRef()
        {
            var tok = Cur;
            Expr t = Name(ExpectName(), tok);
            while (IsOp(".") && Peek().Kind == TokenKind.Name) { _i++; t = Attr(t, ExpectName(), tok); }
            if (AcceptOp("["))
            {
                var args = new List<Expr> { ParseTypeRef() };
                while (AcceptOp(",")) args.Add(ParseTypeRef());
                ExpectOp("]");
                t = At(new SubscriptExpr { Target = t, Index = args.Count == 1 ? args[0] : At(new TupleExpr { Elts = args }, tok) }, tok);
            }
            return t;
        }

        static string TypeString(Expr t)
        {
            switch (t)
            {
                case NameExpr n: return n.Id;
                case AttributeExpr a: return TypeString(a.Target) + "." + a.Name;
                case SubscriptExpr s: return TypeString(s.Target) + "[" + (s.Index is TupleExpr tu ? string.Join(", ", tu.Elts.Select(TypeString)) : TypeString(s.Index)) + "]";
                case ConstExpr c when c.Kind == ConstKind.None: return "void";
                default: return null;
            }
        }

        Expr TypeExprFromDotted(string dotted, Token at)
        {
            var parts = dotted.Split('.');
            Expr e = Name(MapTypeName(parts[0]), at);
            for (int k = 1; k < parts.Length; k++) e = Attr(e, parts[k], at);
            return e;
        }

        string MapTypeName(string n)
        {
            switch (n)
            {
                case "String": case "StringName": case "NodePath": return "str";
                case "Variant": case "Callable": return "Any";
                case "Array": return "list";
                case "Dictionary": return "dict";
                case "void": return "None";
            }
            // a script-local enum is an int; a script-local class has its Typhon name
            for (var c = _cls; c != null; c = c.Outer)
            {
                if (c.Members.TryGetValue(n, out var k) && k == MemberKind.Enum) return "int";
                if (c.Hoisted.TryGetValue(n, out var py)) return py;
                if (c.Inner.TryGetValue(n, out var inner)) return inner.PyName;
                if (c.GdName == n) return c.PyName;
            }
            return n;
        }

        /// <summary>The Typhon annotation for a GDScript type.</summary>
        Expr MapType(Expr t)
        {
            switch (t)
            {
                case NameExpr n:
                    if (n.Id.StartsWith("Packed") && n.Id.EndsWith("Array") && PackedElement(n.Id) is string pe)
                        return At(new SubscriptExpr { Target = Name("list", n), Index = MapType(Name(pe, n)) }, n);
                    if (n.Id == "void") return At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, n);
                    for (var c = _cls; c != null; c = c.Outer)
                        if (c.Members.TryGetValue(n.Id, out var k) && k == MemberKind.Enum) return Name("int", n);
                    return Name(MapTypeName(n.Id), n);
                case AttributeExpr a:
                    // Outer.Inner / Script.Enum: an enum is an int; a class of this file by its Typhon name
                    if (a.Target is NameExpr an && FindClassScope(_cls, an.Id) is ClassScope cs)
                    {
                        if (cs.Members.TryGetValue(a.Name, out var k2) && k2 == MemberKind.Enum) return Name("int", a);
                        if (cs.Inner.TryGetValue(a.Name, out var inner)) return Name(inner.PyName, a);
                    }
                    return Attr(MapType(a.Target), a.Name, a);
                case SubscriptExpr s:
                    return At(new SubscriptExpr { Target = MapType(s.Target), Index = s.Index is TupleExpr tu ? At(new TupleExpr { Elts = tu.Elts.Select(MapType).ToList() }, s) : MapType(s.Index) }, s);
                default: return t;
            }
        }

        /// <summary>The value a typed declaration without initializer starts with (GDScript never leaves a typed variable null).</summary>
        Expr DefaultValue(Expr typeRef, Token at)
        {
            string t = TypeString(typeRef) ?? "";
            if (t == "int") return Int(0, at);
            if (t == "float") return At(new ConstExpr { Kind = ConstKind.Float, Raw = "0.0" }, at);
            if (t == "bool") return At(new ConstExpr { Kind = ConstKind.Bool, Raw = "False" }, at);
            if (t == "String" || t == "StringName" || t == "NodePath") return Str("", at);
            if (t == "Array" || t.StartsWith("Array[") || t.StartsWith("Packed") && t.EndsWith("Array")) return At(new ListExpr(), at);
            if (t == "Dictionary" || t.StartsWith("Dictionary[")) return At(new DictExpr(), at);
            if (t == "Color") return Call(Name("Color", at), at, Int(0, at), Int(0, at), Int(0, at), Int(1, at));
            if (GodotStructs.Contains(t)) return Call(Name(t, at), at);
            for (var c = _cls; c != null; c = c.Outer) if (c.Members.TryGetValue(t, out var k) && k == MemberKind.Enum) return Int(0, at);
            return At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, at);
        }

        // ───────────────────────────── light typing ─────────────────────────────

        /// <summary>
        /// The GDScript type of an expression where the source makes it obvious (literals, declared types, constructors,
        /// a few built-in results). Used only for choices the AST must make up front (for i in n, struct copies,
        /// String/Array methods with ambiguous names); Typhon's inference types everything else.
        /// </summary>
        string TypeOf(Expr e)
        {
            switch (e)
            {
                case ConstExpr c:
                    switch (c.Kind)
                    {
                        case ConstKind.Int: return "int";
                        case ConstKind.Float: return "float";
                        case ConstKind.Str: return "String";
                        case ConstKind.Bool: return "bool";
                        default: return null;
                    }
                case NameExpr n:
                    if (LocalType(n.Id, out var lt)) return lt;
                    for (var c = _cls; c != null; c = c.Outer) if (c.Types.TryGetValue(n.Id, out var mt)) return mt;
                    return null;
                case AttributeExpr a when a.Target is NameExpr s && s.Id == "self":
                    for (var c = _cls; c != null; c = c.BaseScope) if (c.Types.TryGetValue(a.Name, out var st)) return st;
                    return null;
                case AttributeExpr a when a.Target is NameExpr cn && FindClassScope(_cls, cn.Id) is ClassScope cs && cs.Types.TryGetValue(a.Name, out var ct):
                    return ct;
                case AttributeExpr a when a.Target is NameExpr hn && HoistedScope(hn.Id) is ClassScope hs && hs.Types.TryGetValue(a.Name, out var ht):
                    return ht;
                case AttributeExpr a when Components.Contains(a.Name) && GodotStructs.Contains(TypeOf(a.Target) ?? ""):
                    return TypeOf(a.Target).EndsWith("i") ? "int" : "float";
                case ListExpr _: return "Array";
                case DictExpr _: return "Dictionary";
                case ComprehensionExpr _: return "Array";
                case UnaryExpr u when u.Op == "-" || u.Op == "+": return TypeOf(u.Operand);
                case UnaryExpr u when u.Op == "not": return "bool";
                case CompareExpr _: return "bool";
                case BoolOpExpr _: return "bool";
                case BinOpExpr b:
                {
                    string l = TypeOf(b.Left), r = TypeOf(b.Right);
                    if (b.Op == "%" && l == "String") return "String";
                    if (l == "int" && r == "int") return "int";
                    if ((l == "int" || l == "float") && (r == "int" || r == "float")) return "float";
                    if (l == "String" && r == "String" && b.Op == "+") return "String";
                    if (GodotStructs.Contains(l ?? "")) return l;
                    if (GodotStructs.Contains(r ?? "") && (b.Op == "*")) return r;
                    return null;
                }
                case SubscriptExpr s when s.Index is SliceExpr: return TypeOf(s.Target);
                case SubscriptExpr s:
                {
                    string t = TypeOf(s.Target);
                    if (t == null) return null;
                    if (t.StartsWith("Array[") && t.EndsWith("]")) return t.Substring(6, t.Length - 7);
                    if (t.StartsWith("Packed")) return PackedElement(t);
                    if (t == "String") return "String";
                    return null;
                }
                case CallExpr c:
                {
                    if (c.Func is NameExpr fn)
                    {
                        switch (fn.Id)
                        {
                            case "len": case "int": case "ord": case "hash": return "int";
                            case "float": return "float";
                            case "str": case "chr": return "String";
                            case "bool": case "isinstance": return "bool";
                        }
                        if (char.IsUpper(fn.Id[0]) && (GodotStructs.Contains(fn.Id) || HoistedScope(fn.Id) != null || _cls.Hoisted.ContainsValue(fn.Id))) return fn.Id;
                        if (char.IsUpper(fn.Id[0])) return fn.Id;
                    }
                    if (c.Func is AttributeExpr fa && fa.Target is NameExpr rn && rn.Id == Rt)
                    {
                        switch (fa.Name)
                        {
                            case "str": case "substr": case "pad_zeros": case "capitalize": case "left": case "right": return "String";
                            case "find": case "randi_range": case "floori": case "ceili": case "roundi": case "posmod": case "wrapi": case "clampi": case "signi": case "absi": case "mini": case "maxi": case "snappedi": case "str_to_int": case "typeof": return "int";
                            case "randf": case "randf_range": case "randfn": case "sqrt": case "sin": case "cos": case "tan": case "atan2": case "fmod": case "fposmod": case "deg_to_rad": case "rad_to_deg": case "lerpf": case "clampf": case "floorf": case "ceilf": case "roundf": case "absf": case "minf": case "maxf": case "snappedf": case "wrapf": case "exp": case "log": case "pow": case "str_to_float": return "float";
                        }
                    }
                    if (c.Func is NameExpr ln && (ln.Id == "list" || ln.Id == "sorted")) return "Array";
                    if (c.Func is NameExpr dn && dn.Id == "dict") return "Dictionary";
                    if (c.Func is AttributeExpr ma)
                    {
                        string rt = TypeOf(ma.Target);
                        if (ma.Name == "copy" && rt != null && (rt.StartsWith("Array") || rt.StartsWith("Dictionary") || rt.StartsWith("Packed"))) return rt;
                        if (ma.Name == "split") return "PackedStringArray";
                        if ((ma.Name == "upper" || ma.Name == "lower" || ma.Name == "strip" || ma.Name == "replace") && rt == "String") return "String";
                        if (ma.Name == "length" || ma.Name == "length_squared" || ma.Name == "dot" || ma.Name == "distance_to" || ma.Name == "angle" || ma.Name == "cross") return GodotStructs.Contains(rt ?? "") ? "float" : null;
                        if (ma.Name is "normalized" or "rotated" or "limit_length" or "abs" or "floor" or "ceil" or "round" or "clamp" or "lerp" or "move_toward" or "direction_to" or "orthogonal" or "bounce" or "reflect" or "slide")
                            return GodotStructs.Contains(rt ?? "") ? rt : null;
                    }
                    return null;
                }
                default: return null;
            }
        }

        ClassScope HoistedScope(string pyName)
        {
            for (var c = _cls; c != null; c = c.Outer)
                foreach (var inner in c.Inner.Values) if (inner.PyName == pyName) return inner;
            return null;
        }

        // ───────────────────────────── expressions ─────────────────────────────

        Expr ParseExpr()
        {
            var e = ParseTernary();
            while (IsKw("as"))
            {
                var tok = Cur;
                _i++;
                var t = ParseTypeRef();
                e = Call(Name("cast", tok), tok, MapType(t), e);
            }
            return e;
        }

        Expr ParseTernary()
        {
            var body = ParseOr();
            if (IsKw("if"))
            {
                var tok = Cur;
                _i++;
                var test = ParseOr();
                ExpectKw("else");
                var orelse = ParseTernary();
                return At(new IfExpr { Test = test, Body = body, OrElse = orelse }, tok);
            }
            return body;
        }

        Expr ParseOr()
        {
            var e = ParseAnd();
            if (!(IsKw("or") || IsOp("||"))) return e;
            var b = At(new BoolOpExpr { Op = "or", Values = { e } }, e);
            while (AcceptKw("or") || AcceptOp("||")) b.Values.Add(ParseAnd());
            return b;
        }

        Expr ParseAnd()
        {
            var e = ParseNot();
            if (!(IsKw("and") || IsOp("&&"))) return e;
            var b = At(new BoolOpExpr { Op = "and", Values = { e } }, e);
            while (AcceptKw("and") || AcceptOp("&&")) b.Values.Add(ParseNot());
            return b;
        }

        Expr ParseNot()
        {
            var tok = Cur;
            if (AcceptKw("not") || AcceptOp("!")) return At(new UnaryExpr { Op = "not", Operand = ParseNot() }, tok);
            return ParseIn();
        }

        Expr ParseIn()
        {
            var e = ParseComparison();
            while (true)
            {
                var tok = Cur;
                if (IsKw("in")) { _i++; e = At(new CompareExpr { Left = e, Ops = { "in" }, Comparators = { ParseComparison() } }, tok); continue; }
                if (IsKw("not") && Peek().Is("in")) { _i += 2; e = At(new CompareExpr { Left = e, Ops = { "not in" }, Comparators = { ParseComparison() } }, tok); continue; }
                return e;
            }
        }

        static readonly HashSet<string> CompareOps = new HashSet<string> { "==", "!=", "<", ">", "<=", ">=" };

        Expr ParseComparison()
        {
            // GDScript does not chain comparisons: a < b < c is (a < b) < c
            var e = ParseBitOr();
            while (Cur.Kind == TokenKind.Op && CompareOps.Contains(Cur.Text))
            {
                var tok = Cur;
                string op = Cur.Text;
                _i++;
                e = At(new CompareExpr { Left = e, Ops = { op }, Comparators = { ParseBitOr() } }, tok);
            }
            return e;
        }

        Expr ParseBitOr()
        {
            var e = ParseBitXor();
            while (IsOp("|")) { var tok = Cur; _i++; e = Bin("|", e, ParseBitXor(), tok); }
            return e;
        }

        Expr ParseBitXor()
        {
            var e = ParseBitAnd();
            while (IsOp("^")) { var tok = Cur; _i++; e = Bin("^", e, ParseBitAnd(), tok); }
            return e;
        }

        Expr ParseBitAnd()
        {
            var e = ParseShift();
            while (IsOp("&")) { var tok = Cur; _i++; e = Bin("&", e, ParseShift(), tok); }
            return e;
        }

        Expr ParseShift()
        {
            var e = ParseAdditive();
            while (IsOp("<<") || IsOp(">>")) { var tok = Cur; string op = Cur.Text; _i++; e = Bin(op, e, ParseAdditive(), tok); }
            return e;
        }

        Expr ParseAdditive()
        {
            var e = ParseTerm();
            while (IsOp("+") || IsOp("-")) { var tok = Cur; string op = Cur.Text; _i++; e = Bin(op, e, ParseTerm(), tok); }
            return e;
        }

        Expr ParseTerm()
        {
            var e = ParseUnary();
            while (IsOp("*") || IsOp("/") || IsOp("%")) { var tok = Cur; string op = Cur.Text; _i++; e = Bin(op, e, ParseUnary(), tok); }
            return e;
        }

        Expr ParseUnary()
        {
            var tok = Cur;
            if (IsOp("-") || IsOp("+"))
            {
                string op = Cur.Text;
                _i++;
                var operand = ParseUnary();
                // -5 / -2.5: a negative literal
                if (op == "-" && operand is ConstExpr c && (c.Kind == ConstKind.Int || c.Kind == ConstKind.Float) && !c.Raw.StartsWith("-"))
                    return At(new UnaryExpr { Op = "-", Operand = c }, tok);
                return At(new UnaryExpr { Op = op, Operand = operand }, tok);
            }
            return ParseBitNot();
        }

        Expr ParseBitNot()
        {
            var tok = Cur;
            if (AcceptOp("~")) return At(new UnaryExpr { Op = "~", Operand = ParseBitNot() }, tok);
            return ParsePower();
        }

        Expr ParsePower()
        {
            // ** is left-associative in GDScript: 2 ** 3 ** 2 is 64
            var e = ParseIs();
            while (IsOp("**"))
            {
                var tok = Cur;
                _i++;
                var right = IsOp("-") || IsOp("+") ? ParseUnary() : ParseIs();
                e = Bin("**", e, right, tok);
            }
            return e;
        }

        Expr ParseIs()
        {
            var e = ParsePostfix();
            while (IsKw("is"))
            {
                var tok = Cur;
                _i++;
                bool negate = AcceptKw("not");
                var t = ParseTypeRef();
                // x is int / float / String / bool / Array / Dictionary: a run-time test of the value's kind (also for a
                // late-bound value, whatever .NET type holds it); x is Node / an inner class: isinstance
                string tn = TypeString(t is SubscriptExpr ts ? ts.Target : t);
                string builtin = tn switch
                {
                    "int" => "is_int", "float" => "is_float", "bool" => "is_bool", "String" => "is_string", "StringName" => "is_string",
                    "Array" => "is_array", "Dictionary" => "is_dictionary", _ => tn != null && tn.StartsWith("Packed") && tn.EndsWith("Array") ? "is_array" : null,
                };
                Expr test = builtin != null ? RtCall(builtin, tok, e) : Call(Name("isinstance", tok), tok, e, IsTypeTarget(t));
                e = negate ? At(new UnaryExpr { Op = "not", Operand = test }, tok) : test;
            }
            return e;
        }

        Expr IsTypeTarget(Expr t)
        {
            var mapped = MapType(t);
            // x is Array[int]: an Array (the element type is not checked)
            if (mapped is SubscriptExpr s) return s.Target;
            return mapped;
        }

        Expr ParsePostfix()
        {
            var tok = Cur;
            if (AcceptKw("await")) throw ErrAt(At(new PassStmt(), tok), "'await' is not supported yet");
            var e = ParseAtom();
            while (true)
            {
                var t = Cur;
                if (IsOp("."))
                {
                    _i++;
                    var nt = Cur;
                    string name = ExpectName();
                    if (IsOp("("))
                    {
                        var savedHint = _lambdaHint;
                        _lambdaHint = name is "sort_custom" or "filter" or "map" or "any" or "all" or "reduce" ? ElementTypeOf(e) : null;
                        List<Expr> margs;
                        try { margs = ParseCallArgs(); } finally { _lambdaHint = savedHint; }
                        e = MethodCall(e, name, margs, nt);
                        continue;
                    }
                    e = MemberAccess(e, name, nt);
                    continue;
                }
                if (IsOp("("))
                {
                    var args = ParseCallArgs();
                    e = CallValue(e, args, t);
                    continue;
                }
                if (IsOp("["))
                {
                    _i++;
                    var index = ParseExpr();
                    ExpectOp("]");
                    e = At(new SubscriptExpr { Target = e, Index = index }, t);
                    continue;
                }
                return e;
            }
        }

        List<Expr> ParseCallArgs()
        {
            ExpectOp("(");
            var args = new List<Expr>();
            while (!IsOp(")"))
            {
                args.Add(ParseExpr());
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
            return args;
        }

        Expr ParseAtom()
        {
            var tok = Cur;
            switch (tok.Kind)
            {
                case TokenKind.Number:
                {
                    _i++;
                    string raw = tok.Text.Replace("_", "");
                    if (raw.StartsWith("0b") || raw.StartsWith("0B")) raw = Convert.ToInt64(raw.Substring(2), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (raw.EndsWith(".")) raw += "0";
                    return At(new ConstExpr { Kind = IsFloatLiteral(raw) ? ConstKind.Float : ConstKind.Int, Raw = raw }, tok);
                }
                case TokenKind.String:
                    _i++;
                    return Str(tok.Text, tok);
                case TokenKind.Name:
                    return ParseNameAtom();
                case TokenKind.Op:
                    if (tok.Text == "(") { _i++; var inner = ParseExpr(); ExpectOp(")"); return inner; }
                    if (tok.Text == "[") return ParseArray();
                    if (tok.Text == "{") return ParseDict();
                    if (tok.Text == "$") return ParseNodePath();
                    if (tok.Text == "%" && Peek().Kind == TokenKind.Name && Peek().Pos == tok.EndPos)
                    {
                        _i++;
                        string unique = "%" + ExpectName();
                        return Call(Attr(Self(tok), "get_node", tok), tok, Str(unique, tok));
                    }
                    break;
            }
            throw Err("unexpected " + Describe(tok));
        }

        Expr ParseNodePath()
        {
            var tok = ExpectOp("$");
            string path;
            if (Cur.Kind == TokenKind.String) path = _t[_i++].Text;
            else
            {
                var sb = new System.Text.StringBuilder();
                int end = tok.EndPos;
                while ((Cur.Kind == TokenKind.Name || IsOp("/") || IsOp("..") || IsOp(".") || IsOp("%")) && Cur.Pos == end)
                {
                    sb.Append(Cur.Text);
                    end = Cur.EndPos;
                    _i++;
                }
                path = sb.ToString();
            }
            return Call(Attr(Self(tok), "get_node", tok), tok, Str(path, tok));
        }

        Expr ParseArray()
        {
            var tok = ExpectOp("[");
            var l = At(new ListExpr(), tok);
            while (Cur.Kind == TokenKind.Newline) _i++;
            while (!IsOp("]"))
            {
                l.Elts.Add(ParseExpr());
                if (!AcceptOp(",")) break;
            }
            ExpectOp("]");
            return l;
        }

        Expr ParseDict()
        {
            var tok = ExpectOp("{");
            var d = At(new DictExpr(), tok);
            while (!IsOp("}"))
            {
                // Lua style: { name = value }
                if (Cur.Kind == TokenKind.Name && Peek().Is("="))
                {
                    var kt = Cur;
                    d.Keys.Add(Str(ExpectName(), kt));
                    ExpectOp("=");
                }
                else
                {
                    d.Keys.Add(ParseExpr());
                    ExpectOp(":");
                }
                d.Values.Add(ParseExpr());
                if (!AcceptOp(",")) break;
            }
            ExpectOp("}");
            return d;
        }

        Expr ParseLambda(Token tok)
        {
            // func(a, b): return a + b   (a one-line body)
            if (Cur.Kind == TokenKind.Name) ExpectName(); // a named lambda's name
            ExpectOp("(");
            var lam = At(new LambdaExpr(), tok);
            var saved = _fn;
            var fs = new FuncScope { Name = "<lambda>", IsStatic = _fn?.IsStatic ?? false };
            if (_fn != null) fs.Blocks.AddRange(_fn.Blocks.Select(b => new Dictionary<string, string>(b)));
            var ps = new Dictionary<string, string>();
            while (!IsOp(")"))
            {
                string pn = ExpectName();
                string pt = _lambdaHint;
                if (AcceptOp(":")) pt = TypeString(ParseTypeRef());
                lam.Params.Add(pn);
                ps[pn] = pt;
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
            if (AcceptOp("->")) ParseTypeRef();
            ExpectOp(":");
            fs.Blocks.Add(ps);
            _fn = fs;
            try
            {
                if (Cur.Kind == TokenKind.Newline) throw Err("multi-line lambdas are not supported yet (use a method)");
                AcceptKw("return");
                lam.Body = ParseExpr();
            }
            finally { _fn = saved; }
            return lam;
        }

        static readonly HashSet<string> GlobalConstants = new HashSet<string> { "PI", "TAU", "INF", "NAN" };

        Expr ParseNameAtom()
        {
            var tok = Cur;
            string id = tok.Text;
            switch (id)
            {
                case "true": _i++; return At(new ConstExpr { Kind = ConstKind.Bool, Raw = "True" }, tok);
                case "false": _i++; return At(new ConstExpr { Kind = ConstKind.Bool, Raw = "False" }, tok);
                case "null": _i++; return At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, tok);
                case "self": _i++; return Self(tok);
                case "func": _i++; return ParseLambda(tok);
                case "super":
                {
                    _i++;
                    var sup = Call(Name("super", tok), tok);
                    if (IsOp("("))
                    {
                        // super(args): the base's version of the current method (in _init: the base constructor)
                        var args = ParseCallArgs();
                        string m = _fn == null ? "__init__" : _fn.IsInit ? "__init__" : _fn.Name == "_to_string" ? "__str__" : _fn.Name;
                        return Call(Attr(sup, m, tok), tok, args.ToArray());
                    }
                    return sup;
                }
                case "preload":
                case "load":
                    if (Peek().Is("(")) { _i++; var largs = ParseCallArgs(); return RtCall("load", tok, largs.ToArray()); }
                    break;
            }
            _i++;
            if (IsOp("(")) return GlobalOrMemberCall(id, ParseCallArgs(), tok);
            return ResolveName(id, tok);
        }

        /// <summary>A bare name: a local, a member (implicit self / the class), a GDScript global, or an inherited member.</summary>
        Expr ResolveName(string id, Token tok)
        {
            if (_fn != null && _fn.Accessor == id) return Attr(Self(tok), "_" + id + "_value", tok);
            if (LocalType(id, out _)) return Name(id, tok);
            if (MemberRef(id, tok) is Expr member) return member;
            if (GlobalConstants.Contains(id)) return Attr(Name(Rt, tok), id, tok);
            if (InheritedMemberPossible(id)) return Attr(Self(tok), id, tok);
            return Name(id, tok);
        }

        Expr MemberRef(string id, Token tok)
        {
            for (var c = _cls; c != null; c = c.Outer)
            {
                // members of this class and of the classes of this file it extends
                for (var b = c; b != null; b = b.BaseScope)
                {
                    if (!b.Members.TryGetValue(id, out var kind)) continue;
                    bool own = c == _cls;
                    switch (kind)
                    {
                        case MemberKind.Var:
                        case MemberKind.Func:
                        case MemberKind.Signal:
                            if (own && (_fn == null || !_fn.IsStatic)) return Attr(Self(tok), id, tok);
                            break;
                        case MemberKind.StaticVar:
                        case MemberKind.StaticFunc:
                        case MemberKind.Const:
                        case MemberKind.EnumValue:
                            return Attr(Name(b.PyName, tok), id, tok);
                        case MemberKind.Enum:
                        case MemberKind.Class:
                            if (b.Hoisted.TryGetValue(id, out var py)) return Name(py, tok);
                            if (b.Inner.TryGetValue(id, out var inner)) return Name(inner.PyName, tok);
                            break;
                    }
                }
                if (c.GdName == id) return Name(c.PyName, tok);
            }
            return null;
        }

        /// <summary>Can a bare lowercase name be a member the class inherits from its native base (position, add_child)?</summary>
        bool InheritedMemberPossible(string id)
        {
            if (_fn == null || _fn.IsStatic || char.IsUpper(id[0]) || id == Rt) return false;
            for (var b = _cls; b != null; b = b.BaseScope)
                if (b.BaseName != null && b.BaseScope == null) return true;
            return false;
        }

        static readonly HashSet<string> RuntimeFunctions = new HashSet<string>
        {
            "print", "prints", "printt", "printerr", "printraw", "print_rich", "print_verbose", "push_error", "push_warning", "str",
            "abs", "absf", "absi", "sign", "signf", "signi", "min", "max", "minf", "maxf", "mini", "maxi", "clamp", "clampf", "clampi",
            "lerp", "lerpf", "inverse_lerp", "remap", "sqrt", "pow", "exp", "log", "sin", "cos", "tan", "asin", "acos", "atan", "atan2",
            "sinh", "cosh", "tanh", "floor", "floorf", "floori", "ceil", "ceilf", "ceili", "round", "roundf", "roundi", "fmod", "fposmod",
            "posmod", "snapped", "snappedf", "snappedi", "wrap", "wrapf", "wrapi", "deg_to_rad", "rad_to_deg", "is_nan", "is_inf",
            "is_finite", "is_equal_approx", "is_zero_approx", "move_toward", "smoothstep", "lerp_angle", "angle_difference", "step_decimals",
            "nearest_po2", "ease", "randf", "randi", "randf_range", "randi_range", "randfn", "randomize", "seed", "typeof", "type_string",
            "is_instance_valid", "is_same", "var_to_str", "Signal",
        };

        Expr GlobalOrMemberCall(string id, List<Expr> args, Token tok)
        {
            if (LocalType(id, out _)) return CallValue(Name(id, tok), args, tok);
            if (MemberRef(id, tok) is Expr member)
            {
                // Inner.new() is spelled Inner(...) in Python; a member function or callable member is called
                return CallValue(member, args, tok);
            }
            switch (id)
            {
                case "len": case "range": case "hash": return Call(Name(id, tok), tok, args.ToArray());
                case "int": case "float": case "bool": return Call(Name(id, tok), tok, args.ToArray());
                case "char": return Call(Name("chr", tok), tok, args.ToArray());
                case "ord": return Call(Name("ord", tok), tok, args.ToArray());
                case "assert": return RtCall("assert_", tok, args.ToArray());
                case "String": return args.Count == 0 ? Str("", tok) : RtCall("str", tok, args.ToArray());
                case "Array":
                    return args.Count == 0 ? (Expr)At(new ListExpr(), tok) : Call(Name("list", tok), tok, args.ToArray());
                case "Dictionary":
                    return args.Count == 0 ? (Expr)At(new DictExpr(), tok) : Call(Name("dict", tok), tok, args.ToArray());
                case "StringName": case "NodePath": return args.Count == 0 ? Str("", tok) : RtCall("str", tok, args.ToArray());
            }
            if (id.StartsWith("Packed") && id.EndsWith("Array"))
                return args.Count == 0 ? (Expr)At(new ListExpr(), tok) : Call(Name("list", tok), tok, args.ToArray());
            if (RuntimeFunctions.Contains(id)) return RtCall(id, tok, args.ToArray());
            if (id == "emit_signal" && args.Count >= 1 && args[0] is ConstExpr sig && sig.Kind == ConstKind.Str && _fn != null && !_fn.IsStatic)
                return Call(Attr(Attr(Self(tok), sig.Str, tok), "emit", tok), tok, args.Skip(1).ToArray());
            if (InheritedMemberPossible(id)) return Call(Attr(Self(tok), id, tok), tok, args.ToArray());
            return Call(Name(MapTypeName(id), tok), tok, args.ToArray());
        }

        /// <summary>A call of a value: a callable's .call(...), or an ordinary call.</summary>
        Expr CallValue(Expr callee, List<Expr> args, Token tok) => Call(callee, tok, args.ToArray());

        Expr MemberAccess(Expr target, string name, Token tok)
        {
            // Script.CONST / Inner.CONST / Enum.VALUE: through the Typhon name of the class
            if (target is NameExpr n && name != "new")
            {
                var scope = FindClassScope(_cls, n.Id);
                if (scope != null && scope.Hoisted.TryGetValue(name, out var hoisted)) return Name(hoisted, tok);
                if (scope != null && scope.Inner.TryGetValue(name, out var inner)) return Name(inner.PyName, tok);
            }
            // Color.RED and the like stay as written: the emitter maps Godot's constants
            return Attr(target, name, tok);
        }

        /// <summary>recv.m(args): GDScript's Array, Dictionary and String methods map to Python's or the runtime's.</summary>
        Expr MethodCall(Expr recv, string name, List<Expr> args, Token tok)
        {
            if (name == "new") return Call(recv, tok, args.ToArray());
            // a callable value: f.call(x) is f(x)
            if (name == "call" && !(recv is NameExpr cn && char.IsUpper(cn.Id[0]))) return Call(recv, tok, args.ToArray());
            // emit_signal("hit", x) is self.hit.emit(x)
            if (name == "emit_signal" && args.Count >= 1 && args[0] is ConstExpr sig && sig.Kind == ConstKind.Str)
                return Call(Attr(Attr(recv, sig.Str, tok), "emit", tok), tok, args.Skip(1).ToArray());
            if (recv is AttributeExpr sup && sup.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super" && name == "_init") name = "__init__";
            if (recv is CallExpr supc && supc.Func is NameExpr supn && supn.Id == "super")
            {
                if (name == "_init") name = "__init__";
                if (name == "_to_string") name = "__str__";
                return Call(Attr(recv, name, tok), tok, args.ToArray());
            }
            string rt = TypeOf(recv);
            bool isString = rt == "String" || rt == "StringName";
            bool isArray = rt != null && (rt == "Array" || rt.StartsWith("Array[") || rt.StartsWith("Packed") && rt.EndsWith("Array"));
            bool isDict = rt != null && (rt == "Dictionary" || rt.StartsWith("Dictionary["));
            bool known = isString || isArray || isDict;
            bool native = rt != null && !known && rt != "int" && rt != "float" && rt != "bool";
            Expr A(int k) => args[k];
            Expr M(string m, params Expr[] a) => Call(Attr(recv, m, tok), tok, a);
            switch (name)
            {
                // names only GDScript's containers and strings have
                case "size" when args.Count == 0 && !native: return Call(Name("len", tok), tok, recv);
                case "length" when args.Count == 0 && isString: return Call(Name("len", tok), tok, recv);
                case "is_empty" when args.Count == 0 && !native: return At(new CompareExpr { Left = Call(Name("len", tok), tok, recv), Ops = { "==" }, Comparators = { Int(0, tok) } }, tok);
                case "push_back" when args.Count == 1 && !native: return M("append", A(0));
                case "push_front" when args.Count == 1 && !native: return M("insert", Int(0, tok), A(0));
                case "pop_back" when args.Count == 0 && !native: return RtCall("pop_back", tok, recv);
                case "pop_front" when args.Count == 0 && !native: return RtCall("pop_front", tok, recv);
                case "pop_at" when args.Count == 1 && !native: return M("pop", A(0));
                case "append_array" when args.Count == 1 && !native: return M("extend", A(0));
                case "back" when args.Count == 0 && !native: return At(new SubscriptExpr { Target = recv, Index = At(new UnaryExpr { Op = "-", Operand = Int(1, tok) }, tok) }, tok);
                case "front" when args.Count == 0 && !native: return At(new SubscriptExpr { Target = recv, Index = Int(0, tok) }, tok);
                case "has" when args.Count == 1 && !native: return At(new CompareExpr { Left = A(0), Ops = { "in" }, Comparators = { recv } }, tok);
                case "erase" when args.Count == 1 && !native: return RtCall("erase", tok, recv, A(0));
                case "remove_at" when args.Count == 1 && !native: return M("pop", A(0));
                case "to_upper" when args.Count == 0 && !native: return M("upper");
                case "to_lower" when args.Count == 0 && !native: return M("lower");
                case "begins_with" when args.Count == 1 && !native: return M("startswith", A(0));
                case "ends_with" when args.Count == 1 && !native: return M("endswith", A(0));
                case "strip_edges" when args.Count == 0 && !native: return M("strip");
                case "contains" when args.Count == 1 && (isString || !native && !isArray && !isDict): return At(new CompareExpr { Left = A(0), Ops = { "in" }, Comparators = { recv } }, tok);
                case "substr" when !native: return RtCall("substr", tok, new[] { recv }.Concat(args).ToArray());
                case "to_int" when args.Count == 0 && !native: return RtCall("str_to_int", tok, recv);
                case "to_float" when args.Count == 0 && !native: return RtCall("str_to_float", tok, recv);
                case "is_valid_int" when args.Count == 0 && !native: return RtCall("is_valid_int", tok, recv);
                case "is_valid_float" when args.Count == 0 && !native: return RtCall("is_valid_float", tok, recv);
                case "pad_zeros" when args.Count == 1 && !native: return RtCall("pad_zeros", tok, recv, A(0));
                case "lpad" when !native: return M("rjust", A(0), args.Count > 1 ? A(1) : Str(" ", tok));
                case "rpad" when !native: return M("ljust", A(0), args.Count > 1 ? A(1) : Str(" ", tok));
                case "repeat" when args.Count == 1 && !native: return Bin("*", recv, A(0), tok);
                case "capitalize" when args.Count == 0 && !native: return RtCall("capitalize", tok, recv);
                case "left" when args.Count == 1 && isString: return RtCall("left", tok, recv, A(0));
                case "right" when args.Count == 1 && isString: return RtCall("right", tok, recv, A(0));
                case "find" when args.Count >= 1 && !native && !isString: return RtCall("find", tok, new[] { recv }.Concat(args).ToArray());
                case "filter" when args.Count == 1 && !native: return Comprehension(recv, A(0), filter: true, tok);
                case "map" when args.Count == 1 && !native: return Comprehension(recv, A(0), filter: false, tok);
                case "any" when args.Count == 1 && !native: return Call(Name("any", tok), tok, Comprehension(recv, A(0), filter: false, tok));
                case "all" when args.Count == 1 && !native: return Call(Name("all", tok), tok, Comprehension(recv, A(0), filter: false, tok));
                case "reduce" when !native: return RtCall("reduce", tok, new[] { recv }.Concat(args).ToArray());
                case "sort_custom" when args.Count == 1 && !native: return RtCall("sort_custom", tok, recv, A(0));
                case "has_all" when args.Count == 1 && !native: return RtCall("has_all", tok, recv, A(0));
                // names other classes have too: only for receivers known to be strings, arrays or dictionaries
                case "duplicate" when known: return M("copy");
                case "keys" when isDict && args.Count == 0: return Call(Name("list", tok), tok, M("keys"));
                case "values" when isDict && args.Count == 0: return Call(Name("list", tok), tok, M("values"));
                case "max" when isArray && args.Count == 0: return Call(Name("max", tok), tok, recv);
                case "min" when isArray && args.Count == 0: return Call(Name("min", tok), tok, recv);
                case "slice" when known && args.Count >= 1:
                    return At(new SubscriptExpr { Target = recv, Index = At(new SliceExpr { Lower = A(0), Upper = args.Count > 1 ? A(1) : null }, tok) }, tok);
                case "resize" when isArray && args.Count == 1: return RtCall("resize", tok, recv, A(0));
                case "fill" when isArray && args.Count == 1: return RtCall("fill", tok, recv, A(0));
                case "merge" when isDict: return RtCall("merge", tok, new[] { recv }.Concat(args).ToArray());
            }
            return M(name, args.ToArray());
        }

        /// <summary>xs.filter(f) / xs.map(f): a list comprehension (a lambda's body inlined).</summary>
        Expr Comprehension(Expr recv, Expr f, bool filter, Token tok)
        {
            string v = "__gd_x" + (_tmp++);
            Expr applied;
            if (f is LambdaExpr lam && lam.Params.Count == 1) applied = Substitute(lam.Body, lam.Params[0], v);
            else applied = Call(f, tok, Name(v, tok));
            var comp = At(new ComprehensionExpr { Kind = ComprehensionKind.List, Elt = filter ? Name(v, tok) : applied }, tok);
            var gen = new CompFor { Target = Name(v, tok), Iter = recv };
            if (filter) gen.Ifs.Add(applied);
            comp.Generators.Add(gen);
            return comp;
        }

        static Expr Substitute(Expr e, string from, string to)
        {
            Emit.AstWalker.VisitAll(e, n => { if (n is NameExpr ne && ne.Id == from) ne.Id = to; });
            return e;
        }
    }
}
