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
                __self_class__ = @object as Type ?? @object.GetType();
                if (!__thisclass__.IsAssignableFrom(__self_class__))
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
                    for (Type? owner = __thisclass__.BaseType; owner != null; owner = owner.BaseType)
                    {
                        if (owner == typeof(object) && name == "__new__" && PyMetaclasses.IsMetaclass(__thisclass__))
                        {
                            result = PyMetaclasses.New;
                            return true;
                        }
                        if (owner == typeof(object) && name is "__new__" or "__str__" or "__getattribute__" or "__getstate__" or "__setattr__" or "__delattr__")
                        {
                            var method = Builtins.__object_method("__object_" + name[2..^2]);
                            result = receiver != null && name != "__new__" ? new types.MethodType(method, receiver) : method;
                            return true;
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
                            return true;
                        }
                        MethodInfo[] methods = owner.GetMethods(Declared).Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters).ToArray();
                        if (methods.Length > 0)
                        {
                            result = new PyClosure(name, (args, keywords) => CallMethods(name, methods, receiver, args, keywords));
                            return true;
                        }
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

            static object? InvokeDirect(MethodInfo method, object? target, object?[] args) =>
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
