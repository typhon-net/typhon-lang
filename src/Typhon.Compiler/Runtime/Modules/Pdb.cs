// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Typhon.Runtime;

    /// <summary>Interactive inspection of a stopped Typhon frame and its callers.</summary>
    internal static class pdb
    {
        sealed class BdbQuit : Exception { }

        internal class Pdb
        {
            public dynamic? stdin;
            public dynamic? stdout;
            public virtual dynamic use_rawinput { get; set; }
            public bool nosigint;
            public string prompt = "(Pdb) ";
            public string completekey;
            public string? mode;
            public bool colorize;
            public readonly List<string> cmdqueue = new();

            public readonly List<(sys_.frame, long)> stack = new();
            public long curindex { get; private set; }
            public sys_.frame? curframe => Selected is { } selected ? selected.Frame : null;
            (sys_.frame Frame, long Line)? Selected =>
                curindex >= 0 && curindex < stack.Count ? stack[(int)curindex] : null;

            readonly List<string> _rcLines = new();
            readonly string[] _skip;
            int _nextSourceLine;
            string? _lastCommand;
            bool _continue;
            bool _debugFirstLine;
            object? _traceHook;
            object? _savedTrace;

            public Pdb(string completekey = "tab", object? stdin = null, object? stdout = null, object? skip = null,
                bool nosigint = false, bool readrc = true, string? mode = null, string? backend = null, bool colorize = false)
            {
                if (backend != null && backend != "settrace")
                    throw new NotSupportedException($"unsupported debugger backend: {backend}");
                this.stdin = stdin ?? (object?)sys_.stdin;
                this.stdout = stdout ?? (object?)sys_.stdout;
                this.completekey = completekey;
                _skip = skip == null ? Array.Empty<string>() : Builtins.__iter_dyn(skip).Cast<string>().ToArray();
                use_rawinput = stdin == null || ReferenceEquals(stdin, (object?)sys_.stdin);
                this.nosigint = nosigint;
                this.mode = mode;
                this.colorize = colorize && (bool)_colorize.can_colorize(file: (object?)this.stdout);
                if (readrc)
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string homeRc = System.IO.Path.Combine(home, ".pdbrc");
                    string localRc = System.IO.Path.GetFullPath(".pdbrc");
                    ReadRc(homeRc);
                    if (localRc != homeRc) ReadRc(localRc);
                }
            }

            void ReadRc(string filename)
            {
                try
                {
                    foreach (string line in File.ReadLines(filename))
                    {
                        string command = line.Trim();
                        if (command.Length != 0 && !command.StartsWith('#')) _rcLines.Add(command);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            public virtual void reset()
            {
                stack.Clear();
                curindex = 0;
                _nextSourceLine = 0;
                _lastCommand = null;
                _continue = false;
            }

            public virtual void set_trace(dynamic? frame = null, dynamic? commands = null) =>
                Stop((object?)frame, (object?)commands);

            public static void set_trace(Pdb self, object? frame = null, object? commands = null) =>
                self.Stop(frame, commands);

            void Stop(object? frame, object? commands)
            {
                if (frame != null && frame is not sys_.frame)
                    throw new InvalidCastException("frame must be a frame object");
                sys_.frame stopped = (sys_.frame?)frame ?? sys_._getframe();
                reset();
                for (sys_.frame? item = stopped; item != null; item = item.f_back)
                    stack.Add((item, item.f_lineno));
                stack.Reverse();
                curindex = stack.Count - 1;
                Interact(commands);
            }

            public virtual void set_continue() => Resume();
            public static void set_continue(Pdb self) => self.Resume();

            void Resume()
            {
                _continue = true;
                _debugFirstLine = false;
                if (_traceHook != null && ReferenceEquals(_traceHook, sys_.gettrace()))
                    sys_.settrace(_savedTrace);
            }

            public virtual dynamic? trace_dispatch(params object?[] args) => Dispatch(args);
            public static dynamic? trace_dispatch(Pdb self, params object?[] args) => self.Dispatch(args);

            object? Dispatch(object?[] args)
            {
                if (args.Length != 3 || args[0] is not sys_.frame frame || args[1] is not string reason)
                    throw new InvalidCastException("trace_dispatch() requires a frame, an event name and an argument");
                string? module = frame.f_globals["__name__"] as string;
                if (_debugFirstLine && reason == "line" &&
                    (module == null || !_skip.Any(pattern => fnmatch_.fnmatchcase(module, pattern))))
                {
                    _debugFirstLine = false;
                    Stop(frame, null);
                }
                return _continue ? null : _traceHook;
            }

            public void run(object cmd, object? globals = null, object? locals = null)
            {
                _ = WithTracing(() =>
                {
                    Builtins.exec(cmd, globals, locals);
                    return null;
                });
            }

            public dynamic? runeval(object expr, object? globals = null, object? locals = null) =>
                WithTracing(() => (object?)Builtins.eval(expr, globals, locals));

            public dynamic? runcall(object func, params object?[] args) =>
                WithTracing(() => (object?)Builtins.__invoke(func, args));

            object? WithTracing(Func<object?> execute)
            {
                reset();
                _debugFirstLine = true;
                _savedTrace = sys_.gettrace();
                _traceHook = new PyBoundMethod(this, "trace_dispatch");
                sys_.settrace(_traceHook);
                try { return execute(); }
                catch (BdbQuit) { return null; }
                finally
                {
                    if (ReferenceEquals(_traceHook, sys_.gettrace())) sys_.settrace(_savedTrace);
                    _traceHook = null;
                    _savedTrace = null;
                    _debugFirstLine = false;
                }
            }

            internal void PostMortem(types.TracebackType traceback)
            {
                reset();
                for (types.TracebackType? item = traceback; item != null; item = item.tb_next)
                    stack.Add((item.tb_frame, item.tb_lineno));
                curindex = stack.Count - 1;
                try { Interact(null); }
                catch (BdbQuit) { }
            }

            public virtual void interaction(object? frame, object? tb_or_exc)
            {
                if (tb_or_exc != null)
                {
                    var traceback = TracebackOf(tb_or_exc) ?? throw new ArgumentException("no traceback to debug");
                    PostMortem(traceback);
                }
                else Stop(frame, null);
            }

            void Interact(object? commands)
            {
                var pending = new Queue<string>(_rcLines.Concat(cmdqueue));
                _rcLines.Clear();
                cmdqueue.Clear();
                if (commands != null)
                    foreach (object? command in Builtins.__iter_dyn(commands))
                        pending.Enqueue(command as string ?? throw new InvalidCastException("debugger commands must be strings"));
                bool locationShown = false;
                while (!_continue)
                {
                    if (pending.TryDequeue(out var queued))
                    {
                        onecmd(queued);
                        continue;
                    }
                    if (!locationShown && Selected is { } selected)
                    {
                        PrintFrame(selected.Frame, selected.Line, marked: true);
                        locationShown = true;
                    }
                    Write(prompt, end: "", flush: true);
                    var line = ReadLine();
                    if (line == null)
                    {
                        Resume();
                        return;
                    }
                    onecmd(line);
                }
            }

            string? ReadLine()
            {
                object? input = Builtins.truthy((object?)use_rawinput) ? (object?)sys_.stdin : (object?)stdin;
                if (input is TextReader reader) return reader.ReadLine();
                if (input == null) return null;
                string text = input is io.TextIOBase file ? file.readline() : (string)((dynamic)input).readline();
                return text.Length == 0 ? null : text.TrimEnd('\r', '\n');
            }

            public virtual bool onecmd(string line)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) trimmed = _lastCommand ?? "";
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) return _continue;
                _lastCommand = trimmed;
                int split = trimmed.IndexOfAny(new[] { ' ', '\t' });
                string command = split < 0 ? trimmed : trimmed[..split];
                string argument = split < 0 ? "" : trimmed[(split + 1)..].Trim();
                switch (command)
                {
                    case "c": case "cont": case "continue":
                        set_continue();
                        break;
                    case "q": case "quit": case "exit":
                        Resume();
                        throw new BdbQuit();
                    case "w": case "where": case "bt":
                        for (int i = 0; i < stack.Count; i++) PrintFrame(stack[i].Item1, stack[i].Item2, marked: i == curindex);
                        break;
                    case "u": case "up":
                        SelectFrame(argument, older: true);
                        break;
                    case "d": case "down":
                        SelectFrame(argument, older: false);
                        break;
                    case "l": case "list":
                        ListSource(argument);
                        break;
                    case "p": case "pp":
                        Evaluate(argument);
                        break;
                    case "h": case "help":
                        if (argument.Length == 0)
                            Write("Supported commands: where, up, down, list, p, continue, quit");
                        else Error($"no debugger help for {Builtins.repr(argument)}");
                        break;
                    default:
                        Error($"unsupported debugger command: {command}");
                        break;
                }
                return _continue;
            }

            void SelectFrame(string argument, bool older)
            {
                if (stack.Count == 0) { Error("No stack frame"); return; }
                if (older ? curindex == 0 : curindex == stack.Count - 1)
                {
                    Error(older ? "Oldest frame" : "Newest frame");
                    return;
                }
                int count = 1;
                if (argument.Length != 0 && !int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                {
                    Error($"Invalid frame count ({argument})");
                    return;
                }
                long offset = count < 0 ? stack.Count : count;
                curindex = Math.Clamp(curindex + (older ? -offset : offset), 0, stack.Count - 1);
                _nextSourceLine = 0;
                var selected = stack[(int)curindex];
                PrintFrame(selected.Item1, selected.Item2, marked: true);
            }

            void ListSource(string argument)
            {
                if (Selected is not { } selected) { Error("No stack frame"); return; }
                string filename = selected.Frame.f_code.co_filename;
                var lines = linecache.getlines(filename, selected.Frame.f_globals);
                int current = checked((int)selected.Line);
                int first = _nextSourceLine == 0 ? Math.Max(1, current - 5) : _nextSourceLine;
                int last = first + 10;
                if (argument.Length != 0 && argument != ".")
                {
                    string[] fields = argument.Split(',');
                    if (fields.Length > 2 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out first))
                    {
                        Error("Invalid source line range");
                        return;
                    }
                    if (fields.Length == 2)
                    {
                        if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out last))
                        {
                            Error("Invalid source line range");
                            return;
                        }
                        if (last < first) last += first;
                    }
                    else
                    {
                        first = Math.Max(1, first - 5);
                        last = first + 10;
                    }
                }
                if (argument == ".") { first = Math.Max(1, current - 5); last = first + 10; }
                for (int i = Math.Max(1, first); i <= Math.Min(last, lines.Count); i++)
                {
                    string marker = i == current ? "->" : "  ";
                    Write(FormattableString.Invariant($"{i,3}\t{marker}\t{((string)lines[i - 1]).TrimEnd('\r', '\n')}"));
                }
                _nextSourceLine = Math.Min(last, lines.Count) + 1;
                if (last > lines.Count) Write("[EOF]");
            }

            void Evaluate(string expression)
            {
                if (curframe is not { } frame) { Error("No stack frame"); return; }
                try
                {
                    var value = (object?)Builtins.eval(expression, frame.f_globals, frame.f_locals);
                    Write(Builtins.repr(value));
                }
                catch (Exception error)
                {
                    Error($"{Builtins.__name_of(error.GetType())}: {error.Message}");
                }
            }

            void PrintFrame(sys_.frame frame, long line, bool marked)
            {
                string file = frame.f_code.co_filename;
                string prefix = marked ? "> " : "  ";
                Write(FormattableString.Invariant($"{prefix}{file}({line}){frame.f_code.co_name}()"));
                string source = linecache.getline(file, line, frame.f_globals).Trim();
                if (source.Length != 0) Write("-> " + source);
            }

            public void message(string text) => Write(text);
            public void error(string text) => Error(text);
            void Error(string text) => Write(colorize
                ? _colorize.ANSIColors.BOLD_RED + "*** " + text + _colorize.ANSIColors.RESET
                : "*** " + text);

            void Write(string text, string end = "\n", bool flush = false) =>
                Builtins.print_kw(new object?[] { text }, end: end, file: (object?)stdout, flush: flush);
        }

        // This is a module attribute so libraries can replace it while providing their own debugger streams.
        public static dynamic set_trace = new PyClosure("set_trace", (args, keywords) =>
        {
            if (args.Length != 0) throw new ArgumentException("set_trace() takes keyword-only arguments");
            foreach (string name in keywords?.Keys ?? (IEnumerable<string>)Array.Empty<string>())
                if (name is not ("header" or "commands"))
                    throw new ArgumentException($"set_trace() got an unexpected keyword argument {Builtins.repr(name)}");
            var debugger = new Pdb();
            if (keywords != null && keywords.TryGetValue("header", out var header) && header != null)
                debugger.message(Builtins.str(header));
            object? commands = null;
            keywords?.TryGetValue("commands", out commands);
            debugger.set_trace(commands: commands);
            return null;
        });

        public static void run(object statement, object? globals = null, object? locals = null) =>
            new Pdb().run(statement, globals, locals);

        public static dynamic? runeval(object expression, object? globals = null, object? locals = null) =>
            new Pdb().runeval(expression, globals, locals);

        public static dynamic? runcall(object func, params object?[] args) => new Pdb().runcall(func, args);

        public static void post_mortem(object? t = null)
        {
            types.TracebackType? traceback = t == null ? sys_.exc_info().Item3 : TracebackOf(t);
            if (traceback == null) throw new ArgumentException("no traceback to debug");
            new Pdb().PostMortem(traceback);
        }

        static types.TracebackType? TracebackOf(object value) => value switch
        {
            Exception error => types.TracebackType.Of(error),
            types.TracebackType trace => trace,
            _ => throw new InvalidCastException("expected a traceback or exception"),
        };
    }
}
