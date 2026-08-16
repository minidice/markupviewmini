import { EditorSelection, EditorState } from "@codemirror/state";
import { markdown } from "@codemirror/lang-markdown";
import { basicSetup } from "codemirror";

const MAX_FIND_QUERY_LENGTH = 4096;

function boundedSelection(selection, documentLength) {
  const anchor = Number.isSafeInteger(selection?.anchor)
    ? Math.min(Math.max(selection.anchor, 0), documentLength)
    : 0;
  const head = Number.isSafeInteger(selection?.head)
    ? Math.min(Math.max(selection.head, 0), documentLength)
    : anchor;
  return EditorSelection.single(anchor, head);
}

function boundedFind(find = {}, previous = null) {
  const hasQuery = Object.prototype.hasOwnProperty.call(find, "query");
  return {
    query: hasQuery
      ? (typeof find.query === "string" ? find.query.slice(0, MAX_FIND_QUERY_LENGTH) : "")
      : (previous?.query ?? ""),
    matchCase: find.matchCase === true,
    wholeWord: find.wholeWord === true,
    useRegex: find.useRegex === true,
  };
}

function boundedMode(mode) {
  return mode === "edit" ? "edit" : "read";
}

function boundedScrollTop(scrollTop) {
  return Number.isFinite(scrollTop) && scrollTop >= 0 ? scrollTop : 0;
}

function boundedSplitRatio(splitRatio) {
  if (!Number.isFinite(splitRatio)) return 0.5;
  return splitRatio >= 0.1 && splitRatio <= 0.9 ? splitRatio : 0.5;
}

function boundedNewline(newline) {
  return newline === "\r\n" || newline === "\r" ? newline : "\n";
}

export function createTabStateStore(options = {}) {
  const extensions = options.extensions ?? [basicSetup, markdown()];
  const tabs = new Map();
  let activeTabId = null;
  let activeEntry = null;
  let disposed = false;

  const hydrate = (snapshot) => {
    if (disposed || typeof snapshot?.tabId !== "string" || typeof snapshot.text !== "string") {
      return null;
    }
    const revision = Number.isSafeInteger(snapshot.revision) && snapshot.revision >= 0
      ? snapshot.revision
      : 0;
    const current = tabs.get(snapshot.tabId);
    if (current && current.revision === revision && current.rawText === snapshot.text) {
      current.requestId = snapshot.requestId ?? current.requestId;
      current.windowId = snapshot.windowId ?? current.windowId;
      current.preferredNewline = boundedNewline(snapshot.preferredNewline ?? current.preferredNewline);
      if (snapshot.dirty !== undefined) current.dirty = snapshot.dirty === true;
      if (snapshot.mode !== undefined) current.mode = boundedMode(snapshot.mode);
      if (snapshot.scrollTop !== undefined) current.scrollTop = boundedScrollTop(snapshot.scrollTop);
      if (snapshot.splitRatio !== undefined) current.splitRatio = boundedSplitRatio(snapshot.splitRatio);
      if (snapshot.find !== undefined) current.find = boundedFind(snapshot.find, current.find);
      return current;
    }

    const documentState = EditorState.create({ doc: snapshot.text, extensions });
    const editorState = documentState.update({
      selection: boundedSelection(snapshot.selection, documentState.doc.length),
    }).state;
    const entry = {
      tabId: snapshot.tabId,
      requestId: snapshot.requestId ?? null,
      windowId: snapshot.windowId ?? null,
      revision,
      dirty: snapshot.dirty === true,
      rawText: snapshot.text,
      editorState,
      preferredNewline: boundedNewline(snapshot.preferredNewline),
      mode: boundedMode(snapshot.mode),
      scrollTop: boundedScrollTop(snapshot.scrollTop),
      splitRatio: boundedSplitRatio(snapshot.splitRatio),
      find: boundedFind(snapshot.find),
    };
    tabs.set(snapshot.tabId, entry);
    return entry;
  };

  const activate = (tabId, view) => {
    if (disposed) return null;
    if (view && activeEntry) {
      activeEntry.editorState = view.state;
      activeEntry.scrollTop = boundedScrollTop(view.scrollDOM?.scrollTop);
    }
    const next = tabs.get(tabId) ?? null;
    if (!next) return null;
    activeTabId = tabId;
    activeEntry = next;
    if (view && view.state !== next.editorState) view.setState(next.editorState);
    return next;
  };

  const updateRevision = (tabId, revision) => {
    const entry = tabs.get(tabId);
    if (!entry || !Number.isSafeInteger(revision) || revision < entry.revision) return null;
    entry.revision = revision;
    return entry;
  };

  const captureHints = (tabId, hints = {}) => {
    const entry = tabs.get(tabId);
    if (!entry || disposed) return null;
    if (hints.editorState instanceof EditorState) entry.editorState = hints.editorState;
    if (typeof hints.rawText === "string") entry.rawText = hints.rawText;
    if (hints.mode !== undefined) entry.mode = boundedMode(hints.mode);
    if (hints.scrollTop !== undefined) entry.scrollTop = boundedScrollTop(hints.scrollTop);
    if (hints.splitRatio !== undefined) entry.splitRatio = boundedSplitRatio(hints.splitRatio);
    if (hints.find !== undefined) entry.find = boundedFind(hints.find);
    return entry;
  };

  const remove = (tabId) => {
    if (activeTabId === tabId) {
      activeTabId = null;
      activeEntry = null;
    }
    return tabs.delete(tabId);
  };

  const dispose = () => {
    tabs.clear();
    activeTabId = null;
    activeEntry = null;
    disposed = true;
  };

  return {
    hydrate,
    activate,
    updateRevision,
    captureHints,
    remove,
    dispose,
    get activeTabId() {
      return activeTabId;
    },
  };
}
