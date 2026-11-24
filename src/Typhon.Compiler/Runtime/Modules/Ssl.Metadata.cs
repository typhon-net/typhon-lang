// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace Typhon.Stdlib
{
    internal static partial class _ssl
    {
        // Protocol version IDs remain valid even when the platform cannot negotiate them.
        public const long PROTO_SSLv3 = 0x300, PROTO_TLSv1 = 0x301, PROTO_TLSv1_1 = 0x302;
        public const long PROTOCOL_SSLv23 = PROTOCOL_TLS;
        public const long SSL_ERROR_SSL = 1, SSL_ERROR_WANT_READ = 2, SSL_ERROR_WANT_WRITE = 3,
            SSL_ERROR_WANT_X509_LOOKUP = 4, SSL_ERROR_SYSCALL = 5, SSL_ERROR_ZERO_RETURN = 6,
            SSL_ERROR_WANT_CONNECT = 7, SSL_ERROR_EOF = 8, SSL_ERROR_INVALID_ERROR_CODE = 10;

        public const bool HAS_NPN = false, HAS_SSLv2 = false, HAS_SSLv3 = false,
            HAS_TLSv1 = false, HAS_TLSv1_1 = false, HAS_TLSv1_2 = true,
            HAS_PSK = false, HAS_PSK_TLS13 = false, HAS_PHA = false, HAS_TLS_UNIQUE = false;
    }
}
