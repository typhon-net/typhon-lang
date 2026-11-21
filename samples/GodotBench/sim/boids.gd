extends Node2D
## Boids: every object steers by its neighbours (separation, alignment, cohesion), found through a uniform grid,
## bounces off the walls of the area, and its node (a Sprite2D) is moved to the new position every frame.
## The simulation is plain GDScript game code: Vector2 math, packed arrays, loops, and node properties.
## This one file runs twice in the benchmark: as a GDScript script, and compiled by Typhon into the C# assembly.

const MAX_SPEED := 180.0
const MIN_SPEED := 60.0
const NEIGHBOR_RADIUS := 32.0
const SEPARATION_RADIUS := 12.0
const CELL := 32.0
const SEPARATION_WEIGHT := 90.0
const ALIGNMENT_WEIGHT := 0.8
const COHESION_WEIGHT := 0.6

var bounds := Vector2(1920.0, 1080.0)
var count := 0
var positions := PackedVector2Array()
var velocities := PackedVector2Array()
var sprites: Array[Sprite2D] = []
var grid_cols := 1
var grid_rows := 1
var cell_head := PackedInt32Array()
var cell_next := PackedInt32Array()

## Microseconds the last step spent in the simulation and in moving the nodes (measured inside step).
var last_sim_usec := 0
var last_sync_usec := 0


func setup(n: int, area: Vector2, texture: Texture2D) -> void:
	bounds = area
	count = n
	grid_cols = int(ceil(area.x / CELL))
	grid_rows = int(ceil(area.y / CELL))
	cell_head.resize(grid_cols * grid_rows)
	cell_next.resize(n)
	for i in range(n):
		var p := Vector2(randf() * area.x, randf() * area.y)
		var angle := randf() * TAU
		positions.append(p)
		velocities.append(Vector2(cos(angle), sin(angle)) * randf_range(MIN_SPEED, MAX_SPEED))
		var sprite := Sprite2D.new()
		sprite.texture = texture
		sprite.position = p
		add_child(sprite)
		sprites.append(sprite)


func _rebuild_grid() -> void:
	cell_head.fill(-1)
	for i in range(count):
		var p := positions[i]
		var cx := clampi(int(p.x / CELL), 0, grid_cols - 1)
		var cy := clampi(int(p.y / CELL), 0, grid_rows - 1)
		var c := cy * grid_cols + cx
		cell_next[i] = cell_head[c]
		cell_head[c] = i


func step(delta: float) -> void:
	var t0 := Time.get_ticks_usec()
	_rebuild_grid()
	var r2 := NEIGHBOR_RADIUS * NEIGHBOR_RADIUS
	var s2 := SEPARATION_RADIUS * SEPARATION_RADIUS
	for i in range(count):
		var p := positions[i]
		var v := velocities[i]
		var separation := Vector2.ZERO
		var alignment := Vector2.ZERO
		var center := Vector2.ZERO
		var neighbors := 0
		var cx := int(p.x / CELL)
		var cy := int(p.y / CELL)
		for gy in range(cy - 1, cy + 2):
			if gy < 0 or gy >= grid_rows:
				continue
			for gx in range(cx - 1, cx + 2):
				if gx < 0 or gx >= grid_cols:
					continue
				var j := cell_head[gy * grid_cols + gx]
				while j != -1:
					if j != i:
						var q := positions[j]
						var d := q - p
						var dist2 := d.length_squared()
						if dist2 < r2:
							neighbors += 1
							alignment += velocities[j]
							center += q
							if dist2 < s2 and dist2 > 0.0:
								separation -= d / dist2
					j = cell_next[j]
		if neighbors > 0:
			alignment = alignment / neighbors - v
			center = center / neighbors - p
			v += (separation * SEPARATION_WEIGHT + alignment * ALIGNMENT_WEIGHT + center * COHESION_WEIGHT) * delta
		var speed := v.length()
		if speed > MAX_SPEED:
			v *= MAX_SPEED / speed
		elif speed < MIN_SPEED and speed > 0.0:
			v *= MIN_SPEED / speed
		p += v * delta
		if p.x < 0.0:
			p.x = -p.x
			v.x = -v.x
		elif p.x > bounds.x:
			p.x = 2.0 * bounds.x - p.x
			v.x = -v.x
		if p.y < 0.0:
			p.y = -p.y
			v.y = -v.y
		elif p.y > bounds.y:
			p.y = 2.0 * bounds.y - p.y
			v.y = -v.y
		positions[i] = p
		velocities[i] = v
	var t1 := Time.get_ticks_usec()
	for i in range(count):
		var sprite := sprites[i]
		sprite.position = positions[i]
		sprite.rotation = velocities[i].angle()
	var t2 := Time.get_ticks_usec()
	last_sim_usec = t1 - t0
	last_sync_usec = t2 - t1


## A number that depends on every position, to check that both variants computed the same simulation.
func checksum() -> float:
	var sum := 0.0
	for i in range(count):
		sum += positions[i].x + positions[i].y * 0.5
	return sum
