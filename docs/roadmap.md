# Roadmap ideas

The [status page](https://typhon-lang.com/status) explains the principles behind these and what they cost.

* C# 15 union types for `A | B` annotations (canonicalized, one generated union per member set)
* `out`/`ref` parameters as tuple returns
* A faster big-int mode: a hybrid int (64-bit fast path, BigInteger only on overflow) instead of BigInteger everywhere
* `decimal` with a precision context; running `doctest` examples; compiling more of CPython's pure-Python stdlib
* Keep the same Python imports and behavior when a module has both a .NET-backed implementation and a CPython-source build.
  Verify both against CPython and measure both in the same .NET workload.
* Pin the version of bundled CPython source, and retain the upstream attribution and full license in the header of each
  unchanged file.
* Grow the conformance suite (port relevant parts of CPython's own test suite)
* Ship the CLI as a `dotnet tool` with platform-specific ReadyToRun packages
* Re-run the Linux benchmark with type inference (the untyped N-body row there predates it)
* `sum(...)`/`any(...)` over generator expressions lowered to loops instead of LINQ
* A fully static Typhon.NumPy API (no `dynamic` return values, e.g. for `a[i]` and `np.dot`), so every NumPy program can be compiled with Native AOT
* Expression fusion for NumPy code: reuse or fuse the temporaries of `np.sqrt(x) * 2.0 - x / 3.0` (the RAM weak spot)
* A dedicated MSBuild SDK and `dotnet new` templates; a language server reusing the Typhon front end

## Interchangeable standard-library implementations

Continue compiling CPython's standard-library sources as a compatibility baseline. For selected
modules, write a second implementation in Typhon that calls .NET for operations it can provide.
Keep Python-specific behavior in the module, including return types, exceptions, and edge cases.

Select implementations per module through Typhon's import resolver. Existing Python clients
should keep the same import names and call sites, including imports through other modules.
Use the same conformance cases to compare both implementations with the pinned CPython release
before offering a switch.

Benchmark the two compiled implementations on the same .NET runtime and inputs. Measure import
and startup time, warmed execution, and allocations so the results show where each implementation
performs better.

For unchanged upstream code checked in as a `.ty` file, add a comment header naming the upstream
project, release or commit, and original file path. Preserve the original copyright and attribution
notices and include the full text of each applicable original license in the header. When adapting
upstream code, retain its notices and licenses and record the changes.
[CPython licensing and acknowledgements](https://docs.python.org/3/license.html)
