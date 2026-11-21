# The Typhon language

## Types: gradual, TypeScript-style

| Python annotation | C# / CLR type | | Python annotation | C# / CLR type |
|---|---|---|---|---|
| `int` | `long` (System.Int64) | | `list[T]` | `List<T>` |
| `float` | `double` | | `dict[K, V]` | `Dictionary<K, V>` |
| `str` | `string` | | `set[T]` | `HashSet<T>` |
| `bool` | `bool` | | `tuple[A, B]` | `(A, B)` (ValueTuple) |
| `None` (return) | `void` | | `Optional[T]`, `T \| None` | `T?` |
| `Any` | `dynamic` | | `Callable[[A], R]` | `Func<A, R>` / `Action<A>` |
| `Iterator[T]` | `IEnumerable<T>` | | `Array[T]` (Typhon) | `T[]` |
| `decimal`, `int32`, `float32`, ... | `decimal`, `int`, `float`, ... | | any .NET type | itself |

Locals are inferred (`x = 1.5` → `var x = 1.5`). Parameters, return types and fields without annotations are inferred
from the whole program:

* **Parameters from call sites.** Every call of a project function or constructor records the argument types. Each
  distinct combination becomes one typed overload, so `scale(3)` and `scale(1.5)` call `scale(int)` and
  `scale(double)` and keep Python's int/float behaviour.
* **Return types from `return` statements**, including recursion (`fib(n)` → `int`) and `None` (a function that
  returns an `int` or `None` → `int?`).
* **Fields from the constructor**: `self.x = x` gets the type `x` has in every constructor signature.

The untyped (`dynamic`) version of a function stays only where it can actually run: when a call passes a value of
unknown type. A function passed as a value (`map(f, xs)`) is called through a lambda and keeps it too. The inference
runs the emitter until the signatures stop changing; if they do not settle, the code is emitted as written.
[`tests/conformance/cases/untyped.ty`](../tests/conformance/cases/untyped.ty) has no annotations at all and compiles
with `--strict`. Methods are inferred the same way from calls whose receiver's class is known (`obj.m(x)`, `self.m(x)`):
an overload per signature, beside the late-bound version that calls through untyped receivers, bound methods and
`getattr` reach; not for methods that a subclass overrides or that override, decorated methods, methods of generic
classes and methods a `Protocol` declares (return types only there). Note that a public function called only
with ints inside the project gets an `int` signature for C# callers too.

Where the compiler still cannot determine a type — an empty `[]`, a value from a dynamic API, a function that is only
called from C# — it falls back to C# `dynamic`, and `TyphonTypeMode` decides what that means:

| Mode | Implicit `dynamic` | Use for |
|---|---|---|
| `strict` | compile error (TY1001) | libraries, hot paths, a prerequisite for Native AOT |
| `warn` (default) | compiles, warning per site | normal code, gradual migration |
| `permissive` | compiles silently | porting existing Python |

An explicit `Any` annotation is always allowed. The cost is real: `benchmarks/bench_nbody.ty` and its
annotation-free twin `bench_nbody_untyped.ty` differ by 46x (see [benchmarks](benchmarks.md)).

## Semantics: Python syntax, CLR types, Python-flavoured operators

Typhon deliberately uses **CLR semantics for types and objects** (fixed class layouts, nominal types, 64-bit `int`,
reference equality unless `__eq__` is defined) and **Python semantics where it is cheap and expected**:

* `int` is a 64-bit `long` and arithmetic is checked: it never wraps around. `sum(range(100000))`, `factorial(20)` or
  `2 ** 62` give CPython's result; a value beyond 64 bits raises `OverflowError` (CPython would switch to arbitrary
  precision, see [`int_overflow.ty`](../tests/conformance/cases/int_overflow.ty)). **Big-int mode** makes `int` a
  `System.Numerics.BigInteger`, unbounded like Python's: `typhon run --bigint`, `<TyphonIntMode>bigint</TyphonIntMode>`
  in a project, or a `# typhon: int=bigint` comment in any module (it applies to the whole program). Range counters
  and `len()` stay 64-bit and are widened where an operation could leave 64 bits; `factorial`, `pow`, `gcd`, ... use
  exact BigInteger overloads. It is slower than 64-bit mode, so it is opt-in
  (see [`bigint_mode.ty`](../tests/conformance/cases/bigint_mode.ty)). `<<`/`>>` shift like Python, not
  modulo 64. Where .NET wants an `int` (list/string indexes, `s.PadLeft(n)`, `List.Insert(i, x)`, `DateTime(y, m, d)`)
  the value is narrowed with an overflow check; overloads that take a `long` (`Math.Max`) get the `long`. .NET ints
  read into Python variables (`n = sb.Length`) become Python ints, and `int32(x)`, `uint8(x)`, ... convert explicitly
