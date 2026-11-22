// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;

    /// <summary>Interpreter type objects re-exported by the pure Python types module.</summary>
    internal static class _types
    {
        public static readonly Type AsyncGeneratorType = typeof(types.AsyncGeneratorType);
        public static readonly Type BuiltinFunctionType = typeof(types.BuiltinFunctionType);
        public static readonly Type BuiltinMethodType = BuiltinFunctionType;
        public static readonly Type CapsuleType = typeof(types.CapsuleType);
        public static readonly Type CellType = typeof(types.CellType);
        public static readonly Type ClassMethodDescriptorType = typeof(types.ClassMethodDescriptorType);
        public static readonly Type CodeType = typeof(sys_.code);
        public static readonly Type CoroutineType = typeof(types.CoroutineType);
        public static readonly Type EllipsisType = typeof(types.EllipsisType);
        public static readonly Type FrameType = typeof(sys_.frame);
        public static readonly Type FrameLocalsProxyType = typeof(types.FrameLocalsProxyType);
        public static readonly Type FunctionType = typeof(types.FunctionType);
        public static readonly Type GeneratorType = typeof(types.GeneratorType);
        public static readonly Type GenericAlias = typeof(types.GenericAlias);
        public static readonly Type GetSetDescriptorType = typeof(types.GetSetDescriptorType);
        public static readonly Type LambdaType = FunctionType;
        public static readonly Type LazyImportType = typeof(types.LazyImportType);
        public static readonly Type MappingProxyType = typeof(types.MappingProxyType);
        public static readonly Type MemberDescriptorType = typeof(types.MemberDescriptorType);
        public static readonly Type MethodDescriptorType = typeof(types.MethodDescriptorType);
        public static readonly Type MethodType = typeof(types.MethodType);
        public static readonly Type MethodWrapperType = typeof(types.MethodWrapperType);
        public static readonly Type ModuleType = typeof(types.ModuleType);
        public static readonly Type NoneType = typeof(types.NoneType);
        public static readonly Type NotImplementedType = typeof(types.NotImplementedType);
        public static readonly Type SimpleNamespace = typeof(types.SimpleNamespace);
        public static readonly Type TracebackType = typeof(types.TracebackType);
        public static readonly Type UnionType = typeof(types.UnionType);
        public static readonly Type WrapperDescriptorType = typeof(types.WrapperDescriptorType);
    }
}
