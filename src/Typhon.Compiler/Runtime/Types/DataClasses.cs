// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    /// <summary>A local Python class whose body defines data attributes.</summary>
    internal sealed class PyLocalDataClass : TypeDelegator
    {
        readonly string _name;
        internal readonly string PythonModule;
        internal readonly string QualName;
        internal readonly Dictionary<string, object?> Members = new(StringComparer.Ordinal);

        public PyLocalDataClass(string name, string module, string qualName, Type parent) : base(parent)
        {
            _name = name;
            PythonModule = module;
            QualName = qualName;
        }

        public override string Name => _name;
        public override string FullName => PythonModule + "." + QualName;
        public override Type? BaseType => typeImpl;
        public override Type UnderlyingSystemType => this;
        public override ConstructorInfo[] GetConstructors(BindingFlags flags) => [];
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override bool Equals(Type? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }
}
