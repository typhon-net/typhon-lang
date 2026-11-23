// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Xml;
    using Typhon.Runtime;

    internal static class xml
    {
        internal static class parsers
        {
            internal static class expat
            {
                public static Parser ParserCreate(string? encoding = null, string? namespace_separator = null)
                {
                    if (namespace_separator?.Length > 1)
                        throw new ArgumentException("namespace_separator must be at most one character, omitted, or None");
                    return new Parser(encoding, namespace_separator);
                }

                internal sealed class ExpatError(string message, long line, long column) : ArgumentException(message)
                {
                    public long lineno { get; } = line;
                    public long offset { get; } = column;
                }

                internal sealed class Parser(string? encoding, string? separator)
                {
                    static readonly Regex EntityDeclarations = new(
                        """<!--.*?-->|<!ENTITY\s+(?:(?<parameter>%)\s+)?(?<name>[^\s>]+)\s+(?:['"](?<value>.*?)['"]|(?<external>SYSTEM|PUBLIC)\s+['"](?<identifier>.*?)['"](?:\s+['"](?<system>.*?)['"])?)(?:\s+NDATA\s+(?<notation>[^\s>]+))?\s*>""",
                        RegexOptions.Singleline | RegexOptions.CultureInvariant);

                    public object? StartElementHandler { get; set; }
                    public object? EndElementHandler { get; set; }
                    public object? CharacterDataHandler { get; set; }
                    public object? EntityDeclHandler { get; set; }
                    public long CurrentLineNumber { get; private set; } = 1;

                    public long ParseFile(object file) => ReadDocument(new PyObjectStream(file));

                    public long Parse(object data)
                    {
                        if (data is string text)
                        {
                            using var input = new StringReader(text);
                            using var reader = XmlReader.Create(input, Options());
                            return ReadEvents(reader);
                        }
                        using var inputBytes = new MemoryStream(ByteOps.Buffer(data), writable: false);
                        return ReadDocument(inputBytes);
                    }

                    long ReadDocument(Stream input)
                    {
                        if (encoding == null)
                        {
                            using var reader = XmlReader.Create(input, Options());
                            return ReadEvents(reader);
                        }
                        using var text = new StreamReader(input, ByteOps.EncodingOf(encoding, "strict"), leaveOpen: true);
                        using var decoded = XmlReader.Create(text, Options());
                        return ReadEvents(decoded);
                    }

                    static XmlReaderSettings Options() => new()
                    {
                        DtdProcessing = DtdProcessing.Parse,
                        XmlResolver = null,
                        MaxCharactersFromEntities = 1_000_000,
                        CloseInput = false,
                    };

                    long ReadEvents(XmlReader reader)
                    {
                        var location = (IXmlLineInfo)reader;
                        try
                        {
                            while (reader.Read())
                            {
                                CurrentLineNumber = location.LineNumber;
                                switch (reader.NodeType)
                                {
                                    case XmlNodeType.Element:
                                        string name = Name(reader);
                                        bool empty = reader.IsEmptyElement;
                                        var attributes = new OrderedDictionary<string, string>(StringComparer.Ordinal);
                                        while (reader.MoveToNextAttribute())
                                            if (separator == null || reader.NamespaceURI != "http://www.w3.org/2000/xmlns/")
                                                attributes[Name(reader)] = reader.Value;
                                        reader.MoveToElement();
                                        Invoke(StartElementHandler, name, attributes);
                                        if (empty) Invoke(EndElementHandler, name);
                                        break;
                                    case XmlNodeType.EndElement:
                                        Invoke(EndElementHandler, Name(reader));
                                        break;
                                    case XmlNodeType.Text:
                                    case XmlNodeType.CDATA:
                                    case XmlNodeType.Whitespace:
                                    case XmlNodeType.SignificantWhitespace:
                                        if (reader.Depth > 0) Invoke(CharacterDataHandler, reader.Value);
                                        break;
                                    case XmlNodeType.DocumentType:
                                        ReadDeclarations(reader.Value);
                                        break;
                                }
                            }
                            return 1;
                        }
                        catch (XmlException error)
                        {
                            throw new ExpatError(error.Message, error.LineNumber, Math.Max(0, error.LinePosition - 1));
                        }
                    }

                    string Name(XmlReader reader) => separator is string joiner && reader.NamespaceURI.Length > 0
                        ? reader.NamespaceURI + joiner + reader.LocalName : reader.Name;

                    void ReadDeclarations(string subset)
                    {
                        if (EntityDeclHandler == null) return;
                        foreach (Match entity in EntityDeclarations.Matches(subset))
                        {
                            if (!entity.Groups["name"].Success) continue;
                            string? Optional(string group) => entity.Groups[group].Success ? entity.Groups[group].Value : null;
                            bool isPublic = Optional("external") == "PUBLIC";
                            Invoke(EntityDeclHandler, Optional("name"), entity.Groups["parameter"].Success ? 1L : 0L,
                                Optional("value"), null, isPublic ? Optional("system") : Optional("identifier"),
                                isPublic ? Optional("identifier") : null, Optional("notation"));
                        }
                    }

                    static void Invoke(object? callback, params object?[] arguments)
                    {
                        if (callback != null) Builtins.__invoke(callback, arguments);
                    }
                }
            }
        }
    }
}
