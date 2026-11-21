# Typhon benchmark report

What was measured, how, and what the numbers mean. Raw tables: [`RESULTS.md`](RESULTS.md) (Windows, plugged in, from
[`run.ps1`](run.ps1)), [`RESULTS_BATTERY.md`](RESULTS_BATTERY.md) (Windows on battery; the first run with compile times
and with whole-program type inference) and [`RESULTS_LINUX.md`](RESULTS_LINUX.md) (Linux, from
[`linux/run_linux.sh`](linux/run_linux.sh)); charts: [`make_charts.py`](make_charts.py).

![Speed-up of Typhon over CPython and NumPy](img/speedup.png)

## TL;DR

* **Typed, pure-Python code runs 1.9–75x faster on Typhon than on CPython** (geometric mean over 9 programs: **11.1x
  with the JIT, 13.6x as a Native AOT executable**). Biggest wins: numeric loops over floats and small objects
  (N-body 75x, spectral norm 48x, Mandelbrot 40x). Smallest: string, dict and sort workloads, where CPython's C
  runtime already does the heavy lifting (1.6–3.0x).
* **Static types are what make it fast, and Typhon now infers them.** Without annotations the N-body source used to
  fall back to .NET `dynamic` (1.5x faster than CPython). With whole-program type inference it runs as fast as the
  annotated version (74x vs 72x in the same run, section 7) and compiles with Native AOT.
* **Running a script once is cheap now.** The first `typhon run` compiles in 0.65–0.8 s for most programs; after that
  a warm compile server or the script cache bring a tiny script to about 80 ms (CPython 30 ms), and every larger
  pure-Python benchmark runs faster than on CPython, compile included. Before the compile-speed work a first run took
  1.8–2.1 s (section 7).
* **On battery** everything gets slower, Typhon's short loops the most; the speed-ups shrink by 4–7% on average but
  keep their order (section 8).
* **Native AOT makes Typhon programs small and lean**: a self-contained 1.5–1.9 MB executable, **9 MB of RAM** for small
  programs (CPython 12 MB, Typhon JIT 20 MB) and startup on par with CPython on Windows (21 vs 20 ms); on Linux
  **1.8 ms and 4 MB**, the fastest and smallest of every tool tested.
* **Compared with other Python compilers on Linux** (same files, same CPython baseline), Typhon AOT is in the same
  league as Codon and Pythran: geometric mean over the 6 programs all four can compile **Typhon AOT 11.8x, Codon
  11.6x, Pythran 13.6x**. Typhon leads on float- and object-heavy code (N-body, Mandelbrot, fib), Codon and Pythran on
  integer/list code (prime sieve, π). In that run only **Codon** stayed fast on code **without type hints** (29x),
  because it infers types for the whole program; Typhon does too now (74x, section 7), the Linux run predates it.
* **NumPy code is at rough parity** with real NumPy (geometric mean 1.04x with the JIT, 1.13x with Native AOT). Typhon
  wins element loops and broadcasting, NumPy wins dense linear algebra (LAPACK) by 5x (Native AOT) to 10x (JIT).
  Since then Typhon.NumPy has a blocked GEMM and LAPACK-style factorizations: matmul, `solve`, `inv` and `det` take
  0.8–1.4x NumPy's time on one thread and 0.8–2.5x on all cores (section 9).
* **You often don't need NumPy on Typhon**: plain loops compiled by Typhon beat the vectorized NumPy code on 3 of 6
  problems (Mandelbrot 29x, pairwise distances 7.8x, π 3.6x).
* **Memory is mixed.** With the JIT, Typhon has a ~8 MB higher floor and a garbage collector that trades memory for
  speed. Native AOT removes the floor. Typed lists are much denser than Python lists (dot product: 175 vs 468 MB), but
  large NumPy temporaries are Typhon's weak spot (up to 5x NumPy's peak).

## Setup

| | Windows | Linux |
|---|---|---|
| Machine | Intel Core Ultra 9 285HX (24 cores: 8 performance + 16 efficiency), 64 GB RAM, laptop | same machine, Ubuntu 26.04 on WSL2 (kernel 6.18), 12 GB / 24 vCPUs visible |
| CPython | 3.15.2 (standard build, experimental JIT off) | 3.15.2 (via uv) |
| NumPy | 2.5.5 + OpenBLAS 0.3.34 (official wheel) | same |
| Typhon | .NET SDK 11.0.100; `typhon build --strict`, run with `dotnet app.dll` (JIT) or as a Native AOT `.exe` | same, AOT linked with clang 21 |
| Others | – | **Codon 0.20.7** (`codon build -release`, host CPU, own NumPy + OpenBLAS 0.3.33); **Pythran 0.19.0** (`-O3 -march=native`, clang++ 21) |

## Method

* **Identical source.** Every benchmark is one `.ty` file that is valid Python, valid Typhon, and (where the tool
  supports the features) valid Codon and Pythran. The only additions for Pythran are `#pythran export` comments.
* **Correctness check.** Every program prints a result; the runners compare every tool's output with CPython's.
  **Every result matched, on both operating systems, for every tool.**
* **Timing.** Each program measures its own hot section with `time.perf_counter()`; data setup and every compile step
  are excluded. Every configuration ran 5 times; the median is reported. End-to-end process time is reported as well.
* **Compile times** (median per program, not in the timings): Typhon 1.3 s, Typhon Native AOT 4.8 s (Windows) /
  3.8 s (Linux), Codon 4.3 s, Pythran 1.6 s.
