# GodotBench: one GDScript file, run by Godot and compiled by Typhon

A Godot 4 project with a scene of thousands of moving objects. Their behaviour is
[`sim/boids.gd`](sim/boids.gd), an ordinary GDScript script (boids: each object steers by its neighbours, found through a
uniform grid, bounces off the walls, and its `Sprite2D` is moved every frame). The project uses that **same file** twice:

* **gdscript**: as a GDScript script, run by Godot's GDScript VM;
* **typhon**: compiled by Typhon's GDScript front end to C# and built into the project's C# assembly, where it is a
  normal Godot C# script class (`res://.godot/typhon/Boids.cs`, class `GodotBench.sim.Boids : Node2D`).

[`bench.gd`](bench.gd) (a GDScript driver, not compiled by Typhon) runs both and prints how long a frame's update takes.

## Run it

You need Godot 4.7 with .NET support and a .NET SDK that can build `net10.0` (the .NET 10 SDK). The project references
the Typhon compiler in this repository, which the build compiles from source.

```sh
# build the C# assembly (Godot does the same when you open the project in the editor and press Build)
godot --headless --path samples/GodotBench --build-solutions --quit
#   or: dotnet build samples/GodotBench/GodotBench.csproj

# the benchmark, headless: both variants for each object count
godot --headless --path samples/GodotBench -- --counts=1000,4000,16000 --frames=300 --warmup=100 --repeat=3

# watch it: the same scene in a window (rendering on, numbers include nothing of it)
godot --path samples/GodotBench -- --counts=4000 --frames=600
```

Options after `--`: `--counts=` (object counts), `--variants=gdscript,typhon`, `--frames=` (measured frames per run),
`--warmup=` (frames before measuring), `--repeat=` (repetitions; the summary shows the best of them).

## How the compiled script gets into Godot

`GodotBench.csproj` is a normal `Godot.NET.Sdk` project with three Typhon settings:

```xml
<TyphonEmitToDisk>true</TyphonEmitToDisk>
<TyphonEmitDir>$(MSBuildProjectDirectory)/.godot/typhon/</TyphonEmitDir>
<TyphonCompile Include="sim/boids.gd" />
```

Godot's C# scripts need glue code that Godot's own source generators write (the script path, the method and property
lists through which the engine calls `_process`, `setup`, `step`, ...). Source generators cannot see each other's
output, so Typhon's source generator is not used here: with `TyphonEmitToDisk`, a build step before the C# compiler runs
`typhon-emit` (`src/Typhon.Emit`), which writes the C# for the listed `.gd` files (and Typhon's runtime) into
`.godot/typhon/`, and the C# compiler compiles them with the project's own files. Godot's generators then treat
`Boids.cs` like a hand-written script. The generated code keeps `#line` directives, so compile errors point at
`boids.gd`. `.gd` files are opt-in (`TyphonCompile` items): the project's other GDScript scripts (here `bench.gd`) stay
GDScript.

The project targets `net10.0`: Typhon's runtime uses .NET 9 APIs, and Godot 4.7 runs a project on any installed .NET
8 or newer. Godot.NET.Sdk compiles the `Debug` configuration (the one `godot --path` and the editor load) without
optimizations; the project sets `<Optimize>true</Optimize>` so the C# is measured as optimized code.

## What is measured

For each variant and object count, `bench.gd` creates the simulation node, seeds Godot's random generator identically
(`seed(12345)`; both variants draw from Godot's generator, so they start from the same state), calls `setup`, and then,
every frame, `sim.step(1/60)` with a fixed delta. After `--warmup` frames it measures `--frames` frames:

* **step**: the time of the `sim.step()` call, as `bench.gd` measures it with `Time.get_ticks_usec()`. It is everything
  the script does in a frame: the simulation (grid rebuild, neighbour search, steering, bouncing) and moving the nodes
  (`sprite.position = ...; sprite.rotation = ...` for every object);
* **simulation** and **nodes**: the two parts of `step`, as the script itself measures them;
* **frame**: wall time from one `_process` to the next (the whole engine frame; in headless mode there is no rendering);
* **checksum**: a sum over all positions at the end. Both variants print the same number, digit for digit: the compiled
  script computes the same simulation.

The call from `bench.gd` into the script is a GDScript call for one variant and a GDScript-to-C# call for the other; it
happens once per frame and is negligible against the step times.

## Results

Measured on an Intel Core i7-10700K (8 cores, 16 threads, 3.8 GHz), 64 GB RAM, Windows 11 Pro, Godot
4.7.2-stable (official .NET build, editor binary, `--headless`), .NET 10.0.15, the assembly built by Godot
(`--build-solutions`, .NET SDK 10.0.404). `--frames=300 --warmup=100 --repeat=3`; for each count and variant the run
with the lowest `step` time is shown, in milliseconds per frame:

| objects | GDScript step | Typhon step | speedup | GDScript simulation | Typhon simulation | speedup | GDScript nodes | Typhon nodes |
|--------:|--------------:|------------:|--------:|--------------------:|------------------:|--------:|---------------:|-------------:|
|    1000 |          3.47 |        1.62 |    2.1x |                2.34 |              0.46 |    5.1x |           1.13 |         1.15 |
|    4000 |         18.78 |        8.15 |    2.3x |               14.57 |              3.21 |    4.5x |           4.20 |         4.93 |
|   16000 |        221.26 |       52.91 |    4.2x |              201.60 |             35.02 |    5.8x |          19.66 |        17.88 |

The checksums of the two variants were identical in every run. Read it as:

* the **simulation** (the script's own logic: loops, arrays, Vector2 math) runs 4.5 to 6 times faster compiled by
  Typhon;
* **moving the nodes** costs the same in both: it is the engine's work, called through GDScript or through C#;
* so the whole **step** gains 2x where the engine part is a large share (1000 objects: 1.6 ms of which 1.2 ms are node
  updates) and 4x where the script's logic dominates (16000 objects, where the neighbour search grows).

The numbers move between runs by 10 to 20% on this machine (other work was running); the speedups are stable.

## Caveats

* GDScript runs in the editor binary (`Godot_v4.7.2-stable_mono_win64.exe`), which is what `godot --path` uses; it is
  a debug-enabled build. An exported game runs GDScript in the release template, without the debugger's bookkeeping,
  which makes GDScript somewhat faster. Export templates were not installed on the measuring machine, so that was not
  measured.
* The C# side runs on .NET 10 with tiered compilation; the warm-up frames let the JIT reach its optimized tier before
  measuring.
* Moving the nodes is engine work (each `position`/`rotation` assignment is a call into the engine that updates
  transforms and notifications). Both variants pay it; it is why the speedup of `step` is smaller than that of the
  simulation part, and smaller still at low object counts.
* The simulation is written as typical, statically typed GDScript (`:=`, typed arrays, packed arrays). Untyped GDScript
  compiles too, but Typhon then has to fall back to late-bound (`dynamic`) code in places, which is much slower.
* Timings vary between runs on a desktop machine; the summary shows the best of `--repeat` runs.
