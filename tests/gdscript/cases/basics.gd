extends SceneTree
# Values, operators and printing: GDScript's rules where they differ from Python's
# (int / int truncates, % keeps the dividend's sign, ** is left-associative, no chained comparisons).

func _init() -> void:
	print(7 / 2, " ", -7 / 2, " ", 7 / -2, " ", 7 % 3, " ", -7 % 3, " ", 7 % -3)
	print(7.0 / 2, " ", 7 / 2.0, " ", 1.0 / 4, " ", 2 ** 10, " ", 2 ** 3 ** 2, " ", 2.0 ** 0.5 * 2.0 ** 0.5)
	var a := 17
	var b := 5
	print(a / b, " ", a % b, " ", a * b, " ", a - b * 2, " ", (a + b) * 2, " ", -a / b, " ", -a % b)
	var x := 2.5
	print(x * 2, " ", x / 2, " ", x + 1, " ", x - 0.5, " ", -x)
	print(1.0, " ", 0.5, " ", 0.1 + 0.2, " ", 1.0 / 3.0, " ", 2.0 / 3.0, " ", 100.0, " ", 1e-5, " ", 1e15)
	print(123456.789, " ", 0.000123, " ", 3.0e-10, " ", 1.0 / 0.0, " ", -1.0 / 0.0)
	print(true, " ", false, " ", null, " ", true and false, " ", true or false, " ", not true)
	print(true && false, " ", true || false, " ", !false, " ", 1 < 2 and 2 < 3)
	print(1 == 1.0, " ", 2 != 3, " ", "a" < "b", " ", "abc" == "abc", " ", 3 >= 3, " ", 4 <= 3)
	print(5 & 3, " ", 5 | 3, " ", 5 ^ 3, " ", ~5, " ", 1 << 10, " ", 1024 >> 3)
	var big := 9223372036854775807
	print(big, " ", big + 1, " ", -big - 1)
	var t := 10 if a > b else 20
	print(t, " ", "yes" if a < b else "no")
	print(int(3.7), " ", int(-3.7), " ", float(3), " ", int("42"), " ", float("2.5"), " ", int(true), " ", str(12) + "!")
	print(abs(-3), " ", abs(-2.5), " ", sign(-4), " ", sign(0.5), " ", min(3, 7), " ", max(3, 7), " ", min(2.5, 1.5), " ", max(1, 2, 3))
	print(clamp(15, 0, 10), " ", clamp(-0.5, 0.0, 1.0), " ", clampi(7, 0, 5), " ", clampf(0.25, 0.0, 1.0))
	print(floor(2.7), " ", ceil(2.1), " ", round(2.6), " ", round(-2.6), " ", floori(2.7), " ", ceili(2.1), " ", roundi(3.4))
	print(snapped(5.3, 2.0), " ", posmod(-7, 3), " ", fposmod(-7.5, 3.0), " ", fmod(7.5, 2.0), " ", wrapi(12, 0, 10), " ", wrapi(-1, 0, 10))
	print(lerp(0.0, 10.0, 0.25), " ", inverse_lerp(0.0, 10.0, 2.5), " ", sqrt(16.0), " ", pow(2.0, 8.0), " ", deg_to_rad(180.0) == PI)
	print(is_equal_approx(0.1 + 0.2, 0.3), " ", is_zero_approx(0.0000001), " ", move_toward(5.0, 10.0, 2.0), " ", move_toward(9.5, 10.0, 2.0))
	print(PI > 3.14 and PI < 3.15, " ", TAU == 2 * PI, " ", INF > 1e300, " ", is_nan(NAN))
	print("x", 1, 2.5, true, null)
	prints("x", 1, 2.5, true, null)
	printt("a", "b", 3)
	print(str(1, "-", 2.0, "-", [3]))
	quit()
