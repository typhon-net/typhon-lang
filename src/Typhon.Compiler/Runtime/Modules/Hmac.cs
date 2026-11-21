// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace Typhon.Stdlib
{
    using System;
    using System.Security.Cryptography;
    using Typhon.Runtime;

    /// <summary>hmac: keyed hashing (RFC 2104) on .NET's HMAC classes, and compare_digest.</summary>
    internal static class hmac
    {
        /// <summary>hmac.new(key, msg=None, digestmod): digestmod is a name ('sha256') or a hashlib constructor.</summary>
        public static HMAC @new(object key, object? msg = null, object? digestmod = null) => new HMAC(key, msg, digestmod);

        /// <summary>hmac.digest(key, msg, digest): the HMAC of msg at once.</summary>
        public static PyBytes digest(object key, object msg, object digest) => new HMAC(key, msg, digest).digest();

        /// <summary>
        /// compare_digest(a, b): equality in time that does not depend on where the values differ (two str, or two
        /// bytes-like values).
        /// </summary>
        public static bool compare_digest(object a, object b)
        {
            if (a is string sa && b is string sb)
            {
                foreach (char ch in sa + sb) if (ch > 127) throw new InvalidCastException("comparing strings with non-ASCII characters is not supported");
                return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(sa), System.Text.Encoding.ASCII.GetBytes(sb));
            }
            if (a is string || b is string) throw new InvalidCastException($"unsupported operand types(s) or combination of types: '{Builtins.PyTypeName(a.GetType())}' and '{Builtins.PyTypeName(b.GetType())}'");
            return CryptographicOperations.FixedTimeEquals(ByteOps.From(a), ByteOps.From(b));
        }

        /// <summary>An HMAC in progress: update() it with data, then digest() / hexdigest().</summary>
        internal sealed class HMAC
        {
            readonly IncrementalHash _h;
            readonly byte[] _key;
            readonly HashAlgorithmName _alg;
            public readonly string name;
            public readonly long digest_size, block_size;

            public HMAC(object key, object? msg = null, object? digestmod = null)
            {
                if (digestmod == null) throw new InvalidCastException("Missing required argument 'digestmod'.");
                string algo = (digestmod switch
                {
                    string s => s,
                    PyHash h => h.name,
                    Delegate d => d.Method.Name,
                    PyFunction f => f.__name__,
                    _ => digestmod.ToString() ?? "",
                }).ToLowerInvariant().Replace("-", "");
                (_alg, digest_size, block_size) = algo switch
                {
                    "md5" => (HashAlgorithmName.MD5, 16L, 64L),
                    "sha1" => (HashAlgorithmName.SHA1, 20L, 64L),
                    "sha256" => (HashAlgorithmName.SHA256, 32L, 64L),
                    "sha384" => (HashAlgorithmName.SHA384, 48L, 128L),
                    "sha512" => (HashAlgorithmName.SHA512, 64L, 128L),
                    _ => throw new ArgumentException($"unsupported hash type {algo}"),
                };
                name = "hmac-" + algo;
                _key = ByteOps.From(key);
                _h = IncrementalHash.CreateHMAC(_alg, _key);
                if (msg != null) update(msg);
            }

            HMAC(HMAC other)
            {
                (_alg, digest_size, block_size, name, _key) = (other._alg, other.digest_size, other.block_size, other.name, other._key);
                _h = IncrementalHash.CreateHMAC(_alg, _key);
                _h.AppendData(other._appended.ToArray());
                _appended.AddRange(other._appended);
            }

            // (copy() replays the data: IncrementalHash cannot be cloned mid-stream)
            readonly System.Collections.Generic.List<byte> _appended = new System.Collections.Generic.List<byte>();

            public void update(object msg) { var b = ByteOps.From(msg); _h.AppendData(b); _appended.AddRange(b); }
            public PyBytes digest() => new PyBytes(_h.GetCurrentHash());
            public string hexdigest() => Convert.ToHexString(_h.GetCurrentHash()).ToLowerInvariant();
            public HMAC copy() => new HMAC(this);
        }
    }
}