* **RAM** is the peak working set (Windows) or max resident set size (Linux) of the whole process.
* **Threads.** NumPy-style programs run in two configurations per tool where possible: NumPy with default OpenBLAS
  threads and with `OPENBLAS_NUM_THREADS=1`; Typhon.NumPy with default threads and `TYPHON_NUMPY_THREADS=1`.
  Comparisons use each side's **faster** configuration. This matters: with default threads, OpenBLAS is 32x slower on
  the 300 x 300 linear-algebra benchmark (1,024 ms vs 31.7 ms single-threaded), a known over-subscription problem on
  hybrid P-/E-core CPUs. Reporting NumPy's default would have shown a false Typhon win.
* **Two measurement traps we hit (and avoided in the final numbers):**
  * On this Windows machine, an executable launched from `%TEMP%` started 3x slower and used 3x the RAM of the very same
    file launched from the repository (a plain C# Native AOT "hello": 62 ms / 25 MB vs 19 ms / 8.5 MB), presumably
    security software inspecting new executables in the temp folder. All benchmark executables run from the repository.
  * In the WSL distro used here, the systemd user session fails to start, and WSL shuts the distro down ~2 minutes after
    every start. The Linux numbers were measured with systemd temporarily disabled in that distro (restored afterwards).

## 1. Pure Python programs (Windows)

| Program | What it stresses | CPython | Typhon JIT | Typhon AOT | Speed-up JIT | Speed-up AOT |
|---|---|---|---|---|---|---|
| N-body | float math on object attributes | 3,024 ms | 40.3 ms | 43.8 ms | **75x** | **69x** |
| Spectral norm | nested loops, small function calls | 469 ms | 9.7 ms | 8.6 ms | **48x** | **54x** |
| Mandelbrot | tight float loop | 2,067 ms | 51.1 ms | 51.9 ms | **40x** | **40x** |
| fib(32) | recursive calls | 134 ms | 9.0 ms | 4.7 ms | **15x** | **29x** |
| Binary trees | millions of small objects (GC) | 783 ms | 75.3 ms | 55.5 ms | **10x** | **14x** |
| Fannkuch | int lists, swaps, slicing | 324 ms | 39.1 ms | 28.4 ms | **8.3x** | **11x** |
| Prime sieve | list indexing, `sum` over a generator | 989 ms | 358 ms | 199 ms | **2.8x** | **5.0x** |
| Word count | string building + dict updates | 413 ms | 161 ms | 140 ms | **2.6x** | **3.0x** |
| `sorted()` | Timsort (C in CPython) on 1M ints + 300k strings | 353 ms | 182 ms | 218 ms | **1.9x** | **1.6x** |
| N-body, untyped | the N-body source without type hints, before type inference (`dynamic`) | 3,286 ms | 2,168 ms | n/a | **1.5x** | – |

**Why the spread.** Typhon turns annotated Python into statically typed code: a `float` attribute is an unboxed
`double` field, `a.x * b.x` is one machine instruction, and a call is a direct call. CPython executes bytecode on boxed
objects. The gap is largest for pure arithmetic on simple types and smallest where the work happens inside CPython's C
runtime anyway (hashing and comparing strings, sorting).

**JIT vs Native AOT.** Ahead-of-time compilation is faster on most programs (no warm-up, no tiered compilation) and
slower on two (N-body, sort), where the JIT's runtime profile-guided optimization helps. AOT targets a portable CPU
baseline by default, while the JIT optimizes for the exact CPU.

**Untyped code needed type inference.** In this run `nbody_untyped` still fell back to `dynamic`: every operation was
a late-bound call, `strict` mode rejected the program and Native AOT could not compile it. Whole-program type inference
(added afterwards) infers every parameter, return value and field of that program from its call sites, so it now
compiles with `--strict` and Native AOT and runs as fast as the annotated version (74x vs 72x in the battery run).

## 2. Startup and footprint

| | Process time Windows | RAM Windows | Process time Linux | RAM Linux | What you ship |
|---|---|---|---|---|---|
| CPython | 19.7 ms | 12 MB | 9.2 ms | 12 MB | the `.py` file + a Python installation |
| Codon | – | – | 8.3 ms | 10 MB | small executable + Codon runtime libraries |
| Typhon JIT | 34.5 ms | 20 MB | 20.0 ms | 30 MB | 0.05 MB `.dll` + the .NET runtime |
| **Typhon Native AOT** | **21.3 ms** | **9 MB** | **1.8 ms** | **4 MB** | **one 1.5–1.9 MB self-contained executable** |

`bench_startup.ty` is a tiny program (split, uppercase, join). End-to-end, a compiled Typhon program is faster than
CPython on every pure-Python benchmark, with the JIT as well as with AOT (e.g. fib: CPython 158 ms, JIT 46 ms, AOT
24 ms on Windows).

## 3. NumPy programs (Windows)

| Program | NumPy (best config) | Typhon JIT (best config) | Typhon AOT | Speed-up (best Typhon build) | RAM NumPy | RAM JIT | RAM AOT |
|---|---|---|---|---|---|---|---|
| Element loop `for i in range(n): a[i]` | 271 ms | 54.0 ms | n/a | **5.0x** | 43 MB | 61 MB | – |
| Broadcasting (pairwise distances, 2000²) | 268 ms | 95.9 ms | 52.9 ms | **5.1x** | 211 MB | 516 MB | 578 MB |
| Reductions along axes (2000 x 2000) | 414 ms | 340 ms | 224 ms | **1.8x** | 89 MB | 91 MB | 72 MB |
| ufuncs (`sqrt`, `exp`, `sin`, 1M x 50) | 1,184 ms | 1,089 ms | 522 ms | **2.3x** | 74 MB | 362 MB | 225 MB |
| Matmul 1000 x 1000 (twice) | 17.4 ms | 24.4 ms | 40.3 ms | **NumPy 1.4x faster** | 64 MB | 89 MB | 56 MB |
| `linalg.solve` + `inv` 300 x 300 | 31.7 ms | 325 ms | 159 ms | **NumPy 5.0x faster** | 37 MB | 37 MB | 16 MB |

**Important:** NumPy itself is not compiled by Typhon; its core is C and Fortran. What runs on Typhon is the *user's
numpy program*, against Typhon.NumPy, a numpy-compatible library written in C# for this project (float64 only).

* Typhon.NumPy wins where NumPy pays per-call Python overhead: the element loop (each `a[i]` on CPython creates a boxed
  `numpy.float64`) and broadcasting.
* Native AOT roughly halves the time of ufuncs, reductions and linear algebra compared with the JIT here.
* NumPy keeps the lead where it calls tuned native libraries: OpenBLAS for matrix multiply, LAPACK for `solve`/`inv`.
  Typhon's AOT matmul is slower than its JIT one, presumably because Native AOT targets a portable instruction set by
  default, so the AVX2/FMA kernel path is not taken (verified since: section 9). The matmul and linalg rows predate
  the rewrite of Typhon.NumPy's dense linear algebra; section 9 has the new numbers.
* Three NumPy programs can't be compiled with Native AOT (`a[i]`, `np.dot` and slicing return `dynamic` in
  Typhon.NumPy). `typhon build --aot` reports the exact lines (TY2001).

