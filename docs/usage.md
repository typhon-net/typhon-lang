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
# A Blazor WebAssembly app: Razor markup with Typhon code-behind, all of it running in the browser (localStorage, a
# Mandelbrot set computed in Python)
dotnet run --project samples/BlazorWasm                  # http://localhost:5090

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

## Standard library modules from CPython's source (`#:pylib`, experimental)

Typhon's runtime implements the standard library modules programs use most, in C#. With a `#:pylib` line, a module it
does not have is compiled together with the program from CPython's own source. Typhon looks for
`Lib/<name>.py` and for packages with `Lib/<name>/__init__.py`, and follows their imported submodules
and relative imports.

```python
#:pylib
import graphlib    # not in Typhon's runtime: compiled from CPython's Lib/graphlib.py
import colorsys

ts = graphlib.TopologicalSorter({"b": {"a"}, "c": {"b"}})
print(list(ts.static_order()), colorsys.rgb_to_hsv(0.2, 0.4, 0.4))
```

The module is compiled like a module of the program: its functions get typed versions for the program's calls, and the
compile cache keeps the result until a source changes. Typhon's runtime modules normally take precedence over `Lib/`
sources. With the directive enabled, import `ast`, `opcode` or `dis` in the entry file to select its CPython
source under the same Python import name. Imports from dependencies can use Typhon's managed implementations.
The original `doctest` runs examples through the Typhon CLI.
Typhon bundles selected CPython 3.15.2 sources for programs that use them without an external Lib directory.
These include `_ast_unparse`, `cmd`, `netrc`, `selectors`, and `token`. Each file header contains the upstream
attribution and license.
Which modules compile is tracked by compiling
each of CPython's modules alone (`docs/conformance.md`); a module that does not compile reports its errors with
`Lib/<name>.py` lines, and tracebacks show its frames. The `Lib` folder is `#:pylib DIR`, else the `TYPHON_PYLIB`
environment variable, else the standard library of the `python3` (`python` on Windows) on the PATH.
The [roadmap](roadmap.md) covers managed and pinned CPython implementations of these modules.

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

[`samples/BlazorWasm`](../samples/BlazorWasm) is a standalone Blazor WebAssembly app with Typhon code-behind, the
client-side counterpart of [`samples/BlazorApp`](../samples/BlazorApp): the components inherit Typhon classes as there,
and the code runs in the browser. A service keeps a to-do list in `localStorage` through Blazor's JavaScript interop
(synchronous under WebAssembly), and a page computes the Mandelbrot set in plain Python and draws it as SVG. `dotnet
publish -c Release` trims it to static files (9.3 MB, 3.0 MB with Brotli); the sample's README covers the trim warnings
Blazor hides by default and `TrimMode=full`.

```sh
dotnet run --project samples/BlazorWasm        # development server: open http://localhost:5090
dotnet publish samples/BlazorWasm -c Release   # static files in samples/BlazorWasm/bin/Release/net11.0/publish/wwwroot
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
items to control it yourself). Nothing in the .NET SDK, MSBuild or Roslyn is modified. GDScript files (`.gd`) are
compiled when they are listed as `TyphonCompile` items (see [GDScript](gdscript.md)).

**Other source generators.** Roslyn runs all source generators on the same input, so no other generator sees the C#
that Typhon's generator produces: Godot's script glue, `[JSExport]`, XAML code-behind and the like do not apply to Typhon
classes. With `<TyphonEmitToDisk>true</TyphonEmitToDisk>` the project does not use Typhon's generator; instead a build
step before the C# compiler runs `typhon-emit` (`src/Typhon.Emit`, a small net8.0 tool that runs on any .NET 8+), which
writes the C# of the `TyphonCompile` items and Typhon's runtime into `$(TyphonEmitDir)` (default
`$(IntermediateOutputPath)typhon/`), and those files are compiled as ordinary `Compile` items that every generator sees.
They keep `#line` directives, so errors point at the Typhon sources; unchanged files are not rewritten. The tool is found
at `$(TyphonEmitToolPath)` (in this repo, `samples/Directory.Build.props` builds it from source;
[samples/GodotBench](../samples/GodotBench) uses this).
