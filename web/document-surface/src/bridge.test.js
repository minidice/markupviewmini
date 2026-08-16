import { describe, expect, it, vi } from "vitest";
import {
  applyHostMessage,
  bindLinkInterception,
  createSurfaceState,
  makeEnvelope,
} from "./bridge.js";
import {
  mountDocumentSurface,
  readBootstrapContext,
} from "./editor-app.js";
import { addRenderedMermaidAction } from "./mermaid-blocks.js";

const WINDOW_ID = "11111111-1111-4111-8111-111111111111";
const TAB_ONE_ID = "22222222-2222-4222-8222-222222222222";
const TAB_TWO_ID = "33333333-3333-4333-8333-333333333333";
const REQUEST_ONE_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const REQUEST_TWO_ID = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const ASSET_BASE_URL = "https://document-assets.local/";
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;

function envelope(revision, overrides = {}) {
  return {
    version: 1,
    type: "document.activate",
    requestId: REQUEST_ONE_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ONE_ID,
    documentRevision: revision,
    payload: {
      path: "C:\\docs\\readme.md",
      text: "",
      mode: "read",
      line: null,
      anchor: null,
      assetBaseUrl: ASSET_BASE_URL,
    },
    ...overrides,
  };
}

function accepted(revision) {
  return {
    version: 1,
    type: "document.changeAccepted",
    requestId: REQUEST_ONE_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ONE_ID,
    documentRevision: revision,
    payload: {},
  };
}

function goToLine(revision, line, overrides = {}) {
  return {
    version: 1,
    type: "navigation.goToLine",
    requestId: REQUEST_ONE_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ONE_ID,
    documentRevision: revision,
    payload: { line },
    ...overrides,
  };
}

function editorCommand(type, revision, overrides = {}) {
  return {
    version: 1,
    type,
    requestId: REQUEST_ONE_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ONE_ID,
    documentRevision: revision,
    payload: {},
    ...overrides,
  };
}

function createWebView() {
  const messages = [];
  let receiveMessage;
  return {
    messages,
    webview: {
      addEventListener(type, listener) {
        if (type === "message") receiveMessage = listener;
      },
      postMessage(message) {
        messages.push(message);
      },
    },
    receive(message) {
      return receiveMessage({ data: message });
    },
  };
}

function createRoot() {
  const root = document.createElement("main");
  root.innerHTML = '<article id="preview" data-preview></article>';
  return root;
}

const BOOTSTRAP = { windowId: WINDOW_ID, tabId: TAB_ONE_ID };