## 4. Problems: plain loops vs NumPy

![Do you still need NumPy?](img/problems.png)

The same problem written twice, as plain Python loops and as vectorized NumPy code, each run on both runtimes
(Windows). On CPython you vectorize because loops are slow. Is that still necessary on Typhon?

| Problem | CPython loops | CPython + NumPy | Typhon loops (JIT) | Typhon loops (AOT) | Typhon + NumPy (JIT) | Typhon + NumPy (AOT) |
|---|---|---|---|---|---|---|
| Mandelbrot 400 x 400, 100 iterations | 496 ms | 415 ms | 14.2 ms | **11.4 ms** | 340 ms | 234 ms |
| Pairwise distances, 1500 points | 148 ms | 30.3 ms | 3.9 ms | **3.3 ms** | 38.7 ms | 8.1 ms |
| Estimate π, 5M points | 388 ms | 160 ms | 45.0 ms | 43.5 ms | 69.6 ms | **32.9 ms** |
| Moving average, 5M samples | 329 ms | **31.3 ms** | 56.0 ms | 56.3 ms | 82.5 ms | n/a |
| Dot product, 10M floats | 152 ms | **1.4 ms** | 6.8 ms | 6.5 ms | 7.2 ms | n/a |
| Matrix multiply 200 x 200 | 206 ms | **0.4 ms** | 12.3 ms | 7.4 ms | 5.3 ms | 0.7 ms |

