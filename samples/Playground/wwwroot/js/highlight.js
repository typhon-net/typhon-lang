// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// Syntax highlighting for the editor (Typhon, which is Python) and the generated C#, in one pass over the text and
// without dependencies. highlight.lines(text, "python" | "csharp") returns the HTML of each line: the text's own
// characters, escaped, with tokens in <span class="t-...">, split at the line breaks (a string or comment that spans
// lines is closed at the end of each line and reopened on the next), so each line can also go into an element of its
// own. Incomplete code, as it is while typing, never throws: an unterminated string or comment runs to the end of its
// line (or of the text, for a triple-quoted, verbatim or raw string and a block comment).
window.highlight = (() => {
  const words = (s) => new Set(s.split(" "));
  const escape = (s) => s.indexOf("<") < 0 && s.indexOf(">") < 0 && s.indexOf("&") < 0 ? s
    : s.replace(/[&<>]/g, (c) => (c === "&" ? "&amp;" : c === "<" ? "&lt;" : "&gt;"));
  const ID = /[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}]*/uy;
  const ID_REST = /[\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}]*/uy;
  const isDigit = (c) => c >= 48 && c <= 57;
  const isSpace = (c) => c === 32 || c === 9 || c === 12 || c === 13 || c === 11;
  const isWordChar = (ch) => ch !== undefined && /[\p{L}\p{N}_]/u.test(ch);
  const lineEnd = (src, i) => { const e = src.indexOf("\n", i); return e < 0 ? src.length : e; };
  const sticky = (re, src, i) => { re.lastIndex = i; return re.exec(src) ? re.lastIndex : i; };
  const spaceEnd = (src, i) => { while (isSpace(src.charCodeAt(i))) i++; return i; };
  // the end of the name at i (i itself when there is none); ASCII without a regular expression
  const nameEnd = (src, i) => {
    const c = src.charCodeAt(i);
    if (c >= 128) return sticky(ID, src, i);
    if (!((c >= 97 && c <= 122) || (c >= 65 && c <= 90) || c === 95)) return i;
    let j = i + 1;
    for (let d; (d = src.charCodeAt(j)) < 128; j++) {
      if (!((d >= 97 && d <= 122) || (d >= 65 && d <= 90) || isDigit(d) || d === 95)) return j;
    }
    return j < src.length ? sticky(ID_REST, src, j) : j;
  };

  /** The highlighted text, line by line. */
  class Lines {
    constructor() { this.done = []; this.line = ""; this.plain = ""; }
    add(cls, text) {
      if (!cls) { this.plain += text; return; } // escaped (and split into lines) once, before the next token
      if (this.plain) this.split(null, this.plain);
      this.plain = "";
      this.split(cls, text);
    }
    split(cls, text) {
      let start = 0;
      for (let nl; (nl = text.indexOf("\n", start)) >= 0; start = nl + 1) {
        this.part(cls, text.slice(start, nl));
        this.done.push(this.line);
        this.line = "";
      }
      this.part(cls, start ? text.slice(start) : text);
    }
    part(cls, text) {
      if (text) this.line += cls ? `<span class="t-${cls}">${escape(text)}</span>` : escape(text);
    }
    end() {
      this.split(null, this.plain);
      this.done.push(this.line);
      return this.done;
    }
  }

  // ---- Python ------------------------------------------------------------------------------------------------------

  const PY_KEYWORDS = words("and as assert async await break class continue def del elif else except finally for from " +
    "global if import in is lambda nonlocal not or pass raise return try while with yield");
  const PY_CONSTANTS = words("True False None");
  const PY_BUILTINS = words("abs aiter all anext any ascii bin bool breakpoint bytearray bytes callable chr classmethod " +
    "compile complex delattr dict dir divmod enumerate eval exec filter float format frozenset getattr globals hasattr " +
    "hash help hex id input int isinstance issubclass iter len list locals map max memoryview min next object oct open " +
    "ord pow print property range repr reversed round set setattr slice sorted staticmethod str sum super tuple type " +
    "vars zip __import__ __name__ __file__ NotImplemented Ellipsis " +
    "BaseException Exception ArithmeticError AssertionError AttributeError EOFError FileNotFoundError ImportError " +
    "IndexError KeyError KeyboardInterrupt LookupError MemoryError ModuleNotFoundError NameError NotImplementedError " +
    "OSError OverflowError PermissionError RecursionError RuntimeError StopIteration StopAsyncIteration SyntaxError " +
    "SystemExit TimeoutError TypeError UnicodeError ValueError ZeroDivisionError Warning DeprecationWarning UserWarning");
  const PY_NUMBER = /0[xX][\da-fA-F_]*|0[oO][0-7_]*|0[bB][01_]*|(?:\d[\d_]*(?:\.[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?\d[\d_]*)?[jJ]?/y;
  const PY_PREFIX = /^(?:[rRbBuUfFtT]|[rR][bBfFtT]|[bBfFtT][rR])$/;
  const PY_DECORATOR = /@[ \t]*[\p{L}\p{Nl}_][\p{L}\p{N}_]*(?:[ \t]*\.[ \t]*[\p{L}\p{Nl}_][\p{L}\p{N}_]*)*/uy;
  // what follows `match` or `case` on a line where they start a statement: something, then a colon at the end
  const PY_SOFT_STATEMENT = /^[ \t]*[^\s=.,:;)\]}][^\n]*:[ \t]*(?:#[^\n]*)?$/;
  // what follows `type` where it declares an alias: `type Name = ...`, `type Name[T] = ...`
  const PY_TYPE_STATEMENT = /[ \t]+[\p{L}\p{Nl}_][\p{L}\p{N}_]*[ \t]*[=[]/uy;

  function python(src, out, nested) {
    const n = src.length;
    let i = 0;
    let depth = 0; // brackets: a line break inside them does not end the statement
    let statementStart = !nested; // only whitespace since the statement began (decorators, soft keywords)
    let declares = ""; // after def or class: the class of the name it declares
    let afterDot = false; // an attribute is neither a keyword nor a builtin
    while (i < n) {
      const c = src.charCodeAt(i);
      const ch = src[i];
      let j;
      if (c === 10) {
        out.add(null, "\n");
        i++;
        if (depth === 0) statementStart = true;
        declares = "";
        continue;
      }
      if (isSpace(c)) { j = spaceEnd(src, i); out.add(null, src.slice(i, j)); i = j; continue; }
      if (ch === "#") { j = lineEnd(src, i); out.add("com", src.slice(i, j)); i = j; continue; }
      if (ch === "\\") { out.add(null, src.slice(i, i + 2)); i += 2; continue; } // a line continuation
      if (ch === '"' || ch === "'") {
        i = pyString(src, i, i, out);
      } else if (isDigit(c) || (ch === "." && isDigit(src.charCodeAt(i + 1)))) {
        j = sticky(PY_NUMBER, src, i);
        out.add("num", src.slice(i, j));
        i = j;
      } else if (ch === "@" && statementStart && depth === 0 && (j = sticky(PY_DECORATOR, src, i)) > i) {
        out.add("dec", src.slice(i, j));
        i = j;
      } else if ((j = nameEnd(src, i)) > i) {
        const word = src.slice(i, j);
        if ((src[j] === '"' || src[j] === "'") && PY_PREFIX.test(word)) {
          i = pyString(src, i, j, out);
        } else {
          let cls = null;
          if (declares) {
            cls = declares;
          } else if (!afterDot) {
            if (PY_KEYWORDS.has(word)) cls = "kw";
            else if (PY_CONSTANTS.has(word)) cls = "con";
            else if (statementStart && depth === 0 && (word === "match" || word === "case")
                     && PY_SOFT_STATEMENT.test(src.slice(j, lineEnd(src, j)))) cls = "kw";
            else if (statementStart && depth === 0 && word === "type" && sticky(PY_TYPE_STATEMENT, src, j) > j) cls = "kw";
            else if (PY_BUILTINS.has(word)) cls = "bi";
          }
          declares = cls === "kw" && word === "def" ? "fn" : cls === "kw" && word === "class" ? "cls" : "";
          out.add(cls, word);
          i = j;
        }
      } else {
        if (ch === "(" || ch === "[" || ch === "{") depth++;
        else if ((ch === ")" || ch === "]" || ch === "}") && depth > 0) depth--;
        out.add(null, ch);
        i++;
        statementStart = ch === ";" && depth === 0;
        afterDot = ch === ".";
        continue;
      }
      statementStart = false;
      afterDot = false;
    }
  }

  // a string literal: its prefix at i, its quote at q; returns the index after it
  function pyString(src, i, q, out) {
    const n = src.length, quote = src[q];
    const delim = src.startsWith(quote + quote + quote, q) ? quote + quote + quote : quote;
    const prefix = src.slice(i, q);
    const format = /[fFtT]/.test(prefix), raw = /[rR]/.test(prefix);
    let j = q + delim.length, from = i;
    while (j < n) {
      const ch = src[j];
      if (ch === "\\") {
        // an escaped character (in a raw string too: r"\"" is one string); \N{NAME} in an f-string is no field
        j = format && !raw && src.startsWith("N{", j + 1) ? Math.min(lineEnd(src, j), src.indexOf("}", j) + 1 || n) : j + 2;
      } else if (src.startsWith(delim, j)) {
        j += delim.length;
        break;
      } else if (ch === "\n" && delim.length === 1) {
        break; // unterminated
      } else if (format && ch === "{" && src[j + 1] !== "{") {
        out.add("str", src.slice(from, j));
        from = j = pyField(src, j, delim, out);
      } else {
        j += format && (ch === "{" || ch === "}") && src[j + 1] === ch ? 2 : 1;
      }
    }
    j = Math.min(j, n);
    out.add("str", src.slice(from, j));
    return j;
  }

  // an f-string's replacement field {expression!conversion:format} at k; returns the index after it
  function pyField(src, k, delim, out) {
    const n = src.length, oneLine = delim.length === 1;
    let m = k + 1, depth = 0;
    for (; m < n; m++) {
      const ch = src[m];
      if (ch === "\n" && oneLine) break;
      if (ch === '"' || ch === "'") { m = skipString(src, m) - 1; continue; }
      if (ch === "(" || ch === "[" || ch === "{") depth++;
      else if (ch === ")" || ch === "]") { if (depth) depth--; }
      else if (ch === "}") { if (!depth) break; depth--; }
      else if (!depth && (ch === ":" || (ch === "!" && src[m + 1] !== "="))) break;
    }
    out.add("int", "{");
    python(src.slice(k + 1, m), out, true);
    if (src[m] === "!") {
      let e = m + 1;
      while (e < n && /\w/.test(src[e])) e++;
      out.add("int", src.slice(m, e));
      m = e;
    }
    if (src[m] === ":") {
      let from = m;
      for (; m < n && src[m] !== "}" && !(src[m] === "\n" && oneLine) && !src.startsWith(delim, m); m++) {
        if (src[m] === "{") {
          out.add("str", src.slice(from, m));
          from = pyField(src, m, delim, out);
          m = from - 1;
        }
      }
      out.add("str", src.slice(from, m));
    }
    if (src[m] === "}") { out.add("int", "}"); m++; }
    return m;
  }

  // the end of a string inside an f-string's field (the field's code is highlighted on its own)
  function skipString(src, m) {
    const n = src.length, quote = src[m];
    const delim = src.startsWith(quote + quote + quote, m) ? quote + quote + quote : quote;
    for (let j = m + delim.length; j < n; j++) {
      if (src[j] === "\\") j++;
      else if (src.startsWith(delim, j)) return j + delim.length;
      else if (src[j] === "\n" && delim.length === 1) return j;
    }
    return n;
  }

  // ---- C# ----------------------------------------------------------------------------------------------------------

  const CS_TYPES = words("bool byte char decimal double float int long object sbyte short string uint ulong ushort " +
    "nint nuint void var dynamic");
  const CS_KEYWORDS = words("abstract as base break case catch checked class const continue default delegate do else " +
    "enum event explicit extern finally fixed for foreach goto if implicit in interface internal is lock namespace new " +
    "operator out override params private protected public readonly ref return sealed sizeof stackalloc static struct " +
    "switch this throw try typeof unchecked unsafe using virtual volatile while async await get init nameof partial " +
    "record required set when where with yield and or not global " + [...CS_TYPES].join(" "));
  const CS_CONSTANTS = words("true false null");
  const CS_DECLARES = words("class struct interface enum record");
  const CS_NUMBER = /(?:0[xX][\da-fA-F_]*|0[bB][01_]*|(?:\d[\d_]*(?:\.\d[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?\d[\d_]*)?)[uUlLfFdDmM]*/y;
  const CS_STRING_PREFIX = /[$@]+(?=")/y;

  function csharp(src, out, nested) {
    const n = src.length;
    let i = 0;
    let lineStart = !nested; // only whitespace since the line began (preprocessor directives)
    let declares = ""; // after class, struct, ...: the class of the name it declares
    let afterType = false; // the previous token can be a type: a name after it, followed by "(", is a declared method
    let afterDot = false;
    let generic = 0; // open type argument lists: "List<long> f(" declares f
    while (i < n) {
      const c = src.charCodeAt(i);
      const ch = src[i];
      let j;
      if (c === 10) { out.add(null, "\n"); i++; lineStart = true; continue; }
      if (isSpace(c)) { j = spaceEnd(src, i); out.add(null, src.slice(i, j)); i = j; continue; }
      if (ch === "/" && src[i + 1] === "/") { j = lineEnd(src, i); out.add("com", src.slice(i, j)); i = j; continue; }
      if (ch === "/" && src[i + 1] === "*") {
        j = src.indexOf("*/", i + 2);
        j = j < 0 ? n : j + 2;
        out.add("com", src.slice(i, j));
        i = j;
        continue;
      }
      if (ch === "#" && lineStart) { j = lineEnd(src, i); out.add("meta", src.slice(i, j)); i = j; continue; }
      lineStart = false;
      let next = false; // afterType for the next token
      if (ch === '"') {
        i = csString(src, i, i, out);
      } else if ((ch === "$" || ch === "@") && (j = sticky(CS_STRING_PREFIX, src, i)) > i) {
        i = csString(src, i, j, out);
      } else if (ch === "'") {
        for (j = i + 1; j < n && src[j] !== "'" && src[j] !== "\n"; j++) if (src[j] === "\\") j++;
        j = Math.min(src[j] === "'" ? j + 1 : j, n);
        out.add("str", src.slice(i, j));
        i = j;
      } else if (isDigit(c) || (ch === "." && isDigit(src.charCodeAt(i + 1)))) {
        j = sticky(CS_NUMBER, src, i);
        out.add("num", src.slice(i, j));
        i = j;
      } else if ((j = nameEnd(src, ch === "@" ? i + 1 : i)) > i + (ch === "@" ? 1 : 0)) {
        const word = src.slice(i, j); // "@name" is a name, never a keyword
        let cls = null;
        if (declares && !CS_KEYWORDS.has(word)) {
          cls = declares;
          declares = "";
          next = true;
        } else if (!afterDot && CS_CONSTANTS.has(word)) {
          cls = "con";
        } else if (!afterDot && CS_KEYWORDS.has(word)) {
          cls = "kw";
          declares = CS_DECLARES.has(word) ? "cls" : "";
          next = CS_TYPES.has(word);
        } else {
          const after = src[spaceEnd(src, j)];
          if (afterType && (after === "(" || src[j] === "<")) cls = "fn";
          next = true;
        }
        out.add(cls, word);
        i = j;
      } else {
        if (ch === "<" && isWordChar(src[i - 1])) generic++;
        else if (ch === ">" && generic > 0 && src[i - 1] !== "=") { generic--; next = true; }
        else if (ch === ";" || ch === "{" || ch === "}" || ch === "=") generic = 0;
        if (ch === "]" || (ch === "?" && (isWordChar(src[i - 1]) || src[i - 1] === ">"))) next = true;
        out.add(null, ch);
        i++;
        declares = "";
        afterDot = ch === ".";
        afterType = next;
        continue;
      }
      afterDot = false;
      afterType = next;
    }
  }

  // a string literal ("...", @"...", $"...", $@"...", """raw""", $$"""raw""") with its prefix at i and its first
  // quote at q; returns the index after it
  function csString(src, i, q, out) {
    const n = src.length, prefix = src.slice(i, q);
    const dollars = prefix.split("$").length - 1, verbatim = prefix.includes("@");
    let quotes = q;
    while (src[quotes] === '"') quotes++;
    quotes -= q;
    const raw = quotes >= 3 && !verbatim, delim = raw ? '"'.repeat(quotes) : '"';
    const braces = raw ? Math.max(dollars, 1) : 1; // the braces that open an interpolation
    let j = q + delim.length, from = i;
    while (j < n) {
      const ch = src[j];
      if (raw ? src.startsWith(delim, j) : ch === '"') {
        if (verbatim && !raw && src[j + 1] === '"') { j += 2; continue; } // "" in a verbatim string
        j += delim.length;
        break;
      }
      if (!raw && !verbatim) {
        if (ch === "\\") { j += 2; continue; }
        if (ch === "\n") break; // unterminated
      }
      if (dollars && ch === "{") {
        let e = j;
        while (src[e] === "{") e++;
        if (!raw && e - j >= 2) { j += 2; continue; } // {{ is a brace
        if (e - j < braces) { j = e; continue; } // fewer braces than $ in a raw string: text
        out.add("str", src.slice(from, e - braces));
        from = j = csField(src, e - braces, braces, !raw && !verbatim, out);
        continue;
      }
      j++;
    }
    j = Math.min(j, n);
    out.add("str", src.slice(from, j));
    return j;
  }

  // an interpolation {expression,alignment:format} whose `braces` opening braces start at k; returns the index after it
  function csField(src, k, braces, oneLine, out) {
    const n = src.length;
    let m = k + braces, depth = 0;
    for (; m < n; m++) {
      const ch = src[m];
      if (ch === "\n" && oneLine) break;
      if (ch === '"' || ch === "'") { m = csSkip(src, m) - 1; continue; }
      if (ch === "(" || ch === "[" || ch === "{") depth++;
      else if (ch === ")" || ch === "]") { if (depth) depth--; }
      else if (ch === "}") { if (!depth) break; depth--; }
      else if (ch === ":" && src[m + 1] === ":") m++; // global::
      else if (ch === ":" && !depth) break;
    }
    out.add("int", src.slice(k, k + braces));
    csharp(src.slice(k + braces, m), out, true);
    if (src[m] === ":") {
      const from = m;
      while (m < n && src[m] !== "}" && !(src[m] === "\n" && oneLine)) m++;
      out.add("str", src.slice(from, m));
    }
    let e = m;
    while (e < n && e - m < braces && src[e] === "}") e++;
    out.add("int", src.slice(m, e));
    return e;
  }

  // the end of a string or char literal inside an interpolation
  function csSkip(src, m) {
    const n = src.length, quote = src[m];
    if (quote === '"' && src.startsWith('""', m + 1)) { // raw
      let e = m;
      while (src[e] === '"') e++;
      const close = src.indexOf(src.slice(m, e), e);
      return close < 0 ? n : close + (e - m);
    }
    for (let j = m + 1; j < n; j++) {
      if (src[j] === "\\") j++;
      else if (src[j] === quote) return j + 1;
      else if (src[j] === "\n") return j;
    }
    return n;
  }

  return {
    /** The HTML of each line of `text` in `language` ("python" or "csharp"). */
    lines(text, language) {
      const out = new Lines();
      (language === "csharp" ? csharp : python)(text, out, false);
      return out.end();
    },
  };
})();
