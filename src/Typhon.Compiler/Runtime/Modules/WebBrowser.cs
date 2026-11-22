// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using Typhon.Runtime;

    /// <summary>Browser controllers with user registration, BROWSER preferences, and platform launchers.</summary>
    internal static class webbrowser
    {
        internal sealed class Error : Exception
        {
            public Error() { }
            public Error(string message) : base(message) { }
        }

        sealed record Controller(object? Factory, object? Instance);

        static readonly object Gate = new();
        static readonly Dictionary<string, Controller> Controllers = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> Preference = new();
        static bool _initialized;

        public static void register(string name, object? klass, object? instance = null, bool preferred = false)
        {
            lock (Gate)
            {
                Initialize();
                Register(name, new Controller(klass, instance), preferred);
            }
        }

        static void Register(string name, Controller controller, bool preferred)
        {
            Controllers[name] = controller;
            if (preferred) Preference.Insert(0, name);
            else Preference.Add(name);
        }

        static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            if (OperatingSystem.IsWindows())
                Register("windows-default", new Controller(null, new DesktopBrowser("windows-default")), false);
            else if (OperatingSystem.IsMacOS())
                Register("macosx", new Controller(null, new DesktopBrowser("macosx")), false);
            else
            {
                bool desktop = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
                if (desktop)
                {
                    RegisterInstalled("xdg-open", background: false);
                    foreach (string candidate in new[] { "firefox", "chromium", "chromium-browser", "google-chrome" })
                        RegisterInstalled(candidate, background: true);
                }
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM")))
                    foreach (string candidate in new[] { "www-browser", "links", "elinks", "lynx", "w3m" })
                        RegisterInstalled(candidate, background: false);
            }

            string? configured = Environment.GetEnvironmentVariable("BROWSER");
            if (string.IsNullOrEmpty(configured)) return;
            foreach (string entry in configured.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (entry.Contains("%s", StringComparison.Ordinal) || Controllers.ContainsKey(entry))
                    Preference.Insert(0, entry);
                else
                    Register(entry, new Controller(null, new GenericBrowser(entry)), preferred: true);
            }
        }

        static void RegisterInstalled(string name, bool background)
        {
            string? command = shutil.which(name);
            if (command != null)
                Register(name, new Controller(null, background ? new BackgroundBrowser(command) : new GenericBrowser(command)), false);
        }

        static string[] Choices(string? name = null)
        {
            lock (Gate)
            {
                Initialize();
                return name == null ? Preference.ToArray() : new[] { name };
            }
        }

        public static dynamic get(string? @using = null)
        {
            foreach (string name in Choices(@using))
                if (FindController(name) is { } controller) return controller;
            throw new Error("could not locate runnable browser");
        }

        static object? FindController(string name)
        {
            if (name.Contains("%s", StringComparison.Ordinal))
            {
                string[] words = SplitCommand(name);
                return words[^1] == "&" ? new BackgroundBrowser(words[..^1]) : new GenericBrowser(words);
            }
            Controller? controller;
            lock (Gate) Controllers.TryGetValue(name, out controller);
            if (controller?.Instance != null) return controller.Instance;
            if (controller?.Factory != null) return Builtins.__invoke(controller.Factory, Array.Empty<object?>());
            if (System.IO.Path.IsPathRooted(name) && System.IO.File.Exists(name))
                return new GenericBrowser(name);
            return null;
        }

        public static bool open(string url, long @new = 0, bool autoraise = true)
        {
            foreach (string name in Choices())
            {
                if (FindController(name) is not { } browser) continue;
                bool opened = browser is BaseBrowser native
                    ? native.open(url, @new, autoraise)
                    : Builtins.truthy((object?)Builtins.__invoke(Builtins.getattr(browser, "open"), new object?[] { url, @new, autoraise }));
                if (opened) return true;
            }
            return false;
        }

        public static bool open_new(string url) => open(url, 1);
        public static bool open_new_tab(string url) => open(url, 2);

        // BROWSER accepts shell-style quoting for an executable and its arguments.
        // Launch through ProcessStartInfo.ArgumentList so the URL remains a single argument.
        static string[] SplitCommand(string command)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            bool present = false;
            char quote = '\0';
            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (c == quote) { quote = '\0'; continue; }
                if (quote == '\0' && char.IsWhiteSpace(c))
                {
                    if (present) { words.Add(word.ToString()); word.Clear(); present = false; }
                    continue;
                }
                if (quote == '\0' && c is '\'' or '"') { quote = c; present = true; continue; }
                if (c == '\\' && quote != '\'')
                {
                    if (i + 1 >= command.Length) throw new ArgumentException("No escaped character");
                    char next = command[i + 1];
                    if (quote == '\0' || next is '"' or '\\') { word.Append(next); i++; present = true; continue; }
                }
                word.Append(c);
                present = true;
            }
            if (quote != '\0') throw new ArgumentException("No closing quotation");
            if (present) words.Add(word.ToString());
            if (words.Count == 0) throw new ArgumentException("empty browser command");
            return words.ToArray();
        }

        internal abstract class BaseBrowser
        {
            public string name { get; }
            public string basename { get; }

            protected BaseBrowser(string name)
            {
                this.name = name;
                basename = System.IO.Path.GetFileName(name);
            }

            public abstract bool open(string url, long @new = 0, bool autoraise = true);
            public bool open_new(string url) => open(url, 1);
            public bool open_new_tab(string url) => open(url, 2);

            protected static void CheckUrl(string url)
            {
                if (url.TrimStart().StartsWith('-'))
                    throw new ArgumentException($"Invalid URL (leading dash disallowed): {Builtins.repr(url)}");
            }
        }

        internal class GenericBrowser : BaseBrowser
        {
            public List<string> args { get; }
            public GenericBrowser(object name) : this(CommandParts(name)) { }
            GenericBrowser(string[] command) : base(command[0]) { args = command.Skip(1).ToList(); }

            static string[] CommandParts(object name)
            {
                if (name is string command) return new[] { command, "%s" };
                string[] parts = Builtins.__iter_dyn(name).Cast<string>().ToArray();
                if (parts.Length == 0) throw new IndexOutOfRangeException("empty browser command");
                return parts;
            }

            public override bool open(string url, long @new = 0, bool autoraise = true) =>
                Run(url, background: false);

            protected bool Run(string url, bool background)
            {
                CheckUrl(url);
                var start = new ProcessStartInfo(name) { UseShellExecute = false };
                foreach (string part in args) start.ArgumentList.Add(part.Replace("%s", url, StringComparison.Ordinal));
                try
                {
                    using var process = Process.Start(start);
                    if (process == null) return false;
                    if (background) return !process.HasExited;
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
                catch (Win32Exception) { return false; }
                catch (System.IO.IOException) { return false; }
            }
        }

        internal sealed class BackgroundBrowser : GenericBrowser
        {
            public BackgroundBrowser(object name) : base(name) { }
            public override bool open(string url, long @new = 0, bool autoraise = true) =>
                Run(url, background: true);
        }

        sealed class DesktopBrowser : BaseBrowser
        {
            public DesktopBrowser(string name) : base(name) { }

            public override bool open(string url, long @new = 0, bool autoraise = true)
            {
                CheckUrl(url);
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }
                catch (Win32Exception) { return false; }
                catch (System.IO.IOException) { return false; }
            }
        }
    }
}
