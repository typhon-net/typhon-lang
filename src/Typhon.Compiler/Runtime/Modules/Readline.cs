// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Text;
    using Typhon.Runtime;

    internal static class readline_
    {
        const string DefaultDelimiters = " \t\n\"\\'\x60@$><=;|&{(";
        static object? _completer;
        static string _delimiters = DefaultDelimiters;
        static char _completionKey = '\t';
        static string _lineBuffer = "";
        static int _begin, _end;
        [ThreadStatic] static Editor? _editor;

        public const string backend = "readline";

        public static object? get_completer() => _completer;

        public static object? set_completer(object? function = null)
        {
            _completer = function;
            return null;
        }

        public static string get_completer_delims() => _delimiters;

        public static void set_completer_delims(string delimiters) => _delimiters = delimiters;

        public static string get_line_buffer() => _editor?.Text.ToString() ?? _lineBuffer;

        public static long get_begidx() => _begin;

        public static long get_endidx() => _end;

        public static void insert_text(string text)
        {
            if (_editor is Editor editor) editor.Insert(text);
            else _lineBuffer += text;
        }

        public static void redisplay() => _editor?.Display();

        public static void parse_and_bind(string line)
        {
            int separator = line.LastIndexOf(':');
            if (separator < 0) return;
            string key = line[..separator].Trim();
            string command = line[(separator + 1)..].Trim();
            char character = key.Equals("tab", StringComparison.OrdinalIgnoreCase) ? '\t' :
                key.Length == 1 ? key[0] : '\0';
            if (character == '\0') return;
            if (command == "complete") _completionKey = character;
            else if (command == "self-insert" && _completionKey == character) _completionKey = '\0';
        }

        internal static bool CanEdit =>
            _completer != null && !Console.IsInputRedirected && !Console.IsOutputRedirected && !OperatingSystem.IsBrowser();

        internal static string? ReadLine(string prompt)
        {
            var editor = new Editor(prompt, _lineBuffer);
            _editor = editor;
            _lineBuffer = "";
            if (editor.Text.Length != 0) editor.Display();
            try
            {
                while (true)
                {
                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return editor.Text.ToString();
                    }
                    if (key.Key is ConsoleKey.D or ConsoleKey.Z && (key.Modifiers & ConsoleModifiers.Control) != 0 &&
                        editor.Text.Length == 0)
                    {
                        Console.WriteLine();
                        return null;
                    }
                    if (key.KeyChar != '\0' && key.KeyChar == _completionKey && _completer is object completer)
                        editor.Complete(completer);
                    else
                        editor.Edit(key);
                    editor.Display();
                }
            }
            finally { _editor = null; }
        }

        sealed class Editor
        {
            internal readonly StringBuilder Text;
            readonly string _prompt;
            int _cursor, _displayLength;

            internal Editor(string prompt, string initial)
            {
                _prompt = prompt;
                Text = new StringBuilder(initial);
                _cursor = Text.Length;
            }

            internal void Insert(string text)
            {
                Text.Insert(_cursor, text);
                _cursor += text.Length;
            }

            internal void Display()
            {
                string text = Text.ToString();
                Console.Write('\r');
                Console.Write(_prompt);
                Console.Write(text);
                if (_displayLength > text.Length) Console.Write(new string(' ', _displayLength - text.Length));
                if (_cursor < Math.Max(text.Length, _displayLength))
                {
                    Console.Write('\r');
                    Console.Write(_prompt);
                    Console.Out.Write(text.AsSpan(0, _cursor));
                }
                _displayLength = text.Length;
            }

            internal void Edit(ConsoleKeyInfo key)
            {
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow when _cursor > 0: _cursor--; break;
                    case ConsoleKey.RightArrow when _cursor < Text.Length: _cursor++; break;
                    case ConsoleKey.Home: _cursor = 0; break;
                    case ConsoleKey.End: _cursor = Text.Length; break;
                    case ConsoleKey.Backspace when _cursor > 0: Text.Remove(--_cursor, 1); break;
                    case ConsoleKey.Delete when _cursor < Text.Length: Text.Remove(_cursor, 1); break;
                    default:
                        if (!char.IsControl(key.KeyChar) || key.KeyChar == '\t') Insert(key.KeyChar.ToString());
                        break;
                }
            }

            static int CharacterCount(ReadOnlySpan<char> text)
            {
                int count = 0;
                foreach (var rune in text.EnumerateRunes()) count++;
                return count;
            }

            internal void Complete(object completer)
            {
                string source = Text.ToString();
                int start = _cursor;
                while (start > 0 && !_delimiters.Contains(source[start - 1])) start--;
                _begin = CharacterCount(source.AsSpan(0, start));
                _end = CharacterCount(source.AsSpan(0, _cursor));
                string word = source[start.._cursor];
                string? common = null;
                try
                {
                    for (long index = 0; ; index++)
                    {
                        if (Builtins.__invoke(completer, new object?[] { word, index }) is not string match) break;
                        if (common == null) common = match;
                        else
                        {
                            int count = 0;
                            while (count < common.Length && count < match.Length && common[count] == match[count]) count++;
                            common = common[..count];
                        }
                    }
                }
                catch (Exception) { return; }
                if (common == null || common.Length == 0 || source != Text.ToString()) return;
                Text.Remove(start, _cursor - start);
                _cursor = start;
                Insert(common);
            }
        }
    }
}
