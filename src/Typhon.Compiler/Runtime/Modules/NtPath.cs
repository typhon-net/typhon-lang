// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using Typhon.Runtime;

    internal static partial class ntpath
    {
        public static (PyBytes, PyBytes, PyBytes) splitroot(PyBytes p)
        {
            var (drive, root, tail) = splitroot(p.decode("latin-1"));
            return (PyBytes.Latin1(drive), PyBytes.Latin1(root), PyBytes.Latin1(tail));
        }
    }
}
