# Typhon Playground

Type Typhon (type-annotated Python) in a browser, press **Run**, and see what it prints, its diagnostics, its
traceback and the C# the compiler generated for it. Nothing runs on a server: the page is a Blazor WebAssembly app,
and the Typhon compiler, Roslyn and the program all run in the browser's .NET runtime, like SharpLab or try.dot.net.

```sh
dotnet run --project samples/Playground                       # http://localhost:5180
dotnet publish samples/Playground -c Release -o out/playground  # static files: out/playground/wwwroot
python3 -m http.server 8080 -d out/playground/wwwroot          # or any other web server
```

No workload is needed (no `wasm-tools`): the .NET SDK builds and publishes it as it is.

The page has an editor (syntax highlighting, Tab indents, Enter keeps the indentation and indents after a colon,
Ctrl+Enter runs), a few
example programs (classes, generators, comprehensions, a dataclass, an f-string table, .NET interop, a traceback,
`dynamic`, big integers, `input()`), the type mode and big-int options of the `typhon` command, an output pane with the
diagnostics (click one to go to its line, or to its line in the generated C#), stdout, stderr and the exit code, a
**Generated C#** tab, and the time each step took. **Copy link** puts the program into the page's URL (after `#`, so
it never reaches a server); a link opens with the program, which runs when you press Run.

## How it works

```
 main.ty ──► Typhon compiler ──► C# ──► Roslyn ──► IL ──► Assembly loaded in its own load context ──► Main()
             (Typhon.Compiler)          (Microsoft.CodeAnalysis.CSharp)  with a fresh Typhon.Runtime     stdout/stderr → page
                                          ▲
                                          │ metadata references from byte arrays, fetched once from refs/*.dll
```

`Core/` holds all of it, independent of the browser (`tests/Typhon.Playground.Tests` runs the same code on .NET):

* **Compiling** (`PlaygroundCompiler`) is `typhon run`'s Script target (`src/Typhon.Cli/CliCompiler.cs`) for one file
  held in a string: `TyphonCompiler.Parse` and `Emit` with a `RoslynTypeResolver`, then one Roslyn compilation named
  `typhon_script`, which references the precompiled `Typhon.Runtime.dll` and may use its internal types (its
  `InternalsVisibleTo`). The runtime's source is never compiled in the browser. One compilation without source keeps
  the references; every compile adds its syntax tree to it, so Roslyn builds the symbols of the references only once.
* **References.** A browser has no assembly files, and the assemblies of a published Blazor app are of no use to
  Roslyn: they are trimmed, and wrapped in WebAssembly modules (Webcil) under fingerprinted names. So the build ships
  its own: the reference assemblies of the .NET targeting pack (`Microsoft.NETCore.App.Ref`, the public API without
  code) for the 24 framework assemblies that Typhon programs need (every assembly `Typhon.Runtime` references, such as
  System.Runtime, System.Collections, System.Linq, System.Console, Microsoft.CSharp for `dynamic`,
  System.Runtime.Numerics and System.Text.RegularExpressions), plus `Typhon.Runtime.dll`, as static files under
  `refs/` ([PlaygroundReferences.targets](PlaygroundReferences.targets) lists them). The app fetches them in parallel
  with `HttpClient` when the page opens, by the names and hashes the build embedded in it (`refs/<name>?v=<hash>`: a
  new build is never mixed with cached files of an older one), and creates the references with
  `MetadataReference.CreateFromImage(bytes)`.
* **Trimming.** The published app is trimmed, but the trimmer never sees the code that calls the framework here:
  `Typhon.Runtime.dll` (loaded from `refs/`, not part of the app) and the programs, which compile against the full
  public API of the reference assemblies. So the implementations of those 24 assemblies and System.Private.CoreLib
  are kept whole (`TrimmerRootAssembly` in [Playground.csproj](Playground.csproj)); without that, every program
  fails with `MissingMethodException`. Exception messages are kept as .NET writes them (`UseSystemResourceKeys` off),
  so a traceback says `The given key 'b' was not present in the dictionary` rather than a resource key.
