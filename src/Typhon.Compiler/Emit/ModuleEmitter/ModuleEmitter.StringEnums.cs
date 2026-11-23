// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Linq;
using System.Collections.Generic;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        static bool IsStringEnum(ClassDef cls) => cls.Bases.Any(IsBase) ||
            cls.Decorators.Any(Decorators.IsStringEnumDecorator);

        static bool IsBase(Expr expression) => DottedName(expression) is "StrEnum" or "enum.StrEnum";

        static bool IsObjectIntegerEnum(ClassDef cls) => cls.Decorators.Any(Decorators.IsIntegerEnumDecorator);

        void EmitObjectEnum(ClassDef cls, bool integer)
        {
            string name = Esc(cls.Name);
            string runtimeClass = integer ? "PyIntegerEnum" : "PyStrEnum";
            string keyType = integer ? "global::System.Numerics.BigInteger" : "string";
            var context = new ClassContext { Def = cls, Name = cls.Name, CsSelfType = name };
            var previous = _cls;
            _cls = context;
            EmitDocComment(cls.Body);
            LineDirective(cls);
            EmitClassSource(cls.Body, cls.Line);
            EmitConditionalModuleClassAttribute(cls);
            EmitAttributes(cls.Decorators, decorator => decorator is "_simple_enum" or "enum._simple_enum");
            _w.Line($"internal partial class {name} : global::Typhon.Runtime.{runtimeClass}");
            _w.Open();
            _w.Line($"private {name}() {{ }}");
            _w.Line($"internal static readonly OrderedDictionary<{keyType}, {name}> _value2member_map_ = new();");

            var allocator = cls.Body.OfType<FunctionDef>().FirstOrDefault(method => method.Name == "__new__");
            if (allocator != null) EmitFunction(allocator, isModuleLevel: false);
            _w.Line($"private static {name} __enum_member(string name, object?[] args)");
            _w.Open();
            if (allocator == null)
            {
                string value = integer ? "args[0]" : "(string)args[0]!";
                _w.Line($"var member = ({name})global::Typhon.Runtime.{runtimeClass}.Allocate(typeof({name}), {value});");
            }
            else
                _w.Line($"var member = ({name})new global::Typhon.Runtime.PyBoundMethod(typeof({name}), \"__new__\", includeNonPublic: true).__call__([typeof({name}), .. args]);");
            string key = integer ? "member.Integer" : "member.value";
            _w.Line($"if (_value2member_map_.TryGetValue({key}, out var existing)) return existing;");
            _w.Line("member._name_ = name;");
            _w.Line($"_value2member_map_[{key}] = member;");
            _w.Line("return member;");
            _w.Close();

            foreach (var statement in cls.Body)
            {
                if (statement is AssignStmt { Targets.Count: 1 } assignment && assignment.Targets[0] is NameExpr field)
                {
                    IEnumerable<Expr> values = assignment.Value is TupleExpr tuple ? tuple.Elts : new[] { assignment.Value };
                    var args = integer && assignment.Value is CallExpr { Args.Count: 0 } auto &&
                        DottedName(auto.Func) is "auto" or "enum.auto"
                        ? new[] { "global::Typhon.Runtime.PyIntegerEnum.NextAutoValue(_value2member_map_.Keys)" }
                        : values.Select(value => $"(object?){Paren(EmitLooseValue(value))}");
                    LineDirective(assignment);
                    _w.Line($"public static readonly {name} {Esc(field.Id)} = __enum_member({CsString(field.Id)}, [{string.Join(", ", args)}]);");
                    context.FieldTypes[field.Id] = name;
                    context.Members.Add(field.Id);
                }
                else if (statement is FunctionDef method && method != allocator)
                    EmitMethod(method, context, new List<Expr>());
                else if (!(statement is FunctionDef || statement is PassStmt || statement is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }))
                    Unsupported(statement, integer ? "this statement in a simple IntEnum" : "this statement in a StrEnum");
            }
            if (Obs != null) Obs.Fields[TypeInference.ClassKey(_m, cls.Name)] = context.FieldTypes;
            _w.Close();
            _cls = previous;
        }
    }
}
