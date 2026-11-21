# BlazorWasm: Razor markup, Typhon code-behind, in the browser

A standalone Blazor WebAssembly app built like [BlazorApp](../BlazorApp): every component keeps its markup in a `.razor`
file and its logic in a `.ty` file, and the startup code and the service are Typhon too; there is no C# file. The
difference is where the code runs. The browser downloads the .NET runtime and the app's assembly once, and from then on
the Python code runs in the page, on .NET's WebAssembly runtime. No server is involved at run time:
`dotnet publish` produces static files that any web server can serve.

```sh
dotnet run --project samples/BlazorWasm                     # development server: http://localhost:5090

# Release build: trimmed, static files. Open http://localhost:8000
dotnet publish samples/BlazorWasm -c Release
python3 -m http.server 8000 -d samples/BlazorWasm/bin/Release/net11.0/publish/wwwroot

python3 samples/BlazorWasm/fractal.ty                       # the Mandelbrot code under CPython: the set as text
```

It needs only the .NET 11 SDK. The WebAssembly runtime (`Microsoft.NETCore.App.Runtime.Mono.browser-wasm`) comes from
NuGet with the first restore, as in [WasmLife](../WasmLife); no workload is needed. The C# that Typhon generates is in
`obj/Debug/net11.0/generated/Typhon.Compiler/`, the Razor compiler's output next to it.

| Page | Markup | Code-behind | Shows |
|---|---|---|---|
| `/` | [Home.razor](Components/Pages/Home.razor) | [home.ty](Components/Pages/home.ty) | `OnInitialized` running in the browser: .NET reports the platform as `Browser`, `browser-wasm` |
| `/counter`, `/counter/{start}` | [Counter.razor](Components/Pages/Counter.razor) | [counter.ty](Components/Pages/counter.ty) | the same code as BlazorApp's counter: fields, `@onclick` handlers (also with arguments), `@bind`, a route `[Parameter]`, `OnParametersSet` |
| `/todos` | [Todos.razor](Components/Pages/Todos.razor) | [todos.ty](Components/Pages/todos.ty) | an `[Inject]`ed service that keeps the list in the browser's `localStorage` (JavaScript interop, the `csv` module), an `<EditForm>` validated by DataAnnotations attributes, `@bind:after` |
| `/mandelbrot` | [Mandelbrot.razor](Components/Pages/Mandelbrot.razor) | [mandelbrot.ty](Components/Pages/mandelbrot.ty), [fractal.ty](fractal.ty) | a CPU-heavy computation in plain Python, drawn as SVG (one `<path>` per colour), clickable tiles that zoom in, async handlers, `OnAfterRender` |

The service is [services/todo_store.ty](services/todo_store.ty). [main.ty](main.ty) registers it and starts the app.

## Standalone app, not a Blazor Web App

.NET offers two ways to run components in the browser: a standalone Blazor WebAssembly app (this one), or a Blazor Web
App whose components use the interactive WebAssembly render mode. The second needs two projects (a server project that
prerenders and hosts, and a client project with the WebAssembly components) and an ASP.NET Core server at run time.
The standalone app is one project, `Microsoft.NET.Sdk.BlazorWebAssembly`, whose published output is a folder of static
files. BlazorApp already shows the server side, so this sample shows the other end.

## What changes compared with BlazorApp

