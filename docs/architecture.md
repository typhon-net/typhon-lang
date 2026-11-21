# How Typhon works

```
 .ty ──► Lexer (INDENT/DEDENT) ──► Parser ──► AST ──► scope analysis ──► C# + #line ──► Roslyn ──► IL (.dll/.exe)
                                                         + type resolution          ▲
                                                           (Roslyn symbols)         │ same compilation as the
                                                                                    │ project's own .cs files
```

* **Front end** (`src/Typhon.Compiler/Syntax`): a hand-written tokenizer and recursive-descent parser for the Python
  subset (`Parser.cs` with `Parser.Statements.cs`, `Parser.Expressions.cs` and `Parser.Match.cs`). No dependency on
  CPython. A second front end parses GDScript (`GdScriptLexer.cs`, `GdScriptParser.cs`) into the same AST; see
  [GDScript](gdscript.md).
* **Lowering** (`src/Typhon.Compiler/Emit`): Typhon → C#. `ClassModel` normalizes classes before emission,
  `TypeInference` runs the whole-program fixpoint, and `ModuleEmitter` writes the C#: one partial class split by concern
  under `Emit/ModuleEmitter/` (module and imports, functions and function values, classes, construction and dunder
  bridges, statements, assignments, loops and `match`, expressions, calls, operators, collections, subscripts, .NET
  members, standard-library typing, and `ModuleEmitter.Types.cs` for the types of expressions). The interesting parts:
  * *Python has no `new`.* `Foo(1)` is a call or a construction depending on what `Foo` is, so the compiler resolves
    names against Typhon modules **and** the Roslyn compilation (referenced assemblies + C# code in the same project).
  * *Python locals are function-scoped, C# locals are block-scoped.* `LocalsAnalyzer` computes, per variable, the
    lowest common block of all uses; variables are declared inline with `var` (so C# infers the type) where possible,
    split into independent variables where their uses don't interact, and hoisted otherwise.
  * Python protocol methods (`__add__`, `__eq__`, `__str__`, `__getitem__`, `__iter__`, `__len__`, ...) are kept and
    bridged to .NET operators, `Equals`, `ToString`, indexers and `IEnumerable<T>`.
* **Back end = Roslyn.** The generated C# is compiled by Roslyn, which gives us overload resolution, generic inference,
  lambdas, async state machines, iterators, closures, PDBs and IL emission for free. `#line` directives map every
  error, warning, stack-trace line and breakpoint back to the `.ty` file.
* **Build integration** (`src/Typhon.Compiler/Generator`): an incremental Roslyn **source generator** receives the
  `.ty` files as `AdditionalFiles`. That is why Typhon and C# can live in one project and reference each other in
  both directions, and why Typhon types show up in C# IntelliSense.
* **Runtime** (`src/Typhon.Compiler/Runtime/`, see its README: `Builtins/`, `Types/`, `Modules/`): Python builtins (`print`, `len`, `range`,
  `sorted`, `format`, `frozenset`, str/list/dict/set methods, ...), small stdlib shims (`math`, `time`, `random` (CPython's Mersenne Twister and algorithms: a seeded program draws the same numbers),
  `sys` with `sys.stdout`/`stderr`/`stdin`, `os`, `asyncio`) and the commonly used parts of `itertools`,
  `collections` (`Counter`, `defaultdict`, `deque`, `OrderedDict`, `namedtuple`), `functools` (`reduce`,
  `cmp_to_key`, `@cache`/`@lru_cache`), `operator`, `string`, `fractions`, `pathlib`, `datetime` (`date`, `time`,
  `datetime`, `timedelta`, `timezone` with CPython's normalization, reprs, `strftime` and `strptime`), `heapq` (CPython's algorithm,
  so heaps print the same), `bisect`, `copy`, `json` (CPython's output format), `re` (on .NET's regex engine, with
  Python's syntax, group numbering and `sub`/`split`/`findall` rules), `io.StringIO`, `array`, `struct`, `hashlib`,
  `statistics` (exact fractions like CPython, so results match digit for digit), `textwrap`, `base64`/`binascii`, `csv`
  (ports of CPython's reader state machine and writer quoting), `glob`/`fnmatch`, `shutil`, `uuid`,
  `tempfile`, `timeit` and `doctest` (`testmod()` runs no examples), plus `bytes`, `bytearray` and binary files, compiled
  *into* every Typhon assembly as internal types — no runtime dependency. Scripts compiled in memory by `typhon run`
  reference a precompiled copy instead (`src/Typhon.Runtime`), which saves recompiling it on every run.
* **CLI** (`src/Typhon.Cli`): `typhon run` takes the fastest way that is still exact. An unchanged script that ran
  before comes from the **script cache** (like CPython's `__pycache__`, in the user's cache directory; a changed source
  file, imported module or compiler build invalidates and overwrites the entry; unused entries go after 30 days).
  Otherwise a background **compile server** with a warm compiler compiles it (like Roslyn's VBCSCompiler; one per user
  and CLI build, reachable only by that user, exits after 10 idle minutes). The very first run compiles in the
  process and starts the server. Switch them off with `TYPHON_CACHE=0` / `--no-cache` and `TYPHON_SERVER=0` /
  `--no-server`; `--time` shows which path was taken and how long it took.
  The CLI references the base shared framework, plus ASP.NET Core (Microsoft.AspNetCore.App, located next to the
  running runtime) when a module imports `Microsoft.AspNetCore.*` / `Microsoft.Extensions.*` or has
  `#:sdk Microsoft.NET.Sdk.Web`. A program run in the CLI's process loads that framework's assemblies through an
  `AssemblyLoadContext` resolving handler; the script cache records the framework folder (an update recompiles), and
  the compile server reports the frameworks with each build.
* **numpy** (`src/Typhon.NumPy`): a numpy-compatible API (float64 `ndarray`, ufuncs, `@`, `linalg`, `random`) written
  in C# on top of `System.Numerics.Tensors.TensorPrimitives` (SIMD). `@` is a packed, cache-blocked, threaded GEMM with
  a 6 x 8 AVX2/FMA micro-kernel (`MatMul.cs`); `linalg` uses LAPACK's algorithms (recursive LU and Cholesky, blocked
  Householder QR, tridiagonal QL, Golub-Kahan SVD) built on that GEMM (`LinAlg*.cs`). No native BLAS is needed.


## Design notes and important decisions


1. **Transpile through C# instead of emitting IL directly.** Emitting IL ourselves would mean re-implementing C#'s
   overload resolution, generic inference, lambdas/closures, async and iterator state machines, `dynamic` call sites,
   and PDB generation. Going through Roslyn gives all of that, guarantees "fully compatible with C#" by construction,
   and still produces plain IL. The front end is backend-agnostic, so a direct IL backend remains possible.
2. **Source generator, not a separate compiler.** It plugs into every C# project without touching the SDK, works in
   the IDE, and lets C# and Typhon reference each other inside one assembly. The price: a pure Typhon project is still a
   `.csproj`, and generators run on every IDE keystroke (they need to stay fast). A dedicated MSBuild SDK
   (`<Project Sdk="Typhon.Sdk">`) can come later.
3. **Static by default, dynamic as an explicit escape hatch.** Speed and C# interop both come from static types;
   gradual typing keeps porting possible. `typhon build --aot` builds a native executable; it additionally rejects dynamic values that come from libraries (TY2001), because Native AOT cannot run late-bound code.
4. **"Valid Python" as a design constraint.** Never add syntax CPython can't parse. You get editor support, formatters
   and linters for free, and — as the benchmarks show — the same file can run on both runtimes, which is also the
   cheapest possible conformance test: diff the outputs (that is exactly what `tests/conformance` does).
5. **Names are kept as written** (`calculate_area` stays `calculate_area` for C# callers). Leading `_` → `internal`
   / `protected internal`, `__x` → `private`. A PascalCase-mapping option is easy to add if C# consumers want it.
6. **Python protocols are bridged, not emulated.** `__eq__` becomes `Equals` + `==`, `__iter__` becomes
   `IEnumerable<T>`, `__getitem__` an indexer — so Typhon objects behave natively in C#, LINQ and data binding.
