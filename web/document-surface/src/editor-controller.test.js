import { describe, expect, it, vi } from "vitest";
import {
  applyRawChanges,
  createEditorController,
  editWithinLimits,
  EDITOR_LIMITS,
} from "./editor-controller.js";
import { mountDocumentSurface } from "./editor-app.js";
import { createTabStateStore } from "./tab-state-store.js";

const WINDOW_ID = "11111111-1111-4111-8111-111111111111";
const TAB_ID = "22222222-2222-4222-8222-222222222222";
const REQUEST_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const BATCH_ID = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

function activation(text = "one\ntwo\nthree", overrides = {}) {
  return {
    version: 1,
    type: "document.activate",
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: 7,
    payload: {
      path: "C:\\docs\\readme.md",
      text,
      mode: "edit",
      line: null,
      anchor: null,
      assetBaseUrl: "https://document-assets.local/",
      preferredNewline: "\r\n",
    },
    ...overrides,
  };
}

function harness(options = {}) {
  const messages = [];
  const host = { postMessage: (message) => messages.push(message) };
  const store = createTabStateStore();
  const controller = createEditorController({
    host,
    store,
    renderAccepted: vi.fn(),
    ...options,
  });
  controller.hydrate(activation());
  return { controller, messages, store };
}

function hostResponse(type, revision, payload = {}) {
  return {
    version: 1,
    type,
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: revision,
    payload,
  };
}

