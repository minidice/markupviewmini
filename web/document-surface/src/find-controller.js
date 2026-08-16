const EXCLUDED_TEXT_SELECTOR = [
  "script",
  "style",
  "[hidden]",
  '[aria-hidden="true"]',
  "svg",
  ".katex",
  ".mermaid-diagram",
  ".mermaid-placeholder",
  "[data-mermaid-source]",
].join(", ");

const VISIBLE_EXCLUDED_SELECTOR = [
  "svg",
  ".katex",
  ".mermaid-diagram",
  ".mermaid-placeholder",
  "[data-mermaid-source]",
].join(", ");

const BLOCK_ELEMENTS = new Set([
  "ADDRESS", "ARTICLE", "ASIDE", "BLOCKQUOTE", "DD", "DIV", "DL", "DT", "FIELDSET",
  "FIGCAPTION", "FIGURE", "FOOTER", "FORM", "H1", "H2", "H3", "H4", "H5", "H6",
  "HEADER", "HR", "LI", "MAIN", "NAV", "OL", "P", "PRE", "SECTION", "TABLE", "TBODY",
  "TD", "TFOOT", "TH", "THEAD", "TR", "UL",
]);

const WORD_CHARACTER = "[\\p{L}\\p{M}\\p{N}\\p{Pc}]";
const COLLAPSIBLE_WHITESPACE = /[\t\n\f\r ]/u;
const WHITESPACE_RUNS = /[\t\n\f\r ]+|[^\t\n\f\r ]+/gu;
const PRESERVED_WHITE_SPACE = new Set(["break-spaces", "pre", "pre-wrap"]);

