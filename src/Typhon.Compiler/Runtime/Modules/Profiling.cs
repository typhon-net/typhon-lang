// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981 // Keep the Python package names.

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Typhon.Runtime;
    using Label = (string filename, long line, string name);

    internal static class profiling
    {
        public static class tracing
        {
            public static void run(object statement, string? filename = null, object? sort = null)
            {
                var profiler = new Profile();
                try { profiler.run(statement); }
                catch (SystemExit) { }
                if (filename == null) profiler.print_stats(sort);
                else profiler.dump_stats(filename);
            }

            public static void runctx(object statement, object? globals, object? locals, string? filename = null, object? sort = null)
            {
                var profiler = new Profile();
                try { profiler.runctx(statement, globals, locals); }
                catch (SystemExit) { }
                if (filename == null) profiler.print_stats(sort);
                else profiler.dump_stats(filename);
            }

            public static class __main__
            {
                public static void main()
                {
                    const string Usage = "Usage: cProfile.py [-o output_file_path] [-s sort] scriptfile [arg] ...";
                    var args = sys_.argv;
                    string? output = null, sort = null;
                    int index = 1;
                    while (index < args.Count && args[index].StartsWith('-'))
                    {
                        string option = args[index++];
                        if (option is "-h" or "--help") { Builtins.print(Usage); return; }
                        if (option == "--") break;
                        if (index == args.Count) throw new SystemExit(Usage);
                        string argument = args[index++];
                        if (option is "-o" or "--outfile") output = System.IO.Path.GetFullPath(argument);
                        else if (option is "-s" or "--sort") sort = argument;
                        else throw new SystemExit("unsupported profiling option " + option);
                    }
                    if (index == args.Count) { Builtins.print(Usage); throw new SystemExit(2L); }
                    string path = args[index];
                    sys_.argv = args.Skip(index).ToList();
                    var globals = new OrderedDictionary<string, object?>
                    {
                        ["__name__"] = "__main__", ["__file__"] = path, ["__package__"] = null, ["__spec__"] = null,
                    };
                    var source = Builtins.compile(File.ReadAllText(path), path, "exec");
                    runctx(source, globals, globals, output, sort);
                }
            }

            public class Profile : PyProfiling.IProfileObserver
            {
                readonly object? _timer;
                readonly double _timeunit;
                bool _subcalls;
                readonly Dictionary<Label, Counter> _counts = new();
                Sample? _current;

                public OrderedDictionary<Label, (long, long, double, double, OrderedDictionary<Label, (long, long, double, double)>)> stats = new();

                public Profile(object? timer = null, double? timeunit = null, bool subcalls = true, bool builtins = true)
                { _timer = timer; _timeunit = timeunit ?? 0; _subcalls = subcalls; }

                public void enable(bool? subcalls = null, bool? builtins = null)
                {
                    if (subcalls != null) _subcalls = subcalls.Value;
                    if (PyProfiling.Observer != null && !ReferenceEquals(PyProfiling.Observer, this))
                        throw new InvalidOperationException("Another profiling tool is already active");
                    PyProfiling.Observer = this;
                }

                public void disable()
                {
                    if (!ReferenceEquals(PyProfiling.Observer, this)) return;
                    PyProfiling.Observer = null;
                    if (_current == null) return;
                    double end = Now();
                    while (_current != null) Leave(_current, end);
                }

                public void clear() { disable(); _counts.Clear(); stats.Clear(); }

                public Profile __enter__() { enable(); return this; }
                public bool __exit__(object? exceptionType, object? exception, object? traceback) { disable(); return false; }

                public dynamic? runcall(object func, params object?[] args)
                {
                    enable();
                    try { return Builtins.__invoke(func, args); }
                    finally { disable(); }
                }

                public Profile run(object cmd)
                {
                    var globals = new OrderedDictionary<string, object?>();
                    if (sys_.modules.TryGetValue("__main__", out var main) && main is PyModule module)
                        foreach (var (name, value) in module.__dict__) globals[name] = value;
                    return runctx(cmd, globals, globals);
                }

                public Profile runctx(object cmd, object? globals, object? locals)
                {
                    enable();
                    try { Builtins.exec(cmd, globals, locals); }
                    finally { disable(); }
                    return this;
                }

                double Now() => _timer == null ? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency :
                    Convert.ToDouble((object?)Builtins.__invoke(_timer, []), CultureInfo.InvariantCulture) * (_timeunit > 0 ? _timeunit : 1.0);

                static Label Key(sys_.code code) => (code.co_filename, code.co_firstlineno, code.co_name);

                object PyProfiling.IProfileObserver.Enter(sys_.frame frame)
                {
                    var key = Key(frame.f_code);
                    if (!_counts.TryGetValue(key, out var counter)) _counts.Add(key, counter = new Counter(frame.f_code));
                    Totals? edge = null;
                    if (_subcalls && _current != null && !_current.Counter.Edges.TryGetValue(counter, out edge))
                        _current.Counter.Edges.Add(counter, edge = new Totals());
                    double start = Now();
                    counter.Times.Enter();
                    edge?.Enter();
                    return _current = new Sample(counter, edge, _current, start);
                }

                void PyProfiling.IProfileObserver.Leave(object? state)
                {
                    if (state is Sample { Active: true } sample) Leave(sample, Now());
                }

                void Leave(Sample sample, double now)
                {
                    sample.Active = false;
                    _current = sample.Parent;
                    double duration = now - sample.Start, own = duration - sample.Children;
                    sample.Counter.Times.Leave(duration, own);
                    sample.Edge?.Leave(duration, own);
                    if (sample.Parent != null) sample.Parent.Children += duration;
                }

                public List<ProfileEntry> getstats() => _counts.Values.Select(counter => new ProfileEntry(counter)).ToList();

                public void create_stats() { disable(); snapshot_stats(); }

                public void snapshot_stats()
                {
                    stats = new();
                    foreach (var (key, count) in _counts)
                        stats[key] = (count.Times.Calls - count.Times.Recursive, count.Times.Calls, count.Times.Own, count.Times.Total, new());
                    foreach (var (caller, count) in _counts)
                        foreach (var (callee, times) in count.Edges)
                            stats[Key(callee.Code)].Item5[caller] = (times.Calls, times.Calls - times.Recursive, times.Own, times.Total);
                }

                public void dump_stats(string file)
                {
                    create_stats();
                    using var output = Builtins.open_binary(file, "wb");
                    marshal.dump(stats, output);
                }

                public void print_stats(object? sort = null)
                {
                    create_stats();
                    IEnumerable<KeyValuePair<Label, Counter>> rows = sort switch
                    {
                        "calls" or "ncalls" => _counts.OrderByDescending(row => row.Value.Times.Calls),
                        "time" or "tottime" => _counts.OrderByDescending(row => row.Value.Times.Own),
                        "cumtime" or "cumulative" => _counts.OrderByDescending(row => row.Value.Times.Total),
                        _ => _counts.OrderBy(row => row.Key.filename, StringComparer.Ordinal).ThenBy(row => row.Key.line).ThenBy(row => row.Key.name, StringComparer.Ordinal),
                    };
                    long calls = _counts.Values.Sum(c => c.Times.Calls);
                    long primitive = _counts.Values.Sum(c => c.Times.Calls - c.Times.Recursive);
                    double seconds = _counts.Values.Sum(c => c.Times.Own);
                    string recursion = calls == primitive ? "" : $" ({primitive} primitive calls)";
                    Builtins.print(FormattableString.Invariant($"         {calls} function calls{recursion} in {seconds:F3} seconds\n"));
                    Builtins.print("   ncalls  tottime  percall  cumtime  percall filename:lineno(function)");
                    foreach (var (key, counter) in rows)
                    {
                        var times = counter.Times;
                        long count = times.Calls, nonrecursive = count - times.Recursive;
                        string label = count == nonrecursive ? count.ToString(CultureInfo.InvariantCulture) : $"{count}/{nonrecursive}";
                        double ownAverage = count > 0 ? times.Own / count : 0;
                        double totalAverage = nonrecursive > 0 ? times.Total / nonrecursive : 0;
                        Builtins.print(FormattableString.Invariant(
                            $"{label,9}{times.Own,9:F3}{ownAverage,9:F3}{times.Total,9:F3}{totalAverage,9:F3} {System.IO.Path.GetFileName(key.filename)}:{key.line}({key.name})"));
                    }
                }

                internal sealed class Counter(sys_.code code)
                {
                    public readonly sys_.code Code = code;
                    public readonly Totals Times = new();
                    public readonly Dictionary<Counter, Totals> Edges = new();
                }

                internal sealed class Totals
                {
                    int _depth;
                    public long Calls, Recursive;
                    public double Total, Own;
                    public void Enter() { Calls++; if (_depth++ > 0) Recursive++; }
                    public void Leave(double total, double own) { if (--_depth == 0) Total += total; Own += own; }
                }

                sealed class Sample(Counter counter, Totals? edge, Sample? parent, double start)
                {
                    public readonly Counter Counter = counter;
                    public readonly Totals? Edge = edge;
                    public readonly Sample? Parent = parent;
                    public readonly double Start = start;
                    public double Children;
                    public bool Active = true;
                }

                internal class ProfileSubEntry : PyStructSeq
                {
                    public readonly sys_.code code;
                    public readonly long callcount, reccallcount;
                    public readonly double totaltime, inlinetime;
                    internal ProfileSubEntry(sys_.code key, Totals counts)
                    {
                        code = key; callcount = counts.Calls; reccallcount = counts.Recursive;
                        totaltime = counts.Total; inlinetime = counts.Own;
                    }
                    protected override string TypeName => "profiler_subentry";
                    protected override string[] Fields => ["code", "callcount", "reccallcount", "totaltime", "inlinetime"];
                    protected override object?[] Values => [code, callcount, reccallcount, totaltime, inlinetime];
                }

                internal sealed class ProfileEntry : ProfileSubEntry
                {
                    public readonly List<ProfileSubEntry>? calls;
                    internal ProfileEntry(Counter counter) : base(counter.Code, counter.Times) =>
                        calls = counter.Edges.Count == 0 ? null : counter.Edges.Select(pair => new ProfileSubEntry(pair.Key.Code, pair.Value)).ToList();
                    protected override string TypeName => "profiler_entry";
                    protected override string[] Fields => [.. base.Fields, "calls"];
                    protected override object?[] Values => [.. base.Values, calls];
                }
            }
        }
    }
}
