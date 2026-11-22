// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    /// <summary>Sequence similarity, line deltas, and the context and unified text diff formats.</summary>
    internal static class difflib
    {
        public static bool IS_CHARACTER_JUNK(string ch) => IS_CHARACTER_JUNK(ch, " \t");
        public static bool IS_CHARACTER_JUNK(string ch, string ws) => ws.Contains(ch, StringComparison.Ordinal);
        public static bool IS_LINE_JUNK(string line) => line.Trim() is "" or "#";

        internal sealed class Match : ITuple, IReadOnlyList<long>, IPyRepr
        {
            public long a { get; }
            public long b { get; }
            public long size { get; }

            public Match(long a, long b, long size) { this.a = a; this.b = b; this.size = size; }

            public int Count => 3;
            public long this[long index] => index switch
            {
                0 or -3 => a,
                1 or -2 => b,
                2 or -1 => size,
                _ => throw new IndexOutOfRangeException(),
            };
            long IReadOnlyList<long>.this[int index] => this[index];
            int ITuple.Length => Count;
            object ITuple.this[int index] => this[index];
            public IEnumerator<long> GetEnumerator() { yield return a; yield return b; yield return size; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Deconstruct(out long a, out long b, out long size) { a = this.a; b = this.b; size = this.size; }
            public string __repr__() => FormattableString.Invariant($"Match(a={a}, b={b}, size={size})");
            public override string ToString() => __repr__();
        }

        internal enum Edit { Equal, Replace, Delete, Insert }

        internal readonly record struct Opcode(Edit Kind, int FromStart, int FromEnd, int ToStart, int ToEnd)
        {
            public (string, long, long, long, long) AsTuple() =>
                (Kind.ToString().ToLowerInvariant(), FromStart, FromEnd, ToStart, ToEnd);
        }

        static IEnumerable<string> Characters(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (char.IsSurrogatePair(text, index))
                {
                    yield return text.Substring(index, 2);
                    index++;
                }
                else yield return Builtins.CharStr(text[index]);
            }
        }

        static object?[] Elements(object sequence) => sequence is string text
            ? Characters(text).ToArray()
            : Builtins.__iter_dyn(sequence).ToArray();

        static string[] Lines(object sequence)
        {
            if (sequence is string) throw new InvalidCastException("input must be a sequence of strings, not str");
            return Builtins.__iter_dyn(sequence).Cast<object?>()
                .Select(line => line is string text ? text : throw new InvalidCastException(
                    $"lines to compare must be str, not {(line == null ? "NoneType" : Builtins.PyTypeName(line.GetType()))}"))
                .ToArray();
        }

        /// <summary>Matches contiguous runs, preferring the earliest starts on ties.</summary>
        internal sealed class SequenceMatcher
        {
            static readonly object NoneKey = new object();

            readonly Func<dynamic, bool>? _isJunk;
            readonly bool _autoJunk;
            readonly Dictionary<object, List<int>> _positions = new(PyKeyComparer.Instance);
            readonly HashSet<object> _junk = new(PyKeyComparer.Instance);
            object?[] _from = Array.Empty<object?>();
            object?[] _to = Array.Empty<object?>();
            Dictionary<object, int>? _frequencies;
            List<Match>? _matches;
            List<Opcode>? _edits;

            public object a { get; private set; } = "";
            public object b { get; private set; } = "";

            public SequenceMatcher(Func<dynamic, bool>? isjunk = null, object? a = null, object? b = null, bool autojunk = true)
            {
                _isJunk = isjunk;
                _autoJunk = autojunk;
                if (a != null) set_seq1(a);
                if (b != null) set_seq2(b);
            }

            public void set_seqs(object a, object b)
            {
                set_seq1(a);
                set_seq2(b);
            }

            public void set_seq1(object a)
            {
                if (ReferenceEquals(a, this.a)) return;
                _from = Elements(a);
                this.a = a;
                _matches = null;
                _edits = null;
            }

            public void set_seq2(object b)
            {
                if (ReferenceEquals(b, this.b)) return;
                _to = Elements(b);
                this.b = b;
                _matches = null;
                _edits = null;
                _frequencies = null;
                _positions.Clear();
                _junk.Clear();
                for (int j = 0; j < _to.Length; j++)
                {
                    object key = Key(_to[j]);
                    if (!_positions.TryGetValue(key, out var offsets)) _positions[key] = offsets = new List<int>();
                    offsets.Add(j);
                }
                if (_isJunk != null)
                    foreach (var key in _positions.Keys)
                        if (_isJunk(ReferenceEquals(key, NoneKey) ? null! : key)) _junk.Add(key);
                foreach (var key in _junk) _positions.Remove(key);
                if (_autoJunk && _to.Length >= 200)
                {
                    int limit = _to.Length / 100 + 1;
                    foreach (var key in _positions.Where(pair => pair.Value.Count > limit).Select(pair => pair.Key).ToList())
                        _positions.Remove(key);
                }
            }

            static object Key(object? value) => value ?? NoneKey;

            public Match find_longest_match(long alo = 0, long? ahi = null, long blo = 0, long? bhi = null) =>
                Longest(checked((int)alo), checked((int)(ahi ?? _from.Length)),
                    checked((int)blo), checked((int)(bhi ?? _to.Length)));

            Match Longest(int fromStart, int fromEnd, int toStart, int toEnd)
            {
                int bestFrom = fromStart, bestTo = toStart, bestLength = 0;
                var previous = new Dictionary<int, int>();
                var current = new Dictionary<int, int>();
                for (int i = fromStart; i < fromEnd; i++)
                {
                    current.Clear();
                    if (_positions.TryGetValue(Key(_from[i]), out var offsets))
                    {
                        foreach (int j in offsets)
                        {
                            if (j < toStart) continue;
                            if (j >= toEnd) break;
                            int length = 1 + previous.GetValueOrDefault(j - 1);
                            current.Add(j, length);
                            if (length <= bestLength) continue;
                            bestFrom = i - length + 1;
                            bestTo = j - length + 1;
                            bestLength = length;
                        }
                    }
                    (previous, current) = (current, previous);
                }

                // Popular entries cannot seed a run but can extend it. Junk can extend an already matched run.
                foreach (bool extendJunk in new[] { false, true })
                {
                    while (bestFrom > fromStart && bestTo > toStart &&
                        _junk.Contains(Key(_to[bestTo - 1])) == extendJunk &&
                        Builtins.eq(_from[bestFrom - 1], _to[bestTo - 1]))
                    {
                        bestFrom--;
                        bestTo--;
                        bestLength++;
                    }
                    while (bestFrom + bestLength < fromEnd && bestTo + bestLength < toEnd &&
                        _junk.Contains(Key(_to[bestTo + bestLength])) == extendJunk &&
                        Builtins.eq(_from[bestFrom + bestLength], _to[bestTo + bestLength]))
                        bestLength++;
                }
                return new Match(bestFrom, bestTo, bestLength);
            }

            public List<Match> get_matching_blocks()
            {
                if (_matches != null) return _matches;
                var ranges = new Stack<(int FromStart, int FromEnd, int ToStart, int ToEnd)>();
                var matches = new List<Match>();
                ranges.Push((0, _from.Length, 0, _to.Length));
                while (ranges.TryPop(out var range))
                {
                    var match = Longest(range.FromStart, range.FromEnd, range.ToStart, range.ToEnd);
                    if (match.size == 0) continue;
                    matches.Add(match);
                    int i = (int)match.a, j = (int)match.b, count = (int)match.size;
                    if (range.FromStart < i && range.ToStart < j)
                        ranges.Push((range.FromStart, i, range.ToStart, j));
                    if (i + count < range.FromEnd && j + count < range.ToEnd)
                        ranges.Push((i + count, range.FromEnd, j + count, range.ToEnd));
                }
                matches.Sort((left, right) => left.a.CompareTo(right.a));
                var merged = new List<Match>();
                foreach (var match in matches)
                {
                    if (merged.Count > 0 && merged[^1] is var previous &&
                        previous.a + previous.size == match.a && previous.b + previous.size == match.b)
                        merged[^1] = new Match(previous.a, previous.b, previous.size + match.size);
                    else merged.Add(match);
                }
                merged.Add(new Match(_from.Length, _to.Length, 0));
                return _matches = merged;
            }

            internal List<Opcode> Edits()
            {
                if (_edits != null) return _edits;
                var edits = new List<Opcode>();
                int i = 0, j = 0;
                foreach (var match in get_matching_blocks())
                {
                    int nextI = (int)match.a, nextJ = (int)match.b;
                    if (i < nextI || j < nextJ)
                    {
                        Edit kind = i == nextI ? Edit.Insert : j == nextJ ? Edit.Delete : Edit.Replace;
                        edits.Add(new Opcode(kind, i, nextI, j, nextJ));
                    }
                    i = nextI + (int)match.size;
                    j = nextJ + (int)match.size;
                    if (match.size != 0) edits.Add(new Opcode(Edit.Equal, nextI, i, nextJ, j));
                }
                return _edits = edits;
            }

            public List<(string, long, long, long, long)> get_opcodes() =>
                Edits().Select(change => change.AsTuple()).ToList();

            internal IEnumerable<List<Opcode>> Groups(int context)
            {
                var edits = Edits();
                if (edits.Count == 0) yield break;
                var group = new List<Opcode>();
                for (int index = 0; index < edits.Count; index++)
                {
                    var change = edits[index];
                    if (change.Kind == Edit.Equal)
                    {
                        if (index == 0) change = Tail(change, context);
                        if (index == edits.Count - 1) change = Head(change, context);
                        if (change.FromEnd - change.FromStart > 2L * context)
                        {
                            group.Add(Head(change, context));
                            yield return group;
                            group = new List<Opcode>();
                            change = Tail(change, context);
                        }
                    }
                    group.Add(change);
                }
                if (group.Count != 0 && (group.Count > 1 || group[0].Kind != Edit.Equal)) yield return group;
            }

            static Opcode Head(Opcode change, int count) => change with
            {
                FromEnd = change.FromStart + Math.Min(count, change.FromEnd - change.FromStart),
                ToEnd = change.ToStart + Math.Min(count, change.ToEnd - change.ToStart),
            };

            static Opcode Tail(Opcode change, int count) => change with
            {
                FromStart = change.FromEnd - Math.Min(count, change.FromEnd - change.FromStart),
                ToStart = change.ToEnd - Math.Min(count, change.ToEnd - change.ToStart),
            };

            public IEnumerable<List<(string, long, long, long, long)>> get_grouped_opcodes(long n = 3) =>
                Groups(checked((int)n)).Select(group => group.Select(change => change.AsTuple()).ToList());

            public double ratio() => Ratio(get_matching_blocks().Sum(match => match.size));

            public double quick_ratio()
            {
                if (_frequencies == null)
                {
                    _frequencies = new Dictionary<object, int>(PyKeyComparer.Instance);
                    foreach (var value in _to)
                    {
                        object key = Key(value);
                        _frequencies[key] = _frequencies.GetValueOrDefault(key) + 1;
                    }
                }
                var used = new Dictionary<object, int>(PyKeyComparer.Instance);
                int matches = 0;
                foreach (var value in _from)
                {
                    object key = Key(value);
                    int count = used.GetValueOrDefault(key);
                    if (count >= _frequencies.GetValueOrDefault(key)) continue;
                    used[key] = count + 1;
                    matches++;
                }
                return Ratio(matches);
            }

            public double real_quick_ratio() => Ratio(Math.Min(_from.Length, _to.Length));

            double Ratio(long matches)
            {
                long total = (long)_from.Length + _to.Length;
                return total == 0 ? 1.0 : 2.0 * matches / total;
            }
        }

        internal sealed class Differ
        {
            readonly Func<string, bool>? _lineJunk;
            readonly Func<string, bool>? _charJunk;

            public Differ(Func<string, bool>? linejunk = null, Func<string, bool>? charjunk = null)
            {
                _lineJunk = linejunk;
                _charJunk = charjunk;
            }

            public IEnumerable<string> compare(IEnumerable<string> a, IEnumerable<string> b) => compare((object)a, b);

            public IEnumerable<string> compare(object a, object b)
            {
                var before = Lines(a);
                var after = Lines(b);
                var matcher = new SequenceMatcher(_lineJunk == null ? null : value => _lineJunk((string)value), before, after);
                foreach (var change in matcher.Edits())
                {
                    var lines = change.Kind switch
                    {
                        Edit.Replace => CompareChangedLines(before, after, change),
                        Edit.Delete => WithPrefix("- ", before, change.FromStart, change.FromEnd),
                        Edit.Insert => WithPrefix("+ ", after, change.ToStart, change.ToEnd),
                        _ => WithPrefix("  ", before, change.FromStart, change.FromEnd),
                    };
                    foreach (string line in lines) yield return line;
                }
            }

            IEnumerable<string> CompareChangedLines(string[] before, string[] after, Opcode change)
            {
                const int window = 10;
                const double cutoff = 0.74999;
                var matcher = new SequenceMatcher(_charJunk == null ? null : value => _charJunk((string)value));
                int from = change.FromStart, to = change.ToStart;
                for (int j = to; j < change.ToEnd; j++)
                {
                    int aligned = change.FromStart + (j - change.ToStart);
                    int start = Math.Max(from, aligned - window);
                    int end = Math.Min(change.FromEnd, aligned + window + 1);
                    if (start >= end) break;
                    matcher.set_seq2(after[j]);
                    int best = -1;
                    double score = cutoff;
                    for (int i = start; i < end; i++)
                    {
                        matcher.set_seq1(before[i]);
                        if (matcher.real_quick_ratio() <= score || matcher.quick_ratio() <= score) continue;
                        double ratio = matcher.ratio();
                        if (ratio <= score) continue;
                        score = ratio;
                        best = i;
                    }
                    if (best < 0) continue;

                    foreach (string line in ChangedBlock(before, from, best, after, to, j)) yield return line;
                    if (before[best] == after[j]) yield return "  " + before[best];
                    else
                    {
                        matcher.set_seqs(before[best], after[j]);
                        foreach (string line in MarkChanges(before[best], after[j], matcher.Edits())) yield return line;
                    }
                    from = best + 1;
                    to = j + 1;
                }
                foreach (string line in ChangedBlock(before, from, change.FromEnd, after, to, change.ToEnd)) yield return line;
            }

            static IEnumerable<string> ChangedBlock(string[] before, int fromStart, int fromEnd,
                string[] after, int toStart, int toEnd)
            {
                var removed = WithPrefix("- ", before, fromStart, fromEnd);
                var added = WithPrefix("+ ", after, toStart, toEnd);
                return toEnd - toStart < fromEnd - fromStart ? added.Concat(removed) : removed.Concat(added);
            }

            static IEnumerable<string> MarkChanges(string before, string after, List<Opcode> edits)
            {
                string Marks(string source, bool left)
                {
                    var parts = Characters(source).ToArray();
                    var marks = new System.Text.StringBuilder(parts.Length);
                    foreach (var change in edits)
                    {
                        int start = left ? change.FromStart : change.ToStart;
                        int end = left ? change.FromEnd : change.ToEnd;
                        for (int i = start; i < end; i++)
                            marks.Append(change.Kind switch
                            {
                                Edit.Replace => "^",
                                Edit.Delete => "-",
                                Edit.Insert => "+",
                                _ => parts[i].All(char.IsWhiteSpace) ? parts[i] : " ",
                            });
                    }
                    return marks.ToString().TrimEnd();
                }

                yield return "- " + before;
                string removed = Marks(before, left: true);
                if (removed.Length != 0) yield return "? " + removed + "\n";
                yield return "+ " + after;
                string added = Marks(after, left: false);
                if (added.Length != 0) yield return "? " + added + "\n";
            }
        }

        static IEnumerable<string> WithPrefix(string prefix, string[] lines, int start, int end)
        {
            for (int index = start; index < end; index++) yield return prefix + lines[index];
        }

        static string DateSuffix(string date) => date.Length == 0 ? "" : "\t" + date;

        static string Range(int start, int end, bool unified)
        {
            long count = (long)end - start;
            long first = count == 0 ? start : (long)start + 1;
            if (count == 1 || !unified && count == 0) return FormattableString.Invariant($"{first}");
            long second = unified ? count : end;
            return FormattableString.Invariant($"{first},{second}");
        }

        static bool ColorEnabled()
        {
            string? setting = Environment.GetEnvironmentVariable("PYTHON_COLORS");
            if (setting is "0" or "1") return setting == "1";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORCE_COLOR"))) return true;
            return Environment.GetEnvironmentVariable("TERM") != "dumb" && !Console.IsOutputRedirected;
        }

        public static IEnumerable<string> unified_diff(IEnumerable<string> a, IEnumerable<string> b,
            string fromfile = "", string tofile = "", string fromfiledate = "", string tofiledate = "",
            long n = 3, string lineterm = "\n", bool color = false) =>
            unified_diff((object)a, b, fromfile, tofile, fromfiledate, tofiledate, n, lineterm, color);

        public static IEnumerable<string> unified_diff(object a, object b, string fromfile = "", string tofile = "",
            string fromfiledate = "", string tofiledate = "", long n = 3, string lineterm = "\n", bool color = false)
        {
            var before = Lines(a);
            var after = Lines(b);
            bool useColor = color && ColorEnabled();
            string Format(string code, string line) => useColor ? $"\u001b[{code}m{line}\u001b[0m" : line;
            bool writeHeader = true;
            foreach (var group in new SequenceMatcher(null, before, after).Groups(checked((int)n)))
            {
                if (writeHeader)
                {
                    writeHeader = false;
                    yield return Format("1", "--- " + fromfile + DateSuffix(fromfiledate) + lineterm);
                    yield return Format("1", "+++ " + tofile + DateSuffix(tofiledate) + lineterm);
                }
                string from = Range(group[0].FromStart, group[^1].FromEnd, unified: true);
                string to = Range(group[0].ToStart, group[^1].ToEnd, unified: true);
                yield return Format("36", $"@@ -{from} +{to} @@{lineterm}");
                foreach (var change in group)
                {
                    if (change.Kind == Edit.Equal)
                        foreach (string line in WithPrefix(" ", before, change.FromStart, change.FromEnd))
                            yield return Format("0", line);
                    if (change.Kind is Edit.Replace or Edit.Delete)
                        foreach (string line in WithPrefix("-", before, change.FromStart, change.FromEnd))
                            yield return Format("31", line);
                    if (change.Kind is Edit.Replace or Edit.Insert)
                        foreach (string line in WithPrefix("+", after, change.ToStart, change.ToEnd))
                            yield return Format("32", line);
                }
            }
        }

        public static IEnumerable<string> context_diff(IEnumerable<string> a, IEnumerable<string> b,
            string fromfile = "", string tofile = "", string fromfiledate = "", string tofiledate = "",
            long n = 3, string lineterm = "\n") =>
            context_diff((object)a, b, fromfile, tofile, fromfiledate, tofiledate, n, lineterm);

        public static IEnumerable<string> context_diff(object a, object b, string fromfile = "", string tofile = "",
            string fromfiledate = "", string tofiledate = "", long n = 3, string lineterm = "\n")
        {
            var before = Lines(a);
            var after = Lines(b);
            bool writeHeader = true;
            foreach (var group in new SequenceMatcher(null, before, after).Groups(checked((int)n)))
            {
                if (writeHeader)
                {
                    writeHeader = false;
                    yield return "*** " + fromfile + DateSuffix(fromfiledate) + lineterm;
                    yield return "--- " + tofile + DateSuffix(tofiledate) + lineterm;
                }
                yield return "***************" + lineterm;
                yield return "*** " + Range(group[0].FromStart, group[^1].FromEnd, unified: false) + " ****" + lineterm;
                if (group.Any(change => change.Kind is Edit.Replace or Edit.Delete))
                    foreach (var change in group)
                        foreach (string line in WithPrefix(ContextPrefix(change.Kind), before, change.FromStart, change.FromEnd))
                            yield return line;
                yield return "--- " + Range(group[0].ToStart, group[^1].ToEnd, unified: false) + " ----" + lineterm;
                if (group.Any(change => change.Kind is Edit.Replace or Edit.Insert))
                    foreach (var change in group)
                        foreach (string line in WithPrefix(ContextPrefix(change.Kind), after, change.ToStart, change.ToEnd))
                            yield return line;
            }
        }

        static string ContextPrefix(Edit kind) => kind switch
        {
            Edit.Replace => "! ",
            Edit.Insert => "+ ",
            Edit.Delete => "- ",
            _ => "  ",
        };

        public static IEnumerable<string> ndiff(IEnumerable<string> a, IEnumerable<string> b, Func<string, bool>? linejunk = null) =>
            ndiff((object)a, b, linejunk);

        public static IEnumerable<string> ndiff(object a, object b, Func<string, bool>? linejunk = null) =>
            new Differ(linejunk, IS_CHARACTER_JUNK).compare(a, b);

        public static IEnumerable<string> ndiff(IEnumerable<string> a, IEnumerable<string> b, Func<string, bool>? linejunk,
            Func<string, bool>? charjunk) => ndiff((object)a, b, linejunk, charjunk);

        public static IEnumerable<string> ndiff(object a, object b, Func<string, bool>? linejunk,
            Func<string, bool>? charjunk) => new Differ(linejunk, charjunk).compare(a, b);

        public static IEnumerable<string> restore(IEnumerable<string> delta, long which)
        {
            string prefix = which switch
            {
                1 => "- ",
                2 => "+ ",
                _ => throw new ArgumentException($"unknown delta choice (must be 1 or 2): {which}"),
            };
            foreach (string line in delta)
                if (line.StartsWith("  ", StringComparison.Ordinal) || line.StartsWith(prefix, StringComparison.Ordinal))
                    yield return line[2..];
        }

        public static List<T> get_close_matches<T>(object word, IEnumerable<T> possibilities, long n = 3, double cutoff = 0.6)
        {
            if (n <= 0) throw new ArgumentException("n must be > 0");
            if (!(cutoff >= 0 && cutoff <= 1)) throw new ArgumentException("cutoff must be in [0.0, 1.0]");
            var matcher = new SequenceMatcher(b: word);
            var matches = new List<(double Score, T Candidate)>();
            foreach (T candidate in possibilities)
            {
                matcher.set_seq1(candidate!);
                if (matcher.real_quick_ratio() < cutoff || matcher.quick_ratio() < cutoff) continue;
                double score = matcher.ratio();
                if (score >= cutoff) matches.Add((score, candidate));
            }
            return matches.OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Candidate, Builtins.PyComparer<T>())
                .Take((int)Math.Min(n, matches.Count)).Select(match => match.Candidate).ToList();
        }
    }
}
