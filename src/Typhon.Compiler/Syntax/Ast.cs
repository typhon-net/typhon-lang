// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Typhon.Compiler.Syntax
{
    public abstract class Node
    {
        public int Line;
        public int Col;
        public int StartOffset;
        public int EndOffset;
    }

    // ───────────────────────────── Expressions ─────────────────────────────

    public abstract class Expr : Node { }

    public sealed class NameExpr : Expr { public string Id; }

    public enum ConstKind { Int, Float, Str, Bool, None, Ellipsis, Bytes, Imaginary }

    public sealed class ConstExpr : Expr
    {
        public ConstKind Kind;
        public string Raw;    // numbers: source text; bool: "True"/"False"
        public string Str;    // decoded string value
    }

    public sealed class FStringPart
    {
        public string Literal;     // either a literal...
        public Expr Expr;          // ...or an interpolation hole
        public char Conversion;    // 'r', 's', 'a' or '\0'
        public string FormatSpec;  // null if none
        public List<FStringPart> SpecParts; // a format spec with replacement fields of its own (f"{x:>{width}}"), else null
        public string ExprText;    // the hole's expression as written (a t-string's Interpolation.expression)
    }

    public sealed class FStringExpr : Expr { public List<FStringPart> Parts = new List<FStringPart>(); public bool IsTemplate; }

    public sealed class AttributeExpr : Expr { public Expr Target; public string Name; }

    public sealed class Arg
    {
        public string Name;       // keyword name, or null
        public Expr Value;
        public bool Star;         // *args
        public bool DoubleStar;   // **kwargs
    }

    public sealed class CallExpr : Expr { public Expr Func; public List<Arg> Args = new List<Arg>(); }

    public sealed class SubscriptExpr : Expr { public Expr Target; public Expr Index; }

    public sealed class SliceExpr : Expr { public Expr Lower, Upper, Step; }

    public sealed class BinOpExpr : Expr { public string Op; public Expr Left, Right; }

    public sealed class UnaryExpr : Expr { public string Op; public Expr Operand; }

    public sealed class BoolOpExpr : Expr { public string Op; public List<Expr> Values = new List<Expr>(); }

    public sealed class CompareExpr : Expr
    {
        public Expr Left;
        public List<string> Ops = new List<string>();
        public List<Expr> Comparators = new List<Expr>();
    }

    public sealed class IfExpr : Expr { public Expr Test, Body, OrElse; }

    public sealed class LambdaExpr : Expr
    {
        public List<string> Params = new List<string>();
        public string VarArgs, KwArgs;
        public List<Expr> Defaults = new List<Expr>();
        public Expr Body;
        public bool RequiresArgumentBinding => VarArgs != null || KwArgs != null || Defaults.Count != 0;
        public IEnumerable<string> ParameterNames()
        {
            foreach (var p in Params) yield return p;
            if (VarArgs != null) yield return VarArgs;
            if (KwArgs != null) yield return KwArgs;
        }
    }

    public sealed class TupleExpr : Expr { public List<Expr> Elts = new List<Expr>(); public bool Parenthesized; }

    public sealed class ListExpr : Expr { public List<Expr> Elts = new List<Expr>(); }

    public sealed class SetExpr : Expr { public List<Expr> Elts = new List<Expr>(); }

    public sealed class DictExpr : Expr { public List<Expr> Keys = new List<Expr>(); public List<Expr> Values = new List<Expr>(); }

    public enum ComprehensionKind { List, Generator, Set, Dict }

    public sealed class CompFor { public Expr Target; public Expr Iter; public List<Expr> Ifs = new List<Expr>(); public bool IsAsync; }

    public sealed class ComprehensionExpr : Expr
    {
        public ComprehensionKind Kind;
        public Expr Elt;        // for dicts: the key
        public Expr ValueElt;   // dicts only
        public List<CompFor> Generators = new List<CompFor>();
    }

    public sealed class AwaitExpr : Expr { public Expr Value; }

    public sealed class YieldExpr : Expr { public Expr Value; public bool From; }

    public sealed class StarredExpr : Expr { public Expr Value; }

    /// <summary>Assignment expression (walrus): <c>(name := value)</c>.</summary>
    public sealed class NamedExpr : Expr { public NameExpr Target; public Expr Value; }

    // ───────────────────────────── Statements ─────────────────────────────

    public abstract class Stmt : Node { }

    public sealed class ExprStmt : Stmt { public Expr Value; }

    public sealed class AssignStmt : Stmt { public List<Expr> Targets = new List<Expr>(); public Expr Value; }

    public sealed class AugAssignStmt : Stmt { public Expr Target; public string Op; public Expr Value; }

    public sealed class AnnAssignStmt : Stmt { public Expr Target; public Expr Annotation; public Expr Value; }

    public sealed class PassStmt : Stmt { }
    public sealed class BreakStmt : Stmt { }
    public sealed class ContinueStmt : Stmt { }

    public sealed class ReturnStmt : Stmt { public Expr Value; }

    public sealed class RaiseStmt : Stmt { public Expr Exc; public Expr Cause; }

    public sealed class GlobalStmt : Stmt { public List<string> Names = new List<string>(); public bool Nonlocal; }

    public sealed class AssertStmt : Stmt { public Expr Test; public Expr Msg; }

    public sealed class DelStmt : Stmt { public List<Expr> Targets = new List<Expr>(); }

    public sealed class ImportAlias : Node { public string Name; public string AsName; }

    public sealed class ImportStmt : Stmt { public List<ImportAlias> Names = new List<ImportAlias>(); public bool IsLazy; }

    public sealed class ImportFromStmt : Stmt
    {
        public string Module;   // dotted, may be empty for "from . import x"
        public int Level;       // number of leading dots
        public bool IsLazy;
        public List<ImportAlias> Names = new List<ImportAlias>(); // Name "*" for wildcard
    }

    public sealed class IfStmt : Stmt { public Expr Test; public List<Stmt> Body = new List<Stmt>(); public List<Stmt> OrElse = new List<Stmt>(); }

    public sealed class WhileStmt : Stmt { public Expr Test; public List<Stmt> Body = new List<Stmt>(); public List<Stmt> OrElse = new List<Stmt>(); }

    public sealed class ForStmt : Stmt
    {
        public Expr Target; public Expr Iter; public bool IsAsync;
        public List<Stmt> Body = new List<Stmt>(); public List<Stmt> OrElse = new List<Stmt>();
    }

    public sealed class ExceptHandler : Node { public Expr Type; public string Name; public List<Stmt> Body = new List<Stmt>(); }

    public sealed class TryStmt : Stmt
    {
        public List<Stmt> Body = new List<Stmt>();
        public List<ExceptHandler> Handlers = new List<ExceptHandler>();
        public List<Stmt> OrElse = new List<Stmt>();
        public List<Stmt> FinalBody = new List<Stmt>();
    }

    public sealed class WithItem { public Expr Context; public Expr Var; }

    public sealed class WithStmt : Stmt { public List<WithItem> Items = new List<WithItem>(); public List<Stmt> Body = new List<Stmt>(); public bool IsAsync; }

    /// <summary>match subject: case pattern [if guard]: body ...</summary>
    public sealed class MatchStmt : Stmt { public Expr Subject; public List<MatchCase> Cases = new List<MatchCase>(); }

    public sealed class MatchCase : Node { public Pattern Pattern; public Expr Guard; public List<Stmt> Body = new List<Stmt>(); }

    /// <summary>Structural pattern of a match case (PEP 634).</summary>
    public abstract class Pattern : Node { }

    /// <summary>A literal or a dotted constant (Color.RED): matches when equal. Also None/True/False (compared by identity).</summary>
    public sealed class ValuePattern : Pattern { public Expr Value; }

    /// <summary>A bare name binds the subject; '_' (Name == null) matches anything without binding.</summary>
    public sealed class CapturePattern : Pattern { public string Name; }

    /// <summary>pattern as name</summary>
    public sealed class AsPattern : Pattern { public Pattern Inner; public string Name; }

    /// <summary>p1 | p2 | ...</summary>
    public sealed class OrPattern : Pattern { public List<Pattern> Alternatives = new List<Pattern>(); }

    /// <summary>[p1, p2, *rest] or (p1, p2): a sequence of that length; StarIndex marks the *rest element (-1 if none).</summary>
    public sealed class SequencePattern : Pattern { public List<Pattern> Items = new List<Pattern>(); public int StarIndex = -1; public string StarName; }

    /// <summary>{key: pattern, **rest}</summary>
    public sealed class MappingPattern : Pattern { public List<Expr> Keys = new List<Expr>(); public List<Pattern> Values = new List<Pattern>(); public string Rest; }

    /// <summary>Cls(p1, attr=p2): an instance of Cls whose attributes match.</summary>
    public sealed class ClassPattern : Pattern
    {
        public Expr Cls;
        public List<Pattern> Positional = new List<Pattern>();
        public List<string> KeywordNames = new List<string>();
        public List<Pattern> KeywordPatterns = new List<Pattern>();
    }

    public enum ParamKind { Normal, VarArgs, KwArgs }

    public sealed class Param : Node
    {
        public string Name;
        public Expr Annotation;
        public Expr Default;
        public ParamKind Kind;
        public bool PositionalOnly;
        public bool KeywordOnly;
    }

    public sealed class TypeParam { public string Name; public Expr Bound; }

    public sealed class FunctionDef : Stmt
    {
        public string Name;
        public List<TypeParam> TypeParams = new List<TypeParam>();
        public List<Param> Params = new List<Param>();
        public Expr Returns;
        public List<Stmt> Body = new List<Stmt>();
        public List<Expr> Decorators = new List<Expr>();
        public bool IsAsync;
        public bool IsCachedProperty;
        internal bool UsedInClassBody;
        internal bool IsMissingMethodStub;
        /// <summary>The name Python sees (its __name__) when Name was changed: a decorated function's undecorated body.</summary>
        public string PyName;
    }

    internal sealed class ConditionalClassBinding : Stmt { public ClassDef Definition; }

    public sealed class ClassDef : Stmt
    {
        internal bool ConditionalModuleDefinition;
        internal string PyName;
        public string Name;
        public List<TypeParam> TypeParams = new List<TypeParam>();
        public List<Expr> Bases = new List<Expr>();
        public Expr Metaclass;
        public List<Arg> ClassKeywords = new List<Arg>();
        public List<Stmt> Body = new List<Stmt>();
        public List<Expr> Decorators = new List<Expr>();
        /// <summary>Set for a class made from namedtuple(...) / typing.NamedTuple: its fields in order (it behaves like a tuple of them).</summary>
        public List<string> NamedTupleFields;
        /// <summary>The namedtuple subclass provides its own allocation logic, lowered to its constructor.</summary>
        public bool NamedTupleCustomNew;
        /// <summary>The class statement applies object.__new__ and binds the uninitialized instance to the class name.</summary>
        public bool UninitializedSingleton;
    }

    public sealed class ModuleAst
    {
        public string Path;
        public List<Stmt> Body = new List<Stmt>();
        /// <summary>Global names that a failed optional import would have bound.</summary>
        public HashSet<string> FailedModuleImports = new HashSet<string>();
        /// <summary>Decorators that name Python callables but stand where Typhon does not apply them (methods, classes).</summary>
        public HashSet<Expr> UnappliedDecorators = new HashSet<Expr>();
        /// <summary>Methods decorated in Python: their decorators, outermost first (the class emits the decorated method).</summary>
        public Dictionary<FunctionDef, List<Expr>> MethodDecorators = new Dictionary<FunctionDef, List<Expr>>();
    }
}
