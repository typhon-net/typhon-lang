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

    [Fact]
    public void DelegateConstructorsAcceptInstanceMethods()
    {
        const string source = """
            from System import EventArgs, EventHandler

            class Subscriber:
                def __init__(self):
                    self.calls = 0
                    callback = EventHandler(self.receive)
                    callback.Invoke(self, EventArgs.Empty)

                def receive(self, sender: object, args: EventArgs) -> None:
                    self.calls += 1

            print(Subscriber().calls)
            """;
        string stdout = IntegerTests.RunScript(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n", stdout);
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

    [Fact]
    public void AsyncMethodAssignmentsKeepInferredTaskFields()
    {
        const string source = """
            from System.Threading.Tasks import Task

            class Worker:
                def __init__(self) -> None:
                    self.running = Task.CompletedTask

                async def run(self) -> None:
                    await Task.CompletedTask

                def start(self) -> None:
                    self.running = self.run()

            def identity(value):
                return value

            w = Worker()
            w.start()
            print(identity(w.running) is not None)
            """;
        string stdout = RunStrict(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal("True\n", stdout);
    }

    static string RunStrict(string source, out int exitCode, out string stdErr)
    {
        string dir = Path.Combine(Suite.RepoRoot, "artifacts", "interop-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "app.ty");
            File.WriteAllText(path, source);
            var env = new Dictionary<string, string> { ["TYPHON_CACHE"] = "0", ["TYPHON_SERVER"] = "0" };
            var run = Suite.Run("dotnet", [Suite.Cli, "run", "--strict", path], TimeSpan.FromMinutes(2), env);
            exitCode = run.ExitCode;
            stdErr = run.StdErr;
            return Suite.Normalize(run.StdOut);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Windows Forms and WPF need the UI thread in a single-threaded COM apartment, which the CLR sets up from [STAThread] on
    /// the entry point. @STAThread on a module function (the one that starts the UI) also marks the generated Main.
    /// </summary>
    [Fact]
    public void StaThreadOnAFunctionMarksTheGeneratedMain()
    {
        const string source = """
            from System import STAThread
            from System.Reflection import Assembly


            def attribute_names(method_name: str) -> list[str]:
                for method in Assembly.GetExecutingAssembly().EntryPoint.DeclaringType.GetMethods():
                    if method.Name == method_name:
                        return sorted(a.GetType().Name for a in method.GetCustomAttributes(False) if a.GetType().Name.endswith("ThreadAttribute"))
                return []


            @STAThread
            def run() -> None:
                print("Main:", attribute_names("Main"))
                print("run:", attribute_names("run"))


            if __name__ == "__main__":
                run()
            """;
        string stdout = RunStrict(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal("Main: ['STAThreadAttribute']\nrun: ['STAThreadAttribute']\n", stdout);
    }

    /// <summary>Without an apartment attribute Main gets none (the CLR's default, a multi-threaded apartment).</summary>
    [Fact]
    public void MainHasNoApartmentAttributeByDefault()
    {
        const string source = """
            from System.Reflection import Assembly

            entry = Assembly.GetExecutingAssembly().EntryPoint
            print(entry.Name, [a.GetType().Name for a in entry.GetCustomAttributes(False) if a.GetType().Name.endswith("ThreadAttribute")])
            """;
        string stdout = RunStrict(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal("Main []\n", stdout);
    }

    /// <summary>
    /// typing.cast(T, x) has the type T, so a field or variable assigned from it is typed too (XAML's FindName returns an
    /// object: self.input = cast(TextBox, view.FindName("Input"))). Strict mode would reject a field of unknown type.
    /// </summary>
    [Fact]
    public void CastGivesItsTypeToFieldsAndVariables()
    {
        const string source = """
            from typing import cast
            from System.Text import StringBuilder


            def lookup(name: str) -> object:
                return StringBuilder(name)


            class View:
                def __init__(self) -> None:
                    self.builder = cast(StringBuilder, lookup("abc"))


            view = View()
            view.builder.Append("def")
            other = cast(StringBuilder, lookup("xy"))
            print(view.builder.ToString(), view.builder.Length, other.Insert(0, "-").ToString())
            """;
        string stdout = RunStrict(source, out int exit, out string stderr);
        Assert.True(exit == 0, stderr);
        Assert.Equal("abcdef 6 -xy\n", stdout);
    }
}