* **Typhon's plain loops are 6–38x faster than CPython's plain loops** (geometric mean 17x).
* **Against vectorized NumPy, plain Typhon loops win 3 of 6 problems**: where vectorization forces extra work
  (Mandelbrot must iterate every point 100 times because arrays can't exit early per element) or builds large
  temporary arrays (pairwise distances, π).
* **NumPy wins where one tuned kernel does the whole job**: BLAS for dot product and matrix multiply, a single C pass
  for `cumsum`. A naive triple loop doesn't beat BLAS on any runtime. (Typhon.NumPy with AOT gets the 200 x 200
  matmul to 0.7 ms, close to NumPy's 0.4 ms; with the JIT, warm-up dominates such a short call.)

Peak RAM for the problems (Windows):

| Problem | CPython loops | CPython + NumPy | Typhon loops (JIT) | Typhon loops (AOT) | Typhon + NumPy (JIT) | Typhon + NumPy (AOT) |
|---|---|---|---|---|---|---|
| Dot product | 468 MB | 180 MB | 175 MB | 182 MB | 185 MB | n/a |
| Moving average | 395 MB | 142 MB | 137 MB | 118 MB | 301 MB | n/a |
| π estimate | 12 MB | 257 MB | 21 MB | 9 MB | 370 MB | 353 MB |
| Pairwise distances | 12 MB | 114 MB | 21 MB | 9 MB | 130 MB | 113 MB |
| Mandelbrot | 12 MB | 42 MB | 20 MB | 9 MB | 111 MB | 67 MB |
| Matrix multiply | 15 MB | 30 MB | 23 MB | 11 MB | 27 MB | 11 MB |

A Python `list[float]` stores a pointer to a separately allocated float object per element (8 + 24 bytes); Typhon's
`list[float]` is a `List<double>` (8 bytes). That's why plain loops over big lists need 2.7–2.9x less memory on Typhon.

## 5. Typhon vs Codon vs Pythran (Linux)

![Three ways to compile Python](img/compilers.png)

[Codon](https://github.com/exaloop/codon) compiles whole Python-syntax programs to native code via LLVM, with
whole-program type inference. [Pythran](https://github.com/serge-sans-paille/pythran) compiles exported numeric
functions to C++ extension modules for CPython. Both run on Linux only here, so this comparison runs on Linux (WSL2,
same machine) against CPython on Linux. Speed-ups are relative to CPython on Linux.

| Program | CPython | Codon | Pythran | Typhon JIT | Typhon AOT |
|---|---|---|---|---|---|
| N-body | 2,439 ms | 66.6 ms (37x) | n/a: classes | **39.3 ms (62x)** | 45.2 ms (54x) |
| Spectral norm | 381 ms | 9.9 ms (39x) | 9.8 ms (39x) | **9.3 ms (41x)** | 13.8 ms (28x) |
| Mandelbrot | 1,348 ms | 82.6 ms (16x) | 52.7 ms (26x) | **48.7 ms (28x)** | **48.7 ms (28x)** |
| fib(32) | 108 ms | 8.0 ms (14x) | 4.3 ms (25x) | 7.5 ms (14x) | **3.9 ms (28x)** |
| Binary trees | 626 ms | n/a: `Optional["Node"]` | n/a: classes | 76.6 ms (8.2x) | **48.1 ms (13x)** |
| Fannkuch | 261 ms | 27.5 ms (9.5x) | **17.7 ms (15x)** | 39.5 ms (6.6x) | 28.1 ms (9.3x) |
| Prime sieve | 979 ms | 80.5 ms (12x) | **67.6 ms (15x)** | 347 ms (2.8x) | 191 ms (5.1x) |
| `sorted()` | 359 ms | **92.5 ms (3.9x)** | n/a: not in a function | 176 ms (2.0x) | 220 ms (1.6x) |
| Word count | 351 ms | 143 ms (2.5x) | 302 ms (1.2x) | 182 ms (1.9x) | **133 ms (2.6x)** |
| N-body, untyped | 2,465 ms | **85.2 ms (29x)** | n/a: classes | 2,340 ms (1.1x) | n/a: dynamic |

On the 6 programs every tool can compile, the geometric means are **Pythran 13.6x, Typhon AOT 11.8x, Codon 11.6x,
Typhon JIT 9.2x**.

* **Float- and object-heavy code** (N-body, Mandelbrot, spectral norm, fib): Typhon is at or near the top.
* **Integer and list code** (prime sieve, Fannkuch, and the π problem): Codon and Pythran are clearly faster. Not
  profiled yet; likely contributors are `sum(...)` over a generator expression being lowered to LINQ (prime sieve),
  Python-exact floored modulo through a helper (π), and bounds-checked `List<T>` access, while Codon defaults to C
  semantics. This is the clearest optimization lead for Typhon.
* **Untyped code**: in this run only Codon is fast, because it infers types for the whole program; Typhon fell back to
  `dynamic`. Typhon has whole-program inference now: on Windows the untyped N-body runs at 74x (section 7). The Linux
  run has not been repeated yet.
* **Coverage**: Pythran compiles only functions without classes (4 programs n/a), Codon rejected one program (a string
  forward reference in a type annotation), Typhon AOT rejects code that uses `dynamic`.

NumPy programs on Linux (speed-up vs the faster CPython + NumPy configuration):

| Program | CPython + NumPy | Codon | Pythran | Typhon JIT | Typhon AOT |
|---|---|---|---|---|---|
| Element loop | 270 ms | 14.1 ms (19x) | **8.6 ms (31x)** | 61.5 ms (4.4x) | n/a: dynamic |
| Broadcasting | 143 ms | 133 ms (1.1x) | **28.0 ms (5.1x)** | 102 ms (1.4x) | 60.8 ms (2.4x) |
| ufuncs | 660 ms | 437 ms (1.5x) | **287 ms (2.3x)** | 870 ms (0.8x) | 340 ms (1.9x) |
| Reductions | 242 ms | **214 ms (1.1x)** | 241 ms (1.0x) | 346 ms (0.7x) | 255 ms (1.0x) |
| Matmul 1000 x 1000 | **11.8 ms** | 26.3 ms (0.4x) | n/a: not in a function | 20.5 ms (0.6x) | 43.9 ms (0.3x) |
| linalg solve + inv | **24.2 ms** | 137 ms (0.2x) | n/a: `np.linalg` | 519 ms (0.05x) | 156 ms (0.2x) |

Pythran's C++ expression templates are the strongest for NumPy-style code. For dense linear algebra, NumPy's
OpenBLAS/LAPACK beats every compiler.

Comparability notes (from the Linux runner): Pythran converts Python lists to C++ containers at every call (included in
its time; e.g. the 10M-element dot product) and every Pythran module imports NumPy (+~60 ms and +~17 MB per process);
Codon uses 64-bit integers and C numerics by default (no overflow happens in these programs); Codon and Pythran target
the host CPU (`-march=native`), Typhon AOT a portable baseline.

## 6. Why Typhon sometimes uses more RAM

Both runtimes are measured the same way (peak memory of the whole process), so the numbers are comparable as "what the
OS charges the process", not as "how much data the program holds":

| Cause | Evidence | Effect on Typhon |
|---|---|---|
| **.NET runtime floor (JIT only)**: runtime, JIT and framework libraries | tiny program: CPython 12 MB, Typhon JIT 20 MB, **Typhon AOT 9 MB** | about +8 MB with the JIT, none with Native AOT |
| **Garbage collection vs reference counting**: CPython frees an object as soon as its last reference disappears; .NET collects garbage in batches, sized by the "gen0 budget" | with a 4 MB gen0 budget, Fannkuch and word count drop from 40–41 MB to 27 MB at the same speed | +13–20 MB in allocation-heavy programs |
| **Large NumPy temporaries**: `np.sqrt(x) * 2.0 - x / 3.0` creates several large intermediate arrays; NumPy frees each immediately, .NET only reclaims them in a full collection | `System.GC.ConserveMemory=5` halves the ufuncs peak (362 → 181 MB, and runs 12% faster) | up to 5x NumPy's peak |
| **Dynamic typing**: untyped code loads the C# runtime binder and generates code per call site | `nbody_untyped` 59 MB vs typed `nbody` 21 MB | +38 MB |

GC configuration experiment (Typhon JIT programs, median of 3 runs):

| Program | .NET defaults | `ConserveMemory=5` | gen0 budget 4 MB |
|---|---|---|---|
| Fannkuch | 42 ms, 40 MB | 41 ms, 41 MB | 35 ms, **27 MB** |
| Word count | 175 ms, 41 MB | 177 ms, 41 MB | 156 ms, **27 MB** |
| Binary trees | 82 ms, 43 MB | 80 ms, 43 MB | 79 ms, **37 MB** |
| NumPy ufuncs | 1,404 ms, 362 MB | 1,237 ms, **181 MB** | 1,426 ms, 362 MB |
| NumPy broadcasting | 144 ms, 516 MB | 143 ms, **424 MB** | 141 ms, 515 MB |
| π with NumPy | 104 ms, 370 MB | 91 ms, 370 MB | 103 ms, 370 MB |

The published numbers use .NET defaults: no GC tuning. The structural fix for NumPy temporaries belongs in the
compiler: Typhon knows that the intermediate results of `np.sqrt(x) * 2.0 - x / 3.0` are dead after the expression and
could reuse their buffers or fuse the expression into one loop.

## 7. One-off runs: compile time included

Sections 1–6 time programs that were compiled beforehand. Python scripts are often run once, so `run.ps1` also
measures the whole process from start to exit: `python script.py`, `typhon run script.ty` in its three situations,
and a program built ahead of time. From the battery run ([`RESULTS_BATTERY.md`](RESULTS_BATTERY.md)), CLI published
with ReadyToRun:

* **First run**: `typhon run` compiles in its own process (parse, type inference, Roslyn) and starts the compile server.
* **Compile server**: later runs send the script to a background process that keeps the compiler loaded and warm.
* **Cached**: an unchanged script that ran before is not compiled at all (one cache slot per script, invalidated by any
  changed source file, imported module or compiler build).

![Running a script: CPython vs typhon run](img/oneoff.png)

| Program | CPython | First run | Compile server | Cached | `typhon build` | Built, per run | Build pays off | `build --aot` | AOT, per run | AOT pays off |
|---|---|---|---|---|---|---|---|---|---|---|
| Tiny script (start-up) | 30 ms | 642 ms | 84 ms | 80 ms | 1,034 ms | 45 ms | never | 5,775 ms | 23 ms | after 831 runs |
| fib(32) | 155 ms | 654 ms | 88 ms | 83 ms | 1,026 ms | 72 ms | after 13 runs | 5,917 ms | 34 ms | after 49 runs |
| Spectral norm | 536 ms | 671 ms | 107 ms | 111 ms | 939 ms | 58 ms | after 2 runs | 5,887 ms | 41 ms | after 12 runs |
| Fannkuch | 373 ms | 672 ms | 144 ms | 128 ms | 1,051 ms | 100 ms | after 4 runs | 5,680 ms | 76 ms | after 20 runs |
| Word count | 428 ms | 935 ms | 400 ms | 281 ms | 996 ms | 221 ms | after 5 runs | 6,736 ms | 228 ms | after 34 runs |
| `sorted()` | 892 ms | 1,029 ms | 375 ms | 371 ms | 985 ms | 319 ms | after 2 runs | 5,948 ms | 356 ms | after 12 runs |
| Binary trees | 948 ms | 719 ms | 204 ms | 174 ms | 955 ms | 128 ms | after 2 runs | 6,610 ms | 99 ms | after 8 runs |
| Prime sieve | 1,449 ms | 1,102 ms | 431 ms | 431 ms | 923 ms | 425 ms | 1st run | 6,083 ms | 251 ms | after 6 runs |
| Mandelbrot | 2,063 ms | 691 ms | 167 ms | 147 ms | 954 ms | 116 ms | 1st run | 5,613 ms | 93 ms | after 3 runs |
| N-body | 3,283 ms | 721 ms | 146 ms | 126 ms | 915 ms | 95 ms | 1st run | 5,546 ms | 78 ms | after 2 runs |
| N-body, untyped (inferred) | 3,301 ms | 670 ms | 165 ms | 114 ms | 947 ms | 103 ms | 1st run | 5,852 ms | 80 ms | after 2 runs |
| NumPy matmul | 169 ms | 667 ms | 167 ms | 140 ms | 1,128 ms | 121 ms | after 24 runs | 6,199 ms | 88 ms | after 77 runs |
| NumPy ufuncs | 1,666 ms | 2,080 ms | 1,703 ms | 1,632 ms | 952 ms | 1,525 ms | after 7 runs | 6,151 ms | 710 ms | after 7 runs |

"Pays off": the number of runs n after which one build plus n runs of the built program take less time than n runs of
CPython ("never": the built program is not faster than CPython).

* **First run**: 0.64–0.81 s for 11 of 17 programs, about 2.8x less than before (1.8–2.1 s, see below); it already
  beats CPython on 6 programs.
* **While you work**: the compile server and the cache stay within 6–180 ms of a program built ahead of time. CPython
  remains faster only for the tiny script (30 vs 84 ms) and is level on the two NumPy programs whose time is spent in
  BLAS and ufunc kernels.
* **Building ahead** gives the fastest process: `typhon build` takes about 1 s and pays off after 1–13 runs on
  pure-Python programs; Native AOT builds take 5.5–6.7 s and give the leanest process.

**What made `typhon run` faster.** Measured with `typhon run --time` and in-process experiments on battery:

| `typhon run`, tiny script | Process time | What changed |
|---|---|---|
| before | 1,879 ms | ~1.2 s of the compile was Roslyn, mostly Roslyn itself being JIT-compiled on every start |
| + Typhon runtime precompiled | 1,344 ms | `print`, `str`, list/dict helpers etc. used to be compiled from source into every script |
| + CLI published with ReadyToRun | 642 ms | Roslyn and the CLI are precompiled to native code |
| + warm compile server | 84 ms | the compiler stays loaded and JIT-optimized between runs (compiles in 20–110 ms) |
| + script cache | 80 ms | an unchanged script is not compiled at all |

Once warm, Typhon's own work (parser, whole-program type inference, C# emission) takes 6–30 ms per program.

## 8. On battery

The battery run ([`RESULTS_BATTERY.md`](RESULTS_BATTERY.md), Windows "Balanced" power plan, 77% → 72% charge) against
the plugged-in run ([`RESULTS.md`](RESULTS.md)). Both are medians of 5 runs on the same machine; the battery run also
includes whole-program type inference (only `nbody_untyped` changes) and the faster CLI (only compile times change).

| | Plugged in | On battery | Change |
|---|---|---|---|
| Typed programs, speed-up over CPython (geometric mean, JIT / AOT) | 11.1x / 13.6x | 10.7x / 12.6x | −4% / −7% |
| CPython, hot sections (9 typed programs) | | | −9% to +42% (median +7%) |
| Typhon JIT, hot sections | | | +5% to +39% (fib) |
| Typhon AOT, hot sections | | | +3% to +55% (Fannkuch) |
| Tiny script, whole process: CPython / JIT / AOT | 19.7 / 34.5 / 21.3 ms | 29.7 / 45.3 / 22.7 ms | +51% / +31% / +7% |
| `typhon build --aot` (median per program) | 4,767 ms | 6,098 ms | +28% |
| NumPy programs, speed-up (geometric mean, JIT) | 1.04x | 1.15x | |

* Battery mode costs Typhon more than CPython in relative terms: Typhon's hot sections are short (milliseconds), so
  lower clocks and core scheduling weigh more than in CPython's multi-second loops. The ranking does not change.
* Battery runs vary: an earlier battery run on the same day measured process starts about twice as slow as plugged in.
* Every program printed the same result as CPython in both runs.

## 9. Dense linear algebra: matmul and `numpy.linalg` (Linux)

Sections 3 and 5 show NumPy 1.4–20x ahead on matrix multiply and `solve`/`inv`. This section explains that gap and
measures Typhon.NumPy after a rewrite of its dense linear algebra.

**Why NumPy was faster.** NumPy hands both to OpenBLAS (here scipy-openblas 0.3.34, Haswell kernels, 16 threads by
default). Typhon.NumPy had:

* **matmul**: B transposed once, then every element a dot product of two contiguous rows (2 x 4 tiles, 8 Vector256
  FMA accumulators). Without packing, from k ≈ 1000 on the two rows of A and four of Bᵀ (48 KB) no longer fit in L1,
  every step streams from L2, and four horizontal sums end every tile: 18–20 GFLOP/s up to n = 512, **10–11 GFLOP/s
  from n = 1000**, one thread. OpenBLAS packs A and B into cache-sized panels and keeps a block of C in registers:
  55–60 GFLOP/s, which is close to this core's AVX2/FMA peak.
* **solve / inv / det**: an unblocked LU (one row axpy per element below the pivot, memory-bound once the matrix
  leaves L2) and scalar forward/back substitution, column by column through strided reads; `inv` solved against the
  identity that way. 1–10 GFLOP/s: `inv` of 1000 x 1000 took **1.5 s, NumPy 63 ms**.
* Not the cause: `dynamic` or boxing (the calls are statically typed), bounds checks, allocation, or the algorithm for
  `inv` (NumPy's is also LU plus two triangular solves on the identity). Memory layout neither: Typhon.NumPy arrays
  are always contiguous (slices and `.T` are copies).

**What changed** (`src/Typhon.NumPy/MatMul.cs`, `LinAlg.cs`, `LinAlgEigen.cs`):

* **GEMM structured like GotoBLAS/BLIS**: A and B packed per 256-deep panel into 6-row and 8-column slivers, a 6 x 8
  micro-kernel holding C in 12 Vector256 registers (one broadcast and two FMAs per element of A; a Vector128 variant
  for Arm64 and for Native AOT builds without AVX), 72 x 256 blocks of A in L2, the C tile and the next B sliver
  prefetched (power-of-two sizes lost 20–30 % without), C tiles spread over threads. Every element is summed in the
  same order whatever the thread count, so results are identical on 1 or 16 threads. Packing reads any strides, so
  the factorizations multiply sub-matrices and transposes without copying.
* **LAPACK's algorithms on that GEMM**: recursive LU with partial pivoting (most of the work becomes a few large
  products; 8-column leaves with vectorized row updates), recursive triangular solves and Cholesky, blocked
  Householder QR (compact WY, LAPACK's sign convention), `solve`/`inv` as LAPACK's `gesv`, `det` through `slogdet`
  as NumPy computes it. New functions: `slogdet`, `cholesky`, `qr`, `eigh`/`eigvalsh` (Householder tridiagonalization
  + implicit QL), `svd`/`svdvals` (Golub-Kahan-Reinsch), `lstsq`, `pinv`, `matrix_rank`, `cond`, `LinAlgError`; `@`
  broadcasts batch dimensions. Methods with hot loops are `AggressiveOptimization`: tier-0 code made 100 x 100 solves
  5x slower in short programs.

**Before and after.** i7-10700K (8 cores / 16 threads, AVX2 + FMA, no AVX-512) on WSL2, .NET 11.0.100, CPython 3.14.4,
NumPy 2.5.5 + scipy-openblas 0.3.34. Program: [`linalg/sweep.ty`](linalg/sweep.ty) (valid Python and Typhon; Typhon
via `typhon build` + `dotnet`). Other builds were running on the machine (load average 4–25), so each cell is the
best of N calls in a process (N = 3–50 by size) and of 3 process runs interleaved across configurations; treat
differences below ~15 % as noise. Milliseconds; GFLOP/s in parentheses (2n³ for matmul, ⅔n³ for an LU, 2n³ for inv).

| | n | Typhon before, 1 thread | Typhon after, 1 thread | NumPy, 1 thread | Typhon before, 16 threads | Typhon after, 16 threads | NumPy, 8 threads | NumPy, 16 threads (default) |
|---|---|---|---|---|---|---|---|---|
| matmul | 64 | 0.048 | 0.020 (26) | 0.013 (40) | 0.093 | 0.021 (25) | 0.013 (40) | 0.013 (40) |
| matmul | 128 | 0.153 | 0.092 (46) | 0.079 (53) | 0.129 | 0.070 (60) | 0.029 (145) | 0.027 (155) |
| matmul | 256 | 1.36 | 0.678 (49) | 0.562 (60) | 0.586 | 0.321 (105) | 0.197 (170) | 1.78 (19) |
| matmul | 512 | 9.75 | 5.78 (46) | 4.66 (58) | 2.75 | 1.71 (157) | 1.46 (184) | 2.19 (122) |
| matmul | 1000 | 100 | 44.9 (45) | 33.7 (59) | 19.7 | 11.9 (169) | 12.2 (163) | 25.5 (79) |
| matmul | 1024 | 125 | 48.9 (44) | 36.4 (59) | 21.6 | 13.6 (157) | 10.7 (201) | 19.0 (113) |
| matmul | 2048 | 1,055 | 413 (42) | 315 (55) | 287 | 94.4 (182) | 115 (149) | 148 (116) |
| solve (vector) | 100 | 1.11 | 0.067 | 0.054 | 0.966 | 0.067 | 0.075 | |
| solve (vector) | 300 | 1.95 | 0.671 | 0.597 | 1.97 | 0.830 | 0.388 | |
| solve (vector) | 1000 | 80.8 | 22.5 (30) | 18.5 (36) | 426 | 16.1 (41) | 8.86 (75) | |
| inv | 100 | 1.53 | 0.132 | 0.141 | 1.49 | 0.130 | 0.100 | |
| inv | 300 | 25.8 | 2.28 (24) | 2.73 (20) | 26.2 | 2.81 (19) | 1.12 (48) | |
| inv | 1000 | 1,478 | 90.8 (22) | 63.3 (32) | 1,530 | 38.7 (52) | 30.8 (65) | |
| det | 100 | 0.106 | 0.069 | 0.055 | 0.107 | 0.071 | 0.056 | |
| det | 300 | 1.88 | 0.770 (23) | 0.615 (29) | 2.27 | 0.966 (19) | 0.390 (46) | |
| det | 1000 | 84.1 | 23.1 (29) | 20.2 (33) | 120 | 16.7 (40) | 10.2 (66) | |
| solve (1000 right-hand sides) | 1000 | 1,529 | 86.0 | 64.4 | 1,424 | 41.9 | 31.1 | |

NumPy's default 16 threads is shown for matmul only: on this busy machine OpenBLAS's spinning threads slow `solve`
and `inv` down by orders of magnitude (the threading table of the Linux run: 20 s instead of 57 ms for
`bench_np_linalg`). Every result checksum matched NumPy's in every configuration.

* **One thread**: matmul went from 10–20 to 42–49 GFLOP/s, **74–86 % of OpenBLAS's speed** from n = 128 on. `solve`,
  `inv` and `det` take 0.8–1.4x NumPy's time at every size; before, they took 2–23x.
* **All cores**: matmul from n = 512 on runs at 0.8–1.2x OpenBLAS's 8-thread speed (and ahead of its default 16
  threads), the n = 1000 factorizations at 0.55–0.8x. Small products and LU around n = 300 gain little from threads
  (0.4–0.6x); see below. `norm` of a 1000 x 1000 matrix: 0.38 → 0.22 ms on one thread, 0.044 ms on all (NumPy 0.21 /
  0.042 ms).

The functions that did not exist before (best of 2 runs; `cholesky` and `eigh` on a symmetric positive-definite
matrix, `qr` and `svd` on a random square one; full `U` and `Vh`):

| | n | Typhon, 1 thread | NumPy, 1 thread | Typhon, 16 threads | NumPy, 8 threads |
|---|---|---|---|---|---|
| qr | 100 | 0.301 | 0.368 | 0.302 | 0.403 |
| qr | 300 | 3.85 | 4.79 | 5.88 | 4.90 |
| qr | 1000 | 130 | 93.2 | 121 | 52.7 |
| cholesky | 100 | 0.077 | 0.055 | 0.076 | 0.033 |
| cholesky | 300 | 0.663 | 0.714 | 0.917 | 0.908 |
| cholesky | 1000 | 16.3 | 17.1 | 17.8 | 13.3 |
| eigh | 100 | 0.933 | 0.663 | 0.944 | 0.725 |
| eigh | 300 | 23.9 | 6.79 | 24.1 | 5.10 |
| eigh | 1000 | 996 | 170 | 1,135 | 88.9 |
| svd | 100 | 2.07 | 1.36 | 2.12 | 1.73 |
| svd | 300 | 42.3 | 14.8 | 43.7 | 14.3 |
| svd | 1000 | 2,924 | 465 | 2,299 | 313 |

The two NumPy benchmark programs of sections 3 and 5 (hot section, median of 5 runs, same session):

| Program | Typhon before: 16 threads / 1 | Typhon after: 16 threads / 1 | NumPy: 8 threads / 1 / 16 (default) |
|---|---|---|---|
| `bench_np_matmul` (two 1000 x 1000 products) | 55.5 / 351 ms | **39.9** / 165 ms | 40.6 / 105 / 115 ms |
| `bench_np_linalg` (10 x `solve` + `inv`, 300 x 300) | 867 / 809 ms | 130 / **98.0** ms | 38.9 / 53.6 ms / – |

Both hot sections include the first call, and with it the JIT compilation of the kernels (~25 ms: methods marked
`AggressiveOptimization` are compiled on first use, ReadyToRun code does not cover them). That is why the short
`bench_np_linalg` takes 1.8x NumPy's single-thread time while the sweep's steady state is 0.8–1.4x.

**What is still slower, and why.**

* **Single-threaded GEMM, ~20 % behind**: OpenBLAS's hand-scheduled assembly against RyuJIT's code. The micro-kernel
  alone reaches 60–66 GFLOP/s on cache-resident panels; the whole product loses another ~25 % to packing (memory
  bound) and to streaming C through the cache once per 256-deep panel. An AVX-512 machine would want a wider kernel
  (not written: no hardware here to tune it on).
* **Threads on small problems**: a `Parallel.For` dispatch costs 10–20 µs, OpenBLAS keeps spinning workers that start
  in about a microsecond (which is also why its default thread count collapses on a loaded machine). LU is threaded
  only inside its GEMMs (its 8-column panels are serial); OpenBLAS has a parallel recursive `getrf`. Hence 128 x 128
  products and LU/`inv` around n = 300 gain little from threads.
* **eigh and svd** use unblocked Householder reductions (tridiagonal, bidiagonal: memory-bound row operations) and QR
  iterations, where LAPACK uses blocked reductions and divide and conquer (`syevd`, `gesdd`): 1.4–3.5x slower at
  n = 100–300, 6–13x at n = 1000. QR's panels are unthreaded level-2 code, so it barely gains from threads.
* **Native AOT** uses the default x64 baseline (no AVX), so it runs the Vector128 kernel without FMA: 14–15 GFLOP/s on
  one thread (`bench_np_matmul` AOT: 126 → 67 ms). Building with `IlcInstructionSet` (e.g. `x86-64-v3`) would give it
  the AVX2 kernel; that is a `typhon build --aot` option, not part of this change.

**A native BLAS?** An optional P/Invoke backend (OpenBLAS or MKL found at run time, managed code as the fallback)
would close the remaining single-thread GEMM gap (~1.25x) and most of the eigh/svd gap. It was not added: neither
Linux CI nor Windows ships a BLAS (this machine has none either, only NumPy's private copy with renamed ILP64 symbols),
LP64/ILP64 and symbol names differ between distributions, and OpenBLAS's spinning threads would compete with the .NET
thread pool, as the NumPy default-thread column shows.

Reproduce: `typhon build benchmarks/linalg/sweep.ty -o out && dotnet out/typhon_sweep.dll matmul` (or `linalg`) and
`python benchmarks/linalg/sweep.ty matmul`; one thread: `TYPHON_NUMPY_THREADS=1` / `OPENBLAS_NUM_THREADS=1`. Kernel
tests: `dotnet test tests/Typhon.NumPy.Tests`.

## Limitations

* One machine (a laptop CPU with performance and efficiency cores); Linux runs in WSL2 on the same machine. Runs vary
  by roughly ±10–30 %; Windows and Linux numbers are not directly comparable with each other (compare within a table).
  Power matters on a laptop (section 8); `run.ps1` now records it in the results file.
* The main Windows run and the Linux run predate whole-program type inference; only `nbody_untyped` is affected.
* A small benchmark set written for this proof of concept, with type annotations (Typhon needs them; CPython ignores
  them). Real applications spend time in I/O, libraries and startup that these programs don't model.
* Not measured: PyPy, Cython, mypyc, Numba, CPython's experimental JIT.
* Typhon.NumPy implements a subset of NumPy (float64 only) and does not match NumPy's random-number streams.
* Typhon and Typhon.NumPy were vibe-coded in a short time (see the README); results show what the approach can do, not
  a finished product.

## Reproduce

```powershell
# Windows: CPython (+ NumPy) vs Typhon JIT + Native AOT (needs the VS "Desktop development with C++" workload for AOT)
python -m venv .venv; .venv\Scripts\pip install numpy matplotlib
./benchmarks/run.ps1 -Repeat 5                    # writes benchmarks/RESULTS.md (incl. one-off runs and the power state)

# Linux (WSL): CPython, Codon, Pythran, Typhon (setup: see the header of benchmarks/linux/run_linux.sh)
wsl -- bash -lc "cd benchmarks/linux && bash ./run_linux.sh -n 5"   # writes benchmarks/RESULTS_LINUX.md

.venv\Scripts\python benchmarks/make_charts.py    # writes benchmarks/img/*.png
```
