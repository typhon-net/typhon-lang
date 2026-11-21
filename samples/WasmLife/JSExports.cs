// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// The functions of browser.ty that JavaScript can call, as exports.WasmLife.JSExports.<name>.
//
// Why C#: [JSExport] works through a source generator (the JSExport generator writes the marshalling code), and Roslyn
// runs all source generators on the same input, so no generator sees what another one generated. A @JSExport on a
// Typhon function does compile to [JSExport], but the JSExport generator never sees it, and nothing is exported.
// Here each export only forwards to the Typhon function of the same name, with the same .NET types.
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("browser")]

namespace WasmLife;

public static partial class JSExports
{
    [JSExport] public static void new_world(int width, int height) => browser.new_world(width, height);
    [JSExport] public static int width() => browser.width();
    [JSExport] public static int height() => browser.height();
    [JSExport] public static int step(int generations) => browser.step(generations);
    [JSExport] public static byte[] cells() => browser.cells();
    [JSExport] public static void toggle(int x, int y) => browser.toggle(x, y);
    [JSExport] public static void set_alive(int x, int y, bool alive) => browser.set_alive(x, y, alive);
    [JSExport] public static void clear() => browser.clear();
    [JSExport] public static void randomize(int seed, double density) => browser.randomize(seed, density);
    [JSExport] public static string[] pattern_names() => browser.pattern_names();
    [JSExport] public static void place(string name) => browser.place(name);
    [JSExport] public static int generation() => browser.generation();
    [JSExport] public static int population() => browser.population();
}