function escapeRegularExpression(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

function createMatcher(query, options) {
  const source = options.useRegex ? query : escapeRegularExpression(query);
  const bounded = options.wholeWord
    ? `(?<!${WORD_CHARACTER})(?:${source})(?!${WORD_CHARACTER})`
    : source;
  return new RegExp(bounded, options.matchCase ? "gu" : "giu");
}

function renderedTextGroups(root) {
  if (!root?.ownerDocument || root.matches?.(EXCLUDED_TEXT_SELECTOR)) return [];
  const groups = [];
  const preservedWhitespace = new WeakMap();
  let group = null;

  const flush = () => {
    if (group && (/\S/u.test(group.text) || group.hasPreservedText)) groups.push(group);
    group = null;
  };
  const ensureGroup = () => {
    group ??= { text: "", spans: [], pendingWhitespace: [], hasPreservedText: false };
    return group;
  };
  const appendMappedText = (text, node, rawStart, rawEnd, collapsed = false) => {
    const current = ensureGroup();
    const start = current.text.length;
    current.text += text;
    current.spans.push({
      node,
      start,
      end: current.text.length,
      rawStart,
      rawEnd,
      collapsed,
    });
  };
  const flushCollapsedWhitespace = () => {
    if (!group?.pendingWhitespace.length) return;
    if (group.text.length > 0) {
      const start = group.text.length;
      group.text += " ";
      for (const whitespace of group.pendingWhitespace) {
        group.spans.push({ ...whitespace, start, end: start + 1, collapsed: true });
      }
    }
    group.pendingWhitespace = [];
  };
  const preservesNodeWhitespace = (node) => {
    const parent = node.parentElement;
    if (!parent) return false;
    if (parent.closest("pre")) return true;
    if (preservedWhitespace.has(parent)) return preservedWhitespace.get(parent);
    const whiteSpace = root.ownerDocument.defaultView
      ?.getComputedStyle?.(parent)?.whiteSpace?.toLowerCase();
    const preserves = PRESERVED_WHITE_SPACE.has(whiteSpace);
    preservedWhitespace.set(parent, preserves);
    return preserves;
  };
  const append = (node) => {
    if (node.data.length === 0) return;
    if (preservesNodeWhitespace(node)) {
      flushCollapsedWhitespace();
      appendMappedText(node.data, node, 0, node.data.length);
      group.hasPreservedText = true;
      return;
    }

    WHITESPACE_RUNS.lastIndex = 0;
    let run;
    while ((run = WHITESPACE_RUNS.exec(node.data)) !== null) {
      const rawStart = run.index;
      const rawEnd = rawStart + run[0].length;
      if (COLLAPSIBLE_WHITESPACE.test(run[0][0])) {
        ensureGroup().pendingWhitespace.push({ node, rawStart, rawEnd });
      } else {
        flushCollapsedWhitespace();
        appendMappedText(run[0], node, rawStart, rawEnd);
      }
    }
  };
  const walk = (node) => {
    if (node.nodeType === 3) {
      append(node);
      return;
    }
    if (node.nodeType !== 1) return;
    if (node !== root && node.matches(EXCLUDED_TEXT_SELECTOR)) {
      if (node.matches(VISIBLE_EXCLUDED_SELECTOR)) flush();
      return;
    }

    const isBoundary = node !== root
      && (BLOCK_ELEMENTS.has(node.tagName) || node.tagName === "BR");
    if (isBoundary) flush();
    for (const child of node.childNodes) walk(child);
    if (isBoundary) flush();
  };

  walk(root);
  flush();
  return groups;
}

function collectMatches(root, matcher) {
  const matches = [];
  for (const group of renderedTextGroups(root)) {
    matcher.lastIndex = 0;
    let match;
    while ((match = matcher.exec(group.text)) !== null) {
      if (match[0].length === 0) return { matches: [], hasZeroLength: true };
      const start = match.index;
      const end = start + match[0].length;
      const fragments = group.spans
        .filter((span) => span.start < end && span.end > start)
        .map((span) => {
          if (span.collapsed) {
            return { node: span.node, start: span.rawStart, end: span.rawEnd };
          }
          return {
            node: span.node,
            start: span.rawStart + Math.max(0, start - span.start),
            end: span.rawStart + Math.min(span.end, end) - span.start,
          };
        })
        .reduce((result, fragment) => {
          const previous = result.at(-1);
          if (previous?.node === fragment.node && fragment.start <= previous.end) {
            previous.end = Math.max(previous.end, fragment.end);
          } else {
            result.push(fragment);
          }
          return result;
        }, []);
      matches.push({ fragments });
    }
  }
  return { matches, hasZeroLength: false };
}

function wrapMatches(matches) {
  const fragments = matches.flatMap((match) => match.fragments);
  for (let index = fragments.length - 1; index >= 0; index -= 1) {
    const fragment = fragments[index];
    const mark = fragment.node.ownerDocument.createElement("mark");
    mark.className = "document-find-match";
    const range = fragment.node.ownerDocument.createRange();
    range.setStart(fragment.node, fragment.start);
    range.setEnd(fragment.node, fragment.end);
    range.surroundContents(mark);
    fragment.mark = mark;
  }
  for (const match of matches) match.marks = match.fragments.map((fragment) => fragment.mark);
  return matches;
}

export function createFindController(root, view = {}) {
  let disposed = false;
  let matches = [];
  const state = {
    query: "",
    matchCase: false,
    wholeWord: false,
    useRegex: false,
    activeIndex: -1,
    total: 0,
    error: null,
  };

  const render = () => view.render?.({ ...state });

  const clearMarks = () => {
    const parents = new Set();
    for (const mark of matches.flatMap((match) => match.marks)) {
      const parent = mark.parentNode;
      if (!parent) continue;
      parents.add(parent);
      mark.replaceWith(mark.ownerDocument.createTextNode(mark.textContent ?? ""));
    }
    for (const parent of parents) parent.normalize();
    matches = [];
    state.activeIndex = -1;
    state.total = 0;
  };

  const activate = (index) => {
    for (const match of matches) {
      for (const mark of match.marks) {
        mark.classList.remove("is-document-find-active");
        mark.removeAttribute("aria-current");
      }
    }
    if (matches.length === 0) {
      state.activeIndex = -1;
      render();
      return null;
    }
    state.activeIndex = (index + matches.length) % matches.length;
    const active = matches[state.activeIndex];
    for (const mark of active.marks) mark.classList.add("is-document-find-active");
    active.marks[0]?.setAttribute("aria-current", "true");
    active.marks[0]?.scrollIntoView?.({ block: "center" });
    render();
    return active.marks[0];
  };

  const search = (query, options = {}) => {
    if (disposed) return [];
    clearMarks();
    state.query = typeof query === "string" ? query : "";
    state.matchCase = options.matchCase === true;
    state.wholeWord = options.wholeWord === true;
    state.useRegex = options.useRegex === true || options.regex === true;
    state.error = null;

    if (state.query === "") {
      render();
      return [];
    }

    let matcher;
    try {
      matcher = createMatcher(state.query, state);
    } catch (error) {
      state.error = error instanceof Error ? error.message : "Invalid regular expression.";
      render();
      return [];
    }

    if (matcher.exec("")?.[0].length === 0) {
      state.error = "Regular expressions that match empty text are not supported.";
      render();
      return [];
    }

    const result = collectMatches(root, matcher);
    if (result.hasZeroLength) {
      state.error = "Regular expressions that match empty text are not supported.";
      render();
      return [];
    }

    matches = wrapMatches(result.matches);
    state.total = matches.length;
    if (matches.length === 0) {
      render();
      return [];
    }
    activate(0);
    return matches.map((match) => match.marks[0]);
  };

  return {
    openFind() {
      if (disposed) return [];
      if (state.query) {
        return search(state.query, {
          matchCase: state.matchCase,
          wholeWord: state.wholeWord,
          useRegex: state.useRegex,
        });
      }
      render();
      return [];
    },
    closeFind() {
      if (disposed) return;
      clearMarks();
      state.error = null;
      render();
    },
    search,
    nextMatch() {
      if (disposed || matches.length === 0) return null;
      return activate(state.activeIndex + 1);
    },
    previousMatch() {
      if (disposed || matches.length === 0) return null;
      return activate(state.activeIndex - 1);
    },
    dispose() {
      if (disposed) return;
      clearMarks();
      disposed = true;
    },
  };
}