describe("incremental CodeMirror transactions", () => {
  it("accepts exact production edit limits and rejects one code unit or range over", () => {
    expect(editWithinLimits(10_000, 64 * 1024 * 1024)).toBe(true);
    expect(editWithinLimits(10_000, 64 * 1024 * 1024 + 1)).toBe(false);
    expect(editWithinLimits(10_001, 64 * 1024 * 1024)).toBe(false);
  });

  it("reports exact selection and scroll hints for host-owned roundtrip", () => {
    const onViewChanged = vi.fn();
    const { controller } = harness({ onViewChanged });

    controller.view.dispatch({ selection: { anchor: 7, head: 3 } });
    controller.view.scrollDOM.scrollTop = 48;
    controller.view.scrollDOM.dispatchEvent(new Event("scroll"));

    expect(onViewChanged).toHaveBeenCalledWith(TAB_ID, {
      selection: { anchor: 7, head: 3 },
      scrollTop: 48,
    });
    controller.dispose();
  });

  it("reports each post-transaction hint only after that document edit is accepted", () => {
    const onViewChanged = vi.fn();
    const { controller } = harness({ onViewChanged });
    controller.view.scrollDOM.scrollTop = 37;

    controller.view.dispatch({
      changes: { from: 0, insert: "A" },
      selection: { anchor: 1 },
    });
    controller.view.dispatch({
      changes: { from: 1, insert: "B" },
      selection: { anchor: 2 },
    });

    expect(onViewChanged).not.toHaveBeenCalled();

    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8))).toBe(true);
    expect(onViewChanged).toHaveBeenCalledTimes(1);
    expect(onViewChanged).toHaveBeenLastCalledWith(TAB_ID, {
      selection: { anchor: 1, head: 1 },
      scrollTop: 37,
    });

    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 9))).toBe(true);
    expect(onViewChanged).toHaveBeenCalledTimes(2);
    expect(onViewChanged).toHaveBeenLastCalledWith(TAB_ID, {
      selection: { anchor: 2, head: 2 },
      scrollTop: 37,
    });
    controller.dispose();
  });

  it("keeps edit one hints when rapid edit two is rejected", () => {
    const onViewChanged = vi.fn();
    const { controller, messages, store } = harness({ onViewChanged });
    controller.view.dispatch({ selection: { anchor: 3 } });
    controller.view.scrollDOM.scrollTop = 12;
    controller.view.scrollDOM.dispatchEvent(new Event("scroll"));
    onViewChanged.mockClear();

    controller.view.scrollDOM.scrollTop = 20;
    controller.view.dispatch({
      changes: { from: 3, insert: "X" },
      selection: { anchor: 4 },
    });
    controller.view.scrollDOM.scrollTop = 77;
    controller.view.dispatch({
      changes: { from: 4, insert: "Y" },
      selection: { anchor: 5 },
    });

    expect(onViewChanged).not.toHaveBeenCalled();
    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8))).toBe(true);
    expect(onViewChanged).toHaveBeenLastCalledWith(TAB_ID, {
      selection: { anchor: 4, head: 4 },
      scrollTop: 20,
    });
    onViewChanged.mockClear();
    expect(controller.handleHostMessage(hostResponse("document.changeRejected", 8, {
      resyncRequestId: "55555555-5555-4555-8555-555555555555",
    }))).toBe(true);

    const restored = store.activate(TAB_ID);
    expect(controller.view.state.doc.toString()).toBe("oneX\ntwo\nthree");
    expect(controller.view.state.selection.main).toMatchObject({ anchor: 4, head: 4 });
    expect(controller.view.scrollDOM.scrollTop).toBe(20);
    expect(restored.editorState.selection.main).toMatchObject({ anchor: 4, head: 4 });
    expect(restored.scrollTop).toBe(20);
    expect(onViewChanged).not.toHaveBeenCalled();
    expect(messages.at(-1)).toEqual({
      version: 1,
      type: "document.resync",
      requestId: "55555555-5555-4555-8555-555555555555",
      windowId: WINDOW_ID,
      tabId: TAB_ID,
      documentRevision: 8,
      payload: {},
    });
    expect(JSON.stringify(messages.at(-1))).not.toMatch(/text|body/iu);
    controller.dispose();
  });

  it("uses the approved production serialization limits", () => {
    // Break caught: test-sized defaults accidentally ship and weaken the host/web contract.
    expect(EDITOR_LIMITS).toEqual({
      ordinaryInserted: 16 * 1024 * 1024,
      chunkInserted: 1024 * 1024,
      totalInserted: 64 * 1024 * 1024,
      maxChanges: 10_000,
    });
  });

  it("reconstructs 10,000 changes in one left-to-right pass", () => {
    // Break caught: rebuilding the entire growing document once per range becomes quadratic.
    const rawText = "ab".repeat(10_000);
    const changes = Array.from({ length: 10_000 }, (_, index) => ({
      from: index * 2,
      to: index * 2 + 1,
      insertedText: "X",
    }));
    const originalSlice = String.prototype.slice;
    let sourceSlices = 0;
    const slice = vi.spyOn(String.prototype, "slice").mockImplementation(function countedSlice(...args) {
      if (this.toString() === rawText) sourceSlices += 1;
      return originalSlice.apply(this, args);
    });

    const result = applyRawChanges(rawText, changes);

    slice.mockRestore();
    expect(result).toBe("Xb".repeat(10_000));
    expect(sourceSlices).toBe(10_001);
  });

  it("posts one correlated ordinary envelope with ascending pre-transaction UTF-16 ranges", () => {
    // Break caught: serializing the final document or post-change offsets corrupts disjoint edits.
    const { controller, messages } = harness();

    controller.view.dispatch({
      changes: [
        { from: 0, to: 3, insert: "ONE\nX" },
        { from: 8, to: 13, insert: "THREE" },
      ],
    });

    expect(messages).toEqual([{
      version: 1,
      type: "document.changed",
      requestId: REQUEST_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ID,
      documentRevision: 7,
      payload: {
        expectedRevision: 7,
        changes: [
          { from: 0, to: 3, insertedText: "ONE\r\nX" },
          { from: 8, to: 13, insertedText: "THREE" },
        ],
      },
    }]);
    expect(messages[0].payload).not.toHaveProperty("text");
    expect(messages[0].payload).not.toHaveProperty("body");
    controller.dispose();
  });

  it("maps CodeMirror positions back to raw CRLF UTF-16 offsets", () => {
    // Break caught: CodeMirror stores line breaks as one position, but C# counts both CR and LF.
    const { controller, messages } = harness();
    controller.hydrate(activation("one\r\ntwo\r\nthree", { documentRevision: 8 }));

    controller.view.dispatch({ changes: { from: 8, to: 13, insert: "done\nnow" } });

    expect(messages[0]).toMatchObject({
      documentRevision: 8,
      payload: {
        expectedRevision: 8,
        changes: [{ from: 10, to: 15, insertedText: "done\r\nnow" }],
      },
    });
    controller.dispose();
  });

  it("preserves UTF-16 offsets and inserted text around a surrogate pair", () => {
    // Break caught: code-point indexing reports emoji ranges one code unit short to C#.
    const { controller, messages } = harness();
    controller.hydrate(activation("a😀b", { documentRevision: 8 }));

    controller.view.dispatch({ changes: { from: 1, to: 3, insert: "x😀" } });

    expect(messages).toEqual([expect.objectContaining({
      type: "document.changed",
      documentRevision: 8,
      payload: {
        expectedRevision: 8,
        changes: [{ from: 1, to: 3, insertedText: "x😀" }],
      },
    })]);
    expect(controller.view.state.doc.toString()).toBe("ax😀b");
    controller.dispose();
  });

  it("accepts exactly 10,000 ordinary ranges and rejects one more atomically", () => {
    // Break caught: an off-by-one rejects the maximum valid edit or applies an unsendable 10,001st range.
    const exactText = "ab".repeat(10_000);
    const exact = harness();
    exact.controller.hydrate(activation(exactText, { documentRevision: 8 }));
    exact.controller.view.dispatch({ changes: Array.from({ length: 10_000 }, (_, index) => ({
      from: index * 2,
      to: index * 2 + 1,
      insert: "X",
    })) });

    expect(exact.messages).toHaveLength(1);
    expect(exact.messages[0].type).toBe("document.changed");
    expect(exact.messages[0].payload.changes).toHaveLength(10_000);
    expect(exact.controller.view.state.doc.toString()).toBe("Xb".repeat(10_000));
    exact.controller.dispose();

    const overText = "ab".repeat(10_001);
    const over = harness();
    over.controller.hydrate(activation(overText, { documentRevision: 8 }));
    const beforeView = over.controller.view.state;
    const beforeStore = over.store.activate(TAB_ID);
    over.controller.view.dispatch({ changes: Array.from({ length: 10_001 }, (_, index) => ({
      from: index * 2,
      to: index * 2 + 1,
      insert: "X",
    })) });

    expect(over.messages).toEqual([]);
    expect(over.controller.view.state).toBe(beforeView);
    expect(over.store.activate(TAB_ID).editorState).toBe(beforeStore.editorState);
    expect(over.store.activate(TAB_ID).rawText).toBe(overText);
    over.controller.dispose();
  });

  it("uses ordinary serialization at the inserted limit and batches one code unit over", () => {
    // Break caught: the 16-Mi comparison is inverted or off by one.
    const exact = harness({
      limits: { ordinaryInserted: 4, chunkInserted: 3, totalInserted: 8, maxChanges: 10_000 },
    });
    exact.controller.view.dispatch({ changes: { from: 0, insert: "1234" } });
    expect(exact.messages.map(({ type }) => type)).toEqual(["document.changed"]);
    exact.controller.dispose();

    const over = harness({
      limits: { ordinaryInserted: 4, chunkInserted: 3, totalInserted: 8, maxChanges: 10_000 },
    });
    over.controller.view.dispatch({ changes: { from: 0, insert: "12345" } });
    expect(over.messages.map(({ type }) => type)).toEqual([
      "document.changeBatchStart",
      "document.changeBatchChunk",
      "document.changeBatchChunk",
      "document.changeBatchCommit",
    ]);
    over.controller.dispose();
  });

  it("serializes a large transaction as Start, bounded Chunks, then Commit", () => {
    // Break caught: a transaction above the ordinary limit falls back to a full-body replacement.
    vi.spyOn(crypto, "randomUUID").mockReturnValue(BATCH_ID);
    const { controller, messages } = harness({
      limits: { ordinaryInserted: 4, chunkInserted: 3, totalInserted: 20, maxChanges: 10_000 },
    });

    controller.view.dispatch({ changes: { from: 0, to: 3, insert: "abcdefg" } });

    expect(messages.map((message) => message.type)).toEqual([
      "document.changeBatchStart",
      "document.changeBatchChunk",
      "document.changeBatchChunk",
      "document.changeBatchChunk",
      "document.changeBatchCommit",
    ]);
    expect(messages[0]).toMatchObject({
      requestId: REQUEST_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ID,
      documentRevision: 7,
      payload: {
        batchId: BATCH_ID,
        expectedRevision: 7,
        changes: [{ from: 0, to: 3, insertedLength: 7 }],
      },
    });
    expect(messages.slice(1, -1).map((message) => message.payload)).toEqual([
      { batchId: BATCH_ID, changeIndex: 0, offset: 0, text: "abc" },
      { batchId: BATCH_ID, changeIndex: 0, offset: 3, text: "def" },
      { batchId: BATCH_ID, changeIndex: 0, offset: 6, text: "g" },
    ]);
    expect(messages.at(-1).payload).toEqual({ batchId: BATCH_ID });
    controller.dispose();
    vi.restoreAllMocks();
  });

  it("never splits a surrogate pair across batch chunks", () => {
    // Break caught: a chunk ending with a lone high surrogate is rejected by System.Text.Json.
    vi.spyOn(crypto, "randomUUID").mockReturnValue(BATCH_ID);
    const { controller, messages } = harness({
      limits: { ordinaryInserted: 1, chunkInserted: 3, totalInserted: 20, maxChanges: 10_000 },
    });

    controller.view.dispatch({ changes: { from: 0, insert: "ab😀z" } });

    const chunks = messages
      .filter((message) => message.type === "document.changeBatchChunk")
      .map((message) => message.payload);
    expect(chunks).toEqual([
      { batchId: BATCH_ID, changeIndex: 0, offset: 0, text: "ab" },
      { batchId: BATCH_ID, changeIndex: 0, offset: 2, text: "😀z" },
    ]);
    expect(chunks.every(({ text }) => !/(?:[\uD800-\uDBFF]$|^[\uDC00-\uDFFF])/u.test(text))).toBe(true);
    expect(() => JSON.stringify(messages)).not.toThrow();
    controller.dispose();
    vi.restoreAllMocks();
  });

  it("emits exact-size chunks and preserves multiple-change batch order", () => {
    // Break caught: batch metadata or chunk offsets bleed across adjacent change indexes.
    vi.spyOn(crypto, "randomUUID").mockReturnValue(BATCH_ID);
    const { controller, messages, store } = harness({
      limits: { ordinaryInserted: 1, chunkInserted: 3, totalInserted: 10, maxChanges: 10_000 },
    });
    controller.hydrate(activation("abcdef", { documentRevision: 8 }));

    controller.view.dispatch({ changes: [
      { from: 0, to: 1, insert: "WXYZ" },
      { from: 4, to: 6, insert: "😀Q" },
    ] });

    expect(messages.map(({ type }) => type)).toEqual([
      "document.changeBatchStart",
      "document.changeBatchChunk",
      "document.changeBatchChunk",
      "document.changeBatchChunk",
      "document.changeBatchCommit",
    ]);
    expect(messages[0].payload.changes).toEqual([
      { from: 0, to: 1, insertedLength: 4 },
      { from: 4, to: 6, insertedLength: 3 },
    ]);
    expect(messages.slice(1, -1).map(({ payload }) => payload)).toEqual([
      { batchId: BATCH_ID, changeIndex: 0, offset: 0, text: "WXY" },
      { batchId: BATCH_ID, changeIndex: 0, offset: 3, text: "Z" },
      { batchId: BATCH_ID, changeIndex: 1, offset: 0, text: "😀Q" },
    ]);
    expect(controller.view.state.doc.toString()).toBe("WXYZbcd😀Q");
    expect(store.activate(TAB_ID).rawText).toBe("WXYZbcd😀Q");
    controller.dispose();
    vi.restoreAllMocks();
  });

  it("accepts the atomic total exactly and rolls back view and store one code unit over", () => {
    // Break caught: the 64-Mi total permits one extra code unit or rejects its exact boundary.
    const exact = harness({
      limits: { ordinaryInserted: 2, chunkInserted: 3, totalInserted: 6, maxChanges: 10_000 },
    });
    exact.controller.view.dispatch({ changes: { from: 0, insert: "123456" } });
    expect(exact.messages.map(({ type }) => type)).toEqual([
      "document.changeBatchStart",
      "document.changeBatchChunk",
      "document.changeBatchChunk",
      "document.changeBatchCommit",
    ]);
    expect(exact.messages.slice(1, -1).map(({ payload }) => payload.text)).toEqual(["123", "456"]);
    exact.controller.dispose();

    const over = harness({
      limits: { ordinaryInserted: 2, chunkInserted: 3, totalInserted: 6, maxChanges: 10_000 },
    });
    const beforeView = over.controller.view.state;
    const beforeStore = over.store.activate(TAB_ID);
    over.controller.view.dispatch({ changes: { from: 0, insert: "1234567" } });

    expect(over.messages).toEqual([]);
    expect(over.controller.view.state).toBe(beforeView);
    expect(over.store.activate(TAB_ID).editorState).toBe(beforeStore.editorState);
    expect(over.store.activate(TAB_ID).rawText).toBe("one\ntwo\nthree");
    over.controller.dispose();
  });

  it("rejects a transaction above the atomic total without changing the editor state", () => {
    // Break caught: applying an unsendable transaction leaves CodeMirror ahead of the authoritative buffer.
    const { controller, messages } = harness({
      limits: { ordinaryInserted: 4, chunkInserted: 3, totalInserted: 5, maxChanges: 10_000 },
    });
    const before = controller.view.state;

    controller.view.dispatch({ changes: { from: 0, to: 0, insert: "123456" } });

    expect(controller.view.state).toBe(before);
    expect(controller.view.state.doc.toString()).toBe("one\ntwo\nthree");
    expect(messages).toEqual([]);
    controller.dispose();
  });

  it("rejects too many ranges atomically", () => {
    // Break caught: range-count overflow applies locally even though no valid host message can represent it.
    const { controller, messages } = harness({
      limits: { ordinaryInserted: 20, chunkInserted: 3, totalInserted: 20, maxChanges: 1 },
    });
    const before = controller.view.state;

    controller.view.dispatch({ changes: [
      { from: 0, to: 1, insert: "O" },
      { from: 4, to: 5, insert: "T" },
    ] });

    expect(controller.view.state).toBe(before);
    expect(messages).toEqual([]);
    controller.dispose();
  });

  it("reports a body-free local limit error and preserves accepted selection and scroll until a valid action", () => {
    const onEditError = vi.fn();
    const { controller, messages, store } = harness({
      limits: { ordinaryInserted: 2, chunkInserted: 3, totalInserted: 5, maxChanges: 1 },
      onEditError,
    });
    controller.view.dispatch({ selection: { anchor: 3 } });
    controller.view.scrollDOM.scrollTop = 41;
    controller.view.scrollDOM.dispatchEvent(new Event("scroll"));
    onEditError.mockClear();
    const beforeView = controller.view.state;
    const beforeStore = store.activate(TAB_ID);

    controller.view.dispatch({
      changes: { from: 0, insert: "PRIVATE-BODY" },
      selection: { anchor: 0 },
    });

    expect(messages).toEqual([]);
    expect(controller.view.state).toBe(beforeView);
    expect(controller.view.state.selection.main.head).toBe(3);
    expect(controller.view.scrollDOM.scrollTop).toBe(41);
    expect(store.activate(TAB_ID).editorState).toBe(beforeStore.editorState);
    expect(store.activate(TAB_ID).rawText).toBe("one\ntwo\nthree");
    expect(onEditError).toHaveBeenCalledWith({ code: "edit-limit-exceeded" });
    expect(JSON.stringify(onEditError.mock.calls)).not.toContain("PRIVATE-BODY");

    controller.view.dispatch({ selection: { anchor: 2 } });
    expect(onEditError).toHaveBeenLastCalledWith(null);
    expect(controller.view.state.selection.main.head).toBe(2);
    controller.dispose();
  });

  it("does not emit edits for hydration or revision reconciliation", () => {
    // Break caught: view.setState during tab activation is mistaken for a user transaction.
    const { controller, messages } = harness();

    controller.hydrate(activation("host replacement", { documentRevision: 8 }));
    controller.updateRevision(TAB_ID, 9);

    expect(messages).toEqual([]);
    expect(controller.view.state.doc.toString()).toBe("host replacement");
    controller.dispose();
  });

  it("serializes rapid edits against each newly accepted revision", () => {
    // Break caught: rapid transactions all carry revision 7, so every edit after the first is stale.
    const renderAccepted = vi.fn();
    const { controller, messages } = harness({ renderAccepted });

    controller.view.dispatch({ changes: { from: 0, insert: "A" } });
    controller.view.dispatch({ changes: { from: 1, insert: "B" } });

    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({
      type: "document.changed",
      documentRevision: 7,
      payload: { expectedRevision: 7 },
    });

    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8))).toBe(true);
    expect(renderAccepted).toHaveBeenLastCalledWith("Aone\ntwo\nthree", {
      tabId: TAB_ID,
      revision: 8,
    });
    expect(messages).toHaveLength(2);
    expect(messages[1]).toMatchObject({
      type: "document.changed",
      documentRevision: 8,
      payload: {
        expectedRevision: 8,
        changes: [{ from: 1, to: 1, insertedText: "B" }],
      },
    });

    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 9))).toBe(true);
    expect(renderAccepted).toHaveBeenLastCalledWith("ABone\ntwo\nthree", {
      tabId: TAB_ID,
      revision: 9,
    });
    expect(messages).toHaveLength(2);
    controller.dispose();
  });

  it("rolls back speculative edits and requests body-free resync after rejection", () => {
    // Break caught: stale rejection leaves unacknowledged text mounted or sends document text back in resync.
    const { controller, messages, store } = harness();
    controller.view.dispatch({ changes: { from: 0, insert: "A" } });
    controller.view.dispatch({ changes: { from: 1, insert: "B" } });
    const resyncRequestId = "55555555-5555-4555-8555-555555555555";

    expect(controller.handleHostMessage(hostResponse("document.changeRejected", 9, {
      resyncRequestId,
    }))).toBe(true);

    expect(controller.view.state.doc.toString()).toBe("one\ntwo\nthree");
    expect(store.activate(TAB_ID).rawText).toBe("one\ntwo\nthree");
    expect(messages.at(-1)).toEqual({
      version: 1,
      type: "document.resync",
      requestId: resyncRequestId,
      windowId: WINDOW_ID,
      tabId: TAB_ID,
      documentRevision: 9,
      payload: {},
    });
    expect(messages.at(-1)).not.toHaveProperty("text");
    expect(messages.at(-1).payload).not.toHaveProperty("body");
    controller.dispose();
  });

  it("blocks edits against stale text until the resync activation arrives", () => {
    // Break caught: host revision 9 is paired with revision-7 text, so the next UTF-16 offset can mutate the wrong range.
    const { controller, messages } = harness();
    controller.view.dispatch({ changes: { from: 0, insert: "A" } });
    controller.handleHostMessage(hostResponse("document.changeRejected", 9, {
      resyncRequestId: "55555555-5555-4555-8555-555555555555",
    }));
    const afterRejection = messages.length;
    const acceptedText = controller.view.state.doc.toString();

    controller.view.dispatch({ changes: { from: 3, insert: "WRONG" } });

    expect(controller.view.state.doc.toString()).toBe(acceptedText);
    expect(messages).toHaveLength(afterRejection);
    controller.hydrate(activation("authoritative body", {
      requestId: "55555555-5555-4555-8555-555555555555",
      documentRevision: 9,
    }));
    controller.view.dispatch({ changes: { from: 18, insert: "!" } });
    expect(messages.at(-1)).toMatchObject({
      type: "document.changed",
      documentRevision: 9,
      payload: { expectedRevision: 9 },
    });
    controller.dispose();
  });

  it("allows selection-only transactions while resync blocks document changes", () => {
    // Break caught: the resync guard disables edit-mode find selection and keyboard navigation unnecessarily.
    const { controller } = harness();
    controller.view.dispatch({ changes: { from: 0, insert: "A" } });
    controller.handleHostMessage(hostResponse("document.changeRejected", 9, {
      resyncRequestId: "55555555-5555-4555-8555-555555555555",
    }));

    controller.view.dispatch({ selection: { anchor: 3 } });

    expect(controller.view.state.selection.main.head).toBe(3);
    expect(controller.view.state.doc.toString()).toBe("one\ntwo\nthree");
    controller.dispose();
  });

  it("ignores unowned, malformed, duplicate, and out-of-order host responses", () => {
    // Break caught: a stale host acknowledgement advances or rolls back the wrong pending editor queue.
    const { controller, messages } = harness();
    controller.view.dispatch({ changes: { from: 0, insert: "A" } });

    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8, { extra: true })))
      .toBe(false);
    expect(controller.handleHostMessage({
      ...hostResponse("document.changeAccepted", 8),
      requestId: BATCH_ID,
    })).toBe(false);
    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 9))).toBe(false);
    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8))).toBe(true);
    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8))).toBe(false);
    expect(messages).toHaveLength(1);
    controller.dispose();
  });

  it("does not activate a background tab for a delayed host response", () => {
    // Break caught: validating a delayed acknowledgement via store.activate switches the visible editor tab.
    const { controller, store } = harness();
    controller.view.dispatch({ changes: { from: 0, insert: "A" } });
    controller.hydrate(activation("background", {
      requestId: BATCH_ID,
      tabId: "33333333-3333-4333-8333-333333333333",
      documentRevision: 2,
    }));

    expect(controller.handleHostMessage(hostResponse("document.changeAccepted", 8))).toBe(false);
    expect(store.activeTabId).toBe("33333333-3333-4333-8333-333333333333");
    expect(controller.view.state.doc.toString()).toBe("background");
    controller.dispose();
  });

  it("uses one EditorView across tabs and removes its DOM on disposal", () => {
    // Break caught: tab switches leak views/listeners or dispose leaves an editor attached.
    const parent = document.createElement("section");
    document.body.append(parent);
    const { controller } = harness();
    controller.mount(parent);
    const view = controller.view;
    controller.hydrate(activation("tab two", {
      requestId: BATCH_ID,
      tabId: "33333333-3333-4333-8333-333333333333",
      documentRevision: 2,
    }));

    expect(controller.view).toBe(view);
    expect(parent.contains(view.dom)).toBe(true);
    controller.dispose();
    expect(parent.contains(view.dom)).toBe(false);
    parent.remove();
  });
});

