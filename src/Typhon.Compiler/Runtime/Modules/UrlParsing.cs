// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// Parsing and compatibility rules follow CPython Lib/urllib/parse.py.
// The upstream license is provided in CPython-LICENSE.txt.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using Typhon.Runtime;

    internal static partial class urllib
    {
        internal static partial class parse
        {
            public static List<string> uses_params = new()
            {
                "", "ftp", "hdl", "prospero", "http", "imap", "https", "shttp", "rtsp",
                "rtsps", "rtspu", "sip", "sips", "mms", "sftp", "tel",
            };

            public static List<string> uses_netloc = new()
            {
                "", "ftp", "http", "gopher", "nntp", "telnet", "imap", "wais", "file", "mms", "https",
                "shttp", "snews", "prospero", "rtsp", "rtsps", "rtspu", "rsync", "svn", "svn+ssh", "sftp",
                "nfs", "git", "git+ssh", "ws", "wss", "itms-services",
            };

            static readonly char[] ControlAndSpace = Enumerable.Range(0, 33).Select(code => (char)code).ToArray();
            static readonly char[] AuthoritySeparators = ['/', '?', '#'];
            static readonly char[] NormalizedDelimiters = ['/', '?', '#', '@', ':'];

            public static ParseResult urlparse(string? url, string? scheme = null, bool allow_fragments = true,
                [PyKeywordOnly] bool missing_as_none = false) => new(ParseUrl(url ?? "", scheme, allow_fragments, missing_as_none, splitParams: true));

            public static dynamic urlparse(object? url, object? scheme = null, bool allow_fragments = true,
                [PyKeywordOnly] bool missing_as_none = false)
            {
                var (text, protocol, binary) = CoerceUrl(url, scheme);
                var parts = ParseUrl(text, protocol, allow_fragments, missing_as_none, splitParams: true);
                return binary ? (object)new ParseResultBytes(parts) : new ParseResult(parts);
            }

            public static SplitResult urlsplit(string? url, string? scheme = null, bool allow_fragments = true,
                [PyKeywordOnly] bool missing_as_none = false) => new(ParseUrl(url ?? "", scheme, allow_fragments, missing_as_none, splitParams: false));

            public static dynamic urlsplit(object? url, object? scheme = null, bool allow_fragments = true,
                [PyKeywordOnly] bool missing_as_none = false)
            {
                var (text, protocol, binary) = CoerceUrl(url, scheme);
                var parts = ParseUrl(text, protocol, allow_fragments, missing_as_none, splitParams: false);
                return binary ? (object)new SplitResultBytes(parts) : new SplitResult(parts);
            }

            static (string Url, string? Scheme, bool Binary) CoerceUrl(object? url, object? scheme)
            {
                object? first = Builtins.truthy(url) ? url : Builtins.truthy(scheme) ? scheme : url ?? scheme;
                bool binary = first != null && first is not string;
                string? AsText(object? value)
                {
                    if (value == null) return null;
                    if (value is string text && (!binary || text.Length == 0)) return text;
                    if (value is PyBytes bytes && (binary || bytes.Count == 0)) return Decode(bytes.Data, "ascii", "strict");
                    if (value is PyByteArray array && (binary || array.Count == 0)) return Decode(array.ToArray(), "ascii", "strict");
                    throw new InvalidCastException("Cannot mix str and non-str arguments");
                }
                return (AsText(url) ?? "", AsText(scheme), binary);
            }

            static string WithoutLineControls(string text) => text.Replace("\t", "").Replace("\r", "").Replace("\n", "");

            static string? Cut(ref string text, char separator, int start = 0)
            {
                int index = text.IndexOf(separator, start);
                if (index < 0) return null;
                string tail = text[(index + 1)..];
                text = text[..index];
                return tail;
            }

            static UrlParts ParseUrl(string path, string? scheme, bool allowFragments, bool keepEmpty, bool splitParams)
            {
                path = WithoutLineControls(path.TrimStart(ControlAndSpace));
                if (scheme != null) scheme = WithoutLineControls(scheme.Trim(ControlAndSpace));
                int colon = path.IndexOf(':');
                if (colon > 0 && char.IsAsciiLetter(path[0]))
                {
                    bool validScheme = true;
                    for (int i = 1; i < colon && validScheme; i++)
                        validScheme = char.IsAsciiLetterOrDigit(path[i]) || path[i] is '+' or '-' or '.';
                    if (validScheme)
                    {
                        scheme = path[..colon].ToLowerInvariant();
                        path = path[(colon + 1)..];
                    }
                }

                string? authority = null;
                if (path.StartsWith("//", StringComparison.Ordinal))
                {
                    int delimiter = path.IndexOfAny(AuthoritySeparators, 2);
                    if (delimiter < 0) delimiter = path.Length;
                    authority = path[2..delimiter];
                    path = path[delimiter..];
                    CheckAuthority(authority);
                }

                string? fragment = allowFragments ? Cut(ref path, '#') : null;
                string? query = Cut(ref path, '?');
                string? parameters = splitParams && uses_params.Contains(scheme ?? "")
                    ? Cut(ref path, ';', path.LastIndexOf('/') + 1) : null;
                return new UrlParts(scheme, authority, path, parameters, query, fragment, keepEmpty);
            }

            static (string? UserInfo, string HostAndPort) AuthorityParts(string value)
            {
                int at = value.LastIndexOf('@');
                return at < 0 ? (null, value) : (value[..at], value[(at + 1)..]);
            }

            static void CheckAuthority(string authority)
            {
                bool opens = authority.Contains('['), closes = authority.Contains(']');
                if (opens != closes) throw new ArgumentException("Invalid IPv6 URL");
                if (opens)
                {
                    string hostAndPort = AuthorityParts(authority).HostAndPort;
                    int end = hostAndPort.IndexOf(']');
                    if (end < 0 || !hostAndPort.StartsWith('[') || end + 1 < hostAndPort.Length && hostAndPort[end + 1] != ':')
                        throw new ArgumentException("Invalid IPv6 URL");
                    string host = hostAndPort[1..end];
                    if (host.StartsWith('v') || host.StartsWith('V'))
                    {
                        int dot = host.IndexOf('.');
                        if (dot <= 1 || dot + 1 == host.Length || !host[1..dot].All(char.IsAsciiHexDigit))
                            throw new ArgumentException("IPvFuture address is invalid");
                    }
                    else
                    {
                        int zone = host.IndexOf('%');
                        if (zone >= 0 && (zone + 1 == host.Length || host.IndexOf('%', zone + 1) >= 0))
                            throw new ArgumentException("Invalid IPv6 URL");
                        if (!IPAddress.TryParse(zone < 0 ? host : host[..zone], out var ip) ||
                            ip.AddressFamily != AddressFamily.InterNetworkV6)
                            throw new ArgumentException("Invalid IPv6 URL");
                    }
                }

                if (authority.All(char.IsAscii)) return;
                string plain = authority.Replace("@", "").Replace(":", "").Replace("#", "").Replace("?", "");
                if (plain.Normalize(NormalizationForm.FormKC).IndexOfAny(NormalizedDelimiters) >= 0)
                    throw new ArgumentException($"netloc '{authority}' contains invalid characters under NFKC normalization");
            }

            internal readonly record struct UrlParts(string? Scheme, string? Authority, string Path,
                string? Parameters, string? Query, string? Fragment, bool KeepEmpty)
            {
                internal UrlParts Map(Func<string, string> convert) => new(
                    Scheme != null ? convert(Scheme) : null, Authority != null ? convert(Authority) : null, convert(Path),
                    Parameters != null ? convert(Parameters) : null, Query != null ? convert(Query) : null,
                    Fragment != null ? convert(Fragment) : null, KeepEmpty);

                internal string Reassemble()
                {
                    string path = Path;
                    string? authority = Authority;
                    if (!KeepEmpty && string.IsNullOrEmpty(authority))
                        authority = !string.IsNullOrEmpty(Scheme) && uses_netloc.Contains(Scheme) &&
                            (path.Length == 0 || path.StartsWith('/')) ? "" : null;
                    if (Parameters != null && (KeepEmpty || Parameters.Length != 0)) path += ";" + Parameters;
                    if (authority != null)
                        path = "//" + authority + (path.Length != 0 && !path.StartsWith('/') ? "/" : "") + path;
                    else if (path.StartsWith("//", StringComparison.Ordinal)) path = "//" + path;
                    if (!string.IsNullOrEmpty(Scheme)) path = Scheme + ":" + path;
                    if (Query != null && (KeepEmpty || Query.Length > 0)) path += "?" + Query;
                    if (Fragment != null && (KeepEmpty || Fragment.Length > 0)) path += "#" + Fragment;
                    return path;
                }
            }

            internal abstract class ParsedUrl<T>(UrlParts parts, Func<string, T> convert, bool includeParams) : PyStructSeq where T : class
            {
                protected UrlParts Parts => parts;
                T? Optional(string? text) => text != null ? convert(text) : parts.KeepEmpty ? null : convert("");
                T? AuthorityAttribute(string? text) => text == null ? null : convert(text);

                public T? scheme => Optional(parts.Scheme);
                public T? netloc => Optional(parts.Authority);
                public T path => convert(parts.Path);
                public T? query => Optional(parts.Query);
                public T? fragment => Optional(parts.Fragment);
                protected T? Parameters => Optional(parts.Parameters);
                public T geturl() => convert(parts.Reassemble());
                public object? this[long index] => Item(index);

                (string? User, string? Password) UserInfo()
                {
                    string? user = AuthorityParts(parts.Authority ?? "").UserInfo;
                    string? password = user == null ? null : Cut(ref user, ':');
                    return (user, password);
                }

                (string Host, string? Port) HostInfo()
                {
                    string host = AuthorityParts(parts.Authority ?? "").HostAndPort;
                    string? port;
                    if (host.StartsWith('[') && host.IndexOf(']') is int end && end > 0)
                    {
                        port = end + 1 < host.Length ? host[(end + 2)..] : null;
                        host = host[1..end];
                    }
                    else port = Cut(ref host, ':');
                    return (host, string.IsNullOrEmpty(port) ? null : port);
                }

                public T? username => AuthorityAttribute(UserInfo().User);
                public T? password => AuthorityAttribute(UserInfo().Password);

                public T? hostname
                {
                    get
                    {
                        string host = HostInfo().Host;
                        if (host.Length == 0) return null;
                        int zone = host.IndexOf('%');
                        return convert(zone < 0 ? host.ToLowerInvariant() : host[..zone].ToLowerInvariant() + host[zone..]);
                    }
                }

                public long? port
                {
                    get
                    {
                        string? text = HostInfo().Port;
                        if (text == null) return null;
                        if (!text.All(char.IsAsciiDigit)) throw new ArgumentException($"Port could not be cast to integer value as {Builtins.repr(text)}");
                        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > 65535)
                            throw new ArgumentException("Port out of range 0-65535");
                        return value;
                    }
                }

                static readonly string[] ParseFields = ["scheme", "netloc", "path", "params", "query", "fragment"];
                static readonly string[] SplitFields = ["scheme", "netloc", "path", "query", "fragment"];
                protected override string TypeName => GetType().Name;
                protected override string[] Fields => includeParams ? ParseFields : SplitFields;
                protected override object?[] Values => includeParams ? [scheme, netloc, path, Parameters, query, fragment] : [scheme, netloc, path, query, fragment];
                public PyTuple<string> _fields => new(Fields);
            }

            static PyBytes Encoded(string text, string encoding, string errors) => new(Encode(text, encoding, errors));

            internal sealed class ParseResult(UrlParts parts) : ParsedUrl<string>(parts, text => text, includeParams: true)
            {
                public string? @params => Parameters;
                public ParseResultBytes encode(string encoding = "ascii", string errors = "strict") =>
                    new(Parts.Map(text => Decode(Encode(text, encoding, errors), encoding, errors)), encoding, errors);
            }

            internal sealed class SplitResult(UrlParts parts) : ParsedUrl<string>(parts, text => text, includeParams: false)
            {
                public SplitResultBytes encode(string encoding = "ascii", string errors = "strict") =>
                    new(Parts.Map(text => Decode(Encode(text, encoding, errors), encoding, errors)), encoding, errors);
            }

            internal sealed class ParseResultBytes(UrlParts parts, string codec = "ascii", string errorMode = "strict")
                : ParsedUrl<PyBytes>(parts, text => Encoded(text, codec, errorMode), includeParams: true)
            {
                public PyBytes? @params => Parameters;
                public ParseResult decode(string encoding = "ascii", string errors = "strict") =>
                    new(Parts.Map(text => Decode(Encode(text, codec, errorMode), encoding, errors)));
            }

            internal sealed class SplitResultBytes(UrlParts parts, string codec = "ascii", string errorMode = "strict")
                : ParsedUrl<PyBytes>(parts, text => Encoded(text, codec, errorMode), includeParams: false)
            {
                public SplitResult decode(string encoding = "ascii", string errors = "strict") =>
                    new(Parts.Map(text => Decode(Encode(text, codec, errorMode), encoding, errors)));
            }
        }
    }
}
