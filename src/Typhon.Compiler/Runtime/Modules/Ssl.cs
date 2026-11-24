// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class _ssl
    {
        public static bool RAND_status() => true;
        public static PyBytes RAND_bytes(long count) => new(RandomNumberGenerator.GetBytes(checked((int)count)));

        public class SSLError : PyOSError
        {
            public SSLError(string message) : base(message) { }
            public SSLError(long errno, string message) : base(errno, message) { }
            public object? reason { get; set; }
            public object? library { get; set; }
            public override string Message => strerror as string ?? base.Message;
        }
        public class SSLCertVerificationError(string message) : SSLError(message);

        public const long PROTOCOL_TLS = 2;
        public const long PROTOCOL_TLS_CLIENT = 16, PROTOCOL_TLS_SERVER = 17;
        public const long CERT_NONE = 0, CERT_OPTIONAL = 1, CERT_REQUIRED = 2;
        public const bool HAS_ALPN = true;
        public const long VERIFY_DEFAULT = 0, VERIFY_X509_STRICT = 32, VERIFY_X509_TRUSTED_FIRST = 32768,
            VERIFY_X509_PARTIAL_CHAIN = 524288;
        public const string _DEFAULT_CIPHERS = "DEFAULT";
        public const long PROTO_MINIMUM_SUPPORTED = -2, PROTO_MAXIMUM_SUPPORTED = -1;
        public const long PROTO_TLSv1_2 = 0x303, PROTO_TLSv1_3 = 0x304;
        public const long OP_NO_COMPRESSION = 1L << 17, OP_ENABLE_MIDDLEBOX_COMPAT = 1L << 20;
        public const long OP_CIPHER_SERVER_PREFERENCE = 1L << 22, OP_NO_SSLv3 = 1L << 25;
        public const long OP_NO_TLSv1_2 = 1L << 27, OP_NO_TLSv1_3 = 1L << 29, OP_ALL = 0x80000050L;

        public class _SSLContext
        {
            X509Certificate2? _certificate;
            X509Certificate2Collection _certificateAuthorities = null!;
            long _protocol;
            bool _useSystemTrust;
            internal List<SslApplicationProtocol>? ApplicationProtocols { get; private set; }
            bool _checkHostname;
            long _verifyMode;
            long _verifyFlags;
            internal CipherSuitesPolicy? CipherPolicy { get; private set; }

            public _SSLContext(long protocol) => Initialize(protocol);

            public static _SSLContext __new__(Type cls, object? protocol)
            {
                if (!typeof(_SSLContext).IsAssignableFrom(cls))
                    throw new InvalidCastException("_SSLContext.__new__ requires an SSL context subtype");
                object index = Builtins.__index_value(protocol);
                int mode = index is long small ? checked((int)small) : checked((int)(BigInteger)index);
                var instance = (_SSLContext)RuntimeHelpers.GetUninitializedObject(cls);
                instance.Initialize(mode);
                return instance;
            }

            void Initialize(long protocol)
            {
                if (protocol is not (PROTOCOL_TLS or PROTOCOL_TLS_CLIENT or PROTOCOL_TLS_SERVER))
                    throw new ArgumentException($"invalid or unsupported protocol version {protocol}");
                _protocol = protocol;
                _certificateAuthorities = new X509Certificate2Collection();
                _checkHostname = protocol == PROTOCOL_TLS_CLIENT;
                _verifyMode = _checkHostname ? CERT_REQUIRED : CERT_NONE;
                _verifyFlags = VERIFY_X509_TRUSTED_FIRST;
                options = OP_ALL | OP_NO_COMPRESSION | OP_ENABLE_MIDDLEBOX_COMPAT | OP_CIPHER_SERVER_PREFERENCE | OP_NO_SSLv3;
                minimum_version = PROTO_TLSv1_2;
                maximum_version = PROTO_MAXIMUM_SUPPORTED;
            }

            [PyDescriptor(PyDescriptorKind.Property)]
            public dynamic? _msg_callback
            {
                get => null;
                set
                {
                    object? callback = value;
                    if (callback == null) return;
                    if (!Builtins.callable(callback)) throw new InvalidCastException("callback is not callable");
                    throw new NotImplementedException("the managed TLS provider does not expose TLS protocol messages");
                }
            }

            public long protocol => _protocol;
            [PyDescriptor(PyDescriptorKind.Property)] public long options { get; set; }
            [PyDescriptor(PyDescriptorKind.Property)] public long minimum_version { get; set; }
            [PyDescriptor(PyDescriptorKind.Property)] public long maximum_version { get; set; }
            public bool check_hostname
            {
                get => _checkHostname;
                set
                {
                    _checkHostname = value;
                    if (value && _verifyMode == CERT_NONE) _verifyMode = CERT_REQUIRED;
                }
            }
            [PyDescriptor(PyDescriptorKind.Property)]
            public long verify_mode
            {
                get => _verifyMode;
                set
                {
                    if (value is not (CERT_NONE or CERT_OPTIONAL or CERT_REQUIRED))
                        throw new ArgumentException("invalid certificate verification mode");
                    if (value == CERT_NONE && _checkHostname)
                        throw new ArgumentException("Cannot set verify_mode to CERT_NONE when check_hostname is enabled.");
                    _verifyMode = value;
                }
            }

            [PyDescriptor(PyDescriptorKind.Property)]
            public long verify_flags
            {
                get => _verifyFlags;
                set
                {
                    const long supported = VERIFY_X509_STRICT | VERIFY_X509_TRUSTED_FIRST | VERIFY_X509_PARTIAL_CHAIN;
                    if ((value & ~supported) != 0)
                        throw new NotSupportedException("the selected certificate verification flags are unavailable");
                    _verifyFlags = value;
                }
            }

            public void set_ciphers(string cipherlist)
            {
                if (cipherlist == _DEFAULT_CIPHERS)
                {
                    CipherPolicy = null;
                    return;
                }
                if (OperatingSystem.IsWindows())
                    throw new NotSupportedException("custom cipher suites are managed by the Windows TLS provider");
                var suites = new List<TlsCipherSuite>();
                foreach (string item in cipherlist.Split(':', StringSplitOptions.RemoveEmptyEntries))
                    if (item.StartsWith("TLS_", StringComparison.Ordinal) && Enum.TryParse(item, out TlsCipherSuite suite))
                        suites.Add(suite);
                if (suites.Count == 0) throw new SSLError("No cipher can be selected.");
                CipherPolicy = new CipherSuitesPolicy(suites);
            }

            public void load_cert_chain(object certfile, object? keyfile = null, object? password = null)
            {
                string certificatePath = os_.__path(certfile);
                string keyPath = keyfile == null ? certificatePath : os_.__path(keyfile);
                object? secret = Builtins.callable(password) ? (object?)Builtins.__invoke(password, []) : password;
                try
                {
                    var replacement = secret == null ? X509Certificate2.CreateFromPemFile(certificatePath, keyPath) :
                        X509Certificate2.CreateFromEncryptedPemFile(certificatePath, Builtins.str(secret), keyPath);
                    if (OperatingSystem.IsWindows())
                    {
                        // Schannel needs a private key its provider can open outside this process.
                        // The user-scoped key is temporary and follows the certificate handle.
                        using var ephemeral = replacement;
                        byte[] pkcs12 = ephemeral.Export(X509ContentType.Pkcs12);
                        try { replacement = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet); }
                        finally { CryptographicOperations.ZeroMemory(pkcs12); }
                    }
                    _certificate = replacement;
                }
                catch (CryptographicException error) { throw new SSLError(error.Message); }
            }

            public void _set_alpn_protocols(object protocols)
            {
                byte[] packed = ByteOps.Buffer(protocols);
                var names = new List<SslApplicationProtocol>();
                for (int offset = 0; offset < packed.Length;)
                {
                    int length = packed[offset++];
                    if (length == 0 || length > packed.Length - offset)
                        throw new ArgumentException("invalid ALPN protocol list");
                    names.Add(new SslApplicationProtocol(packed.AsSpan(offset, length).ToArray()));
                    offset += length;
                }
                ApplicationProtocols = names.Count == 0 ? null : names;
            }

            public void set_default_verify_paths()
            {
                var paths = DefaultVerifyPaths.Value;
                string? certificateFile = Environment.GetEnvironmentVariable(paths[0]);
                string? certificateDirectories = Environment.GetEnvironmentVariable(paths[2]);
                if (certificateFile == null && certificateDirectories == null)
                {
                    _useSystemTrust = true;
                    return;
                }

                // SslStream uses the platform trust store. Read Python's current
                // overrides into this context so they take effect after startup.
                certificateFile ??= paths[1];
                certificateDirectories ??= paths[3];
                if (File.Exists(certificateFile))
                    LoadDefaultVerifyLocation(cafile: certificateFile);
                foreach (string directory in certificateDirectories.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    if (Directory.Exists(directory))
                        LoadDefaultVerifyLocation(capath: directory);
            }

            void LoadDefaultVerifyLocation(string? cafile = null, string? capath = null)
            {
                // OpenSSL's default lookup tolerates missing or unreadable certificate locations.
                try { load_verify_locations(cafile, capath); }
                catch (SSLError) { }
            }

            public void load_verify_locations(object? cafile = null, object? capath = null, object? cadata = null)
            {
                if (cafile == null && capath == null && cadata == null)
                    throw new InvalidCastException("cafile, capath and cadata cannot be all omitted");
                var roots = new X509Certificate2Collection();
                try
                {
                    if (cafile != null) roots.ImportFromPemFile(os_.__path(cafile));
                    if (cadata is string pem) roots.ImportFromPem(pem);
                    else if (cadata != null) roots.Add(X509CertificateLoader.LoadCertificate(ByteOps.Buffer(cadata)));
                    if (capath != null)
                    {
                        foreach (string file in Directory.EnumerateFiles(os_.__path(capath)))
                        {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(file), @"^[0-9a-f]{8}\.[0-9]+$"))
                                continue;
                            roots.ImportFromPemFile(file);
                        }
                    }
                    if (roots.Count == 0 && (cafile != null || cadata != null))
                        throw new SSLError("no certificate or CRL found");
                    _certificateAuthorities.AddRange(roots);
                }
                catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
                {
                    foreach (X509Certificate2 root in roots) root.Dispose();
                    if (error is SSLError) throw;
                    throw new SSLError(error.Message);
                }
            }

            internal bool VerifyCertificate(X509Certificate2 certificate, X509Chain? suppliedChain,
                SslPolicyErrors errors, bool serverSide)
            {
                if (_useSystemTrust && errors == SslPolicyErrors.None) return true;
                if (_certificateAuthorities.Count == 0) return false;

                using var chain = new X509Chain();
                var policy = chain.ChainPolicy;
                policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                policy.DisableCertificateDownloads = true;
                policy.RevocationMode = X509RevocationMode.NoCheck;
                policy.ApplicationPolicy.Add(new Oid(serverSide ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1"));
                policy.CustomTrustStore.AddRange(_certificateAuthorities);
                if (suppliedChain != null)
                    foreach (X509ChainElement element in suppliedChain.ChainElements)
                        policy.ExtraStore.Add(element.Certificate);
                if (chain.Build(certificate)) return true;
                if ((verify_flags & VERIFY_X509_PARTIAL_CHAIN) == 0 || chain.ChainElements.Count == 0)
                    return false;
                const X509ChainStatusFlags trustErrors = X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.UntrustedRoot;
                if (chain.ChainStatus.Any(status => (status.Status & ~trustErrors) != 0)) return false;
                byte[] anchor = chain.ChainElements[^1].Certificate.RawData;
                return _certificateAuthorities.Cast<X509Certificate2>().Any(root => root.RawData.AsSpan().SequenceEqual(anchor));
            }

            public _SSLSocket _wrap_socket(socket_.socket sock, bool server_side, string? server_hostname = null,
                [PyKeywordOnly] object? owner = null, [PyKeywordOnly] object? session = null)
            {
                if (server_side && protocol == PROTOCOL_TLS_CLIENT || !server_side && protocol == PROTOCOL_TLS_SERVER)
                    throw new SSLError("SSL context protocol is incompatible with this endpoint");
                if (!server_side && check_hostname && string.IsNullOrEmpty(server_hostname))
                    throw new ArgumentException("check_hostname requires server_hostname");
                return new _SSLSocket(this, sock.OpenConnectedTransport(), server_side, server_hostname);
            }

            internal X509Certificate2 ServerCertificate => _certificate ?? throw new SSLError("server certificate is required");
            internal SslProtocols Protocols
            {
                get
                {
                    long min = minimum_version == PROTO_MINIMUM_SUPPORTED ? PROTO_TLSv1_2 : minimum_version;
                    long max = maximum_version == PROTO_MAXIMUM_SUPPORTED ? PROTO_TLSv1_3 : maximum_version;
                    SslProtocols protocols = 0;
                    if (min <= PROTO_TLSv1_2 && max >= PROTO_TLSv1_2 && (options & OP_NO_TLSv1_2) == 0)
                        protocols |= SslProtocols.Tls12;
                    if (min <= PROTO_TLSv1_3 && max >= PROTO_TLSv1_3 && (options & OP_NO_TLSv1_3) == 0)
                        protocols |= SslProtocols.Tls13;
                    return protocols != 0 ? protocols : throw new SSLError("no supported TLS version is enabled");
                }
            }
        }

        public sealed class _SSLSocket : IDisposable
        {
            readonly SslStream _stream;
            bool _certificateRejected;
            public _SSLContext context { get; }
            public bool server_side { get; }
            public string? server_hostname { get; }

            internal _SSLSocket(_SSLContext context, Stream transport, bool serverSide, string? hostname)
            {
                this.context = context;
                server_side = serverSide;
                server_hostname = hostname;
                _stream = new SslStream(transport, leaveInnerStreamOpen: false, ValidateCertificate);
            }

            bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
            {
                if (context.verify_mode == CERT_NONE) return true;
                if (server_side && context.verify_mode == CERT_OPTIONAL && certificate == null) return true;
                if (server_side || !context.check_hostname) errors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;
                bool accepted = certificate is X509Certificate2 presented &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                    context.VerifyCertificate(presented, chain, errors, server_side);
                _certificateRejected = !accepted;
                return accepted;
            }

            public void do_handshake()
            {
                if (_stream.IsAuthenticated) return;
                try
                {
                    if (server_side)
                        _stream.AuthenticateAsServer(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = context.ServerCertificate,
                            ClientCertificateRequired = context.verify_mode != CERT_NONE,
                            EnabledSslProtocols = context.Protocols,
                            ApplicationProtocols = context.ApplicationProtocols,
                            CipherSuitesPolicy = context.CipherPolicy,
                        });
                    else
                        _stream.AuthenticateAsClient(new SslClientAuthenticationOptions
                        {
                            TargetHost = server_hostname ?? "",
                            EnabledSslProtocols = context.Protocols,
                            ApplicationProtocols = context.ApplicationProtocols,
                            CipherSuitesPolicy = context.CipherPolicy,
                        });
                }
                catch (Exception error) when (error is AuthenticationException or IOException && error is not SSLError)
                {
                    if (_certificateRejected) throw new SSLCertVerificationError(error.Message);
                    throw new SSLError(error.Message);
                }
            }

            // The TLS provider may surface a peer alert on the first read or write after authentication.
            // NetworkStream also wraps socket failures in IOException, with a distinct SocketException cause.
            static bool IsTlsIoError(Exception error) => error is AuthenticationException ||
                error is IOException { InnerException: AuthenticationException or CryptographicException or (Win32Exception and not SocketException) };

            public PyBytes read(long length = 1024)
            {
                try { return StreamOps.Read(_stream, length, once: true); }
                catch (Exception error) when (IsTlsIoError(error)) { throw new SSLError(error.Message); }
            }

            public long write(object data)
            {
                var bytes = ByteOps.From(data);
                try { return StreamOps.Write(_stream, bytes); }
                catch (Exception error) when (IsTlsIoError(error)) { throw new SSLError(error.Message); }
            }
            public string? version() => _stream.IsAuthenticated ? _stream.SslProtocol switch
            {
                SslProtocols.Tls12 => "TLSv1.2",
                SslProtocols.Tls13 => "TLSv1.3",
                _ => null,
            } : null;
            public string? selected_alpn_protocol() => _stream.IsAuthenticated && !_stream.NegotiatedApplicationProtocol.Protocol.IsEmpty
                ? Encoding.ASCII.GetString(_stream.NegotiatedApplicationProtocol.Protocol.Span) : null;
            public object? getpeercert(bool binary_form = false)
            {
                if (!_stream.IsAuthenticated) throw new ArgumentException("handshake is not done yet");
                var certificate = _stream.RemoteCertificate;
                if (certificate == null) return null;
                if (binary_form) return new PyBytes(certificate.GetRawCertData());
                if (context.verify_mode == CERT_NONE) return new OrderedDictionary<string, object?>();
                throw new NotSupportedException("structured peer certificate inspection is unavailable");
            }
            public void Dispose() => _stream.Dispose();
        }

        public sealed class MemoryBIO
        {
            readonly MemoryStream _buffer = new();
            bool _ended;

            public long pending { get { lock (_buffer) return _buffer.Length - _buffer.Position; } }
            public bool eof { get { lock (_buffer) return _ended && _buffer.Position == _buffer.Length; } }

            public long write(object data)
            {
                lock (_buffer)
                {
                    if (_ended) throw new SSLError("cannot write() after write_eof()");
                    var bytes = ByteOps.Buffer(data);
                    long position = _buffer.Position;
                    _buffer.Position = _buffer.Length;
                    _buffer.Write(bytes);
                    _buffer.Position = position;
                    return bytes.Length;
                }
            }

            public PyBytes read(long size = -1)
            {
                lock (_buffer)
                {
                    int requested = checked((int)size);
                    int remaining = (int)(_buffer.Length - _buffer.Position);
                    var bytes = StreamOps.Read(_buffer, requested < 0 ? remaining : Math.Min(requested, remaining));
                    if (_buffer.Position == _buffer.Length) _buffer.SetLength(0);
                    return bytes;
                }
            }

            public void write_eof() { lock (_buffer) _ended = true; }
        }
    }
}
