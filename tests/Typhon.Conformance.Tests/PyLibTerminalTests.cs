// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Xunit;

namespace Typhon.Conformance.Tests;

public sealed class PyLibTerminalTests
{
    static bool LinuxTerminal => OperatingSystem.IsLinux() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is
            System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64;

    [Fact]
    public void PyLibTerminalModes()
    {
        if (!LinuxTerminal) return;
        Compare("terminal_modes", """
            import os
            import pty
            import termios
            import tty

            print(tty.__file__ is not None)
            master, slave = pty.openpty()
            try:
                original = termios.tcgetattr(slave)
                print(os.isatty(slave), len(original), len(original[6]))
                before_raw = tty.setraw(slave)
                raw = termios.tcgetattr(slave)
                print(before_raw == original, bool(raw[3] & termios.ICANON), bool(raw[3] & termios.ECHO))
                print(raw[6][termios.VMIN], raw[6][termios.VTIME])
                termios.tcsetattr(slave, termios.TCSANOW, original)
                before_break = tty.setcbreak(slave, termios.TCSANOW)
                cbreak = termios.tcgetattr(slave)
                print(before_break == original, bool(cbreak[3] & termios.ICANON), bool(cbreak[3] & termios.ECHO))
                print(bool(cbreak[0] & termios.ICRNL) == bool(original[0] & termios.ICRNL))
                print(termios.tcsetwinsize(slave, (34, 99)), termios.tcgetwinsize(slave))
                termios.tcsetattr(slave, termios.TCSANOW, original)
                print(termios.tcgetattr(slave) == original)
            finally:
                os.close(slave)
                os.close(master)
            """);
    }

    [Fact]
    public void PyLibGetpass()
    {
        if (!LinuxTerminal && !OperatingSystem.IsWindows()) return;
        Compare("getpass", """
            import getpass
            import io
            import os
            import sys
            import warnings

            print(getpass.__file__ is not None)
            print(getpass.getpass.__name__)
            os.environ['LOGNAME'] = 'terminal-test-user'
            print(getpass.getuser())

            original = sys.stdin
            try:
                sys.stdin = io.StringIO('first\n')
                stream = io.StringIO()
                with warnings.catch_warnings(record=True) as messages:
                    warnings.simplefilter('always')
                    result = getpass.fallback_getpass('Prompt: ', stream)
                    print(result == 'first', len(messages), messages[0].category.__name__)
                    print(repr(stream.getvalue()))

                sys.stdin = io.StringIO('ab\bcd\n')
                stream = io.StringIO()
                with warnings.catch_warnings(record=True) as messages:
                    warnings.simplefilter('always')
                    result = getpass.fallback_getpass('Masked: ', stream, echo_char='*')
                    print(result == 'acd', len(messages), messages[0].category.__name__)
                    print(repr(stream.getvalue()))
            finally:
                sys.stdin = original

            for bad_mask in ('\n', '**'):
                try:
                    getpass.getpass(echo_char=bad_mask)
                except ValueError as error:
                    print(str(error))
            """);
    }

    [Fact]
    public void PyLibGetpassRestoresTerminalEcho()
    {
        if (!LinuxTerminal || Oracle() is not { } oracle) return;
        var (python, lib) = oracle;
        string dir = Directory.CreateTempSubdirectory("typhon-terminal-echo-").FullName;
        try
        {
            string entry = Path.Combine(dir, "uses_getpass.ty");
            string check = Path.Combine(dir, "check_terminal.py");
            File.WriteAllText(entry, $"#:pylib {lib}\n" + """
                import getpass
                print(getpass.getpass('Prompt: ') == 'synthetic-input')
                """);
            File.WriteAllText(check, """
                import os
                import subprocess
                import sys
                import termios
                import time

                entry, cli = sys.argv[1:]
                for command in ([sys.executable, entry], ['dotnet', cli, 'run', entry, '--no-cache', '--no-server']):
                    master, slave = os.openpty()
                    initial = termios.tcgetattr(slave)
                    process = subprocess.Popen(command, stdin=slave, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                               start_new_session=True)
                    try:
                        end = time.monotonic() + 45
                        while termios.tcgetattr(slave)[3] & termios.ECHO:
                            if process.poll() is not None or time.monotonic() > end:
                                raise RuntimeError(f'echo was never disabled: {process.communicate(timeout=3)}')
                            time.sleep(.03)
                        os.write(master, b'synthetic-input\n')
                        stdout, stderr = process.communicate(timeout=12)
                        assert process.returncode == 0, (stdout, stderr)
                        assert stdout == b'True\n', stdout
                        assert not b'synthetic-input' in stderr, stderr
                        after = termios.tcgetattr(slave)
                        assert after == initial, [(i, a, b) for i, (a, b) in enumerate(zip(initial, after)) if a != b]
                        print('echo disabled during input, settings restored after read')
                    finally:
                        if process.poll() is None:
                            process.kill()
                            process.wait()
                        os.close(slave)
                        os.close(master)
                """);
            var run = Suite.Run(python, [check, entry, Suite.Cli], TimeSpan.FromMinutes(2));
            Assert.True(run.ExitCode == 0, run.StdErr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static (string Python, string Lib)? Oracle()
    {
        string? python = Suite.FindPython(out string why);
        if (python == null) { Console.WriteLine($"PyLibTerminal skipped: {why}"); return null; }
        var oracle = Suite.Run(python,
            ["-c", "import sys, sysconfig; print(sysconfig.get_path('stdlib') if sys.version_info[:2] == (3, 15) else '')"],
            TimeSpan.FromSeconds(30));
        Assert.True(oracle.ExitCode == 0, oracle.StdErr);
        string lib = oracle.StdOut.Trim();
        if (lib.Length == 0) { Console.WriteLine("PyLibTerminal requires the CPython 3.15 library"); return null; }
        return (python, lib);
    }

    static void Compare(string name, string source)
    {
        if (Oracle() is not { } oracle) return;
        var (python, lib) = oracle;
        string dir = Directory.CreateTempSubdirectory("typhon-terminal-").FullName;
        try
        {
            string script = Path.Combine(dir, "uses_" + name + ".ty");
            File.WriteAllText(script, $"#:pylib {lib}\n" + source);
            var expected = Suite.Run(python, [script], TimeSpan.FromMinutes(1));
            Assert.True(expected.ExitCode == 0, expected.StdErr);
            var run = Suite.Run("dotnet", [Suite.Cli, "run", script, "--no-cache", "--no-server"], TimeSpan.FromMinutes(3));
            Assert.True(run.ExitCode == 0, run.StdErr);
            Assert.Equal(Suite.Normalize(expected.StdOut), Suite.Normalize(run.StdOut));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
