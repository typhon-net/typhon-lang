// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Typhon.Runtime;

    /// <summary>Internet sockets backed by the host's .NET socket implementation.</summary>
    internal static partial class socket_
    {
        public const long AF_UNSPEC = 0, AF_INET = 2, SOCK_STREAM = 1, SOCK_DGRAM = 2;
        public static readonly long AF_INET6 = OperatingSystem.IsWindows() ? 23 : OperatingSystem.IsMacOS() ? 30 : 10;
        public const long IPPROTO_IP = 0, IPPROTO_TCP = 6, IPPROTO_UDP = 17, IPPROTO_IPV6 = 41;
        public static readonly long SOL_SOCKET = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 0xffff : 1;
        public static readonly long SO_REUSEADDR = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 4 : 2;
        public static readonly long SO_KEEPALIVE = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 8 : 9;
        public static readonly long SO_ERROR = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 0x1007 : 4;
        public static readonly long SO_TYPE = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 0x1008 : 3;
        public const long TCP_NODELAY = 1, SHUT_RD = 0, SHUT_WR = 1, SHUT_RDWR = 2;
        public const long MSG_OOB = 1, MSG_PEEK = 2, MSG_DONTROUTE = 4;
        public const long AI_PASSIVE = 1, AI_CANONNAME = 2, AI_NUMERICHOST = 4;
        public static readonly bool has_ipv6 = Socket.OSSupportsIPv6;
        public static readonly Type error = typeof(PyOSError), timeout = typeof(TimeoutException);
        public static readonly object _GLOBAL_DEFAULT_TIMEOUT = new object();

        static readonly object DefaultTimeoutLock = new object();
        static double? _defaultTimeout;
        public static double? getdefaulttimeout() { lock (DefaultTimeoutLock) return _defaultTimeout; }

        public static void setdefaulttimeout(double? value)
        {
            TimeoutMilliseconds(value);
            lock (DefaultTimeoutLock) _defaultTimeout = value;
        }

        static int TimeoutMilliseconds(double? seconds)
        {
            if (seconds == null || seconds == 0) return 0;
            if (double.IsNaN(seconds.Value) || seconds.Value < 0) throw new ArgumentException("Timeout value out of range");
            return checked((int)Math.Ceiling(seconds.Value * 1000));
        }

        internal class gaierror : PyOSError
        {
            public gaierror(params object?[] args) : base(args) { }
        }

        internal partial class socket : IDisposable
        {
            readonly Socket _handle;
            double? _timeout;
            public long family { get; }
            public long type { get; }
            public long proto { get; }

            public socket(long family = -1, long type = -1, long proto = -1, object? fileno = null)
            {
                if (fileno != null) throw new NotImplementedException("adopting socket file descriptors is not supported");
                this.family = family == -1 ? AF_INET : family;
                this.type = type == -1 ? SOCK_STREAM : type;
                this.proto = proto == -1 ? 0 : proto;
                try { _handle = new Socket(NetFamily(this.family), NetType(this.type), (ProtocolType)this.proto); }
                catch (SocketException e) { throw SocketFailure(e); }
                ConfigureNewSocket();
            }

            socket(Socket handle, socket listener)
            {
                _handle = handle;
                family = listener.family;
                type = listener.type;
                proto = listener.proto;
                ConfigureNewSocket();
            }

            void ConfigureNewSocket()
            {
                try { settimeout(getdefaulttimeout()); }
                catch { _handle.Dispose(); throw; }
            }

            T Run<T>(Func<Socket, T> operation)
            {
                try { return operation(_handle); }
                catch (SocketException e) { throw SocketFailure(e, _timeout > 0); }
                catch (OperationCanceledException) { throw new TimeoutException("timed out"); }
                catch (ObjectDisposedException) { throw new PyOSError(errno.EBADF, "Bad file descriptor"); }
            }

            void Run(Action<Socket> operation) => Run(s => { operation(s); return 0; });

            public socket __enter__() => this;
            public object? __exit__(object? exc_type, object? exc_value, object? traceback)
            {
                close();
                return null;
            }
            public void close()
            {
                lock (_fileLifetime)
                {
                    if (_closeRequested) return;
                    _closeRequested = true;
                    if (_openFiles == 0) _handle.Dispose();
                }
            }
            public void Dispose() => close();
            public long fileno()
            {
                try { return _handle.SafeHandle.IsClosed ? -1 : _handle.Handle.ToInt64(); }
                catch (ObjectDisposedException) { return -1; }
            }

            public void settimeout(double? value)
            {
                int milliseconds = TimeoutMilliseconds(value);
                Run(s => { s.Blocking = value != 0; s.ReceiveTimeout = milliseconds; s.SendTimeout = milliseconds; });
                _timeout = value;
            }
            public double? gettimeout() => _timeout;
            public void setblocking(bool flag) => settimeout(flag ? null : 0.0);
            public bool getblocking() => _timeout != 0;

            public void bind(object address) => Run(s => s.Bind(ToEndPoint(address, s.AddressFamily)));
            public void listen(long backlog = 128) => Run(s => s.Listen(checked((int)Math.Max(0, backlog))));

            public (socket, dynamic) accept()
            {
                Socket connected = Run(s =>
                {
                    if (_timeout is double seconds && seconds > 0)
                    {
                        using var deadline = new CancellationTokenSource(TimeoutMilliseconds(seconds));
                        return s.AcceptAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                    }
                    return s.Accept();
                });
                var peer = new socket(connected, this);
                try { return (peer, peer.getpeername()); }
                catch { peer.close(); throw; }
            }

            public void connect(object address)
            {
                Run(s =>
                {
                    IPEndPoint end = ToEndPoint(address, s.AddressFamily);
                    if (_timeout is double seconds && seconds > 0)
                    {
                        using var deadline = new CancellationTokenSource(TimeoutMilliseconds(seconds));
                        s.ConnectAsync(end, deadline.Token).AsTask().GetAwaiter().GetResult();
                    }
                    else s.Connect(end);
                });
            }

            public dynamic getsockname() => Run(s => PythonAddress(s.LocalEndPoint ??
                new IPEndPoint(s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0)));
            public dynamic getpeername() => Run(s => PythonAddress(s.RemoteEndPoint ??
                throw new PyOSError(errno.ENOTCONN, "Socket is not connected")));

            public long send(object data, long flags = 0)
            {
                byte[] bytes = Buffer(data);
                return Run(s => s.Send(bytes, NetFlags(flags)));
            }

            public void sendall(object data, long flags = 0)
            {
                byte[] bytes = Buffer(data);
                var netFlags = NetFlags(flags);
                Run(s =>
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int sent = s.Send(bytes.AsSpan(offset), netFlags);
                        if (sent == 0) throw new BrokenPipeError(errno.EPIPE, "Broken pipe");
                        offset += sent;
                    }
                });
            }

            public PyBytes recv(long bufsize, long flags = 0)
            {
                if (bufsize < 0) throw new ArgumentException("negative buffersize in recv");
                var netFlags = NetFlags(flags);
                if (bufsize == 0) return PyBytes.Empty;
                var bytes = new byte[checked((int)bufsize)];
                int read = Run(s => s.Receive(bytes, netFlags));
                return new PyBytes(read == bytes.Length ? bytes : bytes[..read]);
            }

            public long sendto(object data, object address) => sendto(data, 0, address);

            public long sendto(object data, long flags, object address)
            {
                byte[] bytes = Buffer(data);
                SocketFlags netFlags = NetFlags(flags);
                return Run(s => s.SendTo(bytes, netFlags, ToEndPoint(address, s.AddressFamily)));
            }

            public (PyBytes, dynamic?) recvfrom(long bufsize, long flags = 0)
            {
                if (bufsize < 0) throw new ArgumentException("negative buffersize in recvfrom");
                if (type != SOCK_DGRAM) return (recv(bufsize, flags), null);

                // Receive a whole IP datagram before limiting the returned bytes, including on systems that report truncation as an error.
                const int maxDatagram = 65536;
                SocketFlags netFlags = NetFlags(flags);
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(maxDatagram);
                try
                {
                    EndPoint peer = Run(s => (EndPoint)new IPEndPoint(
                        s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                    int count = Run(s => s.ReceiveFrom(buffer, 0, maxDatagram, netFlags, ref peer));
                    int resultSize = (int)Math.Min(count, bufsize);
                    PyBytes result = resultSize == 0 ? PyBytes.Empty : new PyBytes(buffer.AsSpan(0, resultSize).ToArray());
                    return (result, PythonAddress(peer));
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            }

            public void shutdown(long how)
            {
                if (how is < SHUT_RD or > SHUT_RDWR) throw new PyOSError(errno.EINVAL, "Invalid argument");
                Run(s => s.Shutdown((SocketShutdown)how));
            }

            public void setsockopt(long level, long optname, long value)
            {
                var (netLevel, netName) = Option(level, optname);
                Run(s => s.SetSocketOption(netLevel, netName, checked((int)value)));
            }

            public long getsockopt(long level, long optname)
            {
                var (netLevel, netName) = Option(level, optname);
                return Run(s => Convert.ToInt64(s.GetSocketOption(netLevel, netName), CultureInfo.InvariantCulture));
            }
        }

        public static socket create_connection(object address,
            [System.Runtime.InteropServices.Optional] object? timeout,
            object? source_address = null)
        {
            if (address is not ITuple { Length: 2 } parts) throw new InvalidCastException("address must be a (host, port) tuple");
            string host = Host(parts[0]);
            int port = Port(parts[1]);
            double? seconds = (ReferenceEquals(timeout, System.Reflection.Missing.Value) || ReferenceEquals(timeout, _GLOBAL_DEFAULT_TIMEOUT)) ? getdefaulttimeout() :
                timeout == null ? null : Convert.ToDouble(timeout, CultureInfo.InvariantCulture);
            Exception? last = null;
            foreach (var ip in Resolve(host, AddressFamily.Unspecified))
            {
                socket? connected = null;
                try
                {
                    connected = new socket(PythonFamily(ip.AddressFamily));
                    connected.settimeout(seconds);
                    if (source_address != null) connected.bind(source_address);
                    connected.connect(PythonAddress(new IPEndPoint(ip, port)));
                    return connected;
                }
                catch (Exception e) when (e is PyOSError or TimeoutException)
                {
                    connected?.close();
                    last = e;
                }
                catch { connected?.close(); throw; }
            }
            throw last ?? new gaierror(-2L, "getaddrinfo returned no addresses");
        }

        public static List<(long, long, long, string, dynamic)> getaddrinfo(object? host, object? port,
            long family = AF_UNSPEC, long type = 0, long proto = 0, long flags = 0)
        {
            if ((flags & ~(AI_PASSIVE | AI_CANONNAME | AI_NUMERICHOST)) != 0) throw new gaierror(-1L, "unsupported address info flags");
            var netFamily = NetFamily(family);
            var result = new List<(long, long, long, string, dynamic)>();
            int number = Port(port);
            var kinds = new[] { (SOCK_STREAM, IPPROTO_TCP), (SOCK_DGRAM, IPPROTO_UDP) }
                .Where(k => (type == 0 || type == k.Item1) && (proto == 0 || proto == k.Item2)).ToArray();
            if (kinds.Length == 0) throw new gaierror(-7L, "unsupported socket type or protocol");
            string? name = host == null ? null : Host(host);
            if ((flags & AI_CANONNAME) != 0 && name != null && !IPAddress.TryParse(name, out _))
                throw new NotImplementedException("canonical names for non-numeric hosts are not supported");
            IEnumerable<IPAddress> addresses = name == null
                ? LocalAddresses(netFamily, (flags & AI_PASSIVE) != 0)
                : Resolve(name, netFamily, (flags & AI_NUMERICHOST) != 0);
            foreach (var ip in addresses)
                foreach (var (kind, protocol) in kinds)
                    result.Add((PythonFamily(ip.AddressFamily), kind, protocol,
                        result.Count == 0 && (flags & AI_CANONNAME) != 0 ? name ?? "" : "",
                        PythonAddress(new IPEndPoint(ip, number))));
            return result;
        }

        public static string gethostname() => Dns.GetHostName();
        public static string gethostbyname(string hostname) => Resolve(hostname, AddressFamily.InterNetwork)[0].ToString();

        public static PyBytes inet_aton(string ip_string) =>
            IPAddress.TryParse(ip_string, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                ? new PyBytes(ip.GetAddressBytes()) : throw new PyOSError("illegal IP address string passed to inet_aton");

        public static string inet_ntoa(object packed_ip)
        {
            byte[] bytes = Buffer(packed_ip);
            return bytes.Length == 4 ? new IPAddress(bytes).ToString() : throw new PyOSError("packed IP wrong length for inet_ntoa");
        }

        public static PyBytes inet_pton(long address_family, string ip_string)
        {
            var family = NetFamily(address_family);
            bool strictIpv4 = family != AddressFamily.InterNetwork ||
                ip_string.Split('.') is { Length: 4 } parts && parts.All(p => p.Length > 0 &&
                    (p.Length == 1 || p[0] != '0') && p.All(char.IsAsciiDigit));
            if (!strictIpv4 || ip_string.Contains('%') || !IPAddress.TryParse(ip_string, out var ip) || ip.AddressFamily != family)
                throw new PyOSError("illegal IP address string passed to inet_pton");
            return new PyBytes(ip.GetAddressBytes());
        }

        public static string inet_ntop(long address_family, object packed_ip)
        {
            byte[] bytes = Buffer(packed_ip);
            int size = NetFamily(address_family) == AddressFamily.InterNetwork ? 4 : 16;
            return bytes.Length == size ? new IPAddress(bytes).ToString() : throw new ArgumentException("invalid length of packed IP address");
        }

        public static long htons(long x) => (ushort)IPAddress.HostToNetworkOrder(unchecked((short)checked((ushort)x)));
        public static long ntohs(long x) => htons(x);
        public static long htonl(long x) => (uint)IPAddress.HostToNetworkOrder(unchecked((int)checked((uint)x)));
        public static long ntohl(long x) => htonl(x);

        static AddressFamily NetFamily(long family) => family == AF_INET ? AddressFamily.InterNetwork :
            family == AF_INET6 ? AddressFamily.InterNetworkV6 : family == AF_UNSPEC ? AddressFamily.Unspecified :
            throw new PyOSError(errno.EAFNOSUPPORT, "Address family not supported");

        static long PythonFamily(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? AF_INET6 : AF_INET;

        static SocketType NetType(long type) => type switch
        {
            SOCK_STREAM => SocketType.Stream,
            SOCK_DGRAM => SocketType.Dgram,
            _ => throw new PyOSError(errno.ESOCKTNOSUPPORT, "Socket type not supported"),
        };

        static SocketFlags NetFlags(long flags) => (flags & ~(MSG_OOB | MSG_PEEK | MSG_DONTROUTE)) == 0
            ? (SocketFlags)flags : throw new NotImplementedException("unsupported socket message flags");

        static (SocketOptionLevel, SocketOptionName) Option(long level, long name)
        {
            if (level == IPPROTO_TCP && name == TCP_NODELAY) return (SocketOptionLevel.Tcp, SocketOptionName.NoDelay);
            if (level == SOL_SOCKET)
            {
                if (name == SO_REUSEADDR) return (SocketOptionLevel.Socket, SocketOptionName.ReuseAddress);
                if (name == SO_KEEPALIVE) return (SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
                if (name == SO_ERROR) return (SocketOptionLevel.Socket, SocketOptionName.Error);
                if (name == SO_TYPE) return (SocketOptionLevel.Socket, SocketOptionName.Type);
            }
            throw new PyOSError(errno.ENOPROTOOPT, "Socket option not supported");
        }

        static string Host(object? host) => host switch
        {
            string text => text,
            PyBytes bytes => bytes.decode("ascii"),
            _ => throw new InvalidCastException("host must be a string or bytes"),
        };

        static long Integer(object value) => value switch
        {
            long number => number,
            int number => number,
            bool flag => flag ? 1L : 0L,
            BigInteger number => checked((long)number),
            _ => throw new InvalidCastException("an integer is required"),
        };

        static int Port(object? value)
        {
            long number = value switch
            {
                null => 0,
                string service => long.TryParse(service, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                    ? parsed : throw new NotImplementedException("named services are not supported"),
                _ => Integer(value),
            };
            if (number is < 0 or > ushort.MaxValue) throw new OverflowException("port must be 0-65535");
            return (int)number;
        }

        static IEnumerable<IPAddress> LocalAddresses(AddressFamily family, bool any)
        {
            if (has_ipv6 && family != AddressFamily.InterNetwork) yield return any ? IPAddress.IPv6Any : IPAddress.IPv6Loopback;
            if (family != AddressFamily.InterNetworkV6) yield return any ? IPAddress.Any : IPAddress.Loopback;
        }

        static IPAddress[] Resolve(string host, AddressFamily family, bool numeric = false)
        {
            IPAddress[] found;
            if (IPAddress.TryParse(host, out var ip)) found = new[] { ip };
            else if (numeric) throw new gaierror(-2L, "host is not a numeric address");
            else if (host.Length == 0) found = LocalAddresses(family, any: true).ToArray();
            else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) found = LocalAddresses(family, any: false).ToArray();
            else
            {
                try { found = Dns.GetHostAddresses(host); }
                catch (SocketException e) { throw new gaierror(-2L, e.Message); }
            }
            found = found.Where(a => family == AddressFamily.Unspecified || a.AddressFamily == family).ToArray();
            return found.Length > 0 ? found : throw new gaierror(-2L, "no address in requested family");
        }

        static IPEndPoint ToEndPoint(object address, AddressFamily family)
        {
            if (address is not ITuple parts || parts.Length < 2 ||
                parts.Length > (family == AddressFamily.InterNetworkV6 ? 4 : 2))
                throw new InvalidCastException("invalid Internet socket address tuple");
            if (parts.Length >= 3 && Integer(parts[2]!) != 0)
                throw new NotImplementedException("nonzero IPv6 flow information is not supported");
            var ip = Resolve(Host(parts[0]), family)[0];
            if (parts.Length == 4) ip = new IPAddress(ip.GetAddressBytes(), Integer(parts[3]!));
            return new IPEndPoint(ip, Port(parts[1]));
        }

        static object PythonAddress(EndPoint endpoint)
        {
            if (endpoint is not IPEndPoint ip) throw new NotImplementedException("only Internet addresses are supported");
            string host = new IPAddress(ip.Address.GetAddressBytes()).ToString();
            return ip.AddressFamily == AddressFamily.InterNetworkV6 ? (host, (long)ip.Port, 0L, ip.Address.ScopeId) : (host, (long)ip.Port);
        }

        static byte[] Buffer(object data) => data switch
        {
            PyBytes bytes => bytes.Data,
            PyByteArray array => array.ToArray(),
            global::memoryview view => view.ToArray(),
            _ => throw new InvalidCastException("a bytes-like object is required"),
        };

        static Exception SocketFailure(SocketException e, bool timed = false)
        {
            long code = e.NativeErrorCode;
            return e.SocketErrorCode switch
            {
                SocketError.TimedOut => new TimeoutException("timed out"),
                SocketError.WouldBlock when timed => new TimeoutException("timed out"),
                SocketError.WouldBlock or SocketError.InProgress or SocketError.AlreadyInProgress => new BlockingIOError(code, e.Message),
                SocketError.ConnectionRefused => new ConnectionRefusedError(code, e.Message),
                SocketError.ConnectionReset => new ConnectionResetError(code, e.Message),
                SocketError.ConnectionAborted => new ConnectionAbortedError(code, e.Message),
                _ when code == errno.EPIPE => new BrokenPipeError(code, e.Message),
                _ => new PyOSError(code, e.Message),
            };
        }
    }
}
