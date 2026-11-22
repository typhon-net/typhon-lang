// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Typhon.Runtime;
    using Path = System.IO.Path;

    /// <summary>GNU message catalogs, domain bindings and translation fallbacks.</summary>
    internal static class gettext_
    {
        static readonly string DefaultDirectory = Path.Combine(sys_.base_prefix, "share", "locale");
        static readonly ConcurrentDictionary<string, string> DomainDirectories = new(StringComparer.Ordinal);
        static readonly ConcurrentDictionary<string, Catalog> Catalogs = new();
        static volatile string _domain = "messages";

        public static string textdomain(string? domain = null) => domain == null ? _domain : _domain = domain;
        public static string bindtextdomain(string domain, string? localedir = null) => localedir != null
            ? DomainDirectories[domain] = localedir : DomainDirectories.TryGetValue(domain, out string? dir) ? dir : DefaultDirectory;

        static IEnumerable<string> Languages()
        {
            foreach (string name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) return value.Split(':');
            }
            return new[] { "C" };
        }

        static IEnumerable<string> LocaleCandidates(string locale)
        {
            int modifierAt = locale.IndexOf('@');
            string modifier = modifierAt < 0 ? "" : locale[modifierAt..];
            string languageAndEncoding = modifierAt < 0 ? locale : locale[..modifierAt];
            int encodingAt = languageAndEncoding.IndexOf('.');
            string encoding = encodingAt < 0 ? "" : languageAndEncoding[encodingAt..];
            string languageAndRegion = (encodingAt < 0 ? languageAndEncoding : languageAndEncoding[..encodingAt]).Replace('-', '_');
            int regionAt = languageAndRegion.IndexOf('_');
            string region = regionAt < 0 ? "" : languageAndRegion[regionAt..];
            string language = regionAt < 0 ? languageAndRegion : languageAndRegion[..regionAt];
            // Search the preferred language with each available modifier, territory and codeset combination.
            for (int mask = 7; mask >= 0; mask--)
            {
                if ((mask & 4) != 0 && modifier.Length == 0 || (mask & 2) != 0 && region.Length == 0 || (mask & 1) != 0 && encoding.Length == 0)
                    continue;
                yield return language + ((mask & 2) == 0 ? "" : region) + ((mask & 1) == 0 ? "" : encoding) + ((mask & 4) == 0 ? "" : modifier);
            }
        }

        static IEnumerable<string> CatalogPaths(string domain, string dir, object? languages)
        {
            var requested = languages == null ? Languages() : Builtins.__iter_dyn(languages).Select(value => (string)value);
            foreach (string locale in requested.SelectMany(LocaleCandidates).Distinct(StringComparer.Ordinal))
            {
                if (locale == "C") yield break;
                string file = Path.Combine(dir, locale, "LC_MESSAGES", domain + ".mo");
                if (File.Exists(file)) yield return file;
            }
        }

        public static dynamic find(string domain, string? localedir = null, object? languages = null, bool all = false)
        {
            var files = CatalogPaths(domain, localedir ?? DefaultDirectory, languages);
            return all ? files.ToList() : files.FirstOrDefault();
        }

        public static NullTranslations translation(string domain, string? localedir = null, object? languages = null, bool fallback = false)
        {
            NullTranslations? first = null, prior = null;
            foreach (string file in CatalogPaths(domain, localedir ?? DefaultDirectory, languages))
            {
                var messages = Catalogs.GetOrAdd(Path.GetFullPath(file), path => Catalog.Load(File.ReadAllBytes(path)));
                var current = new GNUTranslations(messages);
                first ??= current;
                prior?.add_fallback(current);
                prior = current;
            }
            if (first != null) return first;
            if (fallback) return new NullTranslations();
            throw new FileNotFoundException($"No translation file found for domain: '{domain}'");
        }

        static NullTranslations Domain(string domain)
        {
            try { return translation(domain, bindtextdomain(domain), fallback: true); }
            catch (IOException) { return new NullTranslations(); }
        }

        public static string gettext(string message) => dgettext(_domain, message);
        public static string ngettext(string msgid1, string msgid2, long n) => dngettext(_domain, msgid1, msgid2, n);
        public static string pgettext(string context, string message) => dpgettext(_domain, context, message);
        public static string npgettext(string context, string msgid1, string msgid2, long n) => dnpgettext(_domain, context, msgid1, msgid2, n);
        public static string dgettext(string domain, string message) => Domain(domain).gettext(message);
        public static string dngettext(string domain, string msgid1, string msgid2, long n) => Domain(domain).ngettext(msgid1, msgid2, n);
        public static string dpgettext(string domain, string context, string message) => Domain(domain).pgettext(context, message);
        public static string dnpgettext(string domain, string context, string msgid1, string msgid2, long n) => Domain(domain).npgettext(context, msgid1, msgid2, n);

        internal class NullTranslations
        {
            protected NullTranslations? Fallback;
            public NullTranslations(object? fp = null) { }
            public void add_fallback(NullTranslations fallback)
            {
                if (Fallback == null) Fallback = fallback;
                else Fallback.add_fallback(fallback);
            }
            public virtual string gettext(string message) => Fallback?.gettext(message) ?? message;
            public virtual string ngettext(string msgid1, string msgid2, long n) => Fallback?.ngettext(msgid1, msgid2, n) ?? (n == 1 ? msgid1 : msgid2);
            public virtual string pgettext(string context, string message) => Fallback?.pgettext(context, message) ?? message;
            public virtual string npgettext(string context, string msgid1, string msgid2, long n) => Fallback?.npgettext(context, msgid1, msgid2, n) ?? (n == 1 ? msgid1 : msgid2);
            public virtual OrderedDictionary<string, string> info() => new();
            public virtual string? charset() => null;
        }

        internal sealed class GNUTranslations : NullTranslations
        {
            readonly Catalog _catalog;
            public GNUTranslations(object? fp = null) : this(fp == null ? new Catalog() : Catalog.Load(((PyBytes)((dynamic)fp).read()).Data)) { }
            internal GNUTranslations(Catalog catalog) { _catalog = catalog; }
            public override string gettext(string message) => _catalog.Find(message) ?? base.gettext(message);
            public override string ngettext(string msgid1, string msgid2, long n) => _catalog.FindPlural(msgid1, n) ?? base.ngettext(msgid1, msgid2, n);
            public override string pgettext(string context, string message) => _catalog.Find(context + "\x04" + message) ?? base.pgettext(context, message);
            public override string npgettext(string context, string msgid1, string msgid2, long n) =>
                _catalog.FindPlural(context + "\x04" + msgid1, n) ?? base.npgettext(context, msgid1, msgid2, n);
            public override OrderedDictionary<string, string> info() => new(_catalog.Info);
            public override string? charset() => _catalog.Charset;
        }

        internal sealed class Catalog
        {
            public readonly OrderedDictionary<string, string> Info = new();
            readonly Dictionary<string, string> _messages = new(StringComparer.Ordinal);
            readonly Dictionary<string, string[]> _pluralMessages = new(StringComparer.Ordinal);
            public string? Charset;
            public Func<long, long> Plural = n => n == 1 ? 0 : 1;

            public string? Find(string message) => _messages.TryGetValue(message, out string? translated) ? translated : FindPlural(message, 1);
            public string? FindPlural(string message, long n)
            {
                if (!_pluralMessages.TryGetValue(message, out var variants)) return null;
                long index = Plural(n);
                return index >= 0 && index < variants.Length ? variants[index] : null;
            }

            public static Catalog Load(byte[] data)
            {
                const uint Magic = 0x950412de;
                if (data.Length < 28) throw new IOException("Corrupt GNU message catalog");
                bool little = BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;
                if (!little && BinaryPrimitives.ReadUInt32BigEndian(data) != Magic)
                    throw new IOException("Bad magic number in GNU message catalog");
                uint Read(long offset)
                {
                    if (offset < 0 || offset + 4 > data.Length) throw new IOException("Corrupt GNU message catalog");
                    var span = data.AsSpan((int)offset, 4);
                    return little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
                }
                ReadOnlySpan<byte> Entry(long table, long i)
                {
                    uint size = Read(table + 8 * i), offset = Read(table + 8 * i + 4);
                    if ((long)offset + size >= data.Length) throw new IOException("Corrupt GNU message catalog");
                    return data.AsSpan((int)offset, (int)size);
                }
                if ((Read(4) >> 16) > 1) throw new IOException("Unsupported GNU message catalog version");
                long count = Read(8), originals = Read(12), translations = Read(16);
                if (originals + 8 * count > data.Length || translations + 8 * count > data.Length)
                    throw new IOException("Corrupt GNU message catalog");

                var result = new Catalog();
                for (long i = 0; i < count; i++)
                {
                    if (!Entry(originals, i).IsEmpty) continue;
                    string prior = "";
                    foreach (string entry in new UTF8Encoding(false, true).GetString(Entry(translations, i)).Split('\n'))
                    {
                        string line = entry.Trim();
                        if (line.StartsWith("#-#-#-#-#", StringComparison.Ordinal) && line.EndsWith("#-#-#-#-#", StringComparison.Ordinal)) continue;
                        int colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            prior = line[..colon].Trim().ToLowerInvariant();
                            result.Info[prior] = line[(colon + 1)..].Trim();
                        }
                        else if (prior.Length > 0 && line.Length > 0) result.Info[prior] += "\n" + line;
                    }
                    break;
                }
                if (result.Info.TryGetValue("content-type", out string? content))
                {
                    int start = content.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                    if (start >= 0) result.Charset = content[(start + "charset=".Length)..].Split(';')[0].Trim();
                }
                if (result.Info.TryGetValue("plural-forms", out string? pluralForms))
                    foreach (string expression in pluralForms.Split(';'))
                    {
                        int equals = expression.IndexOf('=');
                        if (equals > 0 && expression[..equals].Trim() == "plural") result.Plural = c2py(expression[(equals + 1)..]);
                    }
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var encoding = Encoding.GetEncoding(result.Charset ?? "ASCII", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                for (long i = 0; i < count; i++)
                {
                    var original = Entry(originals, i);
                    int separator = original.IndexOf((byte)0);
                    string id = encoding.GetString(separator < 0 ? original : original[..separator]);
                    string translated = encoding.GetString(Entry(translations, i));
                    if (separator < 0) result._messages[id] = translated;
                    else result._pluralMessages[id] = translated.Split('\0');
                }
                return result;
            }
        }

        public static Func<long, long> c2py(string plural)
        {
            if (plural.Length > 1000) throw new ArgumentException("plural form expression is too long");
            return new PluralParser(plural).Parse();
        }

        sealed class PluralParser
        {
            static readonly Dictionary<string, int> Precedence = new(StringComparer.Ordinal)
            {
                ["||"] = 1, ["&&"] = 2, ["=="] = 3, ["!="] = 3, ["<"] = 4, [">="] = 4, [">"] = 4, ["<="] = 4,
                ["+"] = 5, ["-"] = 5, ["*"] = 6, ["/"] = 6, ["%"] = 6,
            };
            readonly string _source;
            int _position;
            string _token;
            public PluralParser(string source) { _source = source; _token = Next(); }

            string Next()
            {
                while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++;
                if (_position == _source.Length) return "";
                int start = _position++;
                char first = _source[start];
                if (char.IsAsciiDigit(first))
                    while (_position < _source.Length && char.IsAsciiDigit(_source[_position])) _position++;
                else if (_position < _source.Length && Precedence.ContainsKey(_source.Substring(start, 2))) _position++;
                return _source[start.._position];
            }
            bool Accept(string token)
            {
                if (_token != token) return false;
                _token = Next();
                return true;
            }
            void Require(string token)
            {
                if (!Accept(token)) throw new ArgumentException($"expected '{token}' in plural form expression");
            }
            public Func<long, long> Parse()
            {
                var result = Expression(0);
                Require("");
                return result;
            }
            Func<long, long> Expression(int minimum)
            {
                var left = Atom();
                while (Precedence.TryGetValue(_token, out int priority) && priority >= minimum)
                {
                    string op = _token;
                    _token = Next();
                    var right = Expression(priority + 1);
                    var prior = left;
                    left = op switch
                    {
                        "&&" => n => prior(n) != 0 && right(n) != 0 ? 1 : 0,
                        "||" => n => prior(n) != 0 || right(n) != 0 ? 1 : 0,
                        _ => n => Binary(op, prior(n), right(n)),
                    };
                }
                if (minimum != 0 || !Accept("?")) return left;
                var yes = Expression(0);
                Require(":");
                var no = Expression(0);
                return n => left(n) != 0 ? yes(n) : no(n);
            }
            Func<long, long> Atom()
            {
                if (Accept("n")) return n => n;
                if (Accept("!")) { var operand = Atom(); return n => operand(n) == 0 ? 1 : 0; }
                if (Accept("(")) { var expr = Expression(0); Require(")"); return expr; }
                if (!long.TryParse(_token, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                    throw new ArgumentException($"invalid token '{_token}' in plural form expression");
                _token = Next();
                return _ => value;
            }
            static long Binary(string op, long a, long b) => op switch
            {
                "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b,
                "==" => a == b ? 1 : 0, "!=" => a != b ? 1 : 0, "<" => a < b ? 1 : 0, "<=" => a <= b ? 1 : 0,
                ">" => a > b ? 1 : 0, ">=" => a >= b ? 1 : 0,
                _ => throw new InvalidOperationException("unknown plural form operator"),
            };
        }
    }
}