* `/` is true division, `//` floor division, `%` takes the sign of the divisor, `**` is power
* truthiness in conditions (`if items:`), `and`/`or`/`not`, chained comparisons, `in`/`not in`, `is None`
* slicing with clamping, negative indices and steps (`s[::-1]`), `[0] * n`, `"-" * 40`, `list + list`; computed
  negative positions count from the end (`xs[-k]`, `xs[i - 1]` at `i == 0`). The check is left out where the compiler
  can show a position is non-negative (range counters, locals that only ever hold non-negative ints, `j` inside
  `while i < j:`), so hot loops pay nothing for it
* `==` compares contents (lists, dicts, sets, tuples), `<` orders sequences lexicographically and sets by inclusion,
  `bool` is an int (`True + True`), a `str`'s elements are one-character strs, `dict` keeps insertion order, `range` is
  a sequence (`r[-1]`, `len(r)`, `x in r`), `x or y`/`x and y` give the deciding operand, `xs += ys` extends in place
* a name rebound to a value of another type (`n = str(n)`, `x = int(x)`) becomes a new variable, or a wider one
  (`float`, else `dynamic`) when the rebinding is in a nested block; `if x is not None:` narrows an `int | None`
* values of unknown type (`dynamic`) still call Python's str/list/dict methods (`d.strip()`, `"".join(d)`), unpack
  (`a, b = d`), iterate and index (`d[k]`, `d[i] += 1`); files read lines with exactly the newlines they contain
* late-bound values keep Python's rules where C# would differ: a call through a late-bound callable works for function
  values, bound methods, delegates, objects with `__call__` and classes passed as values (`cls(n, n)`); dict keys and set
  items of any type compare like Python's (`(1, 2)` whatever its items' static types, `d[3.0]` finds the key `3`); a list
  that gets ints and floats (`xs = [0]; xs.append(xs[-1] + 0.5)`) holds both; an int variable updated with a late-bound
  value, or an object variable rebound to one, becomes late-bound itself; `x = None` followed by `x = Node()` types `x` as
  a `Node`; set algebra works between sets of different item types
* a name bound once to a function and only called (`icount = itertools.count`, or a parameter default that callers never
  pass, `def solve(..., bisect=bisect)`) calls that function directly; an instance of a class with `__call__` is
  callable; `self[x, y]` passes the tuple to `__getitem__`/`__setitem__`; attributes may be created on an object from any
  method of any class; a subclass `__init__` may skip its base's `__init__`
* whole-program inference only refines: a definitely late-bound argument makes only its own parameter dynamic, values
  that keep flipping between types settle as dynamic, and a container's item types do not depend on whether a function's
  typed or late-bound version was emitted in a pass
* functions are values: a module function passed, assigned, stored or returned has its own type (one instance of a
  generated `PyFunction` subclass). A parameter that receives it is specialised per function, so a call through it is a
  direct, typed call (`apply(f, x)`, `eval_times_u(part_a, u)`); where the type is lost (a dict of handlers, `print(f)`)
  the value is still callable and prints as `<function f at 0x...>`
* a function that can fall off its end returns `None` there; `printf`-style `"%s: %5.2f" % (name, v)` and
  `"%(key)s" % d` format like CPython
* class hierarchies keep Python's dynamic lookup where it matters: a method that only subclasses define gets a stub in
  the base (raising `AttributeError`), overrides agree on one return type, `obj.attr = v` from outside a class and
  `Cls.CONST = v` after it declare fields, class variables read through `self`, `self.__class__` or `type(self)` reach
  the class, `__rmul__ = __mul__` aliases a method, and `Base.__init__(self, ...)` / `super(Cls, self)` work. A subclass
  that sets attributes before calling `super().__init__()` gets Python's two-phase construction (allocation, then
  `__init__` methods called in the order Python calls them), so the base's `__init__` can call overrides that read them
* `[None] * n` and `[None for _ in ...]` are typed by what is stored into them later, and so are module-level
  `NAMES = []` filled anywhere in the module
* `namedtuple(...)`, `class P(namedtuple(...))` and `class P(NamedTuple)` become classes with the tuple protocol
  (unpacking, indexing, iteration, `len`, equality, hashing, ordering, `P(x=1, y=2)` repr, `_replace`, `_asdict`,
  `_fields`, `_make`), their field types inferred from the calls
