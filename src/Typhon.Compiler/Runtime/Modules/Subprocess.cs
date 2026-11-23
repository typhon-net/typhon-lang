// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Typhon.Runtime;

    internal static class subprocess
    {
        public const long PIPE = -1, STDOUT = -2, DEVNULL = -3;

        public class SubprocessError : PyExceptionBase
        {
            public SubprocessError(string message) : base(message) { }
        }

        public sealed class CalledProcessError : SubprocessError
        {
            public long returncode { get; }
            public object cmd { get; }
            public object? output { get; }
            public object? stdout => output;
            public object? stderr { get; }

            public CalledProcessError(long returncode, object cmd, object? output = null, object? stderr = null)
                : base($"Command {Builtins.repr(cmd)} returned non-zero exit status {returncode}.")
            {
                this.returncode = returncode;
                this.cmd = cmd;
                this.output = output;
                this.stderr = stderr;
            }
        }

        public sealed class TimeoutExpired : SubprocessError
        {
            public object cmd { get; }
            public double timeout { get; }
            public TimeoutExpired(object cmd, double timeout)
                : base($"Command {Builtins.repr(cmd)} timed out after {timeout} seconds")
            {
                this.cmd = cmd;
                this.timeout = timeout;
            }
        }

        public sealed class CompletedProcess(object args, long returncode, object? stdout = null, object? stderr = null)
        {
            public object args { get; } = args;
            public long returncode { get; } = returncode;
            public dynamic? stdout { get; } = stdout;
            public dynamic? stderr { get; } = stderr;

            public void check_returncode()
            {
                if (returncode != 0) throw new CalledProcessError(returncode, args, stdout, stderr);
            }
        }

        public sealed class Popen : IDisposable
        {
            readonly Process _process;
            readonly bool _captureOutput, _captureError, _text;
            readonly string _encoding, _errors;
            Task<byte[]?>? _output, _error;
            Task? _input;

            public object args { get; }
            public dynamic? stdin { get; }
            public dynamic? stdout { get; }
            public dynamic? stderr { get; }
            public long pid => _process.Id;
            public long? returncode => poll();

            public Popen(object args, [PyKeywordOnly] object? stdin = null, [PyKeywordOnly] object? stdout = null,
                [PyKeywordOnly] object? stderr = null, [PyKeywordOnly] object? cwd = null,
                [PyKeywordOnly] IDictionary? env = null, [PyKeywordOnly] bool text = false,
                [PyKeywordOnly] string? encoding = null, [PyKeywordOnly] string? errors = null,
                [PyKeywordOnly] bool close_fds = true, [PyKeywordOnly] object? executable = null,
                [PyKeywordOnly] long bufsize = -1, [PyKeywordOnly] bool shell = false,
                [PyKeywordOnly] bool start_new_session = false)
            {
                this.args = args;
                string[] command = args is string or PyBytes ? [os_.__path(args)] :
                    Builtins.__iter_dyn(args).Select(item => os_.__path((object)item)).ToArray();
                if (command.Length == 0) throw new ArgumentException("args must contain an executable");
                if (stderr != null && Builtins.eq(stderr, STDOUT))
                    throw new NotImplementedException("redirecting stderr to stdout requires a shared child pipe");
                _captureOutput = stdout != null && Builtins.eq(stdout, PIPE);
                _captureError = stderr != null && Builtins.eq(stderr, PIPE);
                _text = text || encoding != null || errors != null;
                _encoding = encoding ?? "utf-8";
                _errors = errors ?? "strict";
                string name = executable != null ? os_.__path(executable) : !shell ? command[0] :
                    OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ??
                        System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
                string? sessionLauncher = start_new_session && !OperatingSystem.IsWindows() ? SessionLauncher() : null;
                var start = new ProcessStartInfo(sessionLauncher ?? name)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = Redirects(stdin),
                    RedirectStandardOutput = Redirects(stdout),
                    RedirectStandardError = Redirects(stderr),
                };
                if (shell && OperatingSystem.IsWindows())
                    start.Arguments = "/c \"" + (command.Length == 1 ? command[0] : WindowsCommandLine(command)) + "\"";
                else
                {
                    if (sessionLauncher != null)
                    {
                        start.ArgumentList.Add("--");
                        start.ArgumentList.Add(name);
                    }
                    if (shell) start.ArgumentList.Add("-c");
                    foreach (string token in shell ? command : command.Skip(1))
                        start.ArgumentList.Add(token);
                }
                if (cwd != null) start.WorkingDirectory = os_.__path(cwd);
                if (env != null)
                {
                    start.Environment.Clear();
                    foreach (DictionaryEntry variable in env)
                        start.Environment.Add((string)variable.Key, (string?)variable.Value);
                }
                try { _process = Process.Start(start) ?? throw new IOException($"could not start '{start.FileName}'"); }
                catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
                {
                    throw new FileNotFoundException($"No such file or directory: {Builtins.repr(start.FileName)}", start.FileName, e);
                }
                if (stdin != null && Builtins.eq(stdin, DEVNULL))
                {
                    _process.StandardInput.Close();
                    _input = Task.CompletedTask;
                }
                if (start.RedirectStandardOutput && !_captureOutput)
                    _output = Read(_process.StandardOutput.BaseStream, capture: false);
                if (start.RedirectStandardError && !_captureError)
                    _error = Read(_process.StandardError.BaseStream, capture: false);
                if (stdin != null && Builtins.eq(stdin, PIPE)) this.stdin = Port(_process.StandardInput.BaseStream, "wb", bufsize);
                if (_captureOutput) this.stdout = Port(_process.StandardOutput.BaseStream, "rb", bufsize);
                if (_captureError) this.stderr = Port(_process.StandardError.BaseStream, "rb", bufsize);
            }

            static string WindowsCommandLine(string[] arguments)
            {
                var command = new System.Text.StringBuilder();
                foreach (string arg in arguments)
                {
                    if (command.Length != 0) command.Append(' ');
                    bool quote = arg.Length == 0 || arg.Contains(' ') || arg.Contains('\t');
                    if (quote) command.Append('"');
                    int slashes = 0;
                    foreach (char ch in arg)
                    {
                        if (ch == '\\') { slashes++; continue; }
                        command.Append('\\', ch == '"' ? 2 * slashes + 1 : slashes);
                        command.Append(ch);
                        slashes = 0;
                    }
                    command.Append('\\', quote ? 2 * slashes : slashes);
                    if (quote) command.Append('"');
                }
                return command.ToString();
            }

            io.IOBase Port(Stream stream, string mode, long bufferSize)
            {
                var binary = new PyBinaryFile(stream, "", mode, closefd: true);
                return _text ? new io.TextIOWrapper(binary, _encoding, _errors, line_buffering: bufferSize == 1) : binary;
            }

            static bool Redirects(object? descriptor)
            {
                if (descriptor == null) return false;
                if (Builtins.eq(descriptor, PIPE) || Builtins.eq(descriptor, DEVNULL)) return true;
                throw new ArgumentException("stdio arguments must be PIPE, DEVNULL, or None");
            }

            public long? poll() => _process.HasExited ? _process.ExitCode : null;
            public void kill() { if (!_process.HasExited) _process.Kill(); }

            public long wait(double? timeout = null)
            {
                Await(_process.WaitForExitAsync(), timeout);
                return _process.ExitCode;
            }

            void Await(Task task, double? timeout)
            {
                try
                {
                    if (timeout is double seconds)
                        task.WaitAsync(TimeSpan.FromSeconds(Math.Max(0, seconds))).GetAwaiter().GetResult();
                    else task.GetAwaiter().GetResult();
                }
                catch (TimeoutException) when (timeout.HasValue) { throw new TimeoutExpired(args, timeout.Value); }
            }

            public (object?, object?) communicate(object? input = null, double? timeout = null)
            {
                if (_input != null && input != null) throw new ArgumentException("cannot send input after starting communication");
                if (input != null && !_process.StartInfo.RedirectStandardInput)
                    throw new ArgumentException("stdin must be PIPE for input");
                _output ??= Read(_process.StartInfo.RedirectStandardOutput ? _process.StandardOutput.BaseStream : null, _captureOutput);
                _error ??= Read(_process.StartInfo.RedirectStandardError ? _process.StandardError.BaseStream : null, _captureError);
                _input ??= _process.StartInfo.RedirectStandardInput && ((io.IOBase?)stdin)?.closed != true
                    ? Write(input) : Task.CompletedTask;
                Await(Task.WhenAll(_output, _error, _input, _process.WaitForExitAsync()), timeout);
                return (Decode(_output.Result), Decode(_error.Result));
            }

            static async Task<byte[]?> Read(Stream? stream, bool capture)
            {
                if (stream == null) return null;
                if (!capture) { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); return null; }
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes).ConfigureAwait(false);
                return bytes.ToArray();
            }

            async Task Write(object? input)
            {
                using var stdin = _process.StandardInput;
                if (input == null) return;
                byte[] data = _text ? Builtins.encode((string)input, _encoding, _errors).Data : ByteOps.Buffer(input);
                try { await stdin.BaseStream.WriteAsync(data).ConfigureAwait(false); }
                catch (IOException) when (_process.HasExited) { }
            }

            object? Decode(byte[]? data)
            {
                if (data == null) return null;
                if (!_text) return new PyBytes(data);
                return ByteOps.Decode(data, _encoding, _errors).Replace("\r\n", "\n").Replace('\r', '\n');
            }

            public Popen __enter__() => this;

            public object? __exit__(object? type, object? error, object? traceback)
            {
                try
                {
                    ((io.IOBase?)stdin)?.close();
                    ((io.IOBase?)stdout)?.close();
                    ((io.IOBase?)stderr)?.close();
                }
                finally { wait(); }
                return null;
            }

            public void Dispose() => _process.Dispose();
        }

        static string SessionLauncher()
        {
            foreach (string path in new[] { "/usr/bin/setsid", "/bin/setsid" })
                if (System.IO.File.Exists(path)) return path;
            throw new PlatformNotSupportedException("starting a new process session requires the host's setsid command");
        }

        public static CompletedProcess run(object args, [PyKeywordOnly] object? stdin = null,
            [PyKeywordOnly] object? input = null, [PyKeywordOnly] object? stdout = null,
            [PyKeywordOnly] object? stderr = null, [PyKeywordOnly] bool capture_output = false,
            [PyKeywordOnly] bool check = false, [PyKeywordOnly] double? timeout = null, [PyKeywordOnly] object? cwd = null,
            [PyKeywordOnly] IDictionary? env = null, [PyKeywordOnly] bool text = false,
            [PyKeywordOnly] string? encoding = null, [PyKeywordOnly] string? errors = null,
            [PyKeywordOnly] bool shell = false, [PyKeywordOnly] bool start_new_session = false)
        {
            if (input != null)
            {
                if (stdin != null) throw new ArgumentException("stdin and input arguments may not both be used");
                stdin = PIPE;
            }
            if (capture_output)
            {
                if (stdout != null || stderr != null)
                    throw new ArgumentException("stdout and stderr arguments may not be used with capture_output");
                stdout = stderr = PIPE;
            }
            using var process = new Popen(args, stdin, stdout, stderr, cwd, env, text, encoding, errors, shell: shell, start_new_session: start_new_session);
            object? output, error;
            try { (output, error) = process.communicate(input, timeout); }
            catch (TimeoutExpired)
            {
                process.kill();
                process.wait();
                throw;
            }
            var completed = new CompletedProcess(args, process.returncode!.Value, output, error);
            if (check) completed.check_returncode();
            return completed;
        }

        public static long check_call(object args, [PyKeywordOnly] object? stdin = null,
            [PyKeywordOnly] object? stdout = null, [PyKeywordOnly] object? stderr = null,
            [PyKeywordOnly] double? timeout = null, [PyKeywordOnly] object? cwd = null,
            [PyKeywordOnly] IDictionary? env = null, [PyKeywordOnly] bool shell = false, [PyKeywordOnly] bool start_new_session = false) =>
            run(args, stdin: stdin, stdout: stdout, stderr: stderr, timeout: timeout, cwd: cwd, env: env,
                check: true, shell: shell, start_new_session: start_new_session).returncode;

        public static dynamic? check_output(object args, [PyKeywordOnly] object? stdin = null,
            [PyKeywordOnly] object? input = null, [PyKeywordOnly] object? stderr = null, [PyKeywordOnly] double? timeout = null,
            [PyKeywordOnly] object? cwd = null, [PyKeywordOnly] IDictionary? env = null, [PyKeywordOnly] bool text = false,
            [PyKeywordOnly] string? encoding = null, [PyKeywordOnly] string? errors = null,
            [PyKeywordOnly] bool shell = false, [PyKeywordOnly] bool start_new_session = false) =>
            run(args, stdin, input, PIPE, stderr, false, true, timeout, cwd, env, text, encoding, errors, shell, start_new_session).stdout;
    }
}
