// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibPyclbrTests
{
    [Fact]
    public void PyLibPyclbr()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibPyclbr skipped: {why}"); return; }
        var oracle = Suite.Run(python, ["-c", "import sysconfig; print(sysconfig.get_path('stdlib'))"], TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string dir = Directory.CreateTempSubdirectory("typhon-pyclbr-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_pyclbr.ty");
            const string source = """"
                import os
                import pyclbr
                import sys
                import tempfile

                with tempfile.TemporaryDirectory() as root:
                    path = os.path.join(root, "browser_pkg")
                    os.mkdir(path)
                    with open(os.path.join(path, "__init__.py"), "w", encoding="utf-8") as f:
                        f.write("raise RuntimeError('do not run the package')\n")
                    with open(os.path.join(path, "child.py"), "w", encoding="utf-8") as f:
                        f.write("""raise RuntimeError('do not run the module')

                class Base:
                    def value(self):
                        return 40

                @decorate
                class Child(Base, Extra[int]):
                    async def work(self, x):
                        def nested():
                            return x
                        return nested()
                    class Inner:
                        pass

                if False:
                    def conditional():
                        pass

                def top(): pass
                """)
                    result = pyclbr.readmodule_ex("browser_pkg.child", [root])
                    print(sorted(result))
                    print("browser_pkg" in sys.modules, "browser_pkg.child" in sys.modules)
                    child = result["Child"]
                    print(child.name, child.module, os.path.basename(child.file), child.lineno, child.end_lineno)
                    print(child.super[0] is result["Base"], child.super[1])
                    print(sorted(child.methods.items()), sorted(child.children))
                    work = child.children["work"]
                    print(work.is_async, work.parent is child, work.lineno, work.end_lineno)
                    print(work.children["nested"].parent is work, work.children["nested"].end_lineno)
                    print(result["conditional"].lineno, result["top"].end_lineno)
                    print(sorted(pyclbr.readmodule("browser_pkg.child", [root])))
                """";
            File.WriteAllText(script, source);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            File.WriteAllText(script, $"#:pylib {oracle.StdOut.Trim()}\n" + source);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
