extends SceneTree
# String methods and % formatting (String::sprintf).

func _init() -> void:
	var s := "Hello, World"
	print(s.length(), " ", s.to_upper(), " ", s.to_lower(), " ", s.substr(7), " ", s.substr(0, 5))
	print(s.begins_with("Hell"), " ", s.ends_with("x"), " ", s.find("o"), " ", s.find("zz"), " ", s.contains("World"), " ", "lo" in s)
	print(s.replace("World", "Godot"), " ", s.split(", "), " ", "  trim me  ".strip_edges(), "|")
	print("a,b,,c".split(","), " ", ", ".join(["x", "y", "z"]), " ", "ab".repeat(3))
	print("42".to_int(), " ", "3.5".to_float(), " ", "12abc".to_int(), " ", "abc".is_valid_int(), " ", "17".is_valid_int())
	print("7".pad_zeros(3), " ", "5".lpad(4), "|", "5".rpad(4), "|", "x".lpad(3, "*"))
	print("hello_world".capitalize(), " ", "abcdef".left(2), " ", "abcdef".right(2), " ", s[0], s[7])
	print("%d|%5d|%-5d|%05d|%+d" % [42, 42, 42, 42, 42])
	print("%x|%X|%o|%c|%%" % [255, 255, 8, 65])
	print("%.2f|%8.3f|%-8.1f|%f" % [3.14159, 2.71828, 1.25, 0.5])
	print("%s and %s" % ["cats", "dogs"], " ", "%s" % 1.0, " ", "%s" % true, " ", "%s" % [[1, 2]])
	print("%d%%" % 50, " ", "value: %s" % null, " ", "%5s|%-5s|" % ["ab", "cd"])
	var name := "Ada"
	var age := 36
	print("%s is %d years old" % [name, age])
	print("multi" + "ple" + " " + str(3) + " parts")
	var esc := "tab\there \"quoted\" back\\slash"
	print(esc)
	print("""triple "quoted" string""")
	print('single quoted')
	var parts: Array[String] = []
	for w in "one two three".split(" "):
		parts.append(w.to_upper())
	print(parts, " ", " ".join(parts))
	quit()
