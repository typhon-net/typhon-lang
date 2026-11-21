extends SceneTree
# Functions: defaults, typed and untyped parameters, recursion, lambdas and callables.

var calls := 0

func fib(n: int) -> int:
	calls += 1
	if n < 2:
		return n
	return fib(n - 1) + fib(n - 2)

func scale(v: float, factor := 2.0) -> float:
	return v * factor

func greet(who: String = "world", punct := "!") -> String:
	return "hello " + who + punct

func sum_all(values: Array[int]) -> int:
	var total := 0
	for v in values:
		total += v
	return total

func apply(f: Callable, v: int) -> int:
	return f.call(v)

func gcd(a: int, b: int) -> int:
	while b != 0:
		var t := b
		b = a % b
		a = t
	return a

func collatz_steps(start: int) -> int:
	var n := start
	var steps := 0
	while n != 1:
		if n % 2 == 0:
			n = n / 2
		else:
			n = 3 * n + 1
		steps += 1
	return steps

func _init() -> void:
	print(fib(15), " calls ", calls)
	print(scale(3.0), " ", scale(3.0, 0.5), " ", greet(), " ", greet("Godot"), " ", greet("you", "?"))
	print(sum_all([1, 2, 3, 4]), " ", gcd(84, 36), " ", collatz_steps(27))
	var square := func(x: int) -> int: return x * x
	print(square.call(7), " ", apply(square, 9))
	var adder := func(x, y): return x + y
	print(adder.call(2, 3))
	var squares: Array[int] = []
	for i in range(1, 6):
		squares.append(square.call(i))
	print(squares)
	quit()
