extends SceneTree
# Arrays and dictionaries: GDScript's methods, typed arrays, printing.

func _init() -> void:
	var a: Array[int] = [5, 3, 8]
	a.append(1)
	a.push_back(9)
	a.push_front(0)
	print(a, " size ", a.size(), " empty ", a.is_empty())
	a.sort()
	print("sorted ", a, " front ", a.front(), " back ", a.back())
	print("pop_back ", a.pop_back(), " pop_front ", a.pop_front(), " -> ", a)
	a.insert(1, 42)
	a.remove_at(0)
	a.erase(8)
	print(a, " find 5: ", a.find(5), " find 77: ", a.find(77), " has 3: ", a.has(3), " 3 in a: ", 3 in a)
	a.reverse()
	print("reversed ", a, " count 42: ", a.count(42))
	var b := a.duplicate()
	b.append(100)
	print(a, " ", b, " ", a.slice(1, 3))
	print("max ", b.max(), " min ", b.min())
	var evens := b.filter(func(v): return v % 2 == 0)
	var doubled := b.map(func(v): return v * 2)
	print(evens, " ", doubled)
	print(b.any(func(v): return v > 50), " ", b.all(func(v): return v > 0))
	print(b.reduce(func(acc, v): return acc + v, 0))
	var words: Array[String] = ["pear", "fig", "banana"]
	words.sort_custom(func(x, y): return x.length() < y.length())
	print(words)
	var empty: Array = []
	print(empty, " ", empty.size(), " ", empty.is_empty(), " ", empty.pop_back())
	var mixed := [1, "two", 3.0, true, null, [4, 5], {"six": 6}]
	print(mixed, " ", mixed.size())
	var grid := []
	for y in range(3):
		var row := []
		for x in range(3):
			row.append(x * y)
		grid.append(row)
	print(grid)
	var filled: Array[float] = []
	filled.resize(3)
	filled.fill(0.5)
	print(filled)
	var d := {"apple": 3, "banana": 5}
	d["cherry"] = 7
	print(d, " size ", d.size())
	print(d.has("apple"), " ", "kiwi" in d, " ", d.get("banana"), " ", d.get("kiwi", -1))
	print(d.keys(), " ", d.values())
	d.erase("apple")
	print(d)
	d.merge({"banana": 50, "date": 9})
	print(d)
	var counts := {}
	for w in ["a", "b", "a", "c", "a"]:
		counts[w] = counts.get(w, 0) + 1
	print(counts)
	var nested := {"list": [1, 2], "dict": {"k": "v"}, 3: 4.5}
	print(nested, " ", nested["list"][1], " ", nested["dict"]["k"])
	print({}, " ", [], " ", [[]], " ", [{}])
	var total := 0
	for k in d:
		total += d[k]
	print("total ", total)
	var arr2 := [3, 1, 2]
	arr2 += [9]
	print(arr2, " ", [1, 2] + [3], " ", [1, 2] == [1, 2], " ", [1] != [2])
	quit()