describe("surface bridge schema", () => {
  it("applies typed undo and redo only for the exact active edit owner", async () => {
    const root = createRoot();
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(7, {
      payload: {
        ...envelope(7).payload,
        text: "base",
        mode: "edit",
        preferredNewline: "\n",
      },
    }));
    surface.editorController.view.dispatch({ changes: { from: 4, insert: "!" } });
    await harness.receive(accepted(8));
    harness.messages.length = 0;

    await harness.receive(editorCommand("editor.undo", 7));
    expect(harness.messages).toEqual([]);
    await harness.receive(editorCommand("editor.undo", 8));
    expect(harness.messages).toContainEqual(expect.objectContaining({
      type: "document.changed",
      documentRevision: 8,
      payload: expect.objectContaining({
        expectedRevision: 8,
        changes: [{ from: 4, to: 5, insertedText: "" }],
      }),
    }));
    await harness.receive(accepted(9));
    harness.messages.length = 0;

    await harness.receive(editorCommand("editor.redo", 9));
    expect(harness.messages).toContainEqual(expect.objectContaining({
      type: "document.changed",
      documentRevision: 9,
      payload: expect.objectContaining({
        expectedRevision: 9,
        changes: [{ from: 4, to: 4, insertedText: "!" }],
      }),
    }));
    surface.dispose();
  });

  it("ignores typed editor history commands in read mode", async () => {
    const root = createRoot();
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(4, {
      payload: { ...envelope(4).payload, text: "read only", mode: "read" },
    }));
    harness.messages.length = 0;

    await harness.receive(editorCommand("editor.undo", 4));

    expect(harness.messages).toEqual([]);
    expect(surface.editorController.view.state.doc.toString()).toBe("read only");
    surface.dispose();
  });

  it("moves focus to the active read or edit surface when mode changes", async () => {
    const root = createRoot();
    document.body.append(root);
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(5, {
      payload: { ...envelope(5).payload, text: "focus", mode: "read" },
    }));

    await harness.receive({
      ...editorCommand("document.setMode", 5),
      payload: { mode: "edit" },
    });
    expect(document.activeElement).toBe(root.querySelector(".cm-content"));

    await harness.receive({
      ...editorCommand("document.setMode", 5),
      payload: { mode: "read" },
    });
    expect(document.activeElement).toBe(root.querySelector("[data-preview]"));
    surface.dispose();
    root.remove();
  });

  it("applies exact current-owner editor preferences to the active surface", async () => {
    const root = createRoot();
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(7, {
      payload: { ...envelope(7).payload, text: "needle", mode: "edit" },
    }));

    await harness.receive({
      ...envelope(7),
      type: "document.setEditorPreferences",
      payload: {
        splitRatio: 0.37,
        find: { matchCase: true, wholeWord: true, useRegex: true },
      },
    });

    expect(root.querySelector("[data-document-workspace]").style
      .getPropertyValue("--editor-split-ratio")).toBe("37%");
    expect(root.querySelector("[data-find-match-case]").checked).toBe(true);
    expect(root.querySelector("[data-find-whole-word]").checked).toBe(true);
    expect(root.querySelector("[data-find-regex]").checked).toBe(true);
    surface.dispose();
  });

  it("posts exact request-tab-revision-owned selection and scroll hints", async () => {
    const root = createRoot();
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(7, {
      payload: { ...envelope(7).payload, text: "0123456789", mode: "edit" },
    }));
    harness.messages.length = 0;

    surface.editorController.view.dispatch({ selection: { anchor: 7, head: 3 } });
    surface.editorController.view.scrollDOM.scrollTop = 48;
    surface.editorController.view.scrollDOM.dispatchEvent(new Event("scroll"));

    expect(harness.messages.at(-1)).toEqual({
      version: 1,
      type: "document.uiHintsChanged",
      requestId: REQUEST_ONE_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 7,
      payload: {
        selection: { anchor: 7, head: 3 },
        scrollTop: 48,
        splitRatio: 0.5,
        find: { matchCase: false, wholeWord: false, useRegex: false },
      },
    });
    surface.dispose();
  });

  it("posts each typed cursor hint only after its exact rapid edit revision is accepted", async () => {
    const root = createRoot();
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(7, {
      payload: { ...envelope(7).payload, text: "0123456789", mode: "edit" },
    }));
    harness.messages.length = 0;
    surface.editorController.view.scrollDOM.scrollTop = 48;

    surface.editorController.view.dispatch({
      changes: { from: 0, insert: "A" },
      selection: { anchor: 1 },
    });
    surface.editorController.view.dispatch({
      changes: { from: 1, insert: "B" },
      selection: { anchor: 2 },
    });

    expect(harness.messages.map(({ type, documentRevision }) => [type, documentRevision]))
      .toEqual([["document.changed", 7]]);

    await harness.receive(accepted(8));

    expect(harness.messages.slice(1).map(({ type, documentRevision }) => [type, documentRevision]))
      .toEqual([
        ["document.uiHintsChanged", 8],
        ["document.changed", 8],
      ]);
    expect(harness.messages[1].payload).toEqual({
      selection: { anchor: 1, head: 1 },
      scrollTop: 48,
      splitRatio: 0.5,
      find: { matchCase: false, wholeWord: false, useRegex: false },
    });

    await harness.receive(accepted(9));

    expect(harness.messages.at(-1)).toMatchObject({
      type: "document.uiHintsChanged",
      documentRevision: 9,
      payload: {
        selection: { anchor: 2, head: 2 },
        scrollTop: 48,
        splitRatio: 0.5,
        find: { matchCase: false, wholeWord: false, useRegex: false },
      },
    });
    surface.dispose();
  });

  it("rejects typed cursor hints and resyncs from the last accepted selection and scroll", async () => {
    const root = createRoot();
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(7, {
      payload: {
        ...envelope(7).payload,
        text: "0123456789",
        mode: "edit",
        selection: { anchor: 2, head: 2 },
        scrollTop: 11,
      },
    }));
    harness.messages.length = 0;
    surface.editorController.view.scrollDOM.scrollTop = 44;
    surface.editorController.view.dispatch({
      changes: { from: 2, insert: "X" },
      selection: { anchor: 3 },
    });

    expect(harness.messages.map(({ type }) => type)).toEqual(["document.changed"]);
    await harness.receive({
      version: 1,
      type: "document.changeRejected",
      requestId: REQUEST_ONE_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 7,
      payload: { resyncRequestId: REQUEST_TWO_ID },
    });

    expect(harness.messages.map(({ type }) => type)).toEqual([
      "document.changed",
      "document.resync",
    ]);
    expect(harness.messages.at(-1)).toEqual({
      version: 1,
      type: "document.resync",
      requestId: REQUEST_TWO_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 7,
      payload: {},
    });
    expect(surface.editorController.view.state.doc.toString()).toBe("0123456789");
    expect(surface.editorController.view.state.selection.main).toMatchObject({ anchor: 2, head: 2 });
    expect(surface.editorController.view.scrollDOM.scrollTop).toBe(11);
    expect(surface.tabStore.activate(TAB_ONE_ID).editorState.selection.main)
      .toMatchObject({ anchor: 2, head: 2 });
    expect(surface.tabStore.activate(TAB_ONE_ID).scrollTop).toBe(11);
    expect(JSON.stringify(harness.messages.at(-1))).not.toMatch(/text|body/iu);
    surface.dispose();
  });

  it("rejects stale activate messages for the same tab", () => {
    const state = createSurfaceState();
    applyHostMessage(state, envelope(4, { payload: { ...envelope(4).payload, text: "new" } }));
    const applied = applyHostMessage(state, envelope(3, {
      requestId: REQUEST_TWO_ID,
      payload: { ...envelope(3).payload, text: "old" },
    }));

    expect(applied).toBe(false);
    expect(state.revision).toBe(4);
    expect(state.text).toBe("new");
  });

  it("accepts a lower revision when activating a different tab", () => {
    const state = createSurfaceState();
    applyHostMessage(state, envelope(9));

    const applied = applyHostMessage(state, envelope(2, {
      requestId: REQUEST_TWO_ID,
      tabId: TAB_TWO_ID,
    }));

    expect(applied).toBe(true);
    expect(state.tabId).toBe(TAB_TWO_ID);
    expect(state.revision).toBe(2);
  });

  it("applies a valid revision and retains its request and reader context", () => {
    const state = createSurfaceState();
    const message = envelope(7, {
      payload: {
        path: "C:\\docs\\readme.md",
        text: "# Current",
        mode: "read",
        line: 12,
        anchor: "current",
        assetBaseUrl: ASSET_BASE_URL,
      },
    });

    expect(applyHostMessage(state, message)).toBe(true);
    expect(state).toMatchObject({
      requestId: REQUEST_ONE_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      revision: 7,
      path: "C:\\docs\\readme.md",
      text: "# Current",
      mode: "read",
      line: 12,
      anchor: "current",
      assetBaseUrl: ASSET_BASE_URL,
    });
  });

  it.each([
    ["version", { version: 2 }],
    ["type", { type: "document.other" }],
    ["request UUID", { requestId: "request-1" }],
    ["window UUID", { windowId: "window-1" }],
    ["tab UUID", { tabId: "tab-1" }],
    ["revision", { documentRevision: -1 }],
    ["mode", { payload: { ...envelope(1).payload, mode: "preview" } }],
    ["path", { payload: { ...envelope(1).payload, path: null } }],
    ["text", { payload: { ...envelope(1).payload, text: null } }],
    ["line", { payload: { ...envelope(1).payload, line: 0 } }],
    ["anchor", { payload: { ...envelope(1).payload, anchor: 12 } }],
    ["asset base missing", { payload: { ...envelope(1).payload, assetBaseUrl: null } }],
    ["asset base remote", { payload: { ...envelope(1).payload, assetBaseUrl: "https://example.com/" } }],
    ["asset base file", { payload: { ...envelope(1).payload, assetBaseUrl: "file:///C:/docs/" } }],
    ["split ratio", { payload: { ...envelope(1).payload, splitRatio: 0.05 } }],
    ["persisted find query", { payload: {
      ...envelope(1).payload,
      find: { query: "private", matchCase: false, wholeWord: false, useRegex: false },
    } }],
    ["incomplete find options", { payload: {
      ...envelope(1).payload,
      find: { matchCase: false, wholeWord: false },
    } }],
  ])("rejects an invalid %s field", (name, invalidPart) => {
    expect(applyHostMessage(createSurfaceState(), envelope(1, invalidPart))).toBe(false);
  });

  it("creates a version-1 envelope with the complete GUID context", () => {
    const randomUUID = vi.spyOn(crypto, "randomUUID").mockReturnValue(REQUEST_TWO_ID);

    const message = makeEnvelope(
      "document.rendered",
      { windowId: WINDOW_ID, tabId: TAB_ONE_ID, documentRevision: 11 },
      { rendered: true },
    );

    expect(message).toEqual({
      version: 1,
      type: "document.rendered",
      requestId: REQUEST_TWO_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 11,
      payload: { rendered: true },
    });
    randomUUID.mockRestore();
  });
});

