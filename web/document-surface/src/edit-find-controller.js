const WORD_CHARACTER = "[\\p{L}\\p{M}\\p{N}\\p{Pc}]";

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

export function createEditFindController(getEditorView, view = {}) {
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

  const activate = (index) => {
    const editor = getEditorView?.();
    if (!editor || matches.length === 0) {
      state.activeIndex = -1;
      render();
      return null;
    }

    state.activeIndex = (index + matches.length) % matches.length;
    const match = matches[state.activeIndex];
    editor.dispatch({
      selection: { anchor: match.from, head: match.to },
      scrollIntoView: true,
    });
    render();
    return match;
  };

  const search = (query, options = {}) => {
    if (disposed) return [];
    state.query = typeof query === "string" ? query : "";
    state.matchCase = options.matchCase === true;
    state.wholeWord = options.wholeWord === true;
    state.useRegex = options.useRegex === true || options.regex === true;
    state.activeIndex = -1;
    state.total = 0;
    state.error = null;
    matches = [];

    const editor = getEditorView?.();
    if (!editor || state.query === "") {
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

    const text = editor.state.doc.toString();
    matcher.lastIndex = 0;
    let match;
    while ((match = matcher.exec(text)) !== null) {
      if (match[0].length === 0) {
        matches = [];
        state.error = "Regular expressions that match empty text are not supported.";
        render();
        return [];
      }
      matches.push({ from: match.index, to: match.index + match[0].length });
    }

    state.total = matches.length;
    if (matches.length > 0) activate(0);
    else render();
    return matches.map(({ from, to }) => ({ from, to }));
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
      matches = [];
      state.activeIndex = -1;
      state.total = 0;
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
      matches = [];
      disposed = true;
    },
  };
}
