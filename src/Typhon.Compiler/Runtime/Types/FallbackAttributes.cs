// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Linq.Expressions;

    // Preserve Python attribute lookup when a generated instance reaches a CLR dynamic call site.
    internal interface IPyFallbackAttributes : IDynamicMetaObjectProvider
    {
        DynamicMetaObject IDynamicMetaObjectProvider.GetMetaObject(Expression parameter) =>
            new PyFallbackAttributeBinding(parameter, this);
    }

    internal sealed class PyFallbackAttributeBinding(Expression expression, object value)
        : DynamicMetaObject(expression, BindingRestrictions.Empty, value)
    {
        DynamicMetaObject Bind(Expression operation) =>
            new(operation, BindingRestrictions.GetTypeRestriction(Expression, LimitType));

        MethodCallExpression Lookup(string name) => Expression.Call(typeof(Builtins), nameof(Builtins.getattr),
            null, Expression.Convert(Expression, typeof(object)), Expression.Constant(name));

        public override DynamicMetaObject BindGetMember(GetMemberBinder binder) => Bind(Lookup(binder.Name));

        public override DynamicMetaObject BindInvokeMember(InvokeMemberBinder binder, DynamicMetaObject[] args) =>
            Bind(Expression.Call(typeof(PyFallbackAttributeBinding), nameof(Invoke), null,
                Lookup(binder.Name), Expression.NewArrayInit(typeof(object), args.Select(a => Expression.Convert(a.Expression, typeof(object)))),
                Expression.Constant(binder.CallInfo.ArgumentNames.ToArray())));

        public static object? Invoke(object? callable, object?[] args, string[] names)
        {
            if (names.Length == 0) return Builtins.__invoke(callable, args);
            int positional = args.Length - names.Length;
            var keywords = new Dictionary<string, object?>(names.Length);
            for (int index = 0; index < names.Length; index++)
                keywords.Add(names[index], args[positional + index]);
            return Builtins.__invoke_kw(callable, args[..positional], keywords);
        }
    }
}
