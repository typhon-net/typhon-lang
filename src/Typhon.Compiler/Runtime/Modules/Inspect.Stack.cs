// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable CS8981

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Typhon.Runtime;

    internal static partial class inspect
    {
        internal sealed class Positions(long? lineno, long? end_lineno, long? col_offset, long? end_col_offset) : PyStructSeq
        {
            static readonly string[] Names = ["lineno", "end_lineno", "col_offset", "end_col_offset"];
            readonly object?[] _values = [lineno, end_lineno, col_offset, end_col_offset];

            public long? lineno => (long?)_values[0];
            public long? end_lineno => (long?)_values[1];
            public long? col_offset => (long?)_values[2];
            public long? end_col_offset => (long?)_values[3];
            public dynamic? this[long index] => Item(index);

            protected override string TypeName => nameof(Positions);
            protected override string[] Fields => Names;
            protected override object?[] Values => _values;
        }

        internal sealed class FrameInfo(sys_.frame frame, string filename, long lineno, string function,
            List<string>? code_context, long? index, [PyKeywordOnly] Positions? positions = null) : PyStructSeq
        {
            static readonly string[] Names = ["frame", "filename", "lineno", "function", "code_context", "index"];
            readonly object?[] _values = [frame, filename, lineno, function, code_context, index];

            public sys_.frame frame => (sys_.frame)_values[0]!;
            public string filename => (string)_values[1]!;
            public long lineno => (long)_values[2]!;
            public string function => (string)_values[3]!;
            public List<string>? code_context => (List<string>?)_values[4];
            public long? index => (long?)_values[5];
            public Positions? positions { get; } = positions;
            public dynamic? this[long item] => Item(item);

            public void Deconstruct(out sys_.frame frame, out string filename, out long lineno, out string function,
                out List<string>? code_context, out long? index) =>
                (frame, filename, lineno, function, code_context, index) =
                (this.frame, this.filename, this.lineno, this.function, this.code_context, this.index);

            protected override string TypeName => nameof(FrameInfo);
            protected override string[] Fields => Names;
            protected override object?[] Values => _values;
            public override string __repr__() => base.__repr__()[..^1] + $", positions={Builtins.repr(positions)})";
        }

        public static List<FrameInfo> stack(long context = 1)
        {
            var frames = new List<FrameInfo>();
            for (sys_.frame? frame = sys_._getframe(); frame != null; frame = frame.f_back)
            {
                string filename = getsourcefile(frame) ?? getfile(frame);
                var site = frame.f_lasti < 0 ? default
                    : frame.f_code.co_positions().Skip((int)Math.Min(frame.f_lasti / 2, int.MaxValue)).FirstOrDefault();
                long line = site.Item1 ?? frame.f_lineno;
                List<string>? codeContext = null;
                long? index = null;
                if (context > 0)
                {
                    object? source = Builtins.__invoke((object)linecache.getlines, [filename, frame.f_globals]);
                    var lines = source as IReadOnlyList<string> ?? Builtins.__iter_dyn(source).Cast<string>().ToArray();
                    if (lines.Count != 0 && line > 0)
                    {
                        int start = (int)Math.Clamp(line - 1 - context / 2, 0, Math.Max(0, lines.Count - context));
                        codeContext = lines.Skip(start).Take((int)Math.Min(context, lines.Count - start)).ToList();
                        index = line - 1 - start;
                    }
                }
                frames.Add(new FrameInfo(frame, filename, line, frame.f_code.co_name, codeContext, index,
                    new Positions(line, site.Item2, site.Item3, site.Item4)));
            }
            return frames;
        }
    }
}
