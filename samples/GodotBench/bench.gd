extends Node2D
## The benchmark driver (a GDScript script itself; it is not compiled by Typhon).
## For every object count it runs the same simulation, sim/boids.gd, in two ways:
##   gdscript  the script as Godot's GDScript VM runs it
##   typhon    the same file compiled by Typhon into the project's C# assembly (res://.godot/typhon/Boids.cs)
## Each run seeds Godot's random generator the same way, steps the simulation with a fixed delta for some warm-up frames,
## then measures the following frames. Per frame it records the time of sim.step() (simulation + moving the nodes),
## the two parts as the script measured them, and the time of the whole frame (from one _process to the next).
##
## godot --headless --path . -- [--counts=500,2000,8000] [--frames=300] [--warmup=100] [--variants=gdscript,typhon] [--repeat=1]

const SCRIPTS := {
	"gdscript": "res://sim/boids.gd",
	"typhon": "res://.godot/typhon/Boids.cs",
}
const DT := 1.0 / 60.0
const AREA := Vector2(1920.0, 1080.0)

var counts: Array[int] = [500, 2000, 8000]
var variants: Array[String] = ["gdscript", "typhon"]
var frames := 300
var warmup := 100
var repeat := 1

var runs: Array = []
var run_index := -1
var sim: Node2D = null
var frame := 0
var step_total := 0
var sim_total := 0
var sync_total := 0
var frame_total := 0
var last_frame_usec := 0
var texture: Texture2D
## [variant, count] -> list of average step milliseconds over the repetitions
var results := {}


func _ready() -> void:
	for arg in OS.get_cmdline_user_args():
		var kv := arg.trim_prefix("--").split("=")
		if kv.size() != 2:
			continue
		match kv[0]:
			"counts":
				counts.clear()
				for c in kv[1].split(","):
					counts.append(int(c))
			"variants":
				variants.clear()
				for v in kv[1].split(","):
					variants.append(v)
			"frames":
				frames = int(kv[1])
			"warmup":
				warmup = int(kv[1])
			"repeat":
				repeat = int(kv[1])
	# headless Godot sleeps between frames (low processor mode): the whole-frame time should be the engine's work
	OS.low_processor_usage_mode = false
	OS.low_processor_usage_mode_sleep_usec = 0
	var image := Image.create(6, 3, false, Image.FORMAT_RGBA8)
	image.fill(Color(0.9, 0.95, 1.0))
	texture = ImageTexture.create_from_image(image)
	if DisplayServer.get_name() != "headless":
		scale = Vector2(1280.0, 720.0) / AREA
	for r in range(repeat):
		for c in counts:
			for v in variants:
				runs.append([v, c])
	print("Godot %s, %s, %d CPU threads" % [Engine.get_version_info()["string"], OS.get_processor_name(), OS.get_processor_count()])
	print("frames=%d warmup=%d delta=%.4f area=%dx%d" % [frames, warmup, DT, int(AREA.x), int(AREA.y)])
	_next_run()


func _next_run() -> void:
	if sim != null:
		remove_child(sim)
		sim.free()
		sim = null
	run_index += 1
	if run_index >= runs.size():
		_summary()
		get_tree().quit()
		return
	var variant: String = runs[run_index][0]
	var count: int = runs[run_index][1]
	var script: Script = load(SCRIPTS[variant])
	sim = script.new()
	add_child(sim)
	seed(12345)
	sim.setup(count, AREA, texture)
	frame = 0
	step_total = 0
	sim_total = 0
	sync_total = 0
	frame_total = 0
	last_frame_usec = Time.get_ticks_usec()


func _process(_delta: float) -> void:
	if sim == null:
		return
	var now := Time.get_ticks_usec()
	var t0 := Time.get_ticks_usec()
	sim.step(DT)
	var t1 := Time.get_ticks_usec()
	frame += 1
	if frame > warmup:
		step_total += t1 - t0
		sim_total += sim.last_sim_usec
		sync_total += sim.last_sync_usec
		frame_total += now - last_frame_usec
	last_frame_usec = now
	if frame == warmup + frames:
		var variant: String = runs[run_index][0]
		var count: int = runs[run_index][1]
		var step_ms := step_total / 1000.0 / frames
		print("%-9s objects=%-6d step=%8.3f ms  (simulation %8.3f ms, nodes %7.3f ms)  frame=%8.3f ms  checksum=%.3f" % [
			variant, count, step_ms, sim_total / 1000.0 / frames, sync_total / 1000.0 / frames,
			frame_total / 1000.0 / frames, sim.checksum()])
		var key := "%s/%d" % [variant, count]
		if not results.has(key):
			results[key] = []
		results[key].append(step_ms)
		_next_run()


func _summary() -> void:
	print("")
	print("average step() time per frame (best of %d), ms:" % repeat)
	print("%8s  %10s  %10s  %8s" % ["objects", "gdscript", "typhon", "speedup"])
	for c in counts:
		var g := _best("gdscript/%d" % c)
		var t := _best("typhon/%d" % c)
		var speedup := "-" if g < 0 or t <= 0 else "%.1fx" % (g / t)
		print("%8d  %10s  %10s  %8s" % [c, "-" if g < 0 else "%.3f" % g, "-" if t < 0 else "%.3f" % t, speedup])


func _best(key: String) -> float:
	if not results.has(key):
		return -1.0
	var best: float = results[key][0]
	for v in results[key]:
		best = min(best, v)
	return best
