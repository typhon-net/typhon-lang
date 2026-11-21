// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace Typhon.Playground;

/// <summary>An example program of the playground; <see cref="Stdin"/> is the input it reads, if any.</summary>
public sealed record Example(string Name, string Source, string Stdin = "");

/// <summary>The example programs the playground offers (tests/Typhon.Playground.Tests compiles and runs every one).</summary>
public static class Examples
{
    public static readonly IReadOnlyList<Example> All =
    [
        new("Hello, world", """
            def greet(name: str) -> str:
                return f"Hello, {name}!"


            print(greet("world"))
            for i in range(3):
                print(i, "squared is", i ** 2)
            """),

        new("Classes", """
            import math


            class Shape:
                def __init__(self, name: str) -> None:
                    self.name = name

                def area(self) -> float:
                    return 0.0

                def __str__(self) -> str:
                    return f"{self.name} with area {self.area():.2f}"


            class Circle(Shape):
                def __init__(self, r: float) -> None:
                    super().__init__("circle")
                    self.r = r

                def area(self) -> float:
                    return math.pi * self.r ** 2


            class Rect(Shape):
                def __init__(self, w: float, h: float) -> None:
                    super().__init__("rect")
                    self.w = w
                    self.h = h

                def area(self) -> float:
                    return self.w * self.h

                @property
                def is_square(self) -> bool:
                    return self.w == self.h


            shapes: list[Shape] = [Circle(1.0), Rect(2.0, 3.0), Rect(2.0, 2.0)]
            for s in shapes:
                print(s)
            print("total area:", round(sum(s.area() for s in shapes), 3))
            rects = [Rect(1.0, 2.0), Rect(3.0, 3.0)]
            print("squares:", [str(r) for r in rects if r.is_square])
            """),

        new("Generators", """
            from typing import Iterator


            def fibonacci() -> Iterator[int]:
                a, b = 0, 1
                while True:
                    yield a
                    a, b = b, a + b


            def take(n: int, items: Iterator[int]) -> list[int]:
                result: list[int] = []
                for x in items:
                    if len(result) == n:
                        break
                    result.append(x)
                return result


            def countdown(n: int) -> Iterator[str]:
                while n > 0:
                    yield f"{n}..."
                    n -= 1
                yield "liftoff!"


            print(take(12, fibonacci()))
            print(" ".join(countdown(5)))
            evens = (x for x in range(20) if x % 2 == 0)
            print("sum of even squares:", sum(x * x for x in evens))
            """),

        new("Comprehensions", """
            words = "the quick brown fox jumps over the lazy dog".split()

            lengths = [len(w) for w in words]
            by_letter = {w[0]: w for w in sorted(words)}
            vowels = {c for w in words for c in w if c in "aeiou"}
            pairs = [(x, y) for x in range(1, 4) for y in range(x, 4)]
            matrix = [[row * 3 + col for col in range(3)] for row in range(3)]

            print("lengths:", lengths)
            print("by first letter:", by_letter)
            print("vowels:", sorted(vowels))
            print("pairs:", pairs)
            print("transposed:", [[row[i] for row in matrix] for i in range(3)])
            print("longest:", max(words, key=len), "| counts:", {w: words.count(w) for w in set(words) if words.count(w) > 1})
            """),

        new("Dataclass", """
            from dataclasses import dataclass, field
            from functools import total_ordering


            @total_ordering
            @dataclass
            class Version:
                major: int
                minor: int = 0
                patch: int = 0

                def __lt__(self, other: "Version") -> bool:
                    return (self.major, self.minor, self.patch) < (other.major, other.minor, other.patch)

                def __str__(self) -> str:
                    return f"{self.major}.{self.minor}.{self.patch}"


            @dataclass
            class Package:
                name: str
                version: Version
                tags: list[str] = field(default_factory=list)


            versions = [Version(1, 2, 3), Version(1, 10), Version(0, 9, 1), Version(1, 2)]
            print("sorted:", ", ".join(str(v) for v in sorted(versions)))
            print("newest:", max(versions))
            print(Version(2) == Version(2, 0, 0), Version(1, 2) < Version(1, 2, 1))

            pkg = Package("typhon", Version(0, 1))
            pkg.tags.append("compiler")
            print(pkg)
            """),

        new("f-string table", """
            from dataclasses import dataclass


            @dataclass
            class Planet:
                name: str
                distance_au: float
                moons: int
                radius_km: float


            planets = [
                Planet("Mercury", 0.39, 0, 2439.7),
                Planet("Venus", 0.72, 0, 6051.8),
                Planet("Earth", 1.0, 1, 6371.0),
                Planet("Mars", 1.52, 2, 3389.5),
                Planet("Jupiter", 5.2, 95, 69911.0),
                Planet("Saturn", 9.54, 146, 58232.0),
            ]

            print(f"{'Planet':<10}{'Distance':>10}{'Moons':>7}{'Radius':>12}")
            print("-" * 39)
            for p in planets:
                print(f"{p.name:<10}{p.distance_au:>8.2f}AU{p.moons:>7}{p.radius_km:>10,.0f}km")
            print("-" * 39)
            total = sum(p.moons for p in planets)
            print(f"{'moons':<10}{total:>17}  ({total / len(planets):.1f} per planet)")
            print(f"{'hex/bin':<10}{255:>#10x}{5:>#7b}{0.125:>12.1%}")
            """),

        new(".NET interop", """
            from System.Text import StringBuilder
            from System.Collections.Generic import Dictionary, Queue
            from System import Math, Guid, Environment

            sb = StringBuilder()
            for i in range(5):
                sb.Append(i).Append(", ")
            print(sb.ToString().rstrip(", "))

            ages = Dictionary[str, int]()
            ages["Ada"] = 36
            ages["Alan"] = 41
            for name in ages:  # like a Python dict, iterating yields the keys
                print(name, "is", ages[name])

            q = Queue[str]()
            q.Enqueue("first")
            q.Enqueue("second")
            print("dequeued:", q.Dequeue(), "| left:", q.Count)
            print("Math.Clamp:", Math.Clamp(15, 0, 10), "| a Guid has", len(str(Guid.NewGuid())), "characters")
            print("running on .NET", Environment.Version.Major)
            """),

        new("Exceptions and tracebacks", """
            class InsufficientFunds(Exception):
                pass


            class Account:
                def __init__(self, owner: str, balance: float) -> None:
                    self.owner = owner
                    self.balance = balance

                def withdraw(self, amount: float) -> None:
                    if amount > self.balance:
                        raise InsufficientFunds(f"{self.owner} has only {self.balance:.2f}")
                    self.balance -= amount


            acct = Account("Ada", 100.0)
            try:
                acct.withdraw(30.0)
                acct.withdraw(100.0)
            except InsufficientFunds as e:
                print("caught:", e)
            finally:
                print("balance:", acct.balance)


            def average(values: list[float]) -> float:
                return sum(values) / len(values)


            print(average([1.0, 2.0, 3.0]))
            print(average([]))  # uncaught: the traceback shows the Typhon lines
            """),

        new("Duck typing (dynamic)", """
            # Any is .NET's 'dynamic': the C# runtime binder resolves t.speak() while the program runs.
            # A parameter without an annotation takes the types it is called with: describe is compiled
            # once for int and once for str.
            from typing import Any


            class Duck:
                def speak(self) -> str:
                    return "Quack"


            class Robot:
                def speak(self) -> str:
                    return "Beep"


            def chorus(things: list[Any]) -> str:
                return " ".join(t.speak() for t in things)


            def describe(x):
                return f"{x} doubled is {x * 2}"


            print(chorus([Duck(), Robot(), Duck()]))
            print(describe(21))
            print(describe("ab"))
            """),

        new("Big integers", """
            # typhon: int=bigint
            # With this comment (or `typhon --bigint`), int is unbounded like Python's; otherwise a checked 64-bit long.
            def factorial(n: int) -> int:
                result = 1
                for i in range(2, n + 1):
                    result *= i
                return result


            print("50! =", factorial(50))
            print("2 ** 200 =", 2 ** 200)
            print("digits in 1000!:", len(str(factorial(1000))))
            """),

        new("Reading input", """
            # input() reads the lines of the stdin box below the editor.
            name = input("Your name: ")
            print()
            print(f"Hello, {name}!")
            numbers = [int(x) for x in input("Some numbers: ").split()]
            print()
            print(f"sum={sum(numbers)} min={min(numbers)} max={max(numbers)}")
            """, Stdin: "Ada\n3 1 4 1 5 9 2 6\n"),
    ];
}
