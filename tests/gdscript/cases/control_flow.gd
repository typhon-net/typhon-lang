extends SceneTree
# Loops, branches and GDScript's match.

const LIMIT := 4

func describe(v) -> String:
	match v:
		0:
			return "zero"
		1, 2, 3:
			return "small"
		"hello":
			return "greeting"
		LIMIT:
			return "the limit"
		[1, 2]:
			return "pair 1 2"
		[var first, ..]:
			return "list starting with " + str(first)
		{"name": var n}:
			return "named " + str(n)
		_:
			return "something else"

func grade(score: int) -> String:
	match score:
		var s when s >= 90:
			return "A"
		var s when s >= 75:
			return "B"
		_:
			return "C"

func _init() -> void:
	var total := 0
	for i in range(5):
		total += i
	print("range(5) sum ", total)
	for i in range(2, 10, 3):
		print("step ", i)
	for i in range(5, 0, -2):
		print("down ", i)
	for i in 3:
		print("int loop ", i)
	var n: int = 2
	for i in n:
		print("typed int loop ", i)
	for s in ["a", "b", "c"]:
		print("item ", s)
	for ch in "hey":
		print("char ", ch)
	var d := {"x": 1, "y": 2}
	for k in d:
		print("key ", k, " = ", d[k])
	var k2 := 0
	while k2 < 10:
		k2 += 1
		if k2 % 2 == 0:
			continue
		if k2 > 7:
			break
		print("odd ", k2)
	for i in range(3):
		if i == 0:
			print(i, " is zero")
		elif i == 1:
			print(i, " is one")
		else:
			print(i, " is more")
	print(describe(0), ", ", describe(2), ", ", describe("hello"), ", ", describe(4), ", ", describe(9))
	print(describe([1, 2]), ", ", describe([7, 8, 9]), ", ", describe({"name": "Ada"}), ", ", describe({"name": "Ada", "age": 3}))
	print(grade(95), grade(80), grade(10))
	var count := 0
	for i in range(4):
		for j in range(4):
			if j > i:
				break
			count += 1
	print("nested ", count)
	if count > 5: print("one-line if")
	var nested := [[1, 2], [3, 4]]
	for row in nested:
		for v in row:
			total += v
	print("total ", total)
	quit()
