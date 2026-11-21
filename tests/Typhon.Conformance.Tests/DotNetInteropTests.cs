// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

/// <summary>
/// Typhon classes deriving from .NET classes and carrying .NET attributes. These cannot be conformance cases (CPython cannot
/// import .NET), so their expected output is written here.
/// </summary>
public sealed class DotNetInteropTests
{
    /// <summary>
    /// An override of a protected .NET member is protected (C# rejects an override that changes the access), also through a
    /// Typhon base class and for an abstract member: Collection&lt;T&gt;.InsertItem and KeyedCollection.GetKeyForItem here,
    /// ComponentBase.OnInitialized in samples/BlazorApp.
    /// </summary>
    [Fact]
    public void OverridesOfProtectedMembersStayProtected()
    {
        const string source = """
            from System.Collections.ObjectModel import Collection, KeyedCollection
            from System.Reflection import BindingFlags


            class Shouting(Collection[str]):
                def InsertItem(self, index: int32, item: str) -> None:
                    super().InsertItem(index, item.upper())


            class Whispering(Shouting):
                def InsertItem(self, index: int32, item: str) -> None:
                    super().InsertItem(index, item + "...")


            class ByInitial(KeyedCollection[str, str]):
                def GetKeyForItem(self, item: str) -> str:
                    return item[:1]


            s = Shouting()
            s.Add("hello")
            w = Whispering()
            w.Add("psst")
            k = ByInitial()
            k.Add("apple")
            k.Add("banana")
            print(s[0], w[0], k["b"])
            flags = BindingFlags.NonPublic | BindingFlags.Instance
            for t, name in [(s.GetType(), "InsertItem"), (w.GetType(), "InsertItem"), (k.GetType(), "GetKeyForItem")]:
                m = t.GetMethod(name, flags)
                print(t.Name, name, "protected" if m.IsFamily else "public" if m.IsPublic else "other")
            """;
        string stdout = IntegerTests.RunScript(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal(
            "HELLO PSST... banana\n" +
            "Shouting InsertItem protected\n" +
            "Whispering InsertItem protected\n" +
            "ByInitial GetKeyForItem protected\n", stdout);
    }

    /// <summary>
    /// typing.Annotated metadata that names a .NET attribute puts the attribute on the property a class-level annotation
    /// declares (Blazor's [Parameter] and [Inject], DataAnnotations' [Required]); other metadata is ignored.
    /// </summary>
    [Fact]
    public void AnnotatedMetadataBecomesAttributes()
    {
        const string source = """
            from typing import Annotated
            from System.ComponentModel import Description, DefaultValue, DescriptionAttribute, DefaultValueAttribute
            from System.ComponentModel.DataAnnotations import Required, StringLength, StringLengthAttribute
            from System.Reflection import *


            class Settings:
                name: Annotated[str, Description("Who is using it"), "metadata only"] = "guest"
                retries: Annotated[int, DefaultValue(3)] = 3
                title: Annotated[str, Required, StringLength(40, MinimumLength=2)] = "untitled"
                ratio: Annotated[float, "metadata only"] = 0.5


            s = Settings()
            t = s.GetType()
            for prop in ["name", "retries", "title", "ratio"]:
                print(prop, sorted([a.GetType().Name for a in t.GetProperty(prop).GetCustomAttributes(False)]))
            print(t.GetProperty("name").GetCustomAttribute[DescriptionAttribute]().Description)
            print(t.GetProperty("retries").GetCustomAttribute[DefaultValueAttribute]().Value)
            length = t.GetProperty("title").GetCustomAttribute[StringLengthAttribute]()
            print(length.MaximumLength, length.MinimumLength)
            print(s.name, s.retries, s.title, s.ratio)
            """;
        string stdout = IntegerTests.RunScript(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal(
            "name ['DescriptionAttribute']\n" +
            "retries ['DefaultValueAttribute']\n" +
            "title ['RequiredAttribute', 'StringLengthAttribute']\n" +
            "ratio []\n" +
            "Who is using it\n" +
            "3\n" +
            "40 2\n" +
            "guest 3 untitled 0.5\n", stdout);
    }
}
