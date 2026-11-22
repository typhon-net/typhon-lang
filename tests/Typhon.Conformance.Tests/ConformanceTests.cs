// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Typhon.Conformance.Tests;

/// <summary>
/// CPython is the oracle: every tests/conformance/cases/*.ty program must print exactly the bytes CPython printed
/// (tests/conformance/expected/*.out, regenerated with tests/conformance/update_expected.py).
/// </summary>
public sealed class ConformanceTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Suite.CaseNames()) data.Add(name);
        return data;
    }

    /// <summary>`typhon run` (Roslyn → IL → JIT) prints what CPython prints. Known failures must still fail.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Jit(string name)
    {
        var run = Suite.Run("dotnet", [Suite.Cli, "run", Suite.CasePath(name)], TimeSpan.FromMinutes(2));
        string expected = Suite.Expected(name);
        bool matches = run.ExitCode == 0 && Suite.Normalize(run.StdOut) == expected;
        if (!string.IsNullOrWhiteSpace(run.StdErr)) output.WriteLine("stderr:\n" + run.StdErr);

        if (Suite.KnownFailures.TryGetValue(name, out var reason))
        {
            output.WriteLine($"known failure: {reason}");
            Assert.False(matches, $"'{name}' is listed in tests/conformance/known-failures.txt but now matches CPython: remove it from the list.");
            return;
        }
        Assert.True(run.ExitCode == 0, $"typhon run exited with {run.ExitCode}\n{run.StdErr}");
        Assert.True(matches, Suite.Diff(expected, Suite.Normalize(run.StdOut)));
    }

    /// <summary>
    /// The compile server produces the same programs as compiling in-process: every case (except known failures) through
    /// a warm server. The test's own server exits 60 s after its last request.
    /// </summary>
    [Fact]
    public void CompileServerMatches()
    {
        var env = new Dictionary<string, string> { ["TYPHON_SERVER"] = "1", ["TYPHON_SERVER_IDLE_SECONDS"] = "60" };
        var start = Suite.Run("dotnet", [Suite.Cli, "server", "start"], TimeSpan.FromMinutes(1), env);
        Assert.True(start.ExitCode == 0, "typhon server start failed\n" + start.StdErr);
        var failures = new List<string>();
        foreach (var name in Suite.CaseNames().Where(n => !Suite.KnownFailures.ContainsKey(n)))
        {
            var run = Suite.Run("dotnet", [Suite.Cli, "run", "--time", Suite.CasePath(name)], TimeSpan.FromMinutes(2), env);
            if (!run.StdErr.Contains("compiled by the compile server")) failures.Add($"{name}: not compiled by the server\n{run.StdErr}");
            else if (run.ExitCode != 0 || Suite.Normalize(run.StdOut) != Suite.Expected(name)) failures.Add($"{name}:\n{Suite.Diff(Suite.Expected(name), Suite.Normalize(run.StdOut))}");
            else output.WriteLine($"{name}: ok");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// #:package Name@Version: the package is restored (once, into the cache), referenced when compiling and loaded when
    /// the program runs.
    /// </summary>
    [Fact]
    public void PackageDirective()
    {
        string dir = Directory.CreateTempSubdirectory("typhon-package-").FullName;
        string script = Path.Combine(dir, "uses_package.ty");
        File.WriteAllText(script, "#:package Newtonsoft.Json@13.0.3\nfrom Newtonsoft.Json import JsonConvert\n\nprint(JsonConvert.SerializeObject([1, 2, 3]))\n");
        try
        {
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal("[1,2,3]\n", Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ImportedDeferredConstructor()
    {
        string directory = Directory.CreateTempSubdirectory("typhon-imported-constructor-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "component.ty"), """
                class Parent:
                    def __init__(self, value):
                        self.value = value + self.offset

                class Deferred(Parent):
                    def __init__(self, value):
                        self.offset = 4
                        super().__init__(value)
                """);
            string main = Path.Combine(directory, "main.ty");
            File.WriteAllText(main, """
                from component import Deferred

                class First:
                    def __init__(self):
                        self.ready = True

                print(First().ready, Deferred(7).value)
                """);
            var result = Suite.Run("dotnet", [Suite.Cli, "run", main], TimeSpan.FromMinutes(2));
            Assert.True(result.ExitCode == 0, result.StdErr);
            Assert.Equal("True 11\n", Suite.Normalize(result.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// #:pylib: modules Typhon's runtime lacks (graphlib, colorsys, shlex, quopri) are compiled from CPython's standard library source, with
    /// the program, and print what CPython prints.
    /// </summary>
    [Fact]
    public void PyLibDirective()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibDirective skipped: {why}"); return; }
        var lib = Suite.Run(python, ["-c", "import sysconfig; print(sysconfig.get_path('stdlib'))"], TimeSpan.FromSeconds(30)).StdOut.Trim();
        Assert.True(Directory.Exists(Path.Combine(lib)), $"no stdlib folder: {lib}");
        string dir = Directory.CreateTempSubdirectory("typhon-pylib-").FullName;
        string script = Path.Combine(dir, "uses_lib.ty");
        File.WriteAllText(script,
            $"#:pylib {lib}\n" +
            "import graphlib\nimport colorsys\nimport shlex\nfrom shlex import shlex as Lexer\nimport _shlex\nimport quopri\n\n" +
            "ts = graphlib.TopologicalSorter({\"b\": {\"a\"}, \"c\": {\"b\"}})\n" +
            "print(list(ts.static_order()))\n" +
            "h, s, v = colorsys.rgb_to_hsv(0.2, 0.4, 0.4)\n" +
            "print(round(h, 3), round(s, 3), round(v, 3))\n" +
            "print(colorsys.hls_to_rgb(0.5, 0.5, 0.5))\n" +
            "print(shlex.split('command \"two words\"'), _shlex.answer())\n" +
            "lexer = Lexer('hello')\nprint(lexer.get_token(), type(lexer).__module__)\n" +
            "print(quopri.encodestring(b'hello world\\t\\nbytes\\x00\\xff'))\n" +
            "print(quopri.decodestring(b'hello_world=21=\\ncontinued', header=True))\n");
        try
        {
            const string sibling = "def answer() -> int:\n    return 42\n";
            File.WriteAllText(Path.Combine(dir, "_shlex.ty"), sibling);
            File.WriteAllText(Path.Combine(dir, "_shlex.py"), sibling);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A Python source child imports beside the native members of a runtime package.</summary>
    [Fact]
    public void PyLibMixedPackages()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibMixedPackages skipped: {why}"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-mixed-packages-").FullName;
        string lib = Path.Combine(dir, "Lib");
        try
        {
            foreach (string package in new[] { "urllib", "collections", "importlib" })
            {
                string folder = Path.Combine(lib, package);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "__init__.py"), "raise RuntimeError('replaced runtime package')\n");
            }
            File.WriteAllText(Path.Combine(lib, "urllib", "request.py"), """
                from urllib.parse import quote

                print("loaded", __name__)

                def escaped(path: str) -> str:
                    return quote(path)
                """);
            File.WriteAllText(Path.Combine(lib, "collections", "typhon_helper.py"), """
                from collections import deque

                def gather() -> str:
                    return str(list(deque([5, 7])))
                """);
            File.WriteAllText(Path.Combine(lib, "importlib", "typhon_helper.py"), """
                from importlib import util

                def normalize() -> str:
                    return util.decode_source(b"source\r\n").strip()
                """);
            string script = Path.Combine(dir, "mix.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                import urllib.request
                from urllib import request as renamed
                import urllib.request as direct
                import urllib.parse
                import collections.typhon_helper
                from collections import typhon_helper as helper, deque
                import importlib.typhon_helper as direct_helper
                from importlib import typhon_helper, util

                print(urllib.request.escaped("a b"))
                print(renamed.__package__, direct.escaped("雪"), urllib.parse.quote("c d"))
                print(collections.typhon_helper.gather(), helper.__package__, deque([8]).pop())
                print(direct_helper.normalize(), typhon_helper.__name__, util.decode_source(b"native"))
                """);
            const string inject = """
                import collections
                import importlib
                import runpy
                import sys
                import urllib
                from pathlib import Path

                lib = Path(sys.argv[2])
                for package in (urllib, collections, importlib):
                    package.__path__.insert(0, str(lib / package.__name__))
                runpy.run_path(sys.argv[1], run_name="__main__")
                """;
            var expected = Suite.Run(python, ["-c", inject, script, lib], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>CPython's own nturl2path source handles Windows path syntax on every host.</summary>
    [Fact]
    public void PyLibWindowsFileUrls()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibWindowsFileUrls skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibWindowsFileUrls requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-windows-file-urls-").FullName;
        string script = Path.Combine(dir, "file_urls.ty");
        File.WriteAllText(script, $"#:pylib {lib}\n" + """
            import nturl2path

            for path in [r"C:\Program Files\Typhon~%+雪", r"C:temp\child", r"\\server\share name\é+.txt",
                         r"\\?\UNC\server\share\thing.txt", r"\\?\D:\folder.txt",
                         r"\rooted\with space", r"relative\雪"]:
                url = nturl2path.pathname2url(path)
                print(repr(url), repr(nturl2path.url2pathname(url)))

            for url in ["//localhost/C:/a%20b", "///C|/folder+leaf", "/////server/share/a%23b",
                        "/once%2520", "/raw%GG+text"]:
                print(repr(nturl2path.url2pathname(url)))
            """);
        try
        {
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Compile the original selectors module and compare TCP readiness over localhost.</summary>
    [Theory]
    [InlineData("SelectSelector")]
    [InlineData("DefaultSelector")]
    public void PyLibSelectors(string selector)
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibSelectors skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibSelectors requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-selectors-").FullName;
        string script = Path.Combine(dir, "selector_loopback.ty");
        File.WriteAllText(script, $"#:pylib {lib}\n" + $$"""
            import selectors
            import socket

            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                listener.listen(1)
                address = listener.getsockname()
                with selectors.{{selector}}() as mux:
                    key = mux.register(listener, selectors.EVENT_READ, "listener")
                    print(key.fileobj is listener, key.data, len(mux.get_map()))
                    print(mux.select(0))
                    with socket.create_connection(address, 2.0) as client:
                        found = mux.select(1.0)
                        print(len(found), found[0][0].data, found[0][1] == selectors.EVENT_READ)
                        print(mux.unregister(listener).data)
                        peer, _ = listener.accept()
                        with peer:
                            mux.register(peer, selectors.EVENT_READ, "peer")
                            client.sendall(b"x")
                            found = mux.select(1.0)
                            print(found[0][0].data, peer.recv(1))
                            print(list(mux.get_map().values())[0].fileobj is peer)
            """);
        try
        {
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Execute source and archive entry points through the original runpy module.</summary>
    [Fact]
    public void PyLibRunpy()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibRunpy skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibRunpy requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-runpy-").FullName;
        string script = Path.Combine(dir, "probe.ty");
        const string payload = """
            import sys

            name_seen = __name__
            package_seen = __package__
            spec_seen = getattr(__spec__, "name", None)
            argv_seen = sys.argv[0] == __file__
            record_seen = __name__ in sys.modules and sys.modules[__name__].__dict__ is globals()
            answer = seed + 1

            if globals().get("fail_inside", False):
                raise ValueError("failed while executing module")
            """;
        try
        {
            File.WriteAllText(Path.Combine(dir, "runner_payload.py"), payload);
            File.WriteAllText(Path.Combine(dir, "runner_path.py"), payload);
            string package = Path.Combine(dir, "runner_package");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), "");
            File.WriteAllText(Path.Combine(package, "__main__.py"), payload);
            using (var file = File.Create(Path.Combine(dir, "runner_archive.pyz")))
            using (var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
            using (var entry = new StreamWriter(archive.CreateEntry("__main__.py").Open()))
                entry.Write(payload);

            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import os
                import runpy
                import sys
                from types import ModuleType

                directory = os.path.dirname(__file__)
                saved_argv = sys.argv[0]
                saved_path = sys.path.copy()
                saved_module = ModuleType("temporary_execution")
                sys.modules["temporary_execution"] = saved_module
                initial = {"seed": 7}

                def report(result):
                    print(result["name_seen"], repr(result["package_seen"]), result["spec_seen"], result["answer"],
                          result["argv_seen"], result["record_seen"])

                report(runpy.run_module("runner_payload", initial))
                report(runpy.run_module("runner_payload", initial, "temporary_execution", True))
                report(runpy.run_module("runner_package", initial, alter_sys=True))
                report(runpy.run_path(os.path.join(directory, "runner_path.py"), initial))
                report(runpy.run_path(os.path.join(directory, "runner_package"), initial, "task.entry"))
                report(runpy.run_path(os.path.join(directory, "runner_archive.pyz"), initial, "archive_entry"))
                print(initial, sys.argv[0] == saved_argv, sys.path == saved_path, sys.modules["temporary_execution"] is saved_module)

                try:
                    runpy.run_module("runner_payload", {"seed": 0, "fail_inside": True}, "temporary_execution", True)
                except ValueError as error:
                    print(str(error), sys.argv[0] == saved_argv, sys.modules["temporary_execution"] is saved_module)

                try:
                    runpy.run_path(os.path.join(directory, "runner_archive.pyz"), {"seed": 0, "fail_inside": True})
                except ValueError as error:
                    print(str(error), sys.argv[0] == saved_argv, sys.path == saved_path, "<run_path>" not in sys.modules)
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A package run through runpy resolves relative and absolute imports from source.</summary>
    [Fact]
    public void PyLibRunpySourceImports()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibRunpySourceImports skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibRunpySourceImports requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-runpy-relative-").FullName;
        try
        {
            string package = Path.Combine(dir, "source_package");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), "initialized = 'parent initialized'\n");
            File.WriteAllText(Path.Combine(package, "helper.py"), "value = 42\n");
            File.WriteAllText(Path.Combine(dir, "loose_helper.py"), "def bump(value):\n    return value + 3\n");
            File.WriteAllText(Path.Combine(package, "__main__.py"), """
                import math, loose_helper
                from loose_helper import bump
                from .helper import value
                from . import helper

                def local_import():
                    from .helper import value as local_value
                    return local_value + 1

                via_parent = helper.value + 2
                via_function = local_import()
                absolute_value = math.floor(2.9) + bump(value)
                """);
            string script = Path.Combine(dir, "probe.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import runpy
                import sys

                result = runpy.run_module("source_package")
                print(result["value"], result["__package__"], result["__name__"])
                print(getattr(sys.modules["source_package"], "initialized"))
                print(getattr(sys.modules["source_package.helper"], "value"))
                print(result["via_parent"], result["via_function"], sys.modules["source_package.helper"] is result["helper"])
                print(result["absolute_value"], sys.modules["loose_helper"] is result["loose_helper"])
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Check the original pkgutil source against real package files.</summary>
    [Fact]
    public void PyLibPkgutilSource()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibPkgutilSource skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibPkgutilSource requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-pkgutil-").FullName;
        try
        {
            string source = Path.Combine(lib, "pkgutil.py");
            File.Copy(source, Path.Combine(dir, "raw_pkgutil.py"));
            File.Copy(source, Path.Combine(dir, "raw_pkgutil.ty"));
            string entries = Path.Combine(dir, "entries");
            string package = Path.Combine(entries, "beta");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(entries, "alpha.py"), "answer = 19\n");
            File.WriteAllText(Path.Combine(package, "__init__.py"), "fact = 42\n");
            File.WriteAllText(Path.Combine(package, "child.py"), "item = 3\n");
            File.WriteAllText(Path.Combine(package, "message.txt"), "beta data");
            using (var archive = System.IO.Compression.ZipFile.Open(Path.Combine(dir, "entries.zip"), System.IO.Compression.ZipArchiveMode.Create))
            {
                void WriteMember(string name, byte[] data)
                {
                    using var member = archive.CreateEntry(name).Open();
                    member.Write(data);
                }
                WriteMember("zip_leaf.py", "value = 31\n"u8.ToArray());
                WriteMember("zip_parent/__init__.py", "answer = 43\n"u8.ToArray());
                WriteMember("zip_parent/child.py", "item = 5\n"u8.ToArray());
                WriteMember("zip_parent/message.bin", "zip\0data"u8.ToArray());
                WriteMember("zip_namespace/child.py", "item = 7\n"u8.ToArray());
            }
            string script = Path.Combine(dir, "probe.ty");
            File.WriteAllText(script, """
                import os
                import raw_pkgutil
                import sys

                entries = os.path.join(os.path.dirname(__file__), "entries")
                sys.path.insert(0, entries)
                try:
                    shown = list(raw_pkgutil.iter_modules([entries], "pre."))
                    print(sorted((item.name, item.ispkg) for item in shown))
                    print(type(shown[0].module_finder).__name__, raw_pkgutil.get_importer(entries) is shown[0].module_finder)
                    print(sorted((item.name, item.ispkg) for item in raw_pkgutil.walk_packages([entries])))
                    print(raw_pkgutil.resolve_name("alpha:answer"), raw_pkgutil.resolve_name("beta:fact", strict=True))
                    print(raw_pkgutil.get_data("beta", "message.txt"))
                finally:
                    sys.path.remove(entries)

                archive = os.path.join(os.path.dirname(__file__), "entries.zip")
                sys.path.insert(0, archive)
                try:
                    print([(item.name, item.ispkg) for item in raw_pkgutil.iter_modules([archive], "pre.")])
                    print([(item.name, item.ispkg) for item in raw_pkgutil.walk_packages([archive])])
                    print(raw_pkgutil.resolve_name("zip_leaf:value"), raw_pkgutil.resolve_name("zip_parent:answer"))
                    print(raw_pkgutil.get_data("zip_parent", "message.bin"))
                finally:
                    sys.path.remove(archive)
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Compile the stock timeit module and run callable, string and CLI paths with deterministic timers.</summary>
    [Fact]
    public void PyLibTimeit()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTimeit skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTimeit requires the CPython 3.15 library"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-timeit-").FullName;
        string script = Path.Combine(dir, "timeit_probe.ty");
        File.WriteAllText(script, $"#:pylib {lib}\n" + """
            import gc
            import timeit

            class Clock:
                def __init__(self) -> None:
                    self.ticks = 0
                    self.setups = 0

                def now(self) -> float:
                    return self.ticks / 10

                def setup(self) -> None:
                    self.setups += 1
                    self.ticks += 100

                def work(self) -> None:
                    self.ticks += 2

            clock = Clock()
            timer = timeit.Timer(clock.work, clock.setup, clock.now)
            print(round(timer.timeit(3), 4), clock.setups, gc.isenabled())
            print([round(elapsed, 4) for elapsed in timer.repeat(2, 2)], clock.setups)

            def trace(loops: int, duration: float) -> None:
                print(loops, round(duration, 4))

            loops, duration = timer.autorange(trace, 0.7)
            print(loops, round(duration, 4), clock.setups)

            namespace = {"clock": clock}
            with_globals = timeit.Timer("clock.work()", setup="clock.setup()", timer=clock.now, globals=namespace)
            print(round(with_globals.timeit(4), 4), clock.setups)



            def wrap_timer(original):
                ticks = iter([0.0, 0.125, 1.0, 1.5, 2.0, 2.5])

                def tick() -> float:
                    return next(ticks)

                return tick

            result = timeit.main(
                ["--number", "0", "--repeat", "1", "--verbose", "--target-time", "0.25", "--unit", "sec", "pass"],
                _wrap_timer=wrap_timer,
            )
            print(result)
            """);
        try
        {
            var env = new Dictionary<string, string> { ["PYTHON_COLORS"] = "0" };
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3), env);
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1), env);
            Assert.True(expected.ExitCode == 0, $"CPython exit code {expected.ExitCode}\n{expected.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Compile CPython's doctest source and execute examples with the runtime compiler.</summary>
    [Fact]
    public void PyLibDoctest()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibDoctest skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibDoctest requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-doctest-").FullName;
        string script = Path.Combine(dir, "uses_doctest.ty");
        File.WriteAllText(script, $"#:pylib {lib}\n" + """
            import doctest

            source = '''
            >>> x = 2
            >>> x + 3
            5
            >>> print("hello")
            hello
            >>> raise ValueError("bad")
            Traceback (most recent call last):
            ...
            ValueError: bad
            >>> 6  # doctest: +SKIP
            '''
            test = doctest.DocTestParser().get_doctest(source, {}, "sample", "<sample>", 0)
            result = doctest.DocTestRunner().run(test)
            print(result.failed, result.attempted, result.skipped)
            """);
        string moduleScript = Path.Combine(dir, "discovers_doctests.ty");
        File.WriteAllText(moduleScript, $"#:pylib {lib}\n" + """
            '''Read a compiled global.

            >>> base
            8
            '''
            import doctest
            import sys

            base = 8

            def doubled(value: int) -> int:
                '''Double an integer.

                >>> doubled(base)
                16
                '''
                return value * 2

            found = doctest.DocTestFinder(exclude_empty=True).find(sys.modules["__main__"])
            print([(test.name, len(test.examples)) for test in found])
            result = doctest.testmod(report=False, exclude_empty=True)
            print(result.failed, result.attempted, result.skipped)
            """);
        string classScript = Path.Combine(dir, "class_doctests.ty");
        File.WriteAllText(classScript, $"#:pylib {lib}\n" + """
            import doctest
            import functools as tools
            import sys

            class Example:
                '''A container with several documented member kinds.

                >>> 7 + 2
                9
                '''

                @property
                def quantity(self) -> int:
                    '''The property returns a quantity.

                    >>> 6 * 7
                    42
                    '''
                    return 42

                @staticmethod
                def twice(value: int) -> int:
                    '''Double a number.

                    >>> 3 + 3
                    6
                    '''
                    return value * 2

                @classmethod
                def kind(owner) -> str:
                    '''Name the class.

                    >>> 'class'.upper()
                    'CLASS'
                    '''
                    return owner.__name__

                @tools.cached_property
                def cached(self) -> int:
                    '''Cache a count.

                    >>> 2 ** 3
                    8
                    '''
                    return 8

            module = sys.modules['__main__']
            finder = doctest.DocTestFinder(exclude_empty=True)
            found = finder.find(module)
            print([(test.name, len(test.examples)) for test in found])
            cached = finder.find(Example.cached, name='Example.cached', module=module)
            print([(test.name, len(test.examples)) for test in cached])
            results = doctest.testmod(report=False, exclude_empty=True)
            print(results.failed, results.attempted, results.skipped)
            """);
        try
        {
            foreach (string source in new[] { script, moduleScript, classScript })
            {
                var expected = Suite.Run(python, [source], TimeSpan.FromMinutes(1));
                Assert.True(expected.ExitCode == 0, expected.StdErr);
                var run = Suite.Run("dotnet", [Suite.Cli, "run", source], TimeSpan.FromMinutes(3));
                Assert.True(run.ExitCode == 0, run.StdErr);
                Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Run shallow and deep copies through the unmodified CPython copy module.</summary>
    [Fact]
    public void CopyFromPythonSource()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"CopyFromPythonSource skipped: {why}"); return; }
        var library = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info >= (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(library.ExitCode == 0, library.StdErr);
        string lib = library.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("CopyFromPythonSource requires CPython 3.15"); return; }

        string dir = Directory.CreateTempSubdirectory("typhon-copy-").FullName;
        try
        {
            File.Copy(Path.Combine(lib, "copy.py"), Path.Combine(dir, "copy.ty"));
            string script = Path.Combine(dir, "uses_copy.ty");
            File.WriteAllText(script, """
                import copy

                print(copy.copy(None), copy.copy(3), copy.copy(slice(1, 4, 2)))
                original = [[1], [2]]
                shallow = copy.copy(original)
                deep = copy.deepcopy(original)
                shallow[0].append(9)
                print(original, shallow, deep)
                print(original is shallow, original[0] is shallow[0], original[0] is deep[0])
                mapping = {"a": [1, 2]}
                replica = copy.deepcopy(mapping)
                print(mapping == replica, mapping["a"] is replica["a"])
                print(copy.deepcopy((["x"], 2)))
                print(copy.deepcopy(frozendict({"a": [1, 2]})))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PyLibFileInput()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibFileInput skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibFileInput requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-fileinput-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_fileinput.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import fileinput
                import os
                from pathlib import Path
                import tempfile

                with tempfile.TemporaryDirectory() as folder:
                    path = Path(folder) / 'first.txt'
                    with open(path, 'w', encoding='utf-8') as stream:
                        stream.write('first\nsecond')
                    with fileinput.FileInput(path, encoding='utf-8') as reader:
                        for line in reader:
                            print(repr(line), reader.lineno(), reader.filelineno(), reader.isfirstline())

                here = os.path.dirname(__file__)
                archives = [os.path.join(here, 'first.gz'), os.path.join(here, 'second.bz2')]
                with fileinput.FileInput(archives, openhook=fileinput.hook_compressed, encoding='utf-8') as reader:
                    for line in reader:
                        print(repr(line), os.path.basename(reader.filename()), reader.lineno(), reader.filelineno())

                with fileinput.FileInput(archives, mode='rb', openhook=fileinput.hook_compressed) as reader:
                    print(list(reader))
                """);
            File.WriteAllBytes(Path.Combine(dir, "first.gz"),
                Convert.FromBase64String("H4sIAAAAAAAA/0tOTDu8kperODU5Py8FAE73/FQNAAAA"));
            File.WriteAllBytes(Path.Combine(dir, "second.bz2"),
                Convert.FromBase64String("QlpoOTFBWSZTWRPK/Y0AAADBgAAQASAcACAAIYAMAycu4u5IpwoSAnlfsaBCWmg5MUFZJlNZOWNjfwAAAcGUABIvAYgAACAIACAAIgA00EDQNBjNWCDe93i7kinChIHLGxv4"));
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PyLibCmd()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibCmd skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibCmd requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-cmd-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_cmd.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """"
                import cmd
                import io

                class Demo(cmd.Cmd):
                    prompt = 'demo> '

                    def do_say(self, value):
                        """Echo a command argument."""
                        self.stdout.write('said: ' + value + '\n')

                    def do_stop(self, value):
                        return True

                    def complete_say(self, text, line, begidx, endidx):
                        return ['hello', 'help']

                out = io.StringIO()
                session = Demo(stdin=io.StringIO('say first\nsay next\nstop\n'), stdout=out)
                session.use_rawinput = False
                session.cmdloop('begin')
                print(repr(out.getvalue()))
                print(session.parseline('?say'), session.parseline('say another'))
                print(session.completenames('s'))
                session.onecmd('help say')
                print(out.getvalue().endswith('Echo a command argument.\n'))
                """");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Run the unmodified CPython codeop source with Typhon's interactive compiler and warning filters.</summary>
    [Fact]
    public void PyLibCodeop()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibCodeop skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibCodeop requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-codeop-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_codeop.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """"
                import __future__
                import codeop

                scope = {}
                compiled = codeop.compile_command('answer = 21 * 2')
                print(compiled is not None, compiled.co_filename)
                exec(compiled, scope)
                print(scope['answer'])
                print(eval(codeop.compile_command('6 * 7', symbol='eval')))
                for text in ['if True:', 'if True:\n    pass', '(6 *', '"""incomplete', 'try:\n    pass']:
                    print(codeop.compile_command(text) is None)
                for text in ['if True\n    pass', 'x =', 'a = (]', '1 +', "'broken", 'x:=9', '1 = 2', 'break', 'continue']:
                    try:
                        codeop.compile_command(text)
                    except SyntaxError:
                        print('syntax error')
                compiler = codeop.Compile()
                stateful = codeop.CommandCompiler()
                annotation_flag = __future__.annotations.compiler_flag
                print(bool(compiler('from __future__ import annotations', '<input>', 'exec').co_flags & annotation_flag))
                print(bool(compiler('pass', '<input>', 'exec').co_flags & annotation_flag))
                print(bool(stateful('from __future__ import annotations', symbol='exec').co_flags & annotation_flag))
                print(stateful('if True:') is None, bool(stateful('pass').co_flags & annotation_flag))

                stateful('from __future__ import barry_as_FLUFL', symbol='exec')
                print(eval(stateful('1 <> 2', symbol='eval')))
                try:
                    stateful('1 != 2', symbol='eval')
                except SyntaxError:
                    print('remembered grammar')
                print(codeop.compile_command('@decorate') is None, codeop.compile_command('match value:') is None)
                print(codeop.compile_command('if True:\n    pass', symbol='exec') is not None)
                print(codeop.compile_command('(', symbol='eval') is None)
                try:
                    codeop.compile_command('if True:', symbol='eval')
                except SyntaxError:
                    print('statement in eval mode')
                try:
                    codeop.compile_command('if True:\npass')
                except IndentationError:
                    print('indentation error')
                try:
                    codeop.compile_command('return')
                except SyntaxError:
                    print('invalid module return')
                import warnings
                with warnings.catch_warnings(record=True) as recorded:
                    warnings.simplefilter('always')
                    warnings.simplefilter('ignore', (SyntaxWarning, DeprecationWarning))
                    warnings.warn('hidden', SyntaxWarning)
                    warnings.warn('hidden', DeprecationWarning)
                    warnings.warn('visible', RuntimeWarning)
                    print(len(recorded), str(recorded[0].message))
                """");
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Run CPython's completion logic on compiled functions, methods and descriptors.</summary>
    [Fact]
    public void PyLibRlcompleter()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibRlcompleter skipped: {why}"); return; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibRlcompleter requires the CPython 3.15 library"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-rlcompleter-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_rlcompleter.ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + """
                import rlcompleter

                def custom_empty():
                    pass

                def custom_value(value: int):
                    pass

                class Target:
                    def first(self):
                        pass

                    def second(self, value: int):
                        pass

                    @property
                    def failure(self) -> int:
                        raise RuntimeError("completer must not evaluate the property")

                target = Target()
                completer = rlcompleter.Completer({"custom_empty": custom_empty, "custom_value": custom_value, "target": target})
                print(completer.global_matches("custom_"))
                print(completer.global_matches("retu"))
                print(completer.attr_matches("target.fi"))
                print(completer.attr_matches("target.se"))
                print(completer.attr_matches("target.fa"))
                print(completer.complete("custom_", 0))
                print(completer.complete("custom_", 1))
                print(completer.complete("custom_", 2))
                """);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PyLibPackages()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibPackages skipped: {why}"); return; }
        string dir = Directory.CreateTempSubdirectory("typhon-pylib-package-").FullName;
        try
        {
            string lib = Path.Combine(dir, "Lib");
            string package = Path.Combine(lib, "pylib_example");
            string nested = Path.Combine(package, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(package, "__init__.py"), """
                print("package")
                BASE = 39
                from .helpers import answer as exported, Answer
                from . import nested as branch
                """);
            File.WriteAllText(Path.Combine(package, "helpers.py"), """
                from . import BASE
                print("helpers")

                def answer() -> int:
                    return BASE + 3

                class Answer:
                    def get(self) -> int:
                        return answer()
                """);
            File.WriteAllText(Path.Combine(nested, "__init__.py"), "print('nested')\n");
            File.WriteAllText(Path.Combine(nested, "reader.py"), """
                from ..helpers import answer as original
                print("reader")

                def read() -> int:
                    return original()
                """);
            File.WriteAllText(Path.Combine(nested, "main.py"), """
                def setup() -> str:
                    print("named module")
                    return "module function"

                LABEL = setup()

                def main() -> str:
                    return LABEL
                """);
            File.WriteAllText(Path.Combine(nested, "__main__.py"), """
                print("imported __main__")
                if __name__ == "__main__":
                    print("executed as script")
                """);
            // The entry is already loaded as __main__. Its path must not be compiled again when it imports os.
            string script = Path.Combine(dir, "os.ty");
            File.WriteAllText(script, """
                #:pylib Lib
                import os
                import pylib_example.nested.reader
                import pylib_example.nested.reader as direct
                import pylib_example.nested.main as with_module_name
                import pylib_example.nested.__main__
                from pylib_example import exported as reexport, Answer as Value
                from pylib_example.nested import reader as from_package
                print(pylib_example.branch.__package__)
                print(reexport(), pylib_example.exported(), Value().get())
                print(with_module_name.main(), hasattr(direct, "read"), direct is from_package)
                print(direct.read(), pylib_example.nested.reader.read(), from_package.read())
                print(os.path.basename("folder/file"))
                """.ReplaceLineEndings("\r\n"));
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1),
                new Dictionary<string, string> { ["PYTHONPATH"] = lib });
            Assert.True(expected.ExitCode == 0, $"CPython exited with {expected.ExitCode}\n{expected.StdErr}");
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, $"exit code {run.ExitCode}\n{run.StdErr}");
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// typhon test runs a test module's tests like pytest: fixtures (also yield fixtures), parametrize, raises, approx,
    /// skip and test classes pass, the failing test is reported with its assert line, the values compared and its output.
    /// </summary>
    [Fact]
    public void PytestStyleTests()
    {
        var run = Suite.Run("dotnet", [Suite.Cli, "test", Path.Combine(Suite.RepoRoot, "tests", "pytest", "test_sample.ty")], TimeSpan.FromMinutes(2));
        string stdout = Suite.Normalize(run.StdOut);
        output.WriteLine(stdout);
        Assert.True(run.ExitCode == 1, $"exit code {run.ExitCode}\n{run.StdErr}");
        Assert.Contains("test_sample.ty ........Fs.. [100%]", stdout);
        Assert.Contains(">   assert add(2, 2) == 5\nE   assert 4 == 5", stdout);
        Assert.Contains("Captured stdout call", stdout);
        Assert.Contains("FAILED test_sample.ty::test_fails - assert 4 == 5", stdout);
        Assert.Matches(@"=+ 1 failed, 10 passed, 1 skipped in \d+\.\d\ds =+", stdout);
    }

    /// <summary>
    /// A cached build is used only while every source file is unchanged: run twice (the second from the cache), edit an
    /// imported module, and the next run must compile again and print the new result.
    /// </summary>
    [Fact]
    public void ScriptCacheTracksSources()
    {
        string dir = Path.Combine(Suite.RepoRoot, "artifacts", "conformance-cache", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.ty"), "from helper import greet\nprint(greet(\"cache\"))\n");
            File.WriteAllText(Path.Combine(dir, "helper.ty"), "def greet(n: str) -> str:\n    return \"hi \" + n\n");
            var env = new Dictionary<string, string> { ["TYPHON_CACHE"] = "1", ["TYPHON_CACHE_DIR"] = Path.Combine(dir, "cache") };
            string[] cmd = [Suite.Cli, "run", "--time", Path.Combine(dir, "main.ty")];

            var first = Suite.Run("dotnet", cmd, TimeSpan.FromMinutes(2), env);
            Assert.Equal("hi cache\n", Suite.Normalize(first.StdOut));
            Assert.Contains("compiled in", first.StdErr);

            var second = Suite.Run("dotnet", cmd, TimeSpan.FromMinutes(2), env);
            Assert.Equal("hi cache\n", Suite.Normalize(second.StdOut));
            Assert.Contains("cached build", second.StdErr);

            File.WriteAllText(Path.Combine(dir, "helper.ty"), "def greet(n: str) -> str:\n    return \"hello \" + n\n");
            var third = Suite.Run("dotnet", cmd, TimeSpan.FromMinutes(2), env);
            Assert.Equal("hello cache\n", Suite.Normalize(third.StdOut));
            Assert.Contains("compiled in", third.StdErr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    public static TheoryData<string> UntypedCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Suite.CaseNames().Where(n => n.StartsWith("untyped", StringComparison.Ordinal))) data.Add(name);
        return data;
    }

    /// <summary>
    /// Cases named untyped*.ty have no annotations at all; whole-program inference must type every parameter, return value
    /// and field, so they compile with --strict (where any implicit dynamic is an error).
    /// </summary>
    [Theory]
    [MemberData(nameof(UntypedCases))]
    [InlineData("optional_field_inference")]
    public void UntypedCompilesStrict(string name)
    {
        var run = Suite.Run("dotnet", [Suite.Cli, "check", "--strict", Suite.CasePath(name)], TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0, $"typhon check --strict failed: inference left something dynamic\n{run.StdErr}{run.StdOut}");
    }

    /// <summary>
    /// The checked-in expected outputs are what the CPython on this machine prints today (guards against stale .out files).
    /// Uses TYPHON_CPYTHON, the repo's .venv, or python3/python on PATH; passes with a note when none is found,
    /// unless TYPHON_REQUIRE_CPYTHON=1 (set in CI).
    /// </summary>
    [Fact]
    public void ExpectedOutputsMatchCPython()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null)
        {
            Assert.True(Environment.GetEnvironmentVariable("TYPHON_REQUIRE_CPYTHON") != "1", $"CPython 3.12+ is required: {why}");
            output.WriteLine($"skipped: {why}");
            return;
        }
        bool hasNumpy = Suite.Run(python, ["-c", "import numpy"], TimeSpan.FromMinutes(1)).ExitCode == 0;
        var stale = new List<string>();
        foreach (var name in Suite.CaseNames())
        {
            if (name.StartsWith("numpy", StringComparison.Ordinal) && !hasNumpy) { output.WriteLine($"{name}: skipped (numpy not installed)"); continue; }
            var run = Suite.Run(python, ["-X", "utf8", Suite.CasePath(name)], TimeSpan.FromMinutes(2));
            if (run.ExitCode != 0) { stale.Add($"{name}: CPython exited with {run.ExitCode}\n{run.StdErr}"); continue; }
            string actual = Suite.Normalize(run.StdOut);
            if (actual != Suite.Expected(name)) stale.Add($"{name}:\n{Suite.Diff(Suite.Expected(name), actual)}");
            else output.WriteLine($"{name}: ok");
        }
        Assert.True(stale.Count == 0, "expected/*.out differs from CPython (run tests/conformance/update_expected.py):\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// `typhon build --aot` produces a native executable with the same output. Opt-in (TYPHON_CONFORMANCE_AOT=1): it needs the
    /// platform's native toolchain and takes ~30 s per case. Cases that use dynamic values (TY2001) are reported and skipped.
    /// </summary>
    [Fact]
    public void NativeAot()
    {
        if (Environment.GetEnvironmentVariable("TYPHON_CONFORMANCE_AOT") != "1") { output.WriteLine("skipped: set TYPHON_CONFORMANCE_AOT=1"); return; }
        var failures = new List<string>();
        string outRoot = Path.Combine(Suite.RepoRoot, "artifacts", "conformance-aot");
        foreach (var name in Suite.CaseNames())
        {
            if (Suite.KnownFailures.ContainsKey(name)) continue;
            string outDir = Path.Combine(outRoot, name);
            var build = Suite.Run("dotnet", [Suite.Cli, "build", Suite.CasePath(name), "--aot", "-o", outDir], TimeSpan.FromMinutes(10));
            if (build.ExitCode != 0)
            {
                if (build.StdErr.Contains("TY2001")) { output.WriteLine($"{name}: not AOT-compatible (dynamic code), skipped"); continue; }
                failures.Add($"{name}: build --aot failed\n{build.StdErr}{build.StdOut}");
                continue;
            }
            string exe = Path.Combine(outDir, "typhon_" + name + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
            var run = Suite.Run(exe, [], TimeSpan.FromMinutes(2));
            string actual = Suite.Normalize(run.StdOut);
            if (run.ExitCode != 0 || actual != Suite.Expected(name)) failures.Add($"{name}: exit {run.ExitCode}\n{run.StdErr}{Suite.Diff(Suite.Expected(name), actual)}");
            else output.WriteLine($"{name}: ok");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}

/// <summary>Paths, the known-failures list and process helpers shared by the tests.</summary>
static class Suite
{
    static string Metadata(string key) =>
        typeof(Suite).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"assembly metadata '{key}' missing");

    public static readonly string RepoRoot = Path.GetFullPath(Metadata("TyphonRepoRoot"));
    public static readonly string Cli = Path.GetFullPath(Metadata("TyphonCli"));
    public static readonly string Emit = Path.GetFullPath(Metadata("TyphonEmit"));
    static readonly string ConformanceDir = Path.Combine(RepoRoot, "tests", "conformance");

    public static IEnumerable<string> CaseNames() =>
        Directory.GetFiles(Path.Combine(ConformanceDir, "cases"), "*.ty").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!;

    public static string CasePath(string name) => Path.Combine(ConformanceDir, "cases", name + ".ty");

    public static string Expected(string name)
    {
        string path = Path.Combine(ConformanceDir, "expected", name + ".out");
        Assert.True(File.Exists(path), $"missing {path}: run tests/conformance/update_expected.py");
        return Normalize(File.ReadAllText(path, Encoding.UTF8));
    }

    public static readonly IReadOnlyDictionary<string, string> KnownFailures = File.ReadAllLines(Path.Combine(ConformanceDir, "known-failures.txt"))
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => { int hash = l.IndexOf('#'); return (Name: (hash < 0 ? l : l[..hash]).Trim(), Reason: hash < 0 ? "" : l[(hash + 1)..].Trim()); })
        .ToDictionary(x => x.Name, x => x.Reason);

    public static string Normalize(string s) => s.Replace("\r\n", "\n");

    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static Result Run(string fileName, IEnumerable<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = Path.Combine(ConformanceDir, "cases"),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONHASHSEED"] = "0";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        // by default every test compiles in its own process, without the script cache: it tests the compiler itself
        psi.Environment["TYPHON_SERVER"] = "0";
        psi.Environment["TYPHON_CACHE"] = "0";
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new Result(-1, stdout.IsCompleted ? stdout.Result : "", $"timed out after {timeout}");
        }
        p.WaitForExit();
        return new Result(p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string? FindPython(out string why)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("TYPHON_CPYTHON") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(RepoRoot, ".venv", "Scripts", "python.exe"));
        candidates.Add(Path.Combine(RepoRoot, ".venv", "bin", "python"));
        candidates.Add("python3");
        candidates.Add("python");
        foreach (var c in candidates)
        {
            if (Path.IsPathRooted(c) && !File.Exists(c)) continue;
            try
            {
                // PEP 695 generics (def first[T]) need 3.12+
                var r = Run(c, ["-c", "import sys; print(sys.version_info >= (3, 12))"], TimeSpan.FromSeconds(30));
                if (r.ExitCode == 0 && r.StdOut.Trim() == "True") { why = ""; return c; }
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        why = "no CPython 3.12+ found (set TYPHON_CPYTHON)";
        return null;
    }

    /// <summary>A small line diff for assertion messages: each differing line as -expected / +actual.</summary>
    public static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder("output differs from CPython (- CPython, + Typhon):\n");
        int shown = 0;
        for (int i = 0; i < Math.Max(e.Length, a.Length) && shown < 20; i++)
        {
            string? el = i < e.Length ? e[i] : null, al = i < a.Length ? a[i] : null;
            if (el == al) continue;
            shown++;
            if (el != null) sb.Append($"  line {i + 1} - {el}\n");
            if (al != null) sb.Append($"  line {i + 1} + {al}\n");
        }
        return sb.ToString();
    }
}