describe("editor surface lifecycle", () => {
  it("shows an accessible local edit-limit error and clears it on mode change", async () => {
    const root = document.createElement("main");
    root.innerHTML = '<article id="preview" data-preview></article>';
    let receiveMessage;
    const webview = {
      addEventListener(type, listener) {
        if (type === "message") receiveMessage = listener;
      },
      removeEventListener() {},
      postMessage() {},
    };
    const surface = mountDocumentSurface(root, webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
      editorLimits: { ordinaryInserted: 2, chunkInserted: 3, totalInserted: 5, maxChanges: 1 },
    });
    await receiveMessage({ data: activation() });

    surface.editorController.view.dispatch({
      changes: { from: 0, insert: "PRIVATE-BODY" },
    });

    const error = root.querySelector("[data-edit-error]");
    expect(error).not.toBeNull();
    expect(error.getAttribute("role")).toBe("alert");
    expect(error.hidden).toBe(false);
    expect(error.textContent).toContain("64 MiB");
    expect(error.textContent).toContain("10,000");
    expect(error.textContent).not.toContain("PRIVATE-BODY");

    await receiveMessage({ data: {
      ...hostResponse("document.setMode", 7, { mode: "read" }),
    } });
    expect(error.hidden).toBe(true);
    expect(error.textContent).toBe("");
    surface.dispose();
    root.remove();
  });

  it("mounts one shared editor beside the preview and disposes its host listener and DOM", async () => {
    // Break caught: surface recreation leaks the WebView listener or an orphaned CodeMirror tree.
    const root = document.createElement("main");
    root.innerHTML = '<article id="preview" data-preview></article>';
    const messages = [];
    let receiveMessage;
    let removedListener = null;
    const webview = {
      addEventListener(type, listener) {
        if (type === "message") receiveMessage = listener;
      },
      removeEventListener(type, listener) {
        if (type === "message") removedListener = listener;
      },
      postMessage(message) {
        messages.push(message);
      },
    };

    const surface = mountDocumentSurface(root, webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
    });
    await receiveMessage({
      data: activation("# reader", {
        payload: { ...activation().payload, text: "# reader", mode: "read" },
      }),
    });

    expect(root.querySelector("[data-document-workspace]")).not.toBeNull();
    expect(root.querySelector("[data-editor] .cm-editor")).toBe(surface.editorController.view.dom);
    expect(root.querySelector("[data-editor]").hidden).toBe(true);
    surface.dispose();
    expect(removedListener).toBe(receiveMessage);
    expect(root.querySelector("[data-editor]")).toBeNull();
    expect(root.querySelector("[data-preview]")).not.toBeNull();
    expect(messages[0].type).toBe("surface.ready");
  });
});