describe("surface bootstrap and events", () => {
  it("routes a multi-block gutter pointer through the exact panel UUID and acknowledges only panel focus", async () => {
    const root = createRoot();
    document.body.append(root);
    const harness = createWebView();
    const surface = mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    const markdown = [
      "before",
      "```mermaid",
      "flowchart LR",
      "A --> B",
      "```",
      "between",
      "```mermaid",
      "flowchart TD",
      "X --> Y",
      "```",
    ].join("\n");
    await harness.receive(envelope(7, {
      payload: {
        ...envelope(7).payload,
        text: markdown,
        mode: "edit",
        preferredNewline: "\n",
      },
    }));
    const gutters = [...root.querySelectorAll('[data-mermaid-action-surface="gutter"]')];
    const panels = [...root.querySelectorAll('[data-mermaid-action-surface="panel"]')];
    expect(gutters).toHaveLength(2);
    expect(panels).toHaveLength(2);

    gutters[1].dispatchEvent(new MouseEvent("click", { bubbles: true }));
    await vi.waitFor(() => expect(harness.messages.filter(
      (message) => message.type === "mermaid.editRequested",
    )).toHaveLength(1));
    const request = harness.messages.find((message) => message.type === "mermaid.editRequested");
    expect(request.payload).toMatchObject({
      from: markdown.indexOf("flowchart TD"),
      actionId: panels[1].dataset.mermaidActionId,
      actionOrigin: "editor",
    });

    const focus = {
      version: 1,
      type: "mermaid.focusRequested",
      requestId: request.requestId,
      windowId: request.windowId,
      tabId: request.tabId,
      documentRevision: request.documentRevision,
      payload: {
        actionId: request.payload.actionId,
        actionOrigin: request.payload.actionOrigin,
      },
    };
    await harness.receive(focus);
    expect(document.activeElement).toBe(panels[1]);
    expect(harness.messages.at(-1)).toEqual({ ...focus, type: "mermaid.focusCompleted" });

    const hiddenId = crypto.randomUUID();
    gutters[0].dataset.mermaidEditAction = "";
    gutters[0].dataset.mermaidActionId = hiddenId;
    gutters[0].dataset.mermaidActionOrigin = "editor";
    const acknowledgements = harness.messages.filter(
      (message) => message.type === "mermaid.focusCompleted",
    ).length;
    await harness.receive({
      ...focus,
      payload: { actionId: hiddenId, actionOrigin: "editor" },
    });
    expect(harness.messages.filter(
      (message) => message.type === "mermaid.focusCompleted",
    )).toHaveLength(acknowledgements);
    expect(document.activeElement).toBe(panels[1]);

    surface.editorController.view.dispatch({
      changes: { from: surface.editorController.view.state.doc.length, insert: "\nafter" },
    });
    const replacementPanel = [...root.querySelectorAll('[data-mermaid-action-surface="panel"]')][1];
    expect(replacementPanel.dataset.mermaidActionId).not.toBe(request.payload.actionId);
    await harness.receive(focus);
    expect(harness.messages.filter(
      (message) => message.type === "mermaid.focusCompleted",
    )).toHaveLength(acknowledgements);
    surface.dispose();
  });

  it("acknowledges focus only for the exact current owner and originating action", async () => {
    // Break caught: a same-control rerender or stale owner focuses a replacement action.
    const root = createRoot();
    document.body.append(root);
    const harness = createWebView();
    const block = { from: 0, to: 20, source: "flowchart LR\nA --> B" };
    const renderDocument = async (_source, { container, onMermaidEditRequested }) => {
      addRenderedMermaidAction(container, block, onMermaidEditRequested);
    };
    const surface = mountDocumentSurface(root, harness.webview, {
      bootstrapContext: BOOTSTRAP,
      renderDocument,
    });
    await harness.receive(envelope(7, {
      payload: { ...envelope(7).payload, text: block.source },
    }));
    const action = root.querySelector("[data-mermaid-edit-action]");
    action.click();
    await vi.waitFor(() => expect(harness.messages.some(
      (message) => message.type === "mermaid.editRequested",
    )).toBe(true));
    const request = harness.messages.find((message) => message.type === "mermaid.editRequested");
    const focus = {
      version: 1,
      type: "mermaid.focusRequested",
      requestId: request.requestId,
      windowId: request.windowId,
      tabId: request.tabId,
      documentRevision: request.documentRevision,
      payload: {
        actionId: request.payload.actionId,
        actionOrigin: request.payload.actionOrigin,
      },
    };

    await harness.receive(focus);

    expect(document.activeElement).toBe(action);
    expect(harness.messages.at(-1)).toEqual({ ...focus, type: "mermaid.focusCompleted" });
    const acknowledgements = harness.messages.filter(
      (message) => message.type === "mermaid.focusCompleted",
    ).length;

    await harness.receive({ ...focus, documentRevision: 6 });
    expect(harness.messages.filter(
      (message) => message.type === "mermaid.focusCompleted",
    )).toHaveLength(acknowledgements);

    await harness.receive(envelope(7, {
      payload: { ...envelope(7).payload, text: block.source },
    }));
    await harness.receive(focus);
    expect(harness.messages.filter(
      (message) => message.type === "mermaid.focusCompleted",
    )).toHaveLength(acknowledgements);
    surface.dispose();
  });

  it("reads validated GUID query parameters and emits a C#-compatible initial ready envelope", () => {
    const bootstrap = readBootstrapContext(`?windowId=${WINDOW_ID}&tabId=${TAB_ONE_ID}`);
    const root = createRoot();
    const harness = createWebView();

    mountDocumentSurface(root, harness.webview, { bootstrapContext: bootstrap });

    expect(bootstrap).toEqual(BOOTSTRAP);
    expect(harness.messages).toHaveLength(1);
    expect(harness.messages[0]).toMatchObject({
      version: 1,
      type: "surface.ready",
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 0,
      payload: {},
    });
    expect(harness.messages[0].requestId).toMatch(UUID_PATTERN);
  });

  it.each([
    "",
    `?windowId=bad&tabId=${TAB_ONE_ID}`,
    `?windowId=${WINDOW_ID}&tabId=bad`,
  ])("rejects invalid bootstrap query context %s", (search) => {
    expect(readBootstrapContext(search)).toBeNull();
  });

  it("intercepts links with a fresh request ID and prevents browser navigation", () => {
    const root = document.createElement("div");
    root.innerHTML = '<a href="https://example.com/docs#part">Open</a>';
    const messages = [];
    bindLinkInterception(
      root,
      () => ({ windowId: WINDOW_ID, tabId: TAB_ONE_ID, documentRevision: 4 }),
      (message) => messages.push(message),
    );

    const event = new MouseEvent("click", { bubbles: true, cancelable: true });
    expect(root.querySelector("a").dispatchEvent(event)).toBe(false);
    expect(event.defaultPrevented).toBe(true);
    expect(messages[0]).toMatchObject({
      version: 1,
      type: "link.open",
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 4,
      payload: { href: "https://example.com/docs#part", disposition: "default" },
    });
    expect(messages[0].requestId).toMatch(UUID_PATTERN);
  });

  it.each([
    ["Ctrl+click", [{ type: "click", options: { button: 0, ctrlKey: true } }]],
    ["middle click", [
      { type: "click", options: { button: 1 } },
      { type: "auxclick", options: { button: 1 } },
    ]],
  ])("emits one correlated new-tab message for %s", (name, events) => {
    const root = document.createElement("div");
    root.innerHTML = '<a href="chapter.md#part">Open</a>';
    const messages = [];
    bindLinkInterception(
      root,
      () => ({ windowId: WINDOW_ID, tabId: TAB_ONE_ID, documentRevision: 8 }),
      (message) => messages.push(message),
    );

    for (const { type, options } of events) {
      root.querySelector("a").dispatchEvent(new MouseEvent(type, {
        bubbles: true,
        cancelable: true,
        ...options,
      }));
    }

    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({
      version: 1,
      type: "link.open",
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 8,
      payload: { href: "chapter.md#part", disposition: "newTab" },
    });
    expect(messages[0].requestId).toMatch(UUID_PATTERN);
  });

  it("cleanup removes primary-click and middle-click interception", () => {
    const root = document.createElement("div");
    root.innerHTML = '<a href="chapter.md">Open</a>';
    const messages = [];
    const cleanup = bindLinkInterception(
      root,
      () => ({ windowId: WINDOW_ID, tabId: TAB_ONE_ID, documentRevision: 8 }),
      (message) => messages.push(message),
    );
    cleanup();
    const anchor = root.querySelector("a");
    anchor.addEventListener("click", (event) => event.preventDefault());
    anchor.addEventListener("auxclick", (event) => event.preventDefault());

    anchor.dispatchEvent(new MouseEvent("click", {
      bubbles: true,
      cancelable: true,
    }));
    anchor.dispatchEvent(new MouseEvent("auxclick", {
      button: 1,
      bubbles: true,
      cancelable: true,
    }));

    expect(messages).toHaveLength(0);
  });

  it("intercepts a link context menu with its raw target and current correlation", () => {
    const root = document.createElement("div");
    root.innerHTML = '<a href="../chapter.md#part"><span>Open</span></a>';
    const messages = [];
    bindLinkInterception(
      root,
      () => ({ windowId: WINDOW_ID, tabId: TAB_ONE_ID, documentRevision: 9 }),
      (message) => messages.push(message),
    );

    const event = new MouseEvent("contextmenu", { bubbles: true, cancelable: true });
    expect(root.querySelector("span").dispatchEvent(event)).toBe(false);
    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({
      version: 1,
      type: "link.contextMenu",
      windowId: WINDOW_ID,
      tabId: TAB_ONE_ID,
      documentRevision: 9,
      payload: { href: "../chapter.md#part" },
    });
  });

  it("echoes the activate request ID from document.rendered", async () => {
    const root = createRoot();
    const harness = createWebView();
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });

    await harness.receive(envelope(2, { payload: { ...envelope(2).payload, text: "# Reader" } }));

    expect(root.querySelector("[data-preview] h1")?.textContent).toBe("Reader");
    expect(harness.messages.at(-1)).toMatchObject({
      type: "document.rendered",
      requestId: REQUEST_ONE_ID,
    });
  });

  it("lets the last equal-revision activation win an async render race", async () => {
    const root = createRoot();
    const harness = createWebView();
    const pending = [];
    const renderDocument = (source, { container }) => new Promise((resolve, reject) => {
      pending.push({ source, container, resolve, reject });
    });
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP, renderDocument });

    const first = harness.receive(envelope(5, {
      payload: { ...envelope(5).payload, text: "first" },
    }));
    const second = harness.receive(envelope(5, {
      requestId: REQUEST_TWO_ID,
      tabId: TAB_TWO_ID,
      payload: { ...envelope(5).payload, text: "second" },
    }));
    pending[1].container.textContent = pending[1].source;
    pending[1].resolve();
    await second;
    pending[0].container.textContent = pending[0].source;
    pending[0].resolve();
    await first;

    expect(root.querySelector("[data-preview]").textContent).toBe("second");
    expect(harness.messages.filter((message) => message.type === "document.rendered"))
      .toHaveLength(1);
    expect(harness.messages.at(-1).requestId).toBe(REQUEST_TWO_ID);
  });

  it.each([0.1, 0.9])("accepts the safe split boundary %s", (splitRatio) => {
    const payload = {
      ...envelope(1).payload,
      splitRatio,
      find: { matchCase: true, wholeWord: false, useRegex: true },
    };

    expect(applyHostMessage(createSurfaceState(), envelope(1, { payload }))).toBe(true);
  });

  it("commits preview and outline only for the latest accepted edit render", async () => {
    // Break caught: a slow revision-5 preview replaces accepted revision 6 and publishes a stale outline.
    const root = createRoot();
    const harness = createWebView();
    const pending = [];
    const renderDocument = (source, { container }) => {
      if (source === "base") {
        container.textContent = source;
        return Promise.resolve();
      }
      return new Promise((resolve) => pending.push({ source, container, resolve }));
    };
    const surface = mountDocumentSurface(root, harness.webview, {
      bootstrapContext: BOOTSTRAP,
      renderDocument,
    });
    await harness.receive(envelope(4, {
      payload: {
        ...envelope(4).payload,
        text: "base",
        mode: "edit",
        preferredNewline: "\n",
      },
    }));
    surface.editorController.view.dispatch({ changes: { from: 4, insert: " five" } });
    surface.editorController.view.dispatch({ changes: { from: 9, insert: " six" } });

    await harness.receive(accepted(5));
    await harness.receive(accepted(6));
    expect(pending.map(({ source }) => source)).toEqual(["base five", "base five six"]);
    pending[1].container.innerHTML = '<h1 id="six" data-source-start="1">Six</h1>';
    pending[1].resolve();
    await Promise.resolve();
    pending[0].container.innerHTML = '<h1 id="five" data-source-start="1">Five</h1>';
    pending[0].resolve();
    await Promise.resolve();

    expect(root.querySelector("[data-preview]").textContent).toBe("Six");
    const outlines = harness.messages.filter((message) => message.type === "document.outline");
    expect(outlines.at(-1)).toMatchObject({
      documentRevision: 6,
      payload: { items: [expect.objectContaining({ text: "Six" })] },
    });
    expect(outlines.some((message) => message.documentRevision === 5)).toBe(false);
    surface.dispose();
  });

  it("applies pending navigation to the latest accepted render in the same activation", async () => {
    // Break caught: render-5 owns the pending command, so render-6 supersession silently drops it.
    const root = createRoot();
    const harness = createWebView();
    const pending = [];
    let scrolled = null;
    const renderDocument = (source, { container }) => {
      if (source === "base") {
        container.textContent = source;
        return Promise.resolve();
      }
      return new Promise((resolve) => pending.push({ source, container, resolve }));
    };
    const surface = mountDocumentSurface(root, harness.webview, {
      bootstrapContext: BOOTSTRAP,
      renderDocument,
      scrollIntoView() { scrolled = this; },
    });
    await harness.receive(envelope(4, {
      payload: {
        ...envelope(4).payload,
        text: "base",
        mode: "edit",
        preferredNewline: "\n",
      },
    }));
    surface.editorController.view.dispatch({ changes: { from: 4, insert: " five" } });
    surface.editorController.view.dispatch({ changes: { from: 9, insert: " six" } });
    await harness.receive(accepted(5));
    await harness.receive(goToLine(5, 2));
    await harness.receive(accepted(6));
    pending[1].container.innerHTML = '<p data-source-start="2" data-source-end="2">latest</p>';
    pending[1].resolve();
    await Promise.resolve();

    expect(scrolled?.textContent).toBe("latest");
    scrolled = null;
    await harness.receive(goToLine(6, 2, { requestId: REQUEST_TWO_ID }));
    expect(scrolled).toBeNull();
    pending[0].resolve();
    await Promise.resolve();
    surface.dispose();
  });

  it("clears the previous preview as soon as a new activation is accepted", async () => {
    const root = createRoot();
    root.querySelector("[data-preview]").textContent = "previous tab";
    const harness = createWebView();
    let finishRender;
    const renderDocument = (source, { container }) => new Promise((resolve) => {
      finishRender = () => {
        container.textContent = source;
        resolve();
      };
    });
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP, renderDocument });

    const activation = harness.receive(envelope(5, {
      payload: { ...envelope(5).payload, text: "new tab" },
    }));

    expect(root.querySelector("[data-preview]").textContent).toBe("");
    finishRender();
    await activation;
    expect(root.querySelector("[data-preview]").textContent).toBe("new tab");
  });

  it("suppresses an error from a stale render", async () => {
    const root = createRoot();
    const harness = createWebView();
    const pending = [];
    const renderDocument = (source, { container }) => new Promise((resolve, reject) => {
      pending.push({ source, container, resolve, reject });
    });
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP, renderDocument });

    const stale = harness.receive(envelope(6));
    const winner = harness.receive(envelope(7, { requestId: REQUEST_TWO_ID }));
    pending[1].resolve();
    await winner;
    pending[0].reject(new Error("stale"));
    await stale;

    expect(harness.messages.some((message) => message.type === "surface.error")).toBe(false);
    expect(harness.messages.at(-1)).toMatchObject({
      type: "document.rendered",
      requestId: REQUEST_TWO_ID,
    });
  });

  it("echoes the activate request ID from a current surface.error", async () => {
    const root = createRoot();
    root.querySelector("[data-preview]").textContent = "previous document";
    const harness = createWebView();
    const renderDocument = async () => { throw new Error("current"); };
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP, renderDocument });

    await harness.receive(envelope(8));

    expect(harness.messages.at(-1)).toMatchObject({
      type: "surface.error",
      requestId: REQUEST_ONE_ID,
      payload: { code: "render-failed" },
    });
    expect(root.querySelector("[data-preview]").textContent).toBe("");
  });
});

