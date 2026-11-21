extends SceneTree
# More GDScript forms: accessor methods, static members across classes, typed loops, negative indices, nested
# containers, type tests, captured locals in lambdas, packed arrays.

static var created := 0

var _hp := 10
var hp: int:
	set = _set_hp, get = _get_hp

func _set_hp(value: int) -> void:
	_hp = max(value, 0)

func _get_hp() -> int:
	return _hp * 10

class Util:
	const FACTOR := 3
	static func twice(v: int) -> int:
		return v * 2
	static func scaled(v: int) -> int:
		return twice(v) * FACTOR

class Animal:
	var name := "animal"
	func speak() -> String:
		return name + " makes a sound"
	func describe() -> String:
		return "[" + speak() + "]"

class Dog extends Animal:
	func _init() -> void:
		name = "dog"
	func speak() -> String:
		return super.speak() + ": woof"

func kind(v) -> String:
	if v is int:
		return "int"
	elif v is float:
		return "float"
	elif v is String:
		return "String"
	elif v is Array:
		return "Array"
	elif v is Dictionary:
		return "Dictionary"
	return "other"

func classify(word: String) -> String:
	match word:
		"cat", "dog":
			return "pet"
		"lion":
			return "wild"
		_:
			return "unknown"

func untyped_sum(a, b):
	return a + b

func _init() -> void:
	created += 1
	hp = 7
	print(hp, " ", _hp)
	hp = -3
	print(hp, " ", _hp)
	print(Util.twice(4), " ", Util.scaled(5), " ", Util.FACTOR, " ", created)
	var d := Dog.new()
	print(d.speak(), " | ", d.describe(), " | ", Animal.new().describe())
	for i: int in range(3):
		print("typed ", i * 2)
	var a := [10, 20, 30, 40]
	print(a[-1], " ", a[-2], " ", a[0] + a[-1])
	var grid := [[0, 0, 0], [0, 0, 0]]
	grid[1][2] = 5
	grid[0][0] += 2
	print(grid)
	print(kind(1), " ", kind(1.5), " ", kind("s"), " ", kind([1]), " ", kind({}), " ", kind(null))
	print(classify("dog"), " ", classify("lion"), " ", classify("cow"))
	var k := 3
	var times := func(x): return x * k
	print(times.call(2), " ", times.call(5))
	var n := 0
	while true:
		n += 1
		if n >= 4:
			break
	print("n ", n)
	var ints := PackedInt32Array()
	ints.append(4)
	ints.append(9)
	ints.push_back(16)
	print(ints, " ", ints.size(), " ", ints[1], " ", ints.has(9))
	var floats := PackedFloat64Array([1.5, 2.0])
	floats.append(0.25)
	print(floats, " ", len(floats), " ", len("abcd"))
	var d2 := {"a": [1, 2], "b": {"c": 3}}
	for key in d2.keys():
		print(key, " -> ", d2[key])
	for value in d2.values():
		print("value ", value)
	print(untyped_sum(2, 3), " ", untyped_sum("a", "b"), " ", untyped_sum(1.5, 2))
	print("line one\nline two")
	assert(n == 4)
	print(not 3 in [1, 2], " ", 3 not in [1, 2])
	var counter := {}
	counter["x"] = 1
	counter["x"] += 4
	print(counter, " %s" % [counter])
	var lst := [3, 1, 2]
	lst.clear()
	print(lst, " ", lst.size())
	quit()
