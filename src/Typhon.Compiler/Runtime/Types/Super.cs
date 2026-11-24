// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    internal static partial class builtins
    {
        internal sealed class super : DynamicObject, IPyRepr
        {
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            public Type __thisclass__ { get; }
            public object? __self__ { get; }
            public Type? __self_class__ { get; }

            public super(object? type, object? @object = null)
            {
                __thisclass__ = type as Type ?? throw new InvalidCastException("super() argument 1 must be a type");
                __self__ = @object;
                if (@object == null) return;
                __self_class__ = @object as Type ?? (@object is PyLocalClassInstance local ? local.PythonType : @object.GetType());
                bool Compatible() => Builtins.DirectSubclass(__self_class__, __thisclass__);
                if (!Compatible() && @object is Type target &&
                    PyMetaclasses.IsMetaclass(__thisclass__))
                    __self_class__ = PyMetaclasses.Of(target);
                if (!Compatible())
                    throw new InvalidCastException("super(type, obj): obj must be an instance or subtype of type");
            }

            public dynamic __get__(object? instance, object? owner = null) =>
                __self__ == null && instance != null ? new super(__thisclass__, instance) : this;

            public override bool TryGetMember(GetMemberBinder binder, out object? result) => TryResolve(binder.Name, out result);

            public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
            {
                if (!TryResolve(binder.Name, out var member))
                    throw new MissingMemberException($"'super' object has no attribute '{binder.Name}'");
                args ??= [];
                var names = binder.CallInfo.ArgumentNames;
                int positional = args.Length - names.Count;
                var keywords = names.Count == 0 ? null : new Dictionary<string, object?>();
                for (int i = 0; i < names.Count; i++) keywords![names[i]] = args[positional + i];
                result = Builtins.__invoke_kw(member, args[..positional], keywords);
                return true;
            }

            internal bool TryResolve(string name, out object? result)
            {
                result = name switch
                {
                    "__thisclass__" => __thisclass__,
                    "__self__" => __self__,
                    "__self_class__" => __self_class__,
                    "__class__" => typeof(super),
                    _ => null,
                };
                if (name is "__thisclass__" or "__self__" or "__self_class__" or "__class__") return true;

                object? receiver = __self__ is Type ? null : __self__;
                if (__self_class__ != null)
                {
                    if (name == "__new__" && PyNamedTupleAllocator.TryGet(__thisclass__, inlinedBase: true, out result)) return true;
                    foreach (Type owner in PyMetaclasses.MethodOrder(__self_class__).SkipWhile(type => type != __thisclass__).Skip(1))
                    {
                        if (owner == typeof(object) && name == "__init_subclass__")
                        {
                            result = new PyClosure(name, (args, keywords) =>
                            {
                                string ownerName = __self_class__ is PyLocalDataClass local ? local.QualName : Builtins.PyTypeName(__self_class__);
                                string method = ownerName + ".__init_subclass__()";
                                if (keywords?.Count > 0) throw new InvalidCastException(method + " takes no keyword arguments");
                                if (args.Length > 0) throw new InvalidCastException($"{method} takes no arguments ({args.Length} given)");
                                return null;
                            });
                            return true;
                        }
                        if (owner == typeof(object) && __self__ is Type checkTarget && PyMetaclasses.IsMetaclass(__thisclass__) &&
                            PyMetaclasses.TypeSlot(name) is PyClosure typeCheck)
                        {
                            result = new types.MethodType(typeCheck, checkTarget);
                            return true;
                        }
                        if (owner == typeof(object) && name == "__repr__" && __self__ is Type classObject &&
                            PyMetaclasses.IsMetaclass(__thisclass__))
                        {
                            result = new PyClosure(name, (args, keywords) => Builtins.DefaultClassRepr(classObject));
                            return true;
                        }
                        if (owner == typeof(object) && name == "__new__" && PyMetaclasses.IsMetaclass(__thisclass__))
                        {
                            result = PyMetaclasses.New;
                            return true;
                        }
                        if (owner == typeof(object) && name is "__new__" or "__str__" or "__getattribute__" or "__getstate__" or "__reduce__" or "__reduce_ex__" or "__setattr__" or "__delattr__")
                        {
                            var method = Builtins.__object_method("__object_" + name[2..^2]);
                            result = receiver != null && name != "__new__" ? new types.MethodType(method, receiver) : method;
                            return true;
                        }
                        if (owner is PyLocalDataClass local && local.Members.TryGetValue(name, out var member))
                        {
                            result = PyMetaclasses.Bind(member, receiver, __self_class__);
                            return true;
                        }
                        if (PyClassDescriptors.TryGetDeclaredAddedAttribute(owner, name, out var added))
                        {
                            result = PyMetaclasses.Bind(added, receiver, __self_class__);
                            return true;
                        }
                        if (name != "__new__" && __self__ is Type && PyMetaclasses.IsMetaclass(owner))
                        {
                            var metaclassMethods = owner.GetMethods(Declared).Where(method => method.Name == name &&
                                method.IsDefined(typeof(PyMetaclassMethodAttribute), false)).ToArray();
                            if (metaclassMethods.Length > 0)
                            {
                                result = new PyClosure(name, (args, keywords) =>
                                    CallMethods(name, metaclassMethods, null, [__self__, .. args], keywords));
                                return true;
                            }
                        }
                        if (PyClassDescriptors.TryGetDeclaredDescriptor(owner, name, out var descriptor))
                        {
                            if (descriptor is staticmethod staticMethod)
                            {
                                result = staticMethod.__get__(null, __self_class__);
                                return true;
                            }
                            if (descriptor is classmethod classMethod)
                            {
                                result = classMethod.__get__(null, __self_class__);
                                return true;
                            }
                            if (receiver == null)
                            {
                                result = descriptor;
                                return true;
                            }
                        }
                        if (owner.GetField(name, Declared) is { IsStatic: true } field)
                        {
                            result = field.GetValue(null);
                            return true;
                        }
                        if (owner.GetProperty(name, Declared) is { GetMethod: { } getter } property && property.GetIndexParameters().Length == 0 &&
                            (getter.IsStatic || receiver != null) && (getter.IsStatic || descriptor != null || !owner.IsDefined(typeof(PySourceAttribute))))
                        {
                            result = InvokeDirect(getter, receiver, []);
                            if (name == "__init_subclass__" && result is types.MethodType { __self__: Type } hook)
                                result = new types.MethodType(hook.__func__, __self_class__);
                            return true;
                        }
                        if (owner.IsGenericType && owner.GetGenericTypeDefinition() == typeof(List<>) &&
                            (Builtins.TryListBaseMember(owner, name, out var listMember) ||
                             Builtins.TryBuiltinClassMethod(owner, name, out listMember)))
                        {
                            result = receiver == null || name == "__new__" ? listMember : new types.MethodType(listMember!, receiver);
                            return true;
                        }
                        if (Builtins.TryDictionaryBaseMember(owner, name, out var dictionaryMember))
                        {
                            result = receiver == null || name == "fromkeys" ? dictionaryMember : new types.MethodType(dictionaryMember!, receiver);
                            return true;
                        }
                        if (receiver != null && PyMetaclasses.TryBindMixinInstanceMethod(receiver, owner, name, out result))
                            return true;
                        MethodInfo[] methods = owner.GetMethods(Declared).Where(m => m.Name == name && !m.IsAbstract &&
                            !m.ContainsGenericParameters && !m.IsDefined(typeof(PyMissingMethodAttribute), false)).ToArray();
                        if (methods.Length > 0)
                        {
                            result = new PyClosure(name, (args, keywords) => CallMethods(name, methods, receiver, args, keywords));
                            return true;
                        }
                        if (name == "__new__" && PyNamedTupleAllocator.TryGet(owner, inlinedBase: false, out result)) return true;
                    }
                }
                return false;
            }

            static object? CallMethods(string name, MethodInfo[] methods, object? receiver, object?[] args, IDictionary<string, object?>? keywords)
            {
                object? target = receiver;
                if (target == null && methods.Any(method => !method.IsStatic))
                {
                    if (args.Length == 0 || !methods[0].DeclaringType!.IsInstanceOfType(args[0]))
                        throw new InvalidCastException($"{name}() requires an instance");
                    target = args[0];
                    args = args[1..];
                }
                var method = PyArgs.Best(methods, args, keywords, out var call) ?? throw PyArgs.NoFit(name, keywords, methods);
                return InvokeDirect(method, target, call);
            }

            // Reflection's Invoke dispatches virtual members on the receiver's class. super must call
            // the method body on the selected base class, with the original receiver as self.
            delegate object? DirectCall(object? target, object?[] args);
            static readonly ConditionalWeakTable<MethodInfo, DirectCall> Invokers = new();

            internal static object? InvokeDirect(MethodInfo method, object? target, object?[] args) =>
                Invokers.GetValue(method, BuildInvoker)(target, args);

            static DirectCall BuildInvoker(MethodInfo method)
            {
                var parameters = method.GetParameters();
                if (method.ReturnType.IsByRef || parameters.Any(p => p.ParameterType.IsByRef))
                    throw new NotSupportedException("super() does not support CLR by-reference methods");
                var thunk = new DynamicMethod("super_" + method.Name, typeof(object), [typeof(object), typeof(object[])],
                    method.DeclaringType!, skipVisibility: true);
                var il = thunk.GetILGenerator();
                if (!method.IsStatic)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, method.DeclaringType!);
                }
                for (int i = 0; i < parameters.Length; i++)
                {
                    var type = parameters[i].ParameterType;
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(type.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, type);
                }
                il.Emit(OpCodes.Call, method);
                if (method.ReturnType == typeof(void)) il.Emit(OpCodes.Ldnull);
                else if (method.ReturnType.IsValueType) il.Emit(OpCodes.Box, method.ReturnType);
                il.Emit(OpCodes.Ret);
                return thunk.CreateDelegate<DirectCall>();
            }

            public static dynamic __make__(object? type, object? @object = null) => new super(type, @object);

            public static dynamic __without_args__() => throw new InvalidOperationException("super(): no arguments");

            public string __repr__() => $"<super: <class '{Builtins.PyTypeName(__thisclass__)}'>, " +
                (__self_class__ == null ? "NULL>" : $"<{Builtins.PyTypeName(__self_class__)} object>>");

            public override string ToString() => __repr__();
        }
    }
}

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;

    internal static partial class Builtins
    {
        public static void __register_subclass(Type type) => PyMetaclasses.RegisterSubclass(type);

        public static void __initialize_subclass(Type type, IDictionary<string, object?>? keywords)
        {
            __register_subclass(type);
            var inherited = new Typhon.Stdlib.builtins.super(type, type);
            if (inherited.TryResolve("__init_subclass__", out var hook)) __invoke_kw(hook, [], keywords);
        }
    }
}
