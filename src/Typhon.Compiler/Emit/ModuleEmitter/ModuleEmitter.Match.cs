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
    // The match statement (PEP 634): patterns as C# conditions and bindings.
    internal sealed partial class ModuleEmitter
    {
        /// <summary>
        /// match → an if/else-if chain over a temporary holding the subject. Each case is one condition: the pattern's tests
        /// with its captures bound inside it (__bind returns true), then the guard, so the guard sees the captures.
        /// </summary>
        void EmitMatch(MatchStmt m)
        {
            string subj = NewTmp("m");
            string subjType = InferType(m.Subject);
            // (a GDScript match on an untyped value: the patterns test an object, a dynamic subject cannot bind 'out var')
            if (_m.IsGdScript && subjType == "dynamic") { _w.Line($"object? {subj} = {EmitValue(m.Subject)};"); subjType = "object"; }
            else _w.Line($"var {subj} = {EmitValue(m.Subject)};");
            bool first = true;
            foreach (var c in m.Cases)
            {
                LineDirective(c);
                string cond = PatternTest(c.Pattern, subj, subjType);
                if (c.Guard != null) cond = Conj(cond, Paren(Cond(c.Guard)));
                _w.Line($"{(first ? "if" : "else if")} ({cond})");
                EmitBlock(c.Body);
                first = false;
            }
        }

        static string Conj(params string[] parts)
        {
            var ps = parts.Where(x => x != "true").ToList();
            if (ps.Contains("false")) return "false";
            return ps.Count == 0 ? "true" : string.Join(" && ", ps.Select(x => x.Contains("||") && !(x.StartsWith("(") && x.EndsWith(")")) ? "(" + x + ")" : x));
        }

        string Bind(string name, string value) => $"((bool){Rt("__bind")}(out {EmitName(new NameExpr { Id = name })}, {value}))";

        static readonly HashSet<string> PrimitiveCsTypes = new HashSet<string> { "long", "int", "double", "float", "bool", "string", "char", "decimal" };

        /// <summary>A C# condition that is true when the value v (of C# type vt, null if unknown) matches the pattern; binds its captures.</summary>
        string PatternTest(Pattern p, string v, string vt)
        {
            switch (p)
            {
                case CapturePattern c: return c.Name == null ? "true" : Bind(c.Name, v);
                case AsPattern a: return Conj(PatternTest(a.Inner, v, vt), Bind(a.Name, v));
                case OrPattern o: return "(" + string.Join(" || ", o.Alternatives.Select(alt => Paren(PatternTest(alt, v, vt)))) + ")";
                case ValuePattern val: return ValueTest(val.Value, v, vt);
                case SequencePattern sq: return SequenceTest(sq, v, vt);
                case MappingPattern mp: return MappingTest(mp, v, vt);
                case ClassPattern cp: return ClassTest(cp, v, vt);
            }
            Unsupported(p, "this pattern");
            return "false";
        }

        string ValueTest(Expr value, string v, string vt)
        {
            if (value is ConstExpr none && none.Kind == ConstKind.None) return $"({v} is null)";
            bool untyped = vt == null || vt == "dynamic" || vt == "object";
            if (value is ConstExpr b && b.Kind == ConstKind.Bool) // compared by identity: 1 does not match True
                return vt == "bool" ? (b.Raw == "True" ? v : "!" + v) : untyped ? $"({v} is {(b.Raw == "True" ? "true" : "false")})" : "false";
            string lt = InferType(value);
            bool primitive = vt != null && PrimitiveCsTypes.Contains(vt) && lt != null && PrimitiveCsTypes.Contains(lt);
            if (primitive && (vt == lt || IsIntegral(vt) && IsIntegral(lt) || (vt == "double" && IsIntegral(lt))))
                return $"({v} == {EmitValue(value)})";
            if (primitive) return vt == "string" || lt == "string" ? "false" : $"({v} == {EmitValue(value)})";
            return $"{Rt("eq")}((object?){Paren(v)}, (object?){Paren(EmitValue(value))})";
        }

        string SequenceTest(SequencePattern sq, string v, string vt)
        {
            int n = sq.Items.Count;
            bool star = sq.StarIndex >= 0;
            if (vt == "string" || vt != null && PrimitiveCsTypes.Contains(vt)) return "false"; // a str is not a sequence for patterns
            if (vt != null && (vt.StartsWith("OrderedDictionary<") || vt.StartsWith("Dictionary<") || vt.StartsWith("IDictionary<") || vt.StartsWith("HashSet<") || vt.StartsWith("frozenset<")))
                return "false"; // nor is a mapping or a set
            var tupleTypes = TupleElementTypes(vt);
            if (tupleTypes != null && !star)
                return tupleTypes.Count != n ? "false" : Conj(sq.Items.Select((it, i) => PatternTest(it, $"{v}.Item{i + 1}", tupleTypes[i])).ToArray());
            string seqVar = v, count, et = null;
            var parts = new List<string>();
            if (vt != null && (vt.StartsWith("List<") || vt.StartsWith("IList<") || vt.StartsWith("IReadOnlyList<") || vt.EndsWith("[]")))
            {
                et = ElementTypeOf(vt);
                count = vt.EndsWith("[]") ? $"{v}.Length" : $"{v}.Count";
                parts.Add($"{v} is not null");
            }
            else
            {
                seqVar = NewTmp("seq");
                parts.Add($"{Rt("__match_seq")}((object?){Paren(v)}, out var {seqVar})"); // (object: out var needs a static call)
                count = $"{seqVar}.Count";
            }
            parts.Add(star ? $"{count} >= {n}" : $"{count} == {n}");
            for (int i = 0; i < n; i++)
                parts.Add(PatternTest(sq.Items[i], !star || i < sq.StarIndex ? $"{seqVar}[{i}]" : $"{seqVar}[{count} - {n - i}]", et));
            if (star && sq.StarName != null) parts.Add(Bind(sq.StarName, $"{Rt("slice")}({seqVar}, {sq.StarIndex}, {count} - {n - sq.StarIndex})"));
            return Conj(parts.ToArray());
        }

        string MappingTest(MappingPattern mp, string v, string vt)
        {
            var parts = new List<string>();
            bool typed = IsDictType(vt);
            string map = v, valueType = typed ? SplitTypeArgs(GenericArg(vt))[1] : null;
            if (typed) parts.Add($"{v} is not null");
            else { map = NewTmp("map"); parts.Add($"{Rt("__match_map")}((object?){Paren(v)}, out var {map})"); }
            for (int i = 0; i < mp.Keys.Count; i++)
            {
                string key = typed ? EmitExpr(mp.Keys[i]) : EmitValue(mp.Keys[i]);
                if (typed) { string tv = NewTmp("mv"); parts.Add($"{map}.TryGetValue({key}, out var {tv})"); parts.Add(PatternTest(mp.Values[i], tv, valueType)); }
                else { parts.Add($"{map}.Contains({key})"); parts.Add(PatternTest(mp.Values[i], $"{map}[{key}]", null)); }
            }
            if (mp.Rest != null) Unsupported(mp, "'**rest' in mapping patterns");
            return Conj(parts.ToArray());
        }

        static readonly Dictionary<string, string> BuiltinPatternTypes = new Dictionary<string, string>
        {
            ["int"] = "long", ["float"] = "double", ["str"] = "string", ["bool"] = "bool",
            ["list"] = "global::System.Collections.IList", ["dict"] = "global::System.Collections.IDictionary",
            ["tuple"] = "global::System.Runtime.CompilerServices.ITuple",
        };

        string ClassTest(ClassPattern cp, string v, string vt)
        {
            string clsName = DottedName(cp.Cls);
            bool builtin = clsName != null && !IsShadowed(clsName) && BuiltinPatternTypes.ContainsKey(clsName);
            string type = builtin ? BuiltinPatternTypes[clsName] : MapType(cp.Cls, cp);
            if (!builtin && clsName != null &&
                TryResolveType(clsName, 0, out var meta, out var pyClass) && pyClass == null && meta != null &&
                (type == "dynamic" || meta.StartsWith("Typhon.Stdlib.ast+", StringComparison.Ordinal)))
                return RuntimeClassTest(cp, v, $"typeof({CsTypeName(meta)})");
            bool untyped = vt == null || vt == "dynamic" || vt == "object";
            string tmp = NewTmp("c");
            var parts = new List<string>();
            if (!untyped && vt == type) { parts.Add($"{v} is not null"); tmp = v; }
            else if (!untyped && clsName == "int" && IsIntegral(vt)) tmp = $"((long){v})";
            else if (!untyped && (PrimitiveCsTypes.Contains(vt) || PrimitiveCsTypes.Contains(type))) return "false";
            else parts.Add($"{v} is {type} {tmp}");
            if (builtin)
            {
                // int(x), str(s): one positional pattern matches the subject itself
                if (cp.Positional.Count > 1) Error(cp, $"{clsName}() accepts at most one positional sub-pattern");
                if (cp.Positional.Count == 1) parts.Add(PatternTest(cp.Positional[0], tmp, type));
            }
            else
            {
                var names = MatchArgs(cp.Cls);
                for (int i = 0; i < cp.Positional.Count; i++)
                {
                    if (names == null || i >= names.Count) { Error(cp, $"{clsName}() accepts {names?.Count ?? 0} positional sub-patterns (__match_args__)"); break; }
                    parts.Add(PatternTest(cp.Positional[i], $"{tmp}.{Esc(names[i])}", MemberType(type, names[i], method: false)));
                }
            }
            for (int i = 0; i < cp.KeywordNames.Count; i++)
                parts.Add(PatternTest(cp.KeywordPatterns[i], $"{tmp}.{Esc(cp.KeywordNames[i])}", builtin ? null : MemberType(type, cp.KeywordNames[i], method: false)));
            return Conj(parts.ToArray());
        }

        string RuntimeClassTest(ClassPattern cp, string subject, string cls)
        {
            string fields = NewTmp("fields");
            string names = string.Join(", ", cp.KeywordNames.Select(CsString));
            var parts = new List<string>
            {
                $"{Rt("__match_class")}((object?){Paren(subject)}, {cls}, {cp.Positional.Count}, [{names}], out var {fields})",
            };
            int i = 0;
            foreach (var pattern in cp.Positional.Concat(cp.KeywordPatterns))
                parts.Add(PatternTest(pattern, $"{fields}[{i++}]", "dynamic"));
            return Conj(parts.ToArray());
        }

        /// <summary>The attribute names positional sub-patterns refer to: __match_args__, or a dataclass's fields in order.</summary>
        List<string> MatchArgs(Expr cls)
        {
            string dotted = DottedName(cls);
            if (dotted == null || !TryResolveType(dotted, 0, out _, out var pyClass) || pyClass == null) return null;
            foreach (var st in pyClass.Body)
                if (st is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr n && n.Id == "__match_args__" && a.Value is TupleExpr t)
                    return t.Elts.OfType<ConstExpr>().Where(c => c.Kind == ConstKind.Str).Select(c => c.Str).ToList();
            if (HasDecorator(pyClass.Decorators, "dataclass"))
                return pyClass.Body.OfType<AnnAssignStmt>().Where(a => a.Target is NameExpr && !(a.Annotation is SubscriptExpr sub && DottedName(sub.Target)?.EndsWith("ClassVar") == true))
                              .Select(a => ((NameExpr)a.Target).Id).ToList();
            return null;
        }
    }
}
