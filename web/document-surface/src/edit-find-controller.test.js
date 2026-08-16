import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { describe, expect, it, vi } from "vitest";
import { createEditFindController } from "./edit-find-controller.js";

function harness(text) {
  const editor = new EditorView({
    state: EditorState.create({ doc: text }),
  });
  const states = [];
  const controller = createEditFindController(
    () => editor,
    { render: (state) => states.push(state) },
  );
  return {
    controller,
    editor,
    states,
    dispose() {
      controller.dispose();
      editor.destroy();
    },
  };
}

describe("CodeMirror edit-mode find", () => {
  it("honors case and Unicode whole-word marks and connector punctuation", () => {
    // Break caught: ASCII \b treats combining marks and connector punctuation as word boundaries.
    const h = harness("Cafe cafe a\u0301 a\u0301x foo_bar foo");

    expect(h.controller.search("Cafe", { matchCase: true })).toHaveLength(1);
    expect(h.controller.search("cafe", { matchCase: false })).toHaveLength(2);
    expect(h.controller.search("a\u0301", { wholeWord: true })).toEqual([{ from: 10, to: 12 }]);
    expect(h.controller.search("foo", { wholeWord: true })).toEqual([{ from: 25, to: 28 }]);
    h.dispose();
  });

  it("reports invalid and zero-length regular expressions without throwing", () => {
    // Break caught: a malformed/empty regex escapes into the WebView event loop or creates an infinite match loop.
    const h = harness("aaa");

    expect(() => h.controller.search("[", { useRegex: true })).not.toThrow();
    expect(h.states.at(-1)).toMatchObject({ total: 0 });
    expect(h.states.at(-1).error).not.toBeNull();
    expect(() => h.controller.search("a*", { useRegex: true })).not.toThrow();
    expect(h.states.at(-1)).toMatchObject({
      total: 0,
      error: "Regular expressions that match empty text are not supported.",
    });
    h.dispose();
  });

  it("wraps next and previous while selecting and scrolling the CodeMirror range", () => {
    // Break caught: edit find updates a count but does not move/scroll the source selection.
    const h = harness("one two one");
    const dispatch = vi.spyOn(h.editor, "dispatch");

    h.controller.search("one");
    expect(h.editor.state.selection.main).toMatchObject({ from: 0, to: 3 });
    expect(dispatch.mock.calls.at(-1)[0]).toMatchObject({ scrollIntoView: true });
    h.controller.previousMatch();
    expect(h.editor.state.selection.main).toMatchObject({ from: 8, to: 11 });
    h.controller.nextMatch();
    expect(h.editor.state.selection.main).toMatchObject({ from: 0, to: 3 });
    expect(h.states.at(-1)).toMatchObject({ activeIndex: 0, total: 2 });
    h.dispose();
  });

  it("recomputes the same retained query and options against replacement editor text", () => {
    // Break caught: switching read/edit targets clears find options or leaves counts from the old target.
    const h = harness("Alpha alpha");
    h.controller.search("Alpha", { matchCase: true, wholeWord: true, useRegex: false });
    h.editor.setState(EditorState.create({ doc: "Alpha Alpha" }));

    h.controller.openFind();

    expect(h.states.at(-1)).toMatchObject({
      query: "Alpha",
      matchCase: true,
      wholeWord: true,
      useRegex: false,
      total: 2,
    });
    h.dispose();
  });
});
