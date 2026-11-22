// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using Typhon.Runtime;

    /// <summary>The cryptographic constructors used by the Python implementation of hashlib.</summary>
    internal static class _hashlib
    {
        public static readonly IReadOnlySet<string> openssl_md_meth_names = hashlib.algorithms_available;

        public static PyHash openssl_md5([Optional] object? data, [Optional, DefaultParameterValue(true)] bool usedforsecurity) => Open("md5", data);
        public static PyHash openssl_sha1([Optional] object? data, [Optional, DefaultParameterValue(true)] bool usedforsecurity) => Open("sha1", data);
        public static PyHash openssl_sha256([Optional] object? data, [Optional, DefaultParameterValue(true)] bool usedforsecurity) => Open("sha256", data);
        public static PyHash openssl_sha384([Optional] object? data, [Optional, DefaultParameterValue(true)] bool usedforsecurity) => Open("sha384", data);
        public static PyHash openssl_sha512([Optional] object? data, [Optional, DefaultParameterValue(true)] bool usedforsecurity) => Open("sha512", data);

        public static PyHash @new(object? name, [Optional] object? data, [Optional, DefaultParameterValue(true)] bool usedforsecurity)
        {
            if (name is not string algorithm)
                throw new InvalidCastException($"new() argument 'name' must be str, not {Builtins.PyTypeName(Builtins.__type_of(name))}");
            string normalized = algorithm.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);
            if (!openssl_md_meth_names.Contains(normalized)) throw new ArgumentException($"unsupported hash algorithm {algorithm}");
            return Open(normalized, data);
        }

        public sealed class UnsupportedDigestmodError : ArgumentException
        {
            public UnsupportedDigestmodError(string name) : base($"unsupported hash algorithm {name}") { }
        }

        public static hmac.HMAC hmac_new(object key, object? msg, object? digestmod)
        {
            string? algorithm = digestmod switch
            {
                string name => name,
                PyBoundMethod { Target: Type owner } method when owner == typeof(_hashlib) &&
                    method.Name.StartsWith("openssl_", StringComparison.Ordinal) => method.Name["openssl_".Length..],
                _ => null,
            };
            if (algorithm == null || !_hmac.TryAlgorithm(algorithm, out string normalized))
                throw new UnsupportedDigestmodError(Builtins.str(digestmod));
            return new hmac.HMAC(key, msg, normalized);
        }

        public static PyBytes hmac_digest(object key, object msg, object digest) =>
            hmac_new(key, msg ?? throw new InvalidCastException("object supporting the buffer API required"), digest).digest();

        public static bool compare_digest(object a, object b) => hmac.compare_digest(a, b);

        static PyHash Open(string name, object? data)
        {
            var hash = new PyHash(name);
            if (!ReferenceEquals(data, Type.Missing)) hash.update(data);
            return hash;
        }
    }
}
