# Typhon runtime

The C# source of Python's builtins and the standard modules Typhon provides. It is not compiled into the compiler:
the compiler embeds every `.cs` file below this folder (see `Typhon.Compiler.csproj`) and
`TyphonCompiler.RuntimeSource` concatenates them, so that each Typhon program or library gets its own internal copy (the
source generator, `typhon build` and Native AOT add it to the compilation). `src/Typhon.Runtime` compiles the same files
once into `Typhon.Runtime.dll`, which `typhon run` and the compile server reference instead, for speed.

Because the files are concatenated into one compilation unit:

- every file declares its `using` directives inside its namespace blocks, never at the top of the file;
- the order of the files does not matter; a new file needs no registration anywhere, it only has to live below this
  folder.

| Folder | What it holds |
|---|---|
| `Builtins/` | `Builtins`, one static partial class: `print`, `len`, `str`, `repr`, formatting, arithmetic and comparison with Python's semantics, slicing, unpacking, the methods of str, list, dict and set |
| `Types/` | the runtime's value types: `PyTuple`, `PyRange`, generators, function values, files, `__dict__` views, Python's key equality |
| `Modules/` | standard modules: `math`, `sys`/`os`/`time`/`asyncio`, `itertools`/`collections`/`functools`/... (`Stdlib.cs`), `heapq`/`bisect`/`copy`/`io`/`array` (`DataModules.cs`), `json`, `re`, `random`, `datetime`, `contextlib`, `dataclasses`, `pytest` (and the `typhon test` runner), and bytes with `struct` and `hashlib` |
