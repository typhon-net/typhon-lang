# Using Typhon

Everything below needs the .NET 11 SDK.

```powershell
dotnet build Typhon.slnx                         # compiler, CLI, numpy library, samples

# 1. A pure Typhon console app — a normal .csproj whose sources are *.ty
dotnet run --project samples/HelloTyphon

# 2. A C# app using a Typhon class library *and* mixing .cs/.ty in one project
dotnet run --project samples/CSharpApp

# An ASP.NET Core minimal API written in Typhon (routes, JSON bodies, dependency injection)
dotnet run --project samples/MinimalApi -- --urls http://localhost:5000

# A Blazor web app: Razor markup with Typhon code-behind (state, event handlers, DI, async lifecycle methods)
dotnet run --project samples/BlazorApp                   # http://localhost:5080

# xUnit tests written in Typhon (@Fact, @Theory with @InlineData, Assert.*)
dotnet test samples/XunitTests

# A browser app on .NET WebAssembly: the Game of Life in plain Python, drawn by JavaScript (http://localhost:5080)
dotnet run --project samples/WasmLife

# Desktop apps written in Typhon, run on Windows: the same to-do app in Windows Forms, WPF and WinUI 3
dotnet run --project samples/Desktop/WinForms             # or Wpf, WinUI; see samples/Desktop/README.md
# The playground: write Typhon in a browser; the compiler, Roslyn and the program run there (Blazor WebAssembly)
dotnet run --project samples/Playground                  # publish: static files, see samples/Playground/README.md

# 3. Python-style scripting with the typhon CLI (published with ReadyToRun: compiles about twice as fast)
dotnet publish src/Typhon.Cli -c Release -r win-x64 -o tools/typhon    # linux-x64, osx-arm64, ...
tools/typhon/typhon run samples/scripts/tour.ty          # compile in memory and run, like `python tour.py`
tools/typhon/typhon emit samples/scripts/tour.ty         # show the generated C#
tools/typhon/typhon build samples/scripts/tour.ty -o out/tour            # → dotnet out/tour/typhon_tour.dll
tools/typhon/typhon build samples/scripts/tour.ty --aot -o out/tour-aot  # → native out/tour-aot/typhon_tour.exe
tools/typhon/typhon server status                        # the background compile server (see below)
tools/typhon/typhon test tests/pytest                     # pytest-style tests (see below)

# 4. Conformance tests: every case must print exactly what CPython prints
dotnet test tests/Typhon.Conformance.Tests
git config core.hooksPath .githooks          # once per clone: CI's checks before each commit (build) and push (suite)
uv venv .venv --python 3.15 && uv pip install --python .venv/bin/python numpy   # the CPython CI compares with

# 5. Benchmarks: the same files under CPython (+ NumPy) and Typhon, time and peak RAM
python -m venv .venv; .venv\Scripts\pip install numpy     # once, for the CPython side
./benchmarks/run.ps1
```

## Tests

**Before a commit and a push.** With `git config core.hooksPath .githooks`, the pre-commit hook builds the solution as
CI does and checks the conformance cases a commit adds or changes: their expected output must be what CPython prints
(the version CI uses: the repo's `.venv`, or `TYPHON_CPYTHON`) and must hold no absolute temp or home paths, which differ
between machines and operating systems. The pre-push hook runs the whole conformance suite, as CI does, unless the
pushed commits change only documentation. `--no-verify` or `TYPHON_SKIP_HOOKS=1` skips them.

**pytest style.** `typhon test [file.ty | dir ...]` runs the tests of `test_*.ty` / `*_test.ty` files (below the current
directory by default): module-level `test*` functions and the `test*` methods of `Test*` classes, in the order they are
defined, after the module's body has run. A test module imports `pytest` for `@pytest.fixture` (also `yield` fixtures and
`scope="module"`, `autouse=True`), `@pytest.mark.parametrize`, `skip`, `skipif`, `xfail`, `pytest.raises(E, match=...)`,
`pytest.approx`, `pytest.fail` and `pytest.skip`; the built-in fixtures `tmp_path` and `capsys` exist. The report follows
pytest's: one character per test, the failures with their `assert` line, the values compared (`assert 4 == 5`) and what the
test printed, and a summary; the exit code is 1 when a test failed, 5 when none was collected. `typhon run` of a test
module only runs its body, as `python test_x.py` would.

