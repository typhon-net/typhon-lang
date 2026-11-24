// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using Path = System.IO.Path;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    internal static partial class _ssl
    {
        static readonly Lazy<string[]> DefaultVerifyPaths = new(ReadDefaultVerifyPaths);

        public static PyTuple<string> get_default_verify_paths() => new(DefaultVerifyPaths.Value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr OpenSslPath();

        static string[] ReadDefaultVerifyPaths()
        {
            string[] libraries = OperatingSystem.IsWindows()
                ? ["libcrypto-3-x64.dll", "libcrypto-3.dll"]
                : OperatingSystem.IsMacOS()
                    ? ["libcrypto.3.dylib", "libcrypto.dylib"]
                    : ["libcrypto.so.3", "libcrypto.so.1.1", "libcrypto.so"];

            foreach (string library in libraries)
            {
                if (!NativeLibrary.TryLoad(library, typeof(_ssl).Assembly,
                    DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out IntPtr handle))
                    continue;

                try
                {
                    string? Read(string name)
                    {
                        if (!NativeLibrary.TryGetExport(handle, "X509_get_default_cert_" + name, out IntPtr entry))
                            return null;
                        return Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<OpenSslPath>(entry)());
                    }

                    if (Read("file_env") is string fileKey && Read("file") is string file &&
                        Read("dir_env") is string directoryKey && Read("dir") is string directory)
                        return [fileKey, file, directoryKey, directory];
                }
                finally { NativeLibrary.Free(handle); }
            }

            string defaultDirectory = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "SSL")
                : "/etc/ssl";
            return ["SSL_CERT_FILE", Path.Combine(defaultDirectory, "cert.pem"),
                "SSL_CERT_DIR", Path.Combine(defaultDirectory, "certs")];
        }
    }
}
