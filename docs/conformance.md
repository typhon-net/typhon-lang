# Conformance tests

CPython is the oracle. `tests/conformance/cases/*.ty` are small programs that cover one area each: arithmetic, strings and
format specs, collections, dict semantics, control flow and exceptions, functions and generators, classes and dunder
methods, printing, async, NumPy. They are valid Python. `tests/conformance/expected/*.out` holds what CPython 3.15 printed
for them, and a case passes only when Typhon's output matches it byte for byte.

```powershell
dotnet test tests/Typhon.Conformance.Tests                                   # typhon run (JIT) vs expected output
$env:TYPHON_CONFORMANCE_AOT = "1"; dotnet test tests/Typhon.Conformance.Tests   # also build every case with --aot
python tests/conformance/update_expected.py                                  # regenerate expected/*.out after changing a case
```

* The suite also re-runs every case under CPython (`TYPHON_CPYTHON`, the repo's `.venv`, or `python` on PATH), so a
  stale `.out` file fails the build.
* Known differences are listed in [`tests/conformance/known-failures.txt`](../tests/conformance/known-failures.txt) with
  the reason. Such a case must keep failing: when a fix makes it pass, the suite fails until the line is removed.
* CI ([`.github/workflows/ci.yml`](../.github/workflows/ci.yml)) runs the suite on Windows and Linux. The Native AOT pass
  can be started by hand.

Status: 27 of 29 cases match CPython under `typhon run`, among them `dynamic_programs` (callable objects, class values,
function aliases, Python dict keys, mixed lists, seeded `random`), `binary_data` (bytes, bytearray, struct,
hashlib), `generator_protocol` (send, throw, close, generator return values, coroutines driven by send, `__dict__`), `syntax_features` (match, walrus, starred
unpacking, slice assignment), `builtin_semantics`, `inference_uses` (compiles with `--strict`), `stdlib_modules`,
`stdlib_data` (heapq, bisect, copy, json, re, io, array, frozenset), `bigint_mode`, `dynamic_and_flow` (late-bound
values, rebinding, optional values, ranges, `and`/`or` values), `functions_and_classes` (functions as values, `%`
formatting, class hierarchies, two-phase construction) and `language_data` (namedtuple, `**kwargs`, tuples as
sequences, lambdas in variables, `globals()`, classes defined in functions). The last full `--aot` pass predates the
twelve newest cases: 10 of the 17 cases then built and matched as native executables
(`arithmetic`, `inference`, `loop_else` and `numpy_basics` use dynamic values, which Native AOT rejects). Known failures: NumPy details
(`a.shape` is not a tuple, stepped `ndarray` slices, boolean `sum`), and ints beyond 64 bits in the default mode (`int_overflow`).
[`big_ints.ty`](../tests/conformance/cases/big_ints.ty) covers ints past 32 bits: loops, recursion, inferred functions,
fields, shifts, floor division, formatting, `math`, ranges and containers. Before `int` became 64-bit, Typhon printed
`sum(range(100000))` as 704982704 instead of 4999950000. Writing the suite surfaced and fixed these
differences: inherited constructors (an `Exception` subclass without `__init__`), `sorted()` over objects with
`__lt__`, `Color.RED.value`/`.name`, `type(x).__name__`, `try/except/else` in functions that return a value,
`except ValueError` catching an `IndexError`, iterating a dict, `d.get(k)` without a default, `del`, the `g` format and
float repr (`1e+16`), and built-in functions as values (`map(str, xs)`, `key=len`). The Native AOT pass found two more:
`len()` of a class with `__len__` and the repr of a `set` relied on reflection that AOT trims away.
