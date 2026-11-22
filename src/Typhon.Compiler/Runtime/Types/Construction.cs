// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;

    internal static partial class Builtins
    {
        /// <summary>Binds forwarded arguments to the selected base class's allocation-independent initializer.</summary>
        public static void __base_init(object instance, Type parent, object?[] args, IDictionary<string, object?>? kwargs)
        {
            var candidates = parent.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "__init__" && !method.IsGenericMethodDefinition && !method.IsVirtual).ToList();
            var selected = PyArgs.Best(candidates, args, kwargs, out var bound)
                ?? throw PyArgs.NoFit(parent.Name + ".__init__", kwargs, candidates);
            try { selected.Invoke(instance, bound); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
