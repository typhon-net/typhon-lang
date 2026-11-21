# GDScript

Typhon compiles GDScript (Godot 4) as well as Python: a `.gd` file goes through a GDScript lexer and parser
(`src/Typhon.Compiler/Syntax/GdScriptLexer.cs`, `GdScriptParser.cs`) into the same AST, and from there through the
same inference, C# emission and Roslyn as a `.ty` file. Where GDScript's semantics differ from Python's, the
emitter follows GDScript (`Emit/ModuleEmitter/ModuleEmitter.GdScript.cs`); Python modules never see those rules.

```sh
typhon run tests/gdscript/cases/basics.gd     # a SceneTree script that prints, like `godot --headless -s basics.gd`
typhon emit tests/gdscript/cases/classes.gd   # the C#
```

Inside a Godot C# project, list the `.gd` files to compile and let Typhon write the C# to disk, so Godot's source
generators see the script classes (see [samples/GodotBench](../samples/GodotBench/README.md)):

```xml
<TyphonEmitToDisk>true</TyphonEmitToDisk>
<TyphonCompile Include="sim/boids.gd" />
```

`.gd` files are compiled only when listed (by `typhon run`/`emit`/`build` for one file, or as `TyphonCompile` items):
a Godot project's other GDScript scripts stay GDScript. The extension alone selects the front end.

## How a .gd file maps to C#

* The file is a class: named by `class_name`, else after the file (`boids.gd` → `Boids`), deriving from what it
  `extends`. It is a top-level `public partial class` in the project's namespace (Godot's script classes must be top
  level and partial); inner classes and named enums live in the module's static class (`boids_gd`), which the script
  class sees through `using static`.
* Member `var`s are properties, initialized in the constructor before `_init`'s body (GDScript's order); a typed `var`
  without a value starts at its type's default (`0`, `""`, `[]`, `Vector2()`), as in GDScript. `@onready` vars are
  assigned at the start of `_ready`. `const`s with literal values are C# `const`s, other `const`s and `static var`s
  static fields. Enum values are `int` constants. `set(value):`/`get:` accessors make a property over a backing field.