describe("document activation navigation", () => {
  it("uses only the host-approved document asset origin for relative images", async () => {
    const root = createRoot();
    const harness = createWebView();
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP });
    await harness.receive(envelope(3, {
      payload: {
        ...envelope(3).payload,
        path: "C:\\docs\\guide folder\\readme.md",
        text: "![logo](images/logo.png)",
      },
    }));

    expect(root.querySelector("img")?.getAttribute("src"))
      .toBe("https://document-assets.local/images/logo.png");
  });

  it("scrolls to an anchor after the winning render", async () => {
    const root = createRoot();
    const harness = createWebView();
    let scrolledElement = null;
    const scrollIntoView = function scrollIntoView() { scrolledElement = this; };
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP, scrollIntoView });
    await harness.receive(envelope(4, {
      payload: { ...envelope(4).payload, text: "# Target", anchor: "target" },
    }));

    expect(scrolledElement).toBe(root.querySelector("#target"));
  });

  it("scrolls to the rendered source range containing the requested line", async () => {
    const root = createRoot();
    const harness = createWebView();
    let scrolledElement = null;
    const scrollIntoView = function scrollIntoView() { scrolledElement = this; };
    mountDocumentSurface(root, harness.webview, { bootstrapContext: BOOTSTRAP, scrollIntoView });
    await harness.receive(envelope(5, {
      payload: { ...envelope(5).payload, text: "# First\n\nSecond", line: 3 },
    }));

    expect(scrolledElement?.textContent).toBe("Second");
    expect(scrolledElement?.dataset).toMatchObject({ sourceStart: "3", sourceEnd: "3" });
  });
});
