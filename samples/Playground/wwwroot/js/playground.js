// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// The editor is a plain <textarea>; this adds what Python code needs from it (Tab indents, Enter keeps the indentation
// and indents after a colon), keeps the line numbers next to it, highlights the code, and runs the program on Ctrl+Enter.
window.playground = {
  initEditor(editor, gutter, syntax, dotnet) {
    const indent = "    ";
    const insert = (text) => {
      // execCommand keeps the browser's undo history; setRangeText is the fallback
      if (!document.execCommand("insertText", false, text)) {
        editor.setRangeText(text, editor.selectionStart, editor.selectionEnd, "end");
        editor.dispatchEvent(new Event("input", { bubbles: true }));
      }
    };
    const lineStart = (pos) => editor.value.lastIndexOf("\n", pos - 1) + 1;

    editor.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        dotnet.invokeMethodAsync("RunFromEditor");
      } else if (e.key === "Tab" && !e.ctrlKey && !e.altKey && !e.metaKey) {
        e.preventDefault();
        const { selectionStart: start, selectionEnd: end, value } = editor;
        if (start === end && !e.shiftKey) {
          insert(indent.slice((start - lineStart(start)) % indent.length));
          return;
        }
        // indent or dedent every selected line
        const first = lineStart(start);
        const last = end > start && value[end - 1] === "\n" ? end - 1 : end;
        const lines = value.slice(first, last).split("\n");
        const changed = lines.map((l) => e.shiftKey ? l.replace(/^ {1,4}|^\t/, "") : indent + l).join("\n");
        editor.setSelectionRange(first, last);
        insert(changed);
        editor.setSelectionRange(first, first + changed.length);
      } else if (e.key === "Enter" && !e.shiftKey && !e.altKey) {
        const { selectionStart: start, value } = editor;
        const line = value.slice(lineStart(start), start);
        let lead = line.match(/^[ \t]*/)[0];
        if (/:\s*(#.*)?$/.test(line)) lead += indent;
        e.preventDefault();
        insert("\n" + lead);
      }
    });

    // The highlighting: the textarea's text is transparent (its caret and selection are not), and under it a <pre> shows
    // the same text, highlighted (highlight.js), in the same font, padding and line height, moved with its scrolling.
    // The whole text is highlighted on input, but only the lines in view (in blocks of 50, with a block more above and
    // below) are in the <pre>: typing and scrolling in a program of thousands of lines stay as fast as in a short one.
    const code = syntax.firstElementChild;
    const block = 50;
    let lineHeight = parseFloat(getComputedStyle(editor).lineHeight) || 20;
    let lines = [""]; // the highlighted text, line by line
    let shown = "", offset = "", inset = "";
    const show = (changed) => {
      const top = editor.scrollTop;
      gutter.scrollTop = top;
      // not under the textarea's scrollbars
      const bars = `0 ${editor.offsetWidth - editor.clientWidth}px ${editor.offsetHeight - editor.clientHeight}px 0`;
      if (bars !== inset) syntax.style.inset = inset = bars;
      const first = Math.max(0, (Math.floor(top / lineHeight / block) - 1) * block);
      const last = Math.min(lines.length, (Math.floor((top + editor.clientHeight) / lineHeight / block) + 2) * block);
      const range = `${first}:${last}`;
      if (changed || range !== shown) {
        code.innerHTML = lines.slice(first, last).join("\n");
        shown = range;
      }
      const moved = `translate(${-editor.scrollLeft}px, ${first * lineHeight - top}px)`;
      if (moved !== offset) code.style.transform = offset = moved;
    };
    let pending = false;
    const render = () => {
      pending = false;
      lines = window.highlight.lines(editor.value, "python");
      show(true);
    };
    const update = () => {
      if (!pending) {
        pending = true;
        requestAnimationFrame(render);
      }
    };
    editor.addEventListener("scroll", () => show(false));
    editor.addEventListener("input", update);
    new ResizeObserver(() => {
      lineHeight = parseFloat(getComputedStyle(editor).lineHeight) || 20;
      show(false);
    }).observe(editor);
    // Blazor sets the value when an example is chosen: no input event, but the highlighting follows it too
    const value = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value");
    Object.defineProperty(editor, "value", {
      configurable: true,
      get() { return value.get.call(this); },
      set(text) { value.set.call(this, text); update(); },
    });
    render();
  },

  // resolves after the browser painted: the status shows before a long synchronous compile starts
  nextFrame() {
    return new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
  },

  goTo(editor, line, column) {
    const lines = editor.value.split("\n");
    let pos = 0;
    for (let i = 0; i < Math.min(line - 1, lines.length); i++) pos += lines[i].length + 1;
    pos += Math.max(0, Math.min(column - 1, (lines[line - 1] || "").length));
    editor.focus();
    editor.setSelectionRange(pos, pos);
    const lineHeight = parseFloat(getComputedStyle(editor).lineHeight) || 20;
    editor.scrollTop = Math.max(0, (line - 4) * lineHeight);
  },

  scrollToLine(view, line) {
    const target = view.querySelector(".code .highlight");
    if (target) target.scrollIntoView({ block: "center" });
  },

  async share(url) {
    history.replaceState(null, "", url);
    try {
      await navigator.clipboard.writeText(url);
      return true;
    } catch {
      return false;
    }
  },
};
