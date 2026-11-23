// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Reflection;
    using Typhon.Runtime;

    internal static class _colorize
    {
        public static bool COLORIZE = true;
        static readonly Dictionary<string, string> Palette = typeof(ANSIColors)
            .GetFields(BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);
        public static readonly HashSet<string> ColorCodes = new HashSet<string>(Palette.Values, StringComparer.Ordinal);
        public static readonly dynamic NoColors = new ANSIColors(false);

        public static dynamic can_colorize = new PyClosure("can_colorize", (args, kwargs) =>
        {
            if (args.Length != 0) throw PyClosure.ArgCountError("can_colorize", 0, 0, args.Length);
            var bound = PyClosure.Args("can_colorize", ["file"], args, kwargs, false);
            return PyConsole.CanColorize(bound.Length == 0 ? null : bound[0], COLORIZE);
        });

        public static dynamic get_colors(bool colorize = false, object? file = null) =>
            colorize || Builtins.truthy((object?)Builtins.__invoke_kw((object?)can_colorize, [], new Dictionary<string, object?> { ["file"] = file }))
                ? new ANSIColors() : NoColors;

        public static Theme get_theme(object? tty_file = null, bool force_color = false, bool force_no_color = false) =>
            new Theme(force_color || !force_no_color && Builtins.truthy((object?)Builtins.__invoke_kw((object?)can_colorize, [],
                new Dictionary<string, object?> { ["file"] = tty_file })));

        internal sealed class Theme
        {
            public global::Typhon.Stdlib.argparse.Theme argparse { get; }
            public DifflibTheme difflib { get; }
            public PickletoolsTheme pickletools { get; }
            public SyntaxTheme syntax { get; }
            public TokenizeTheme tokenize { get; }
            public TimeitTheme timeit { get; }
            public TracebackTheme traceback { get; }
            public UnittestTheme unittest { get; }

            internal Theme(bool color)
            {
                argparse = color ? global::Typhon.Stdlib.argparse.Theme.Color : global::Typhon.Stdlib.argparse.Theme.Plain;
                difflib = new DifflibTheme(color);
                pickletools = new PickletoolsTheme(color);
                syntax = new SyntaxTheme(color);
                tokenize = new TokenizeTheme(color);
                timeit = new TimeitTheme(color);
                traceback = new TracebackTheme(color);
                unittest = new UnittestTheme(color);
            }
        }

        // The color roles match CPython Lib/_colorize.py. See CPython-LICENSE.txt.
        internal sealed class DifflibTheme(bool color)
        {
            string Color(string code) => color ? code : "";
            public string added => Color(ANSIColors.GREEN);
            public string context => reset;
            public string header => Color(ANSIColors.BOLD);
            public string hunk => Color(ANSIColors.CYAN);
            public string removed => Color(ANSIColors.RED);
            public string reset => Color(ANSIColors.RESET);
        }

        internal sealed class PickletoolsTheme
        {
            readonly bool _color;
            internal PickletoolsTheme(bool color) { _color = color; }
            public string annotation => _color ? ANSIColors.GREY : "";
            public string arg_number => _color ? ANSIColors.YELLOW : "";
            public string arg_string => _color ? ANSIColors.GREEN : "";
            public string mark => _color ? ANSIColors.GREY : "";
            public string op_call => _color ? ANSIColors.GREEN : "";
            public string op_container => _color ? ANSIColors.INTENSE_BLUE : "";
            public string op_memo => _color ? ANSIColors.MAGENTA : "";
            public string op_meta => _color ? ANSIColors.GREY : "";
            public string op_stack => _color ? ANSIColors.BOLD_RED : "";
            public string opcode_code => _color ? ANSIColors.CYAN : "";
            public string position => _color ? ANSIColors.GREY : "";
            public string proto => _color ? ANSIColors.YELLOW : "";
            public string reset => _color ? ANSIColors.RESET : "";
        }

        internal sealed class TracebackTheme
        {
            readonly bool _color;
            internal TracebackTheme(bool color) { _color = color; }
            public string type => _color ? ANSIColors.BOLD_MAGENTA : "";
            public string message => _color ? ANSIColors.MAGENTA : "";
            public string note => _color ? ANSIColors.CYAN : "";
            public string filename => _color ? ANSIColors.MAGENTA : "";
            public string line_no => _color ? ANSIColors.MAGENTA : "";
            public string frame => _color ? ANSIColors.MAGENTA : "";
            public string error_highlight => _color ? ANSIColors.BOLD_RED : "";
            public string error_range => _color ? ANSIColors.RED : "";
            public string reset => _color ? ANSIColors.RESET : "";
        }

        internal sealed class UnittestTheme(bool color)
        {
            public string passed => color ? ANSIColors.GREEN : "";
            public string warn => color ? ANSIColors.YELLOW : "";
            public string fail => color ? ANSIColors.RED : "";
            public string fail_info => color ? ANSIColors.BOLD_RED : "";
            public string reset => color ? ANSIColors.RESET : "";
        }

        internal sealed class SyntaxTheme(bool color)
        {
            string Color(string code) => color ? code : "";
            public string prompt => Color(ANSIColors.BOLD_MAGENTA);
            public string keyword => Color(ANSIColors.BOLD_BLUE);
            public string keyword_constant => keyword;
            public string builtin => Color(ANSIColors.CYAN);
            public string comment => Color(ANSIColors.RED);
            public string @string => Color(ANSIColors.GREEN);
            public string number => Color(ANSIColors.YELLOW);
            public string op => reset;
            public string definition => Color(ANSIColors.BOLD);
            public string soft_keyword => keyword;
            public string reset => Color(ANSIColors.RESET);
        }

        internal sealed class TokenizeTheme(bool color)
        {
            public string whitespace => color ? ANSIColors.GREY : "";
            public string error => color ? ANSIColors.BOLD_RED : "";
            public string position => whitespace;
            public string delimiter => color ? ANSIColors.RESET : "";
        }

        internal sealed class TimeitTheme
        {
            readonly bool _color;
            internal TimeitTheme(bool color) { _color = color; }
            public string timing => _color ? ANSIColors.CYAN : "";
            public string best => _color ? ANSIColors.BOLD_GREEN : "";
            public string per_loop => _color ? ANSIColors.GREEN : "";
            public string punctuation => _color ? ANSIColors.GREY : "";
            public string warning => _color ? ANSIColors.YELLOW : "";
            public string warning_worst => _color ? ANSIColors.MAGENTA : "";
            public string warning_best => _color ? ANSIColors.GREEN : "";
            public string reset => _color ? ANSIColors.RESET : "";
        }

        public static string decolor(string text)
        {
            if (text.IndexOf('\x1b') < 0) return text;
            foreach (string code in ColorCodes) text = text.Replace(code, "", StringComparison.Ordinal);
            return text;
        }

        internal class ANSIColors : DynamicObject
        {
            public const string RESET = "\x1b[0m";
            public const string BOLD = "\x1b[1m";
            public const string GREY = "\x1b[90m";
            public const string BLACK = "\x1b[30m";
            public const string RED = "\x1b[31m";
            public const string GREEN = "\x1b[32m";
            public const string YELLOW = "\x1b[33m";
            public const string BLUE = "\x1b[34m";
            public const string MAGENTA = "\x1b[35m";
            public const string CYAN = "\x1b[36m";
            public const string WHITE = "\x1b[37m";
            public const string BOLD_BLACK = "\x1b[1;30m";
            public const string BOLD_RED = "\x1b[1;31m";
            public const string BOLD_GREEN = "\x1b[1;32m";
            public const string BOLD_YELLOW = "\x1b[1;33m";
            public const string BOLD_BLUE = "\x1b[1;34m";
            public const string BOLD_MAGENTA = "\x1b[1;35m";
            public const string BOLD_CYAN = "\x1b[1;36m";
            public const string BOLD_WHITE = "\x1b[1;37m";
            public const string INTENSE_BLACK = "\x1b[90m";
            public const string INTENSE_RED = "\x1b[91m";
            public const string INTENSE_GREEN = "\x1b[92m";
            public const string INTENSE_YELLOW = "\x1b[93m";
            public const string INTENSE_BLUE = "\x1b[94m";
            public const string INTENSE_MAGENTA = "\x1b[95m";
            public const string INTENSE_CYAN = "\x1b[96m";
            public const string INTENSE_WHITE = "\x1b[97m";
            public const string BACKGROUND_BLACK = "\x1b[40m";
            public const string BACKGROUND_RED = "\x1b[41m";
            public const string BACKGROUND_GREEN = "\x1b[42m";
            public const string BACKGROUND_YELLOW = "\x1b[43m";
            public const string BACKGROUND_BLUE = "\x1b[44m";
            public const string BACKGROUND_MAGENTA = "\x1b[45m";
            public const string BACKGROUND_CYAN = "\x1b[46m";
            public const string BACKGROUND_WHITE = "\x1b[47m";
            public const string INTENSE_BACKGROUND_BLACK = "\x1b[100m";
            public const string INTENSE_BACKGROUND_RED = "\x1b[101m";
            public const string INTENSE_BACKGROUND_GREEN = "\x1b[102m";
            public const string INTENSE_BACKGROUND_YELLOW = "\x1b[103m";
            public const string INTENSE_BACKGROUND_BLUE = "\x1b[104m";
            public const string INTENSE_BACKGROUND_MAGENTA = "\x1b[105m";
            public const string INTENSE_BACKGROUND_CYAN = "\x1b[106m";
            public const string INTENSE_BACKGROUND_WHITE = "\x1b[107m";

            readonly bool _enabled;
            public ANSIColors() : this(true) { }
            internal ANSIColors(bool enabled) { _enabled = enabled; }

            public override bool TryGetMember(GetMemberBinder binder, out object? result)
            {
                bool found = Palette.TryGetValue(binder.Name, out var code);
                result = found ? (_enabled ? code : "") : null;
                return found;
            }
        }
    }
}
