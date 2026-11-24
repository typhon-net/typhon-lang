// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Linq;
using System.Collections.Generic;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        bool IsManagedEnumBase(ClassDef definition, Expr basis, string member)
        {
            var target = basis is SubscriptExpr subscript ? subscript.Target : basis;
            string name = DottedName(target);
            if (name != member && name != "enum." + member) return false;

            var resolver = ClassDeclarationResolver(definition);
            return !resolver.ResolveBase(basis, out var source, out var metadata) ||
                source == null && metadata == "Typhon.Stdlib.enum+" + member;
        }

        bool IsStringEnum(ClassDef cls) => cls.Bases.Any(basis => IsManagedEnumBase(cls, basis, "StrEnum")) ||
            cls.Decorators.Any(Decorators.IsStringEnumDecorator);

        bool IsIntegerFlag(ClassDef cls) => cls.Bases.Any(basis => IsManagedEnumBase(cls, basis, "IntFlag"));

        bool IsObjectIntegerEnum(ClassDef cls) => IsIntegerFlag(cls) ||
            cls.ExportsIntegerEnumMembers || cls.Decorators.Any(Decorators.IsIntegerEnumDecorator) || IsIntEnum(cls) && cls.Body.Any(statement => statement is FunctionDef);

        bool IsPlainObjectEnum(ClassDef cls) => cls.Decorators.Any(Decorators.IsPlainEnumDecorator) || TupleEnumBasis(cls) != null;

        Expr TupleEnumBasis(ClassDef definition)
        {
            var bases = definition.BasesBeforeFlattening ?? definition.Bases;
            if (bases.Count != 2) return null;
            var resolver = ClassDeclarationResolver(definition);
            if (!resolver.ResolveBase(bases[0], out var tuple, out _) || tuple == null ||
                resolver.NamedTupleDeclaration(tuple) == null) return null;
            return IsManagedEnumBase(definition, bases[1], "Enum") ||
                resolver.ResolveBase(bases[1], out var source, out string metadata) && source == null && metadata == "Typhon.Stdlib.enum+Enum"
                ? bases[0] : null;
        }

        void EmitObjectEnum(ClassDef cls, bool integer)
        {
            string name = Esc(cls.Name);
            bool plain = IsPlainObjectEnum(cls);
            var tupleBasis = plain ? TupleEnumBasis(cls) : null;
            string tupleType = tupleBasis != null ? MapType(tupleBasis, cls, preserveClrType: true) : null;
            var body = tupleBasis != null ? cls.Body.Where(statement => !statement.IsFlattenedClassMember).ToList() : cls.Body;
            string runtimeClass = integer ? "PyIntegerEnum" : plain ? "PyPlainEnum" : "PyStrEnum";
            bool flags = IsIntegerFlag(cls);
            string keyType = integer ? "global::System.Numerics.BigInteger" : "string";
            var context = new ClassContext { Def = cls, Name = cls.Name, CsSelfType = name };
            var previous = _cls;
            _cls = context;
            EmitDocComment(cls.Body);
            LineDirective(cls);
            EmitClassSource(cls.Body, cls.FirstSourceLine);
            EmitConditionalModuleClassAttribute(cls);
            EmitAttributes(cls.Decorators, decorator => decorator is "_simple_enum" or "enum._simple_enum");
            bool customRepr = body.OfType<FunctionDef>().Any(method => method.Name == "__repr__");
            bool customString = body.OfType<FunctionDef>().Any(method => method.Name == "__str__");
            string repr = (tupleType != null || !cls.ExportsIntegerEnumMembers && customRepr)
                ? ", global::Typhon.Runtime.IPyRepr" : "";
            string baseType = tupleType != null ? $"{tupleType}, global::Typhon.Runtime.IPyPlainEnum" :
                flags ? "global::Typhon.Stdlib.@enum.IntFlag" : plain ? "global::Typhon.Stdlib.@enum.Enum" : $"global::Typhon.Runtime.{runtimeClass}";
            _w.Line($"internal partial class {name} : {baseType}{repr}");
            _w.Open();
            var bases = tupleBasis == null ? new List<Expr>() : new List<Expr> { tupleBasis };
            if (tupleBasis == null) _w.Line($"private {name}() {{ }}");
            else
            {
                EmitInheritedConstructors(context, bases);
                _w.Line("public object? _value_;");
                _w.Line("public string _name_ = \"\";");
                _w.Line("public dynamic? value => _value_;");
                _w.Line("public string name => _name_;");
                string qualifiedName = $"{CsString(cls.Name + ".")} + name";
                if (!customString) _w.Line($"public override string ToString() => {qualifiedName};");
                if (!customRepr)
                    _w.Line($"public new string __repr__() => global::Typhon.Runtime.PyPlainEnum.Representation({qualifiedName}, value);");
            }
            if (cls.ExportsIntegerEnumMembers)
                _w.Line($"protected override string EnumReprModule => {CsString(PythonModuleName.Split('.').Last())};");
            if (plain) _w.Line($"internal static readonly List<{name}> __unique_members = new();");
            else _w.Line($"internal static readonly OrderedDictionary<{keyType}, {name}> _value2member_map_ = new();");
            _w.Line($"private static readonly OrderedDictionary<string, {name}> __member_map = new();");
            _w.Line("public static global::Typhon.Stdlib.types.MappingProxyType __members__ => new(__member_map);");

            string runtimeBase = flags ? "Typhon.Stdlib.enum+IntFlag" : plain ? "Typhon.Stdlib.enum+Enum" : "Typhon.Runtime." + runtimeClass;
            foreach (var field in CollectImplicitFields(cls))
            {
                string member = field.Key;
                if (_r.HasMember(runtimeBase, member) || tupleType != null && IsNamedTupleField(cls, member)) continue;
                string type = field.Value.Key == null ? "dynamic" : MapType(field.Value.Key, field.Value.Value.Key);
                context.Members.Add(member);
                context.FieldTypes[member] = type;
                _w.Line($"{SurfaceVisibility(Visibility(member, false), type)} {type} {Esc(member)};");
            }

            var methods = body.OfType<FunctionDef>().ToList();
            RecordPropertyTypes(methods, context);
            var propertyDone = new HashSet<string>();
            var allocator = methods.FirstOrDefault(method => method.Name == "__new__");
            if (allocator != null) EmitFunction(allocator, isModuleLevel: false);
            _w.Line($"private static {name} __enum_member(string name, object?[] args)");
            _w.Open();
            if (allocator == null && tupleType != null)
            {
                _w.Line($"var member = ({name}){Rt("ConstructClr")}(typeof({name}), args)!;");
                _w.Line($"member._value_ = {Rt("__invoke")}(typeof({tupleType}), args);");
            }
            else if (allocator == null)
            {
                string value = integer || plain ? "args[0]" : "(string)args[0]!";
                _w.Line($"var member = ({name})global::Typhon.Runtime.{runtimeClass}.Allocate(typeof({name}), {value});");
            }
            else
                _w.Line($"var member = ({name})new global::Typhon.Runtime.PyBoundMethod(typeof({name}), \"__new__\", includeNonPublic: true).__call__([typeof({name}), .. args]);");
            string key = integer ? "member.Integer" : "member.value";
            if (plain)
                _w.Line($"if (__unique_members.FirstOrDefault(existing => {Rt("eq")}(existing.value, {key})) is {name} alias) member = alias;");
            else _w.Line($"if (_value2member_map_.TryGetValue({key}, out var existing)) member = existing;");
            _w.Line("else");
            _w.Open();
            _w.Line("member._name_ = name;");
            _w.Line(plain ? "__unique_members.Add(member);" : $"_value2member_map_[{key}] = member;");
            _w.Close();
            _w.Line("__member_map[name] = member;");
            _w.Line("return member;");
            _w.Close();

            var reads = new HashSet<string>();
            foreach (var assignment in body.OfType<AssignStmt>())
                AstWalker.VisitAll(assignment.Value, node =>
                {
                    if (node is NameExpr read) reads.Add(read.Id);
                }, includeNestedScopes: false);
            var originalArguments = new Dictionary<string, string>();
            foreach (var statement in body)
            {
                if (statement is AssignStmt { Targets.Count: 1 } assignment && assignment.Targets[0] is NameExpr field)
                {
                    LineDirective(assignment);
                    string arguments;
                    if (assignment.Value is NameExpr alias && originalArguments.TryGetValue(alias.Id, out string earlier))
                    {
                        arguments = earlier;
                        if (reads.Contains(field.Id))
                        {
                            originalArguments[field.Id] = earlier;
                            context.LateClassSlots[field.Id] = context.LateClassSlots[alias.Id];
                        }
                    }
                    else
                    {
                        var tuple = (!plain || allocator != null || tupleBasis != null) ? assignment.Value as TupleExpr : null;
                        IEnumerable<Expr> values = tuple != null ? tuple.Elts : new[] { assignment.Value };
                        bool previousInitializer = _classFieldInitializer;
                        _classFieldInitializer = true;
                        try
                        {
                            var args = integer && assignment.Value is CallExpr { Args.Count: 0 } auto &&
                                DottedName(auto.Func) is "auto" or "enum.auto"
                                ? new[] { $"global::Typhon.Runtime.{(flags ? "PyIntegerFlag" : "PyIntegerEnum")}.NextAutoValue(_value2member_map_.Keys)" }
                                : values.Select(value => $"(object?){Paren(EmitLooseValue(value))}");
                            arguments = $"[{string.Join(", ", args)}]";
                        }
                        finally { _classFieldInitializer = previousInitializer; }

                        if (reads.Contains(field.Id))
                        {
                            // The next RHS sees the original class-body value before Enum converts it into a member.
                            string storage = NewTmp("__typhon_enum_args");
                            _w.Line($"private static readonly object?[] {storage} = {arguments};");
                            originalArguments[field.Id] = storage;
                            arguments = storage;
                            string binding = NewTmp("__typhon_enum_value");
                            string originalValue = tuple == null ? $"{storage}[0]" : $"new global::Typhon.Runtime.PyTuple<object?>({storage})";
                            _w.Line($"private static readonly object? {binding} = {originalValue};");
                            context.LateClassSlots[field.Id] = binding;
                        }
                    }
                    _w.Line($"public static readonly {name} {Esc(field.Id)} = __enum_member({CsString(field.Id)}, {arguments});");
                    context.FieldTypes[field.Id] = name;
                    context.Members.Add(field.Id);
                }
                else if (statement is FunctionDef method && method != allocator &&
                         (!cls.ExportsIntegerEnumMembers || method.Name != "__repr__"))
                {
                    if (!EmitPropertyMember(method, methods, propertyDone, context, bases))
                        EmitMethod(method, context, bases);
                }
                else if (!(statement is FunctionDef || statement is PassStmt || statement is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }))
                    Unsupported(statement, $"this statement in a simple {(integer ? "IntEnum" : plain ? "Enum" : "StrEnum")}");
            }
            if (flags) _w.Line("internal static readonly bool __enum_complete = true;");
            EmitDunderBridges(methods.Where(method => method != allocator).ToList(), context);
            if (Obs != null) Obs.Fields[TypeInference.ClassKey(_m, cls.Name)] = context.FieldTypes;
            _w.Close();
            _cls = previous;
        }
    }
}
