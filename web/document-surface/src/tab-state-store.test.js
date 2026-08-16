import { undo } from "@codemirror/commands";
import { EditorSelection } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { describe, expect, it } from "vitest";
import { createTabStateStore } from "./tab-state-store.js";

const TAB_A = "22222222-2222-4222-8222-222222222222";
const TAB_B = "33333333-3333-4333-8333-333333333333";

describe("per-tab editor state", () => {
  it("rehydrates the authoritative dirty projection after a surface replacement", () => {
    const store = createTabStateStore();

    store.hydrate({ tabId: TAB_A, text: "unsaved", revision: 9, dirty: true });

    expect(store.activate(TAB_A)).toMatchObject({
      rawText: "unsaved",
      revision: 9,
      dirty: true,
    });
  });

  it("restores the exact editor state, undo history, and UI hints after switching tabs", () => {
    // Break caught: rebuilding a tab from text loses selection and CodeMirror undo history.
    const store = createTabStateStore();
    store.hydrate({
      tabId: TAB_A,
      text: "# A",
      revision: 0,
      preferredNewline: "\r\n",
      mode: "edit",
      selection: { anchor: 3, head: 1 },
      scrollTop: 24,
      splitRatio: 0.35,
      find: { query: "alpha", matchCase: true, wholeWord: false, useRegex: false },
    });
    store.hydrate({
      tabId: TAB_B,
      text: "# B",
      revision: 2,
      preferredNewline: "\n",
      mode: "read",
      selection: { anchor: 0, head: 0 },
      scrollTop: 90,
      splitRatio: 0.65,
      find: { query: "beta", matchCase: false, wholeWord: true, useRegex: true },
    });

    const initial = store.activate(TAB_A);
    const transaction = initial.editorState.update({ changes: { from: 3, insert: " edited" } });
    store.captureHints(TAB_A, {
      editorState: transaction.state,
      scrollTop: 48,
      mode: "edit",
      splitRatio: 0.4,
      find: { query: "updated", matchCase: false, wholeWord: true, useRegex: false },
    });
    const beforeSwitch = store.activate(TAB_A).editorState;

    expect(store.activate(TAB_B)).toMatchObject({
      revision: 2,
      mode: "read",
      scrollTop: 90,
      splitRatio: 0.65,
      find: { query: "beta", matchCase: false, wholeWord: true, useRegex: true },
    });
    const restored = store.activate(TAB_A);

    expect(restored.editorState).toBe(beforeSwitch);
    expect(restored.editorState.selection.main).toMatchObject({ anchor: 3, head: 1 });
    expect(restored).toMatchObject({
      revision: 0,
      preferredNewline: "\r\n",
      mode: "edit",
      scrollTop: 48,
      splitRatio: 0.4,
      find: { query: "updated", matchCase: false, wholeWord: true, useRegex: false },
    });

    const view = new EditorView({ state: restored.editorState });
    expect(undo(view)).toBe(true);
    expect(view.state.doc.toString()).toBe("# A");
    view.destroy();
  });

  it("keeps the exact state when the host repeats an accepted snapshot", () => {
    // Break caught: ordinary tab reactivation reconstructs EditorState and erases WebView-local history.
    const store = createTabStateStore();
    store.hydrate({ tabId: TAB_A, text: "before", revision: 4 });
    const edited = store.activate(TAB_A).editorState.update({
      changes: { from: 6, insert: " local" },
      selection: EditorSelection.cursor(12),
    }).state;
    store.captureHints(TAB_A, { editorState: edited, rawText: "before local" });

    store.hydrate({ tabId: TAB_A, text: "before local", revision: 4 });

    expect(store.activate(TAB_A).editorState).toBe(edited);
  });

  it("keeps each tab query while host activation updates only global find options", () => {
    // Break caught: A(alpha) -> B -> A replaces alpha with the host's empty global query.
    const store = createTabStateStore();
    store.hydrate({
      tabId: TAB_A,
      text: "A",
      revision: 1,
      find: { query: "alpha", matchCase: false, wholeWord: false, useRegex: false },
    });
    store.hydrate({
      tabId: TAB_B,
      text: "B",
      revision: 1,
      find: { query: "beta", matchCase: false, wholeWord: false, useRegex: false },
    });

    store.activate(TAB_B);
    store.hydrate({
      tabId: TAB_A,
      text: "A",
      revision: 1,
      find: { matchCase: true, wholeWord: true, useRegex: false },
    });

    expect(store.activate(TAB_A).find).toEqual({
      query: "alpha",
      matchCase: true,
      wholeWord: true,
      useRegex: false,
    });
  });

  it("bounds serializable hints and discards all tab state on disposal", () => {
    // Break caught: unbounded or malformed UI hints leak into session/recovery payloads.
    const store = createTabStateStore();
    store.hydrate({ tabId: TAB_A, text: "text", revision: 1 });

    store.captureHints(TAB_A, {
      scrollTop: Number.POSITIVE_INFINITY,
      splitRatio: 12,
      mode: "unknown",
      find: { query: "x".repeat(5000), matchCase: 1, wholeWord: true, useRegex: false },
    });
    const state = store.activate(TAB_A);

    expect(state.scrollTop).toBe(0);
    expect(state.splitRatio).toBe(0.5);
    expect(state.mode).toBe("read");
    expect(state.find).toEqual({
      query: "x".repeat(4096),
      matchCase: false,
      wholeWord: true,
      useRegex: false,
    });
    store.dispose();
    expect(store.activate(TAB_A)).toBeNull();
  });
});