* **Running** (`PlaygroundRunner`): every run loads the program and its own copy of `Typhon.Runtime` (the same bytes
  the compiler references) into a new `AssemblyLoadContext`, so nothing of a previous run carries over (logging's
  handlers, random's state, `sys.argv`), as with a new `python` process. stdout and stderr go to the page in the order
  they were written (`Console.SetOut` / `SetError`), `input()` reads the stdin box (`Console.SetIn`). An uncaught
  exception prints the traceback `typhon run` prints, with the lines read from the program's portable PDB by the
  playground itself (the browser's runtime loads no symbols). `sys.exit` raises `SystemExit`, as in Python: it ends only
  the program, with its exit code (`finally` blocks run, `except Exception` does not catch it).
* **The page** (`Home.razor`) shows each step's status and lets the browser paint before the next one, since
  compiling blocks the page's only thread. It compiles and runs the first example as soon as the references are
  loaded: the first compile after loading is the slow one, and the next ones are fast.
* **Highlighting** ([wwwroot/js/highlight.js](wwwroot/js/highlight.js), a small tokenizer for Python and C# in plain
  JavaScript, without dependencies or a build step). The editor stays a `<textarea>`: its text is transparent (the
  caret and the selection are not), and under it a `<pre>` shows the same text highlighted, in the same font, padding
  and line height, moved with the textarea's scrolling and highlighted again on input (only the lines in view are in
  it, so typing in a program of thousands of lines stays fast). The generated C# is
  highlighted once per compile, the whole text at once (a verbatim string or a comment can span lines), then line by
  line. Code is shown without ligatures: Cascadia Code would draw `->` as an arrow, and the highlighted text lines up
  with the textarea's only without them.

`dynamic` (from `Any` and implicitly dynamic code) works: the C# runtime binder runs in the WebAssembly runtime.

## Size and speed

Measured with the Release build above, Chrome 156 (headless) on a Linux laptop, files from a local server:

| | |
|---|---|
| download (114 files) | 28.5 MB uncompressed, **8.2 MB with Brotli**, 10.6 MB with gzip (`dotnet publish` writes `.br` and `.gz` copies) |
| of which | Roslyn 9.4 MB (2.6 MB Brotli), .NET runtime and CoreLib 8.5 MB (2.3 MB), other framework assemblies 7.0 MB (2.2 MB), reference assemblies 2.4 MB (0.75 MB), Typhon compiler 1.2 MB (0.3 MB) |
| keeping the framework whole for programs | 5 MB of that (1.3 MB Brotli) |
| page ready | 1.5 to 6 s, then the first example compiles and runs |
| first compile | 1.6 to 10 s (Typhon → C# 0.6 to 4.9 s, C# → IL 1 to 5.4 s), depending on how much of the compilers the program touches |
| later compiles | 0.1 to 1.5 s (Typhon → C# 5 to 900 ms, C# → IL 20 to 900 ms) |
| running | a loop of 3 million iterations: 70 to 90 ms |

The browser's .NET runtime interprets IL, and its jiterpreter compiles hot code paths to WebAssembly. On the same
machine, the `typhon` command (built, not published with ReadyToRun) compiled the hello world in 5.3 s cold and in
0.2 s through its warm compile server, and ran the loop above in 24 ms: compiling in the page takes about as long,
running programs takes 3 to 4 times longer.

## Limits

* One file, `main.ty`: no imports of other `.ty` modules, and no `numpy` (the playground does not ship Typhon.NumPy).
* The browser is a sandbox: no network (System.Net is not referenced) and no real files; `open()` works on an
  in-memory file system that lasts until the page is reloaded.
* `input()` reads what is in the stdin box when Run is pressed; it does not wait for typing.
* One thread: WebAssembly here runs .NET on the page's thread. `asyncio.run` fails with `PlatformNotSupportedException`
  (its event loop waits on a `BlockingCollection`, and a single-threaded runtime cannot block on a monitor);
  `time.sleep` works but freezes the page while it sleeps.
* A program runs on the page's thread too: an endless loop freezes the tab (reload it), and the output appears when
  the program ends, cut off after 200,000 characters.
* Each run keeps its copy of the runtime loaded (about 600 KB; the browser's runtime cannot unload assemblies): after
  hundreds of runs, reload the page.
* No globalization data (`InvariantGlobalization`): Python's own formatting does not depend on a culture, but .NET
  APIs that take one see the invariant culture.

## Publishing

`dotnet publish -c Release` produces `wwwroot/`, a folder of static files that any web server can host: serve
`.wasm` as `application/wasm`, and let the server send the precompressed `.br`/`.gz` files where it can. The page uses
a relative `<base href="./">`, so the folder works at any path, for example a GitHub Pages project site: copy the
contents of `wwwroot/` to the published branch or folder, and add an empty `.nojekyll` file, or Jekyll leaves out
`_framework/`.

The page requests nothing but its own files and stores nothing in the browser (no `localStorage`, no cookies), so it
also works served in a sandbox (`Content-Security-Policy: sandbox allow-scripts`: an opaque origin, where
`localStorage` throws), as typhon-lang.com serves it.

## Tests

```sh
dotnet test tests/Typhon.Playground.Tests
```

compiles and runs every example and a few more programs with `Core/` on .NET, from source strings, with the
references created from the same files the browser downloads (copied next to the tests): output, tracebacks,
diagnostics and their lines, `sys.exit`, a fresh runtime per run, the output limit, the options, and that the
reference files cover every assembly `Typhon.Runtime` references. The app itself is built by `dotnet build Typhon.slnx`.