**xUnit.** Attributes are decorators (`from Xunit import Fact, Theory, InlineData`, then `@Fact` on a method of a test
class), so a test project of `.ty` files runs with `dotnet test` like a C# one; failures point at the `.ty` line. See
[`samples/XunitTests`](../samples/XunitTests).

## NuGet packages without a project

A script names the packages it needs with `#:package` lines, the directive of .NET's file-based apps (to Python they are
comments, so the file stays valid Python):

```python
#:package Humanizer.Core@2.14.1
#:package Newtonsoft.Json@13.0.3
from Humanizer import NumberToWordsExtension
from Newtonsoft.Json import JsonConvert

print(NumberToWordsExtension.ToWords(1234), JsonConvert.SerializeObject([1, 2, 3]))
```

`typhon run`, `check` and `build` restore them the first time (with `dotnet restore` of a generated project, so NuGet
resolves their dependencies; `@*` or no version takes the latest, re-resolved daily) into the cache folder
(`~/.cache/typhon/packages`, `%LOCALAPPDATA%\Typhon\cache\packages`), compile against their reference assemblies and run
with their implementation assemblies; `typhon build` copies those next to the program, and `--aot` adds them as package
references. Restoring needs the .NET SDK and, the first time, access to the package feed.

## Frameworks the typhon command can use

`typhon run`, `typhon check` and `typhon build` compile against the .NET runtime the CLI runs on
(Microsoft.NETCore.App). A program that imports `Microsoft.AspNetCore.*` or `Microsoft.Extensions.*` also gets the
ASP.NET Core shared framework (Microsoft.AspNetCore.App) of the same major and minor version, so a web app needs no
project file:

```sh
typhon run samples/MinimalApi/main.ty --urls http://localhost:5000
typhon build samples/MinimalApi/main.ty -o out/api && dotnet out/api/typhon_main.dll --urls http://localhost:5000
```

Programs that use it the other way can ask for it like a .NET file-based app does, with a `#:sdk Microsoft.NET.Sdk.Web`
line (a comment to Python). Other programs never reference it, so they compile as fast as before. A built app's
`.runtimeconfig.json` lists both frameworks. When the ASP.NET Core runtime is not installed, the CLI says so.
`typhon build --aot` does not support ASP.NET Core programs (Native AOT for minimal APIs needs the request delegate
generator of a full project): build without `--aot`, or publish a project with `PublishAot`.

## In the browser (WebAssembly)

[`samples/WasmLife`](../samples/WasmLife) runs Conway's Game of Life in the browser. The rules are plain Python
(`life.ty`, which CPython runs too), compiled into a `Microsoft.NET.Sdk.WebAssembly` project and run by .NET's
WebAssembly runtime; it builds with the .NET SDK alone, no workload. JavaScript draws the board and calls a small
Typhon module (`browser.ty`) whose functions use the types the JavaScript interop passes (`int32`, `float`, `str`,
`Array[uint8]`). The `[JSExport]` declarations are a few lines of C# that forward to it: `@JSExport` compiles to the
attribute, but the JSExport source generator cannot see code that another source generator wrote, so it would export
nothing. The sample's README explains the interop.

```sh
dotnet run --project samples/WasmLife          # development server: open http://localhost:5080
dotnet publish samples/WasmLife -c Release     # static files in samples/WasmLife/bin/Release/net11.0/publish/wwwroot
```

## Adding Typhon to any C# project

`.ty` files are compiled like `.cs` files — just put them in the project. In this repo the wiring lives in
`samples/Directory.Build.props`; for an external project it is one package reference (`dotnet pack src/Typhon.Compiler`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <TyphonTypeMode>strict</TyphonTypeMode>   <!-- strict | warn (default) | permissive -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Typhon" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

`**/*.ty` is included automatically (set `EnableDefaultTyphonItems=false` and add `<TyphonCompile Include="..." />`
items to control it yourself). Nothing in the .NET SDK, MSBuild or Roslyn is modified.
