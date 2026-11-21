// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// The editor is a plain <textarea>; this adds what Python code needs from it (Tab indents, Enter keeps the indentation
// and indents after a colon), keeps the line numbers next to it, and runs the program on Ctrl+Enter.
window.playground = {
  initEditor(editor, gutter, dotnet) {
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
    const sync = () => { gutter.scrollTop = editor.scrollTop; };
    editor.addEventListener("scroll", sync);
    editor.addEventListener("input", () => requestAnimationFrame(sync));
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
