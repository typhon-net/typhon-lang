# WasmLife: Python in the browser

Conway's Game of Life with its rules written in plain Python, compiled by Typhon to .NET and run in the browser on
.NET's WebAssembly runtime. JavaScript draws the board on a canvas and handles the buttons and the mouse; every
generation is computed by the Python code. The page starts with a Gosper glider gun. You can draw cells, place other
patterns, fill the board at random and change the speed. Colours show how long a cell has been alive.

| File | What it is |
|---|---|
| `life.ty` | The Game of Life: a `World` class on a wrapping grid, patterns in run-length encoding, and a console demo in its `__main__` block. Plain Python: CPython runs it too. |
| `browser.ty` | The glue, in Typhon: the world the page shows and the functions JavaScript calls, with types the JavaScript interop can pass. |
| `JSExports.cs` | One `[JSExport]` line per function of `browser.ty` (see [Why the exports are in C#](#why-the-exports-are-in-c)). |
| `wwwroot/index.html`, `wwwroot/main.js` | The page: starts .NET, runs the animation loop, draws the canvas, handles the input. |
| `WasmLife.csproj` | A `Microsoft.NET.Sdk.WebAssembly` project, the shape of the .NET `wasmbrowser` template. |

## Prerequisites

* The .NET 11 SDK. No workload is needed: the WebAssembly SDK comes with the .NET SDK, and the WebAssembly runtime
  (the `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` package) is downloaded from NuGet by the first restore.
  The optional `wasm-tools` workload adds ahead-of-time compilation to WebAssembly and a smaller native runtime;
  `dotnet publish` recommends it in a message, and this sample works without it.
* A current browser (Chrome, Edge, Firefox or Safari).
* For the console demo: Python 3 and/or the `typhon` command (see the [Quick start](../../README.md#quick-start-net-11-sdk)).

## Build and run

From the repository root:

```sh
# The logic on the console: CPython and Typhon print the same
python3 samples/WasmLife/life.ty
tools/typhon/typhon run samples/WasmLife/life.ty

# Development server (Debug build). Open http://localhost:5080
dotnet run --project samples/WasmLife

# Release build: trimmed, static files that any web server can serve. Open http://localhost:8000
dotnet publish samples/WasmLife -c Release
python3 -m http.server 8000 -d samples/WasmLife/bin/Release/net11.0/publish/wwwroot
```

The page must come from a web server, not a `file://` URL, because `main.js` is an ES module and the runtime fetches
its files. The published app is about 5.6 MB, 1.8 MB compressed with Brotli (the `.br` and `.gz` files next to each
file are for servers that serve precompressed files). `browser.ty`'s `__main__` block prints a line to the browser's
console when the app starts.

## How the interop works

1. `main.js` imports `_framework/dotnet.js`. `dotnet.create()` downloads and starts the .NET runtime (Mono, compiled
   to WebAssembly) and the app's assemblies. `WasmLife.wasm` holds the IL of the `.ty` and `.cs` files, which the
   runtime interprets.
2. `getAssemblyExports(...)` returns the `[JSExport]` methods, grouped by namespace and class:
   `exports.WasmLife.JSExports.step(1)` calls `JSExports.step` in C#, which calls `step` in `browser.ty`. Typhon
   compiles the functions of `browser.ty` to `public static` methods of the class `WasmLife.browser` (the namespace
   comes from the project, the class from the file name), so C# calls them by those names.
3. The JavaScript interop passes only certain .NET types: `int` and `double` (JavaScript numbers), `bool`, `string`,
   arrays of bytes, ints, doubles and strings (copied: a `byte[]` arrives as a `Uint8Array`, a `string[]` as an
   `Array`), `JSObject`, `Task` (a `Promise`) and delegates. Python's `int` is a 64-bit `long`, which needs an extra
   marshalling attribute, and `list[int]` is a `List<long>`, which the interop does not pass at all. So `browser.ty`
   uses .NET types in its signatures and converts: parameters are `int32`, results are `int32(world.generation)`,
   and `cells()` turns the list of cell ages into a `byte[]`: `[uint8(min(age, 255)) for age in world.cells].ToArray()`
   (the comprehension is a `List<byte>`, so the .NET method `ToArray` works on it).
4. JavaScript keeps no game state: it asks for `cells()` after each step and draws them. Calls in this direction
   are synchronous and cheap; a generation of the 96 x 64 board takes about a millisecond in the Release build.
5. The `__main__` block of `browser.ty` is the app's `Main` (`<TyphonMainModule>browser</TyphonMainModule>` in the
   project, because `life.ty` has a `__main__` block too). `main.js` runs it with `runMain()`, which keeps the runtime
   alive afterwards; `dotnet.run()` would shut it down once `Main` returns, and later calls to the exports would fail.

`World.randomize(seed, density)` uses `random.Random(seed)`, and Typhon's `random` is CPython's Mersenne Twister, so a
seed gives the same world in the browser and under CPython.

### Why the exports are in C#

`from System.Runtime.InteropServices.JavaScript import JSExport` and `@JSExport` on a function of `browser.ty` compile
to a method with `[JSExport]`, but nothing is exported. The attribute only marks the method for the JSExport source
generator, which writes the marshalling code. Typhon is a source generator too, and Roslyn runs all generators on the
same input, so no generator sees the code another one generates. The JSExport generator never sees the methods
Typhon writes. `JSExports.cs` therefore declares the exports as one-line C# methods that forward to `browser.ty`.
For the same reason Typhon code cannot declare `[JSImport]` functions (partial methods that the generator implements),
so the page pulls data from .NET instead of .NET calling into the page.

### Why not Blazor

Blazor WebAssembly would also run Typhon code, but it adds a component model, Razor and a larger download. Drawing a
canvas from JavaScript needs only the runtime and a few exported functions, which is what the `wasmbrowser` template
shape (`Microsoft.NET.Sdk.WebAssembly`) provides.