* `**kwargs` parameters are dicts of the extra keyword arguments (`f(**d)` merges a mapping into them); keyword-only
  parameters after `*args` work
* a long tuple of one item type, and tuples of different lengths in one literal, are sequences (`PyTuple<T>`):
  concatenation, repetition, slicing and `sum(..., ())` work as in Python; short mixed tuples stay C# value tuples
* a lambda stored in a variable (`put = lambda b, i, p: ...`) is a delegate typed by its calls; `globals()` returns
  the module's names; a class defined inside a function that uses none of its variables moves to module level
* `print`/`str`/`repr`/f-strings format like CPython (`True`, `None`, `[1, 2]`, `{'a': 1}`, `1.0`, `Color.RED`)
* Python exceptions map to .NET ones (`ValueError` → `ArgumentException`/`FormatException`, `KeyError` →
  `KeyNotFoundException`, ...), so `except ValueError` catches what .NET throws and C# can catch what Typhon raises

## Supported (PoC)

Modules & packages (folders → namespaces), `import`/`from ... import` of Typhon modules, .NET namespaces/types and the
Typhon stdlib, functions with defaults/`*args`/keyword arguments, PEP 695 generics (`def f[T]`, `class Box[T]`, bounds),
classes with inheritance, `super()`, multiple inheritance and mixins (Python's C3 method resolution order: one base
stays the C# base class, the other bases' members are copied in, `super()` and `Base.method(self)` follow the MRO —
cooperative `super()` across diamonds included, for methods and for `__init__` chains whose classes call
`super().__init__()` first — and `isinstance(x, Mixin)` holds for the classes it is mixed into; a mixin may use
attributes of the classes it is mixed into), properties (+setters), `@staticmethod`/`@classmethod`, `@abstractmethod`/`ABC`,
`@override`, `@final`, `@dataclass` (with `field(default=..., default_factory=..., init=, repr=, compare=)`,
`__post_init__`, `asdict`, `astuple`, `fields`, `replace`), `Enum` and `IntEnum` (with `auto()`), `Protocol` (→ interface,
implemented structurally *or* explicitly), operator overloading, docstrings → XML docs,
decorators written in Python on functions (module-level and nested: `@trace`, `@repeat(3)`, stacked decorators,
`@functools.wraps(f)`; `f = d(f)` is applied where the def runs, so a memoizing decorator also sees recursive calls), on
methods (also below `@staticmethod`; the wrapper gets `self` first and the arguments as written, and C# callers still
see a method) and on classes (run where the class is defined, with the class: registries; the result must be the class
itself), `@functools.total_ordering`, `*args` as a tuple, `sys.exit` raising `SystemExit` (finally blocks run, `except
Exception` does not catch it, uncaught it ends the program with its code; `os._exit` ends it at once), `getattr`/`setattr`/`hasattr`/`delattr` (properties,
class variables, methods as bound methods, attributes added at run time, Python methods of built-in values),
function values called with `*args`, `**kwargs` and keyword arguments, attributes set on functions (`wrapper.calls = 0`),
`functools.partial`, a decorator that names no Python callable → .NET attribute (`@Obsolete("...")`, `@Fact`),
`typing.Annotated` metadata that names a .NET attribute → that attribute on the property a class-level annotation
declares (`start: Annotated[int, Parameter()] = 0` → `[Parameter()] public long start { get; set; } = 0;`, for Blazor's
`[Parameter]`/`[Inject]` or DataAnnotations' `[Required]`; other metadata is ignored, as CPython ignores it), classes
deriving from .NET classes (a method overriding a .NET virtual or abstract member keeps that member's access:
`def OnInitialized(self)` in a subclass of Blazor's `ComponentBase` is `protected override`; see
[`samples/BlazorApp`](../samples/BlazorApp)),
`if/elif/else`, `while`, `for` (counting loops over `range` compile to plain `for` loops; iterating a dict yields its
keys), `for/while ... else`, `match`/`case` (PEP 634: literal, capture, sequence, mapping, class and or-patterns,
guards), the walrus operator `:=`, `except A, B:` (PEP 758), starred unpacking (`a, *rest = xs`, `[*a, *b]`, `f(*args)`,
`{**d1, **d2}`), slice assignment and `del xs[a:b]`, `del d[key]`/`del xs[i]`, `try/except/else/finally`,
`raise`, `with` (Python's `__enter__`/`__exit__` protocol, an `__exit__` that returns true suppresses the exception; a
.NET `IDisposable` such as a file → `using`), `contextlib` (`contextmanager`, `suppress`, `nullcontext`, `closing`),
`async def`/`await`/`async for`/`async with` (→ `Task`), generators (`yield`, `yield from`, `yield` in `try`/`except`/
`finally` and inside expressions such as `x = 2 * (yield x)`; a generator that receives values, `x = yield v`, or returns
one supports `send`, `throw`, `close` and `StopIteration.value`), `bytes`/`bytearray` (all of `bytes`' methods on both)
with `struct` and `hashlib`, `obj.__dict__` and `vars(obj)` as live views of an object's attributes,
lambdas, closures, `global`/`nonlocal`, list/dict/set comprehensions and generator expressions (→ LINQ), f-strings with
the Python format-spec mini-language and `f"{expr = }"`, tuple unpacking, `if __name__ == "__main__":` (or a `__main__.ty` file) as the
entry point, `asyncio` on .NET tasks: `asyncio.run(main())` runs the coroutine on a single-threaded event loop, so
coroutines switch only at `await` as in Python (no races between them, a deterministic order); `gather`, `create_task` (a
Task with `done`/`result`/`exception`/`add_done_callback`), `wait_for` with `TimeoutError`, `as_completed`, `sleep`,
`Queue`, `Lock`, `Event`, `Semaphore`, `TaskGroup`, `async with` on classes with `__aenter__`/`__aexit__`, and async
comprehensions (`[x async for x in agen()]`). A coroutine starts running when it is called (Python starts it at its first
`await`), and a task cannot be cancelled from outside.

## Not supported yet / known gaps

`del` of names/attributes, complex, metaclasses, `decimal`, a Python decorator combined with `@classmethod` or
`@property` on the same method, a class decorator that returns something other than the class, enums with non-int values, `eval`/`exec` and other runtime
reflection on Python objects, multiple inheritance from two .NET classes, static methods
on generic classes need explicit type arguments (`Stack[int].of(...)`), .NET `out`/`ref` parameters (planned: tuple
returns like IronPython), ints beyond 64 bits outside big-int mode (`OverflowError`), exact comparison of an int with a
float past 2**53 (they compare as floats), `doctest` examples (`testmod()` runs none), .NET attributes that another
source generator acts on, such as `@JSExport` (the attribute is emitted, but Roslyn's generators do not see each other's
output; declare such methods in C#, as [`samples/WasmLife`](../samples/WasmLife) does). Union types other than
`X | None` are `object` for now (see [roadmap](roadmap.md)). The [conformance suite](conformance.md) tracks the differences that are known.

**numpy (Typhon.NumPy)** covers the common API (creation, ufuncs with broadcasting, reductions with `axis`, `@`,
`linalg`, `random`, slicing/masks) but: float64 only, slices and `.T` are copies (`reshape`/`ravel` are views),
`a[i]` returns `dynamic` (annotate the target, `v: float = a[i]`, to get back to static code), `==` compares references
(use `np.equal`), random streams are deterministic per seed but differ from NumPy's.

**argparse** is CPython 3.14's (parsing, subcommands, groups, help and usage layout, colors on a terminal, messages).
`parse_args()` returns a late-bound `Namespace` (`args.verbose`; an `argparse.Namespace` annotation keeps it late-bound);
list values are typed lists (`list[int]` for `type=int`). An error, `-h` and `--version` print what CPython prints and
raise `SystemExit` with its status, as in CPython (`except SystemExit` catches it). `set_defaults(func=...)` (subcommand handlers) and `Namespace(a=1)`
work. Not supported: custom `Action` or `HelpFormatter` subclasses. (Functions and fields annotated with a standard module's type, such as
`argparse.ArgumentParser`, `logging.Logger` or `re.Pattern`, are internal to the program, like the runtime's types.)

**logging** covers loggers and their hierarchy, levels, `basicConfig`, `StreamHandler`/`FileHandler`/`NullHandler`,
`Formatter` (`%`, `{` and `$` styles), filters (`Filter` or a function of the record), `extra=` and the last-resort
handler. Approximations: `exc_info=True` and `logging.exception()` show the last exception raised on the thread, as a
traceback of the `.ty` frames without CPython's `~~^^` markers; `%(lineno)d` of a call inside an `except`/`finally`
block is the line in the `try` block that raised (a .NET stack-walk limit); keyword arguments (`exc_info=`, `extra=`)
take at most two message arguments; `%(thread)d` is the managed thread id. Handler, Formatter and Filter cannot be
subclassed; `logging.config`, `logging.handlers` and `LoggerAdapter` are not provided.