* Every member is `public` (GDScript has no access control). A method that overrides a virtual of the native base by
  its GDScript name gets Godot's C# name and signature: `func _process(delta)` → `public override void
  _Process(double delta)`, `_ready` → `_Ready`, `_input(event)` → `_Input(InputEvent)`.
* Godot's API is called by its GDScript names: members of Godot's types are found by the snake_case name and, failing
  that, by its PascalCase spelling (`position` → `Position`, `get_viewport_rect().size` → `GetViewportRect().Size`,
  `Vector2.ZERO` → `Vector2.Zero`, `Color.RED` → `Colors.Red`). Only types in the `Godot` namespace are looked up this
  way, and only from `.gd` modules.
* GDScript floats are 64-bit (C# `double`), Godot's math types use 32-bit floats: where C# needs it, a float is
  converted (`Vector2(x, y)`, `v * delta`, `node.rotation = angle`, `p.x = 2.0 * w - p.x`). A field of a value type
  reached through a property or an element (`position.x += 1`, `points[i].y = 0`) is changed on a copy that is stored
  back, as GDScript does. A Godot method that returns an unsigned int (`Time.get_ticks_usec()`) gives an int.
* With Godot referenced, `print`/`prints`/`printerr`/`push_error`, the random functions and `load`/`preload` call
  Godot's (`GD.Print`, `GD.Randf`, `GD.RandRange`, `GD.Load`), so a compiled script shares the engine's output and random
  generator. Without Godot (`typhon run`), a script's native base is dropped, `quit()` does nothing, values print as Godot
  prints them, and the random functions implement Godot's PCG32 generator.

## Semantics that differ from Python's

`int / int` truncates (`-7 / 2 == -3`), `%` is the remainder of that division (`-7 % 3 == -1`), `**` is
left-associative (`2 ** 3 ** 2 == 64`), comparisons do not chain, `is` tests a type (`x is Node`), ints wrap around on
overflow, `String % [args]` formats like Godot's `String::sprintf`, `print` concatenates its arguments, and values print
as Godot prints them (`true`, `<null>`, `1.0`, `0.33333333333333`, `[1, "a"]`, `{ "k": 1 }`, `(1.0, 2.0)`).

## Supported

`extends` (a class name), `class_name`, `@tool`/`@icon` (ignored), `@export`/`@export_*` (recorded; no `[Export]`
attribute yet), `@onready`, `var` (typed, `:=`, untyped, `static`), `const`, `enum` (named and anonymous), `signal`
(`connect`, `disconnect`, `emit`, `emit_signal`, between scripts compiled by Typhon), `func` (typed and untyped
parameters, defaults, `:=` defaults, return types, `static func`), `_init` with parameters, `super()` / `super.m()`,
`_to_string`, inner classes extending each other or a native class, property accessors (`set(v):`/`get:` blocks and
`set = f, get = g`), `if`/`elif`/`else`, `while`, `for` over `range(...)`, an int (`for i in 10`, `for i in count`),
arrays, dictionaries and strings (typed loop variables too), `break`/`continue`/`pass`/`return`, `match` (literals,
constants, several patterns per branch, `var` bindings, `_`, arrays with `..`, dictionaries, `when` guards), `assert`,
`and`/`or`/`not`/`&&`/`||`/`!`, `in`/`not in`, `is`/`is not`, `as`, conditional expressions, one-line lambdas
(`func(x): return x * 2`) and `callable.call(...)`, array and dictionary literals (also `{ key = value }`), `$Node/Path`
and `%Unique` (`get_node`), `X.new(...)`, `PI`/`TAU`/`INF`/`NAN`, the print functions, `str`, `len`, `range`,
`int`/`float`/`bool`, the math functions (`abs`, `sign`, `min`, `max`, `clamp`, `lerp`, `inverse_lerp`, `remap`, `sqrt`,
`pow`, `exp`, `log`, trigonometry, `floor`/`ceil`/`round` and their `f`/`i` forms, `fmod`, `fposmod`, `posmod`,
`snapped`, `wrapi`/`wrapf`, `deg_to_rad`, `rad_to_deg`, `is_equal_approx`, `is_zero_approx`, `move_toward`,
`smoothstep`, `ease`, ...), `randf`/`randi`/`randf_range`/`randi_range`/`randfn`/`seed`/`randomize`, `typeof`,
`is_instance_valid`, and the methods of Array (`size`, `is_empty`, `append`, `push_back`, `push_front`, `pop_back`,
`pop_front`, `pop_at`, `insert`, `remove_at`, `erase`, `find`, `has`, `count`, `sort`, `sort_custom`, `reverse`,
`duplicate`, `slice`, `front`, `back`, `max`, `min`, `resize`, `fill`, `append_array`, `filter`, `map`, `reduce`, `any`,
`all`), Dictionary (`size`, `has`, `get`, `erase`, `keys`, `values`, `merge`, `duplicate`) and String (`length`,
`to_upper`, `to_lower`, `substr`, `find`, `begins_with`, `ends_with`, `contains`, `replace`, `split`, `join`,
`strip_edges`, `to_int`, `to_float`, `is_valid_int`, `is_valid_float`, `pad_zeros`, `lpad`, `rpad`, `repeat`,
`capitalize`, `left`, `right`). Packed arrays (`PackedVector2Array`, ...) are lists of their element type.

## Not supported yet

`await` and coroutines; Godot signals as the engine knows them (`[Signal]` delegates, connecting to engine signals such
as `timer.timeout`, editor connections); multi-line lambdas; `extends "res://path.gd"` and `preload("x.gd")` as a class;
references between `.gd` files by `class_name`; `@export` as Godot's `[Export]` (inspector); `@rpc`; packed arrays'
copy-on-assignment (they are lists, shared like Arrays); ambiguous method names (`length`, `duplicate`, `keys`, ...) on
values whose type the parser cannot see (untyped variables) are left to Godot's API; untyped code compiles, but where
Typhon's inference cannot type a value it is late-bound (`dynamic`), which is slow.

## Tests

Godot is the oracle, as CPython is for Python: `tests/gdscript/cases/*.gd` are SceneTree scripts that print, and
`tests/gdscript/expected/*.out` holds what Godot printed for them (`GODOT=... python tests/gdscript/update_expected.py`).
`GdScriptTests` in `tests/Typhon.Conformance.Tests` runs each case with `typhon run` and compares; it does not need
Godot.
