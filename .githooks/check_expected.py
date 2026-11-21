# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

"""For the pre-commit hook: each named conformance case, run on this CPython, must print its staged expected output,
and that output must not contain absolute temp or home paths (machine- and OS-specific)."""

import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
CASES = ROOT / "tests" / "conformance" / "cases"
MACHINE_PATHS = re.compile(r"(/tmp/|/var/folders/|C:\\\\Users\\\\|AppData|" + re.escape(str(Path.home())) + r")")


def staged(path: str) -> str | None:
    try:
        return subprocess.check_output(["git", "show", f":{path}"], cwd=ROOT, text=True, encoding="utf-8").replace("\r\n", "\n")
    except subprocess.CalledProcessError:
        return None


def main() -> int:
    failed = 0
    env = dict(os.environ, PYTHONIOENCODING="utf-8", PYTHONHASHSEED="0")
    for name in sys.argv[1:]:
        case = CASES / f"{name}.ty"
        expected = staged(f"tests/conformance/expected/{name}.out")
        if not case.exists():
            continue
        if expected is None:
            print(f"  {name}: no staged expected/{name}.out")
            failed += 1
            continue
        run = subprocess.run([sys.executable, "-X", "utf8", str(case)], capture_output=True, text=True, encoding="utf-8",
                             env=env, cwd=CASES, timeout=120)
        actual = run.stdout.replace("\r\n", "\n")
        if run.returncode != 0:
            print(f"  {name}: CPython exits with {run.returncode}\n{run.stderr[-2000:]}")
            failed += 1
        elif actual != expected:
            lines_a, lines_e = actual.splitlines(), expected.splitlines()
            for i in range(max(len(lines_a), len(lines_e))):
                a = lines_a[i] if i < len(lines_a) else "<missing>"
                e = lines_e[i] if i < len(lines_e) else "<missing>"
                if a != e:
                    print(f"  {name}: line {i + 1}\n    expected: {e}\n    CPython:  {a}")
                    break
            failed += 1
        elif m := MACHINE_PATHS.search(expected):
            print(f"  {name}: the expected output contains a machine-specific path ({m.group(0)!r}); print paths relative to a base")
            failed += 1
        else:
            print(f"  {name}: ok")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
