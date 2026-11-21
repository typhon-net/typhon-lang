extends SceneTree
# Members, implicit self, constants, static members, inner classes, inheritance, properties, enums and signals.

const GREETING := "hello"
const SIZES = [1, 2, 3]
enum Tint { RED, GREEN = 5, BLUE }
enum { NORTH, EAST, SOUTH, WEST }

static var instances := 0
var counter: int = 10
var label := "start"
var history: Array[String] = []
var health: int = 100:
	set(value):
		health = clamp(value, 0, 100)
		history.append("health=" + str(health))
	get:
		return health

signal changed(what: String)

class Shape:
	var name: String
	func _init(n: String) -> void:
		name = n
	func area() -> float:
		return 0.0
	func describe() -> String:
		return "%s with area %.2f" % [name, area()]

class Rect extends Shape:
	var w: float
	var h: float
	func _init(width: float, height: float) -> void:
		super("rect")
		w = width
		h = height
	func area() -> float:
		return w * h

class Circle extends Shape:
	var r := 1.0
	func _init(radius: float) -> void:
		super("circle")
		r = radius
	func area() -> float:
		return 3.0 * r * r
	func describe() -> String:
		return "round " + super()

class Point:
	var x := 0
	var y := 0
	func _init(px: int = 0, py: int = 0) -> void:
		x = px
		y = py
	func _to_string() -> String:
		return "(%d, %d)" % [x, y]
	static func origin() -> Point:
		return Point.new()

static func twice(v: int) -> int:
	return v * 2

func bump(by: int = 1) -> int:
	counter += by
	return counter

func on_changed(what: String) -> void:
	print("signal: ", what)

func _init() -> void:
	instances += 1
	print(GREETING, " ", SIZES, " ", instances)
	print(Tint.RED, " ", Tint.GREEN, " ", Tint.BLUE, " ", NORTH, " ", WEST)
	var c: Tint = Tint.BLUE
	print("enum var ", c, " ", c == Tint.BLUE)
	print(bump(), " ", bump(5), " ", counter, " ", self.counter)
	label = label + "!"
	print(label, " ", twice(21))
	var shapes: Array[Shape] = [Rect.new(2.0, 3.0), Circle.new(2.0), Shape.new("blob")]
	for s in shapes:
		print(s.describe())
	var total := 0.0
	for s in shapes:
		total += s.area()
	print("total area ", total)
	print(shapes[0] is Rect, " ", shapes[1] is Rect, " ", shapes[1] is Shape)
	var p := Point.new(3, 4)
	print(p, " ", Point.origin(), " ", str(p))
	health = 150
	health -= 30
	health = -5
	print(health, " ", history)
	changed.connect(on_changed)
	changed.emit("first")
	emit_signal("changed", "second")
	quit()
