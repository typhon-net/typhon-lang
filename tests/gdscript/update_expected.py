#!/usr/bin/env python3
# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.
"""Regenerates tests/gdscript/expected/*.out with Godot, the oracle for the GDScript front end.

Each case is a SceneTree script; Godot runs it headless (godot --headless -s case.gd) and its standard output, without
the engine's banner line, is the expected output. Typhon must print exactly that (tests/Typhon.Conformance.Tests,
GdScriptTests), and the tests do not need Godot.

    GODOT=/path/to/Godot_v4.x_console.exe python tests/gdscript/update_expected.py [case ...]
"""
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CASES = HERE / "cases"
EXPECTED = HERE / "expected"


def main() -> int:
    godot = os.environ.get("GODOT")
    if not godot:
        print("set GODOT to the Godot 4 executable (on Windows the *_console.exe)", file=sys.stderr)
        return 2
    names = sys.argv[1:] or sorted(p.stem for p in CASES.glob("*.gd"))
    EXPECTED.mkdir(exist_ok=True)
    failed = 0
    for name in names:
        case = CASES / f"{name}.gd"
        # run in the cases folder with a relative path: a Windows Godot started from WSL gets no Linux paths
        run = subprocess.run([godot, "--headless", "--no-header", "-s", case.name], cwd=CASES, capture_output=True, timeout=120)
        out = run.stdout.decode("utf-8").replace("\r\n", "\n")
        lines = out.split("\n")
        if lines and lines[0].startswith("Godot Engine v"):
            lines = lines[1:]
            if lines and lines[0] == "":
                lines = lines[1:]
        out = "\n".join(lines)
        err = run.stderr.decode("utf-8", "replace")
        if run.returncode != 0 or "SCRIPT ERROR" in err or "Parse Error" in err:
            print(f"{name}: Godot failed ({run.returncode})\n{err}", file=sys.stderr)
            failed += 1
            continue
        (EXPECTED / f"{name}.out").write_bytes(out.encode("utf-8"))
        print(f"{name}: {len(out.splitlines())} lines")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
