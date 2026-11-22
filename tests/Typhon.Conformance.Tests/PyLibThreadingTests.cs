// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibThreadingTests
{
    [Fact]
    public void PyLibThreadingSource()
    {
        string? python = Suite.FindPython(out string reason);
        if (python == null)
        {
            Console.WriteLine($"PyLibThreadingSource skipped: {reason}");
            return;
        }
        var stdlib = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(stdlib.ExitCode == 0, stdlib.StdErr);
        string lib = stdlib.StdOut.Trim();
        if (lib.Length == 0)
        {
            Console.WriteLine("PyLibThreadingSource requires the CPython 3.15 library");
            return;
        }

        string directory = Directory.CreateTempSubdirectory("typhon-threading-").FullName;
        try
        {
            string source = Path.Combine(lib, "threading.py");
            File.Copy(source, Path.Combine(directory, "stock_threading.py"));
            File.Copy(source, Path.Combine(directory, "stock_threading.ty"));
            string script = Path.Combine(directory, "probe.ty");
            File.WriteAllText(script, """
                import stock_threading as threading
                from contextvars import ContextVar, copy_context

                local_value = ContextVar('local_value', default='empty')
                local_value.set('parent')
                started = threading.Event()
                resume = threading.Event()
                observed: list[str] = []


                def work(label):
                    observed.append(label + ':' + local_value.get())
                    observed.append(threading.current_thread().name)
                    started.set()
                    resume.wait(5)


                worker = threading.Thread(name='worker', target=work, args=('current',))
                print(worker.name, worker.ident is None, worker.native_id is None, worker.is_alive())
                worker.start()
                assert started.wait(5)
                print(worker.is_alive(), worker.ident is not None, worker.native_id > 0, worker in threading.enumerate())
                worker.join(0)
                print(worker.is_alive())
                resume.set()
                worker.join(5)
                print(worker.is_alive(), observed, threading.current_thread() is threading.main_thread())

                started.clear()
                explicit = threading.Thread(name='context', target=work, args=('copy',), context=copy_context())
                explicit.start()
                assert started.wait(5)
                explicit.join(5)
                print(observed[-2:])

                errors: list[str] = []

                def hook(args):
                    errors.append(f"{args.exc_type.__name__}:{args.exc_value}:{args.thread.name}")


                def fail():
                    raise ValueError('in worker')


                previous = threading.excepthook
                threading.excepthook = hook
                try:
                    broken = threading.Thread(name='error', target=fail)
                    broken.start()
                    broken.join(5)
                    print(errors)
                finally:
                    threading.excepthook = previous

                simple = threading.BoundedSemaphore(1)
                print(simple.acquire(blocking=False), simple.acquire(blocking=False))
                simple.release()
                print(simple.acquire(blocking=False))
                """);
            var expected = Suite.Run(python, ["-X", "thread_inherit_context=0", script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var actual = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(actual.ExitCode == 0, actual.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(actual.StdOut));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
