// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using Microsoft.Win32.SafeHandles;
    using Typhon.Runtime;

    internal static partial class _ssl
    {
        internal static bool IsExported(string name) =>
            name is not (nameof(enum_certificates) or nameof(enum_crls)) || OperatingSystem.IsWindows();

        [PyOptionalModuleMember]
        public static List<PyTuple<object?>> enum_certificates(object? store_name) =>
            WindowsStores.Read(store_name, certificates: true);

        [PyOptionalModuleMember]
        public static List<PyTuple<object?>> enum_crls(object? store_name) =>
            WindowsStores.Read(store_name, certificates: false);

        static class WindowsStores
        {
            static readonly IntPtr SystemStore = new(9), CollectionStore = new(11);
            const uint OpenExisting = 0x4000, ReadOnly = 0x8000;
            const uint ExtensionOnly = 2, PropertyOnly = 4;
            const int NotFound = unchecked((int)0x80092004), NoMoreFiles = 18;
            // Search local machine, enterprise, machine policy, current user, user policy, services and users.
            static readonly uint[] Locations = [0x20000, 0x90000, 0x80000, 0x10000, 0x70000, 0x50000, 0x60000];

            [StructLayout(LayoutKind.Sequential)]
            struct EncodedContext
            {
                internal uint Encoding;
                internal IntPtr Data;
                internal uint Length;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct KeyUsage
            {
                internal uint Count;
                internal IntPtr Identifiers;
            }

            sealed class StoreHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
            {
                protected override bool ReleaseHandle() => CertCloseStore(handle, 0);
            }

            [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
            static extern StoreHandle CertOpenStore(IntPtr provider, uint encoding, IntPtr cryptProvider, uint flags,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string? name);

            [DllImport("crypt32.dll", SetLastError = true)]
            static extern bool CertAddStoreToCollection(StoreHandle collection, StoreHandle store, uint updateFlags, uint priority);

            [DllImport("crypt32.dll", SetLastError = true)]
            static extern bool CertCloseStore(IntPtr store, uint flags);

            [DllImport("crypt32.dll", SetLastError = true)]
            static extern IntPtr CertEnumCertificatesInStore(StoreHandle store, IntPtr previous);

            [DllImport("crypt32.dll", SetLastError = true)]
            static extern IntPtr CertEnumCRLsInStore(StoreHandle store, IntPtr previous);

            [DllImport("crypt32.dll")]
            static extern bool CertFreeCertificateContext(IntPtr context);

            [DllImport("crypt32.dll")]
            static extern bool CertFreeCRLContext(IntPtr context);

            [DllImport("crypt32.dll", SetLastError = true)]
            static extern bool CertGetEnhancedKeyUsage(IntPtr context, uint flags, IntPtr usage, ref uint size);

            static Exception StoreError(int error) => OsErrors.WinError(error, new Win32Exception(error).Message, null);

            static StoreHandle OpenCollection(string name)
            {
                var collection = CertOpenStore(CollectionStore, 0, IntPtr.Zero, 0, null);
                if (collection.IsInvalid) throw StoreError(Marshal.GetLastPInvokeError());
                try
                {
                    bool any = false;
                    int lastError = 0;
                    foreach (uint location in Locations)
                    {
                        using var store = CertOpenStore(SystemStore, 0, IntPtr.Zero, location | ReadOnly | OpenExisting, name);
                        if (!store.IsInvalid && CertAddStoreToCollection(collection, store, updateFlags: 0, priority: 0))
                            any = true;
                        else
                            lastError = Marshal.GetLastPInvokeError();
                    }
                    if (!any) throw StoreError(lastError);
                    return collection;
                }
                catch
                {
                    collection.Dispose();
                    throw;
                }
            }

            static frozenset<string>? ReadKeyUsage(IntPtr certificate, uint flags)
            {
                uint size = 0;
                if (!CertGetEnhancedKeyUsage(certificate, flags, IntPtr.Zero, ref size))
                    return MissingKeyUsage();
                IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    if (!CertGetEnhancedKeyUsage(certificate, flags, buffer, ref size))
                        return MissingKeyUsage();
                    var usage = Marshal.PtrToStructure<KeyUsage>(buffer);
                    var identifiers = new List<string>();
                    for (int i = 0; i < usage.Count; i++)
                    {
                        IntPtr value = Marshal.ReadIntPtr(usage.Identifiers, checked(i * IntPtr.Size));
                        if (Marshal.PtrToStringAnsi(value) is string identifier) identifiers.Add(identifier);
                    }
                    return new frozenset<string>(identifiers);
                }
                finally { Marshal.FreeHGlobal(buffer); }

                static frozenset<string>? MissingKeyUsage()
                {
                    int error = Marshal.GetLastPInvokeError();
                    return error == NotFound ? null : throw StoreError(error);
                }
            }

            internal static List<PyTuple<object?>> Read(object? argument, bool certificates)
            {
                string function = certificates ? nameof(enum_certificates) : nameof(enum_crls);
                if (!OperatingSystem.IsWindows()) throw new MissingMemberException($"module '_ssl' has no attribute '{function}'");
                string name = argument switch
                {
                    string text => text,
                    PyString text => text.Text,
                    _ => throw new InvalidCastException($"{function}() argument 'store_name' must be str, not {Builtins.PyTypeName(Builtins.__type_of(argument))}"),
                };
                if (name.Contains('\0')) throw new ArgumentException("embedded null character");

                using var store = OpenCollection(name);
                var records = new HashSet<PyTuple<object?>>();
                IntPtr current = IntPtr.Zero;
                try
                {
                    while (true)
                    {
                        current = certificates ? CertEnumCertificatesInStore(store, current) : CertEnumCRLsInStore(store, current);
                        if (current == IntPtr.Zero)
                        {
                            int error = Marshal.GetLastPInvokeError();
                            if (error is NotFound or NoMoreFiles) break;
                            throw StoreError(error);
                        }
                        var context = Marshal.PtrToStructure<EncodedContext>(current);
                        var data = new byte[checked((int)context.Length)];
                        Marshal.Copy(context.Data, data, 0, data.Length);
                        object encoding = context.Encoding switch
                        {
                            1 => "x509_asn",
                            0x10000 => "pkcs_7_asn",
                            _ => (long)unchecked((int)context.Encoding),
                        };
                        var raw = new PyBytes(data);
                        if (certificates)
                        {
                            object? usage = ReadKeyUsage(current, PropertyOnly) ?? ReadKeyUsage(current, ExtensionOnly);
                            records.Add(new PyTuple<object?>(new object?[] { raw, encoding, usage ?? true }));
                        }
                        else records.Add(new PyTuple<object?>(new object?[] { raw, encoding }));
                    }
                }
                finally
                {
                    if (current != IntPtr.Zero)
                    {
                        if (certificates) CertFreeCertificateContext(current);
                        else CertFreeCRLContext(current);
                    }
                }
                return new List<PyTuple<object?>>(records);
            }
        }
    }
}