The component code is the same: a Typhon class derived from `ComponentBase` per component, `@inherits counter.CounterBase`
in the markup, fields and properties the markup reads, handlers it binds, lifecycle methods that override
`ComponentBase`'s with their access kept, `Annotated[..., Parameter()]` and `Annotated[..., Inject()]` properties. The
[BlazorApp README](../BlazorApp/README.md#how-the-markup-binds-to-the-typhon-class) explains them. What differs:

* **The page and the startup.** `wwwroot/index.html` is the page; the app renders into its `<div id="app">`.
  [main.ty](main.ty) builds a `WebAssemblyHostBuilder`, names the root component in a string as BlazorApp does
  (`builder.RootComponents.Add["Components.App"]("#app")`: Typhon cannot see the classes the Razor compiler generates)
  and ends with `await builder.Build().RunAsync()`. A `__main__` block that awaits compiles to an `async Task Main`.
  `RunAsync` never completes in a browser; without the `await`, `Main` would return at once and Blazor would keep running
  anyway, but an error during startup would vanish without a message.
  [App.razor](Components/App.razor) holds the router (BlazorApp's `Routes.razor`).
* **No render modes.** Every component is interactive and runs in the browser, so there is no `@rendermode`.
* **Services.** A singleton lives as long as the browser tab. A service can take other services in its constructor
  (`TodoStore.__init__(self, js: IJSRuntime)`); components still cannot, because Razor's half of the class has a
  parameterless constructor.
* **JavaScript interop.** The app runs in the browser's own process, so the `IJSRuntime` that Blazor injects is an
  `IJSInProcessRuntime` and calls can be synchronous: `cast(IJSInProcessRuntime, js)`, then
  `js.Invoke[str]("localStorage.getItem", KEY)` and `js.InvokeVoid("localStorage.setItem", KEY, text)`. (On Blazor
  Server every call is a round trip to the browser and must be awaited.) Blazor's analyzer BL0016 asks for a try block
  around each call; `except JSException` (storage disabled by a privacy setting, a full quota) keeps the list in
  memory. Roslyn analyzers see the code Typhon generates, unlike source generators.
* **Typed storage.** `localStorage` holds strings. The list is saved as CSV with a header, written with
  `csv.DictWriter` into an `io.StringIO` and read back with `csv.DictReader`, which gives typed rows (`dict[str, str]`);
  `json.loads` would return late-bound (`dynamic`) values. Open the "Saved in this browser's localStorage" section of
  the page to see the text.
* **One thread.** WebAssembly .NET has a single thread, shared with the page: while Python computes, the browser can
  neither paint nor react to input. The Mandelbrot page sets `computing`, calls `StateHasChanged()` and
  `await asyncio.sleep(0.02)` (a browser timer, outside `asyncio.run`), so that the page shows "Computing..." before the
  CPU gets busy.
* **Speed.** The runtime interprets the IL (ahead-of-time compilation to WebAssembly needs the `wasm-tools` workload).
  In the Release build the whole set at 240 x 160 points takes 10-20 ms, the double spiral at 360 x 240 points with
  up to 1000 steps per point about 350 ms; the Debug build of `dotnet run` takes two to three times as long. Turning the
  escape times into SVG paths and rendering them takes another 10-20 ms: one `<path>` per colour makes 48 elements
  (and 96 transparent tiles on top that take the clicks), where a `<rect>` per point would be 38,400.

## Publishing, trimming and download size

`dotnet publish -c Release` trims the app and prints no warnings: Blazor WebAssembly sets `TrimMode=partial` (the
framework's assemblies are trimmed, the app's own assembly is kept whole, and with it the Typhon runtime compiled into
it) and `SuppressTrimAnalysisWarnings=true`. The published app passes the same browser test as the development build.

`dotnet publish -c Release -p:SuppressTrimAnalysisWarnings=false` shows what that hides, 143 warnings:

* 136 in the Typhon runtime: IL2026 for the `dynamic` binder (`Microsoft.CSharp`) on the late-bound paths of the
  builtins, `json`, `copy`, `logging` and others; IL2070, IL2072, IL2075 and IL2080 for reflection over types that are
  not known in advance (`copy.deepcopy`, `dataclasses.fields`, bound methods, the pytest runner). The sample is
  compiled with `<TyphonTypeMode>strict</TyphonTypeMode>`, so none of its code is late-bound, and it calls none of these
  functions. WasmLife silences IL2026 and IL2075 with `NoWarn`, as the Native AOT build of the `typhon` command does,
  because the plain WebAssembly SDK shows trim warnings; here Blazor's default already hides them, and the sample keeps
  that default.
* 2 IL2026 for `IJSInProcessRuntime.Invoke[str]` in `todo_store.ty`: the interop serializes arguments and results to
  JSON with reflection. Strings need nothing that trimming could remove.
* 5 from Blazor itself: IL2104 for `Microsoft.AspNetCore.Components` and `Microsoft.JSInterop`, IL2110 and IL2111 for
  `Router.NotFoundPage` in the code the Razor compiler generates for `App.razor`. A C# Blazor app gets these too.

The published `wwwroot` holds 65 files the browser downloads, 9.3 MB, or 3.0 MB from a server that sends the Brotli
files next to them (`.br`; 3.7 MB with the `.gz` files). `python3 -m http.server` sends the uncompressed files. The
largest are the runtime (`dotnet.native.wasm`, 3.0 MB), `System.Private.CoreLib` (2.2 MB) and the app (0.6 MB, most of
it the Typhon runtime). `Microsoft.CSharp` and `System.Linq.Expressions` (0.65 MB, 0.2 MB compressed) are there because
the Typhon runtime's late-bound paths use them, although this program never does. WasmLife, without Blazor, is 5.6 MB
(1.8 MB compressed).

`TrimMode=full` also trims the app's assembly, but then the router finds no pages and shows "Not found": it discovers
routable components by reflection, and the trimmer removes the component classes nothing references (a C# Blazor app
behaves the same). Rooting the components' namespaces fixes that; the result passes the browser test at 8.3 MB (2.7 MB
compressed), with the app's assembly at 121 KB:

```xml
<!-- BlazorWasm.csproj -->
<PropertyGroup><TrimMode>full</TrimMode></PropertyGroup>
<ItemGroup><TrimmerRootDescriptor Include="ILLink.Descriptors.xml" /></ItemGroup>

<!-- ILLink.Descriptors.xml -->
<linker>
  <assembly fullname="BlazorWasm">
    <namespace fullname="BlazorWasm.Components" />
    <namespace fullname="BlazorWasm.Components.Pages" />
    <namespace fullname="BlazorWasm.Components.Layout" />
  </assembly>
</linker>
```

## Limitations

* The Razor compiler and Typhon cannot see each other's output, exactly as in BlazorApp (see
  [What the Razor compiler cannot see](../BlazorApp/README.md#what-the-razor-compiler-cannot-see)): the root component
  is named in a string, a component whose base class is a Typhon class needs `@implements IComponent` to be used by tag
  in another component, and such a child component's parameters are passed untyped. This sample has no such child
  component.
* `[JSExport]` and `[JSImport]` in Typhon code do nothing, because the JavaScript interop generator does not see what
  Typhon generates (see [WasmLife](../WasmLife/README.md#why-the-exports-are-in-c)). `IJSRuntime` needs no generator, so
  Blazor's interop works from Typhon as shown.
* A static file server must answer unknown paths with `index.html` for links into the app to work after a reload:
  `python3 -m http.server` answers `/todos` with 404 (start at `/` and follow the links), the development server
  falls back to `index.html` (`StaticWebAssetSpaFallbackEnabled` in the project).
* Long computations block the page, as described above. Moving them off the page's thread would need .NET's
  multithreaded WebAssembly build or a Web Worker, which this sample does not use.
