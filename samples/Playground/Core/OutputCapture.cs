// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;

namespace Typhon.Playground;

/// <summary>
/// A run's stdout and stderr in the order they were written (a page shows them interleaved, as a terminal would), up
/// to <see cref="Limit"/> characters: a program that prints without end must not exhaust the page's memory.
/// </summary>
public sealed class OutputCapture
{
    public const int Limit = 200_000;

    readonly List<(bool IsError, StringBuilder Text)> _segments = new();
    int _length;

    public OutputCapture()
    {
        Out = new Writer(this, false);
        Err = new Writer(this, true);
    }

    public TextWriter Out { get; }
    public TextWriter Err { get; }
    /// <summary>More was written than <see cref="Limit"/>; the rest was dropped.</summary>
    public bool Truncated { get; private set; }
    public IEnumerable<(bool IsError, string Text)> Segments => _segments.Select(s => (s.IsError, s.Text.ToString()));
    public string Stdout => string.Concat(_segments.Where(s => !s.IsError).Select(s => s.Text.ToString()));
    public string Stderr => string.Concat(_segments.Where(s => s.IsError).Select(s => s.Text.ToString()));

    void Append(bool isError, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        if (_length + text.Length > Limit)
        {
            Truncated = true;
            text = text[..Math.Max(0, Limit - _length)];
            if (text.IsEmpty) return;
        }
        _length += text.Length;
        if (_segments.Count == 0 || _segments[^1].IsError != isError) _segments.Add((isError, new StringBuilder()));
        _segments[^1].Text.Append(text);
    }

    sealed class Writer(OutputCapture owner, bool isError) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => owner.Append(isError, [value]);
        public override void Write(string? value) => owner.Append(isError, value);
        public override void Write(char[] buffer, int index, int count) => owner.Append(isError, buffer.AsSpan(index, count));
        public override void Write(ReadOnlySpan<char> buffer) => owner.Append(isError, buffer);
    }
}
