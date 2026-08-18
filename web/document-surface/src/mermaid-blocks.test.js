import { describe, expect, it, vi, beforeAll } from "vitest";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { createEditorController } from "./editor-controller.js";
import {
  createMermaidEditPayload,
  createMermaidGutter,
  describeLimitedModeReason,
  describeMermaidAction,
  findMermaidBlocks,
  focusMermaidAction,
} from "./mermaid-blocks.js";
import { renderPreview } from "./preview.js";
import { createTabStateStore } from "./tab-state-store.js";
import { setLanguage } from "../../shared/i18n/index.js";

// These assertions name the Korean strings directly, so the language has to be pinned:
// the surface starts on English until the host sends its locale message.
beforeAll(() => setLanguage("ko"));

const WINDOW_ID = "11111111-1111-4111-8111-111111111111";
const TAB_ID = "22222222-2222-4222-8222-222222222222";
const REQUEST_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;

function activation(text) {
  return {
    version: 1,
    type: "document.activate",
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: 7,
    payload: {
      path: "C:\\docs\\diagram.md",
      text,
      mode: "edit",
      line: null,
      anchor: null,
      assetBaseUrl: "https://document-assets.local/",
      preferredNewline: "\n",
    },
  };
}

const mermaidAdapter = {
  async render() {
    return { svg: '<svg viewBox="0 0 10 10"><text>Diagram</text></svg>' };
  },
};

describe("Mermaid fenced blocks", () => {
  it("finds backtick and tilde blocks with exact content-only offsets", () => {
    // Break caught: a block request includes a fence line, surrounding Markdown, or the wrong block.
    const markdown = [
      "before",
      "```mermaid",
      "flowchart LR",
      "A --> B",
      "```",
      "middle",
      "~~~mermaid",
      "flowchart TD",
      "X --> Y",
      "~~~",
      "after",
    ].join("\n");

    expect(findMermaidBlocks(markdown)).toEqual([
      {
        from: 18,
        to: 38,
        source: "flowchart LR\nA --> B",
        openingLine: 2,
      },
      {
        from: 61,
        to: 81,
        source: "flowchart TD\nX --> Y",
        openingLine: 7,
      },
    ]);
    for (const block of findMermaidBlocks(markdown)) {
      expect(markdown.slice(block.from, block.to)).toBe(block.source);
    }
  });

  it("preserves raw CRLF UTF-16 offsets while excluding the closing line break", () => {
    // Break caught: CodeMirror-normalized positions are sent instead of host-owned raw offsets.
    const markdown = "lead\r\n```mermaid\r\nflowchart LR\r\nA --> B\r\n```\r\ntail";
    expect(findMermaidBlocks(markdown)).toEqual([{
      from: 18,
      to: 39,
      source: "flowchart LR\r\nA --> B",
      openingLine: 2,
    }]);
  });

  it("rejects mixed-case, decorated, mismatched, and unclosed Mermaid fences", () => {
    // Break caught: a lookalike or malformed fence is treated as an editable Mermaid block.
    expect(findMermaidBlocks("```Mermaid\nflowchart LR\nA-->B\n```")).toEqual([]);
    expect(findMermaidBlocks("```mermaid extra\nflowchart LR\nA-->B\n```")).toEqual([]);
    expect(findMermaidBlocks("```mermaid\nflowchart LR\nA-->B\n~~~")).toEqual([]);
    expect(findMermaidBlocks("~~~mermaid\nflowchart LR\nA-->B")).toEqual([]);
  });

  it("does not discover Mermaid-looking text inside another fenced block", () => {
    // Break caught: physical-line scanning exposes an inner lookalike that Markdown parses as text.
    const markdown = [
      "````text",
      "```mermaid",
      "flowchart LR",
      "A --> B",
      "```",
      "````",
    ].join("\n");

    expect(findMermaidBlocks(markdown)).toEqual([]);
  });

  it("recognizes real list and blockquote-contained Mermaid fences", () => {
    // Break caught: tracking only column-zero fences drops valid Markdown container fences.
    const list = [
      "- ```mermaid",
      "  flowchart LR",
      "  A --> B",
      "  ```",
    ].join("\n");
    const quote = [
      "> ~~~mermaid",
      "> flowchart LR",
      "> A --> B",
      "> ~~~",
    ].join("\n");

    const [listBlock] = findMermaidBlocks(list);
    expect(listBlock.source).toBe("  flowchart LR\n  A --> B");
    expect(list.slice(listBlock.from, listBlock.to)).toBe(listBlock.source);
    const [quoteBlock] = findMermaidBlocks(quote);
    expect(quoteBlock.source).toBe("> flowchart LR\n> A --> B");
    expect(quote.slice(quoteBlock.from, quoteBlock.to)).toBe(quoteBlock.source);
  });

  it.each([
    [
      "nested list",
      "  - ~~~mermaid\n    flowchart LR\n    A --> B\n    ~~~",
      "    flowchart LR\n    A --> B",
    ],
    [
      "tab-indented list",
      "- ~~~mermaid\n\tflowchart LR\n\tA --> B\n\t~~~",
      "\tflowchart LR\n\tA --> B",
    ],
    [
      "nested blockquote",
      "> > ~~~mermaid\n> > flowchart LR\n> > A --> B\n> > ~~~",
      "> > flowchart LR\n> > A --> B",
    ],
  ])("preserves the exact physical prefix for a %s block", (_name, markdown, expected) => {
    const [block] = findMermaidBlocks(markdown);

    expect(block?.source).toBe(expected);
    expect(markdown.slice(block.from, block.to)).toBe(expected);
  });

  it.each([
    [
      "mixed list",
      "- ~~~mermaid\r\n\tflowchart LR\r\n    A --> B\r\n  ~~~",
      "\tflowchart LR\r\n    A --> B",
    ],
    [
      "mixed nested list",
      "  - ~~~mermaid\r\n\t\tflowchart LR\r\n        A --> B\r\n    ~~~",
      "\t\tflowchart LR\r\n        A --> B",
    ],
    [
      "mixed blockquote",
      "> ~~~mermaid\r\n>\tflowchart LR\r\n>   A --> B\r\n> ~~~",
      ">\tflowchart LR\r\n>   A --> B",
    ],
  ])("keeps exact mixed tab/space physical lines for a %s block", (_name, markdown, expected) => {
    const [block] = findMermaidBlocks(markdown);

    expect(block?.source).toBe(expected);
    expect(markdown.slice(block.from, block.to)).toBe(expected);
  });

  it.each([
    [
      "list",
      "- ~~~mermaid\n    flowchart LR\n  A --> B\n  ~~~",
      "    flowchart LR\n  A --> B",
    ],
    [
      "nested list",
      "  - ~~~mermaid\n        flowchart LR\n    A --> B\n    ~~~",
      "        flowchart LR\n    A --> B",
    ],
    [
      "blockquote",
      "> ~~~mermaid\n>   flowchart LR\n> A --> B\n> ~~~",
      ">   flowchart LR\n> A --> B",
    ],
    [
      "mixed list",
      "- ~~~mermaid\r\n        flowchart LR\r\n\tA --> B\r\n    ~~~",
      "        flowchart LR\r\n\tA --> B",
    ],
  ])("discovers exact %s source when the header has extra logical indentation", (
    _name,
    markdown,
    expected,
  ) => {
    const [block] = findMermaidBlocks(markdown);

    expect(block?.source).toBe(expected);
    expect(markdown.slice(block.from, block.to)).toBe(expected);
  });

  it("creates an exact payload with a stable UTF-8 SHA-256 source hash", async () => {
    // Break caught: hashing normalized or surrounding Markdown makes host snapshot checks unstable.
    const source = "flowchart LR\nA --> B";
    await expect(createMermaidEditPayload({ from: 18, to: 38, source })).resolves.toEqual({
      from: 18,
      to: 38,
      source,
      sourceHash: "e52b4b37f626102c671c29a373c7833fa699b3060e3810eee1e57e51707a4d19",
    });
  });

  it("disables visual edit and exposes the parser reason", () => {
    // Break caught: unsupported Mermaid is force-opened or locked without an explanation.
    const action = describeMermaidAction("sequenceDiagram\nA->>B: hi");
    expect(action.enabled).toBe(false);
    expect(action.reason).not.toBe("");
  });
});

describe("Mermaid block actions", () => {
  it("posts only the rendered block content from the preview action", async () => {
    // Break caught: the rendered action sends another block or Markdown outside its fences.
    const container = document.createElement("div");
    document.body.append(container);
    const requested = vi.fn();
    const markdown = "before\n```mermaid\nflowchart LR\nA --> B\n```\nafter";

    await renderPreview(markdown, {
      container,
      mermaidAdapter,
      onMermaidEditRequested: requested,
    });
    const button = container.querySelector("[data-mermaid-edit-action]");
    expect(button?.textContent).toBe("시각 편집");
    expect(button?.disabled).toBe(false);
    button.click();

    await vi.waitFor(() => expect(requested).toHaveBeenCalledOnce());
    expect(requested.mock.calls[0][0]).toEqual({
      from: 18,
      to: 38,
      source: "flowchart LR\nA --> B",
      sourceHash: "e52b4b37f626102c671c29a373c7833fa699b3060e3810eee1e57e51707a4d19",
      actionId: expect.stringMatching(UUID_PATTERN),
      actionOrigin: "rendered",
    });
    container.remove();
  });

  it("renders a clickable limited-mode preview action and still requests an edit", async () => {
    // Break caught: a diagram the strict flowchart parser rejects (any other Mermaid
    // diagram type, malformed flowchart syntax, ...) used to render a dead, unclickable
    // button. It must still open the editor - just in limited (text-only) mode - so the
    // action stays a real edit affordance, not a wall.
    const container = document.createElement("div");
    const requested = vi.fn();
    await renderPreview("~~~mermaid\nsequenceDiagram\nA->>B: hi\n~~~", {
      container,
      mermaidAdapter,
      onMermaidEditRequested: requested,
    });

    const button = container.querySelector("[data-mermaid-edit-action]");
    expect(button?.disabled).toBe(false);
    expect(button?.getAttribute("aria-disabled")).toBeNull();
    expect(button?.dataset.mermaidLimitedMode).toBe("true");
    expect(button?.tabIndex).toBe(0);
    expect(button?.title).not.toBe("");
    expect(button?.getAttribute("aria-label")).toContain(button.title);
    const reason = container.querySelector(`#${button.getAttribute("aria-describedby")}`);
    expect(reason?.textContent).toBe(button.title);
    button.click();
    await vi.waitFor(() => expect(requested).toHaveBeenCalledOnce());
    container.remove();
  });

  it.each([
    ["safe parser", "sequenceDiagram\nA->>B: hi", "flowchart 다이어그램만 시각 편집을 지원합니다. 텍스트로만 편집할 수 있습니다."],
    ["renderer", "flowchart LR\nA --> B", "다이어그램을 렌더링하지 못해 텍스트로만 편집할 수 있습니다."],
  ])("keeps a discoverable limited-mode action with the %s rejection reason in Korean", async (_name, source, message) => {
    // Break caught: the limited-mode reason surfaced only as an internal English code
    // ("unsupported-syntax", "render-failed", ...) with no on-screen explanation.
    const container = document.createElement("div");
    document.body.append(container);
    const requested = vi.fn();
    const rejectingAdapter = { render: vi.fn(async () => { throw new Error("rejected"); }) };

    await renderPreview(`~~~mermaid\n${source}\n~~~`, {
      container,
      mermaidAdapter: rejectingAdapter,
      onMermaidEditRequested: requested,
    });

    const button = container.querySelector("[data-mermaid-edit-action]");
    expect(button).not.toBeNull();
    expect(button.disabled).toBe(false);
    expect(button.getAttribute("aria-disabled")).toBeNull();
    expect(button.dataset.mermaidLimitedMode).toBe("true");
    expect(button.tabIndex).toBe(0);
    expect(button.title).toBe(message);
    const described = container.querySelector(`#${button.getAttribute("aria-describedby")}`);
    expect(described?.textContent).toBe(message);
    button.focus();
    expect(document.activeElement).toBe(button);
    button.click();
    await vi.waitFor(() => expect(requested).toHaveBeenCalledOnce());
    container.remove();
  });

  it("falls back to a generic Korean message for an unmapped limited-mode reason code", () => {
    // Break caught: an unrecognized reason code (e.g. a future parser addition) would leave
    // the tooltip/description blank instead of degrading to something readable.
    expect(describeLimitedModeReason("some-future-reason-code")).toBe(
      "이 다이어그램은 텍스트로만 편집할 수 있습니다.",
    );
  });

  it("keeps a later rendered action mapped when an earlier Mermaid block cannot render", async () => {
    // Break caught: placeholder removal shifts indexes and assigns the first block to a later diagram.
    const container = document.createElement("div");
    const requested = vi.fn();
    const markdown = [
      "```mermaid",
      "not a diagram",
      "```",
      "between",
      "~~~mermaid",
      "flowchart TD",
      "X --> Y",
      "~~~",
    ].join("\n");

    await renderPreview(markdown, { container, mermaidAdapter, onMermaidEditRequested: requested });
    const buttons = [...container.querySelectorAll("[data-mermaid-edit-action]")];
    expect(buttons).toHaveLength(2);
    expect(buttons.find((button) => button.dataset.mermaidLimitedMode === "true")?.title)
      .toBe(describeLimitedModeReason("flowchart-required"));
    const enabled = buttons.find((button) => button.dataset.mermaidLimitedMode !== "true");
    enabled.click();

    await vi.waitFor(() => expect(requested).toHaveBeenCalledOnce());
    expect(requested.mock.calls[0][0]).toMatchObject({
      from: 48,
      to: 68,
      source: "flowchart TD\nX --> Y",
    });
  });

  it("routes mouse gutter activation through the exact accessible panel identity", async () => {
    // Break caught: the hidden gutter owns the modal identity and becomes an inaccessible
    // focus-restoration target instead of the visible panel action.
    const markdown = "before\r\n```mermaid\r\nflowchart LR\r\nA --> B\r\n```\r\nafter";
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation(markdown));
    document.body.append(controller.view.dom);
    const gutter = controller.view.dom.querySelector('[data-mermaid-action-surface="gutter"]');
    const panel = controller.view.dom.querySelector('[data-mermaid-action-surface="panel"]');
    const button = gutter;

    expect(gutter?.hasAttribute("data-mermaid-edit-action")).toBe(false);
    expect(gutter?.dataset.mermaidActionId).toBeUndefined();

    expect(button?.textContent).toBe("시각 편집");
    expect(button?.disabled).toBe(false);
    button.click();
    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0].type).toBe("mermaid.editRequested");
    expect(Object.keys(messages[0].payload)).toEqual([
      "from", "to", "source", "sourceHash", "actionId", "actionOrigin",
    ]);
    expect(messages[0].payload).toEqual({
      from: 20,
      to: 41,
      source: "flowchart LR\r\nA --> B",
      sourceHash: "c196d4502af12663cc58cdc9a7876f69a6ddd258c08597203e5b860e94900b0d",
      actionId: panel.dataset.mermaidActionId,
      actionOrigin: "editor",
    });
    controller.dispose();
  });

  it("provides a focusable editor action outside CodeMirror's aria-hidden gutter", async () => {
    const markdown = "```mermaid\nsequenceDiagram\nA->>B: hi\n```";
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation(markdown));
    document.body.append(controller.view.dom);

    const button = controller.view.dom.querySelector('[data-mermaid-action-surface="panel"]');
    expect(button).not.toBeNull();
    expect(button.closest('[aria-hidden="true"]')).toBeNull();
    expect(button.disabled).toBe(false);
    expect(button.getAttribute("aria-disabled")).toBeNull();
    expect(button.dataset.mermaidLimitedMode).toBe("true");
    expect(button.tabIndex).toBe(0);
    expect(button.dataset.mermaidActionOrigin).toBe("editor");
    const described = controller.view.dom.querySelector(`#${button.getAttribute("aria-describedby")}`);
    expect(described?.textContent).toBe(describeLimitedModeReason("flowchart-required"));
    button.focus();
    expect(document.activeElement).toBe(button);
    button.click();
    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0].type).toBe("mermaid.editRequested");
    controller.dispose();
  });

  it("posts and restores focus to the exact accessible editor-panel action", async () => {
    const markdown = "```mermaid\nflowchart LR\nA --> B\n```";
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation(markdown));
    document.body.append(controller.view.dom);
    const button = controller.view.dom.querySelector('[data-mermaid-action-surface="panel"]');

    button.click();
    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0].payload).toMatchObject({
      actionId: button.dataset.mermaidActionId,
      actionOrigin: "editor",
    });
    expect(focusMermaidAction(
      controller.view.dom,
      messages[0].payload.actionId,
      messages[0].payload.actionOrigin,
    )).toBe(true);
    expect(document.activeElement).toBe(button);
    controller.dispose();
  });

  it("keeps hidden gutter duplicates out of the keyboard tree and uniquely names each panel action", async () => {
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
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation(markdown));
    document.body.append(controller.view.dom);

    const gutterActions = [...controller.view.dom.querySelectorAll(
      '[data-mermaid-action-surface="gutter"]',
    )];
    expect(gutterActions).toHaveLength(2);
    for (const action of gutterActions) {
      expect(action.tabIndex).toBe(-1);
      expect(action.closest('[aria-hidden="true"]')).not.toBeNull();
      expect(action.hasAttribute("data-mermaid-edit-action")).toBe(false);
      expect(action.dataset.mermaidActionId).toBeUndefined();
      expect(action.getAttribute("aria-label")).toBeNull();
      expect(action.getAttribute("aria-describedby")).toBeNull();
    }

    const panelActions = [...controller.view.dom.querySelectorAll(
      '[data-mermaid-action-surface="panel"]',
    )];
    expect(panelActions).toHaveLength(2);
    expect(panelActions[0].getAttribute("aria-label")).toContain("line 2");
    expect(panelActions[1].getAttribute("aria-label")).toContain("line 7");
    expect(panelActions[0].getAttribute("aria-label"))
      .not.toBe(panelActions[1].getAttribute("aria-label"));
    expect(panelActions[0].dataset.mermaidActionId)
      .not.toBe(panelActions[1].dataset.mermaidActionId);

    panelActions[1].click();
    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0].payload).toMatchObject({
      actionId: panelActions[1].dataset.mermaidActionId,
      actionOrigin: "editor",
      from: markdown.indexOf("flowchart TD"),
    });
    expect(focusMermaidAction(
      controller.view.dom,
      messages[0].payload.actionId,
      messages[0].payload.actionOrigin,
    )).toBe(true);
    expect(document.activeElement).toBe(panelActions[1]);
    controller.dispose();
  });

  it("focuses only the exact originating DOM action and refuses a same-surface rerender", async () => {
    // Break caught: WPF focus lands on whichever action replaced the originating DOM control.
    const firstContainer = document.createElement("div");
    document.body.append(firstContainer);
    const requested = vi.fn();
    await renderPreview("```mermaid\nflowchart LR\nA --> B\n```", {
      container: firstContainer,
      mermaidAdapter,
      onMermaidEditRequested: requested,
    });
    const first = firstContainer.querySelector("[data-mermaid-edit-action]");
    first.click();
    await vi.waitFor(() => expect(requested).toHaveBeenCalledOnce());
    const identity = requested.mock.calls[0][0];

    expect(focusMermaidAction(firstContainer, identity.actionId, identity.actionOrigin)).toBe(true);
    expect(document.activeElement).toBe(first);

    const replacementContainer = document.createElement("div");
    await renderPreview("```mermaid\nflowchart LR\nA --> B\n```", {
      container: replacementContainer,
      mermaidAdapter,
      onMermaidEditRequested: vi.fn(),
    });
    expect(focusMermaidAction(
      replacementContainer,
      identity.actionId,
      identity.actionOrigin,
    )).toBe(false);
  });

  it("falls back to a uniquely hash-matched current block when the stale offset moved", async () => {
    // Break caught: the WPF conflict action closed as cancel and never started a new document-owned session.
    const source = "flowchart LR\nA --> B";
    const sourceHash = "e52b4b37f626102c671c29a373c7833fa699b3060e3810eee1e57e51707a4d19";
    const markdown = `new prefix\n\`\`\`mermaid\n${source}\n\`\`\``;
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    const current = { ...activation(markdown), documentRevision: 8 };
    controller.hydrate(current);

    expect(controller.handleHostMessage({
      version: 1,
      type: "mermaid.reopenRequested",
      requestId: current.requestId,
      windowId: current.windowId,
      tabId: current.tabId,
      documentRevision: 8,
      payload: { from: 11, sourceHash, actionId: REQUEST_ID, actionOrigin: "editor" },
    })).toBe(true);

    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0]).toMatchObject({
      type: "mermaid.editRequested",
      requestId: current.requestId,
      windowId: current.windowId,
      tabId: current.tabId,
      documentRevision: 8,
      payload: {
        from: 22,
        to: 42,
        source,
        sourceHash,
      },
    });
    expect(messages[0].payload).not.toHaveProperty("replacement");
    controller.dispose();
  });

  it("reopens the supported current block at the exact stale offset after its source changed", async () => {
    // Break caught: a source change at the same range makes a stale hash prevent a fresh session.
    const staleSource = "flowchart LR\nA --> B";
    const currentSource = "flowchart LR\nA --> C";
    const markdown = `\`\`\`mermaid\n${currentSource}\n\`\`\``;
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    const current = { ...activation(markdown), documentRevision: 8 };
    const [block] = findMermaidBlocks(markdown);
    const stalePayload = await createMermaidEditPayload({
      from: block.from,
      to: block.to,
      source: staleSource,
    });
    const freshPayload = await createMermaidEditPayload(block);
    controller.hydrate(current);

    expect(controller.handleHostMessage({
      version: 1,
      type: "mermaid.reopenRequested",
      requestId: current.requestId,
      windowId: current.windowId,
      tabId: current.tabId,
      documentRevision: 8,
      payload: {
        from: block.from,
        sourceHash: stalePayload.sourceHash,
        actionId: REQUEST_ID,
        actionOrigin: "editor",
      },
    })).toBe(true);

    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0]).toMatchObject({
      type: "mermaid.editRequested",
      requestId: current.requestId,
      windowId: current.windowId,
      tabId: current.tabId,
      documentRevision: 8,
      payload: freshPayload,
    });
    expect(messages[0].payload.sourceHash).not.toBe(stalePayload.sourceHash);
    controller.dispose();
  });

  it("does not reopen from an ambiguous moved hash fallback", async () => {
    // Break caught: matching duplicate blocks could reopen a different document-owned block.
    const source = "flowchart LR\nA --> B";
    const markdown = `\`\`\`mermaid\n${source}\n\`\`\`\ntext\n\`\`\`mermaid\n${source}\n\`\`\``;
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    const current = { ...activation(markdown), documentRevision: 8 };
    const sourceHash = (await createMermaidEditPayload({ from: 0, to: source.length, source })).sourceHash;
    controller.hydrate(current);

    expect(controller.handleHostMessage({
      version: 1,
      type: "mermaid.reopenRequested",
      requestId: current.requestId,
      windowId: current.windowId,
      tabId: current.tabId,
      documentRevision: 8,
      payload: { from: 999, sourceHash, actionId: REQUEST_ID, actionOrigin: "editor" },
    })).toBe(true);

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(messages).toEqual([]);
    controller.dispose();
  });

  it("does not reopen when neither the stale offset nor hash identifies a current block", async () => {
    // Break caught: an unmatched stale request could choose an unrelated supported block.
    const staleSource = "flowchart LR\nA --> B";
    const markdown = "```mermaid\nflowchart LR\nA --> C\n```";
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    const current = { ...activation(markdown), documentRevision: 8 };
    const sourceHash = (await createMermaidEditPayload({ from: 0, to: staleSource.length, source: staleSource })).sourceHash;
    controller.hydrate(current);

    expect(controller.handleHostMessage({
      version: 1,
      type: "mermaid.reopenRequested",
      requestId: current.requestId,
      windowId: current.windowId,
      tabId: current.tabId,
      documentRevision: 8,
      payload: { from: 999, sourceHash, actionId: REQUEST_ID, actionOrigin: "editor" },
    })).toBe(true);

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(messages).toEqual([]);
    controller.dispose();
  });

  it("still opens a CodeMirror action for unsupported source, marked as limited mode", async () => {
    // Break caught: a source the strict flowchart parser rejects used to leave the CodeMirror
    // gutter/panel action dead. It must still request an edit (in limited/text-only mode).
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation("~~~mermaid\nsequenceDiagram\nA->>B: hi\n~~~"));
    const button = controller.view.dom.querySelector("[data-mermaid-edit-action]");

    expect(button?.disabled).toBe(false);
    expect(button?.getAttribute("aria-disabled")).toBeNull();
    expect(button?.dataset.mermaidLimitedMode).toBe("true");
    expect(button?.tabIndex).toBe(0);
    expect(button?.title).not.toBe("");
    expect(button?.getAttribute("aria-label")).toContain(button.title);
    const reason = controller.view.dom.querySelector(`#${button.getAttribute("aria-describedby")}`);
    expect(reason?.textContent).toBe(button.title);
    button.click();
    await vi.waitFor(() => expect(messages).toHaveLength(1));
    expect(messages[0].type).toBe("mermaid.editRequested");
    controller.dispose();
  });

  it("does not expose a CodeMirror action for Mermaid-looking text in a text fence", () => {
    // Break caught: the gutter offers visual editing for content Markdown treats as plain text.
    const markdown = [
      "````text",
      "```mermaid",
      "flowchart LR",
      "A --> B",
      "```",
      "````",
    ].join("\n");
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation(markdown));

    expect(controller.view.dom.querySelector("[data-mermaid-edit-action]")).toBeNull();
    expect(messages).toEqual([]);
    controller.dispose();
  });

  it("does not request visual editing from an unaccepted editor snapshot", async () => {
    // Break caught: optimistic source/offsets are posted with the last accepted document revision.
    const digest = vi.spyOn(crypto.subtle, "digest")
      .mockResolvedValue(new Uint8Array(32).buffer);
    const markdown = "```mermaid\nflowchart LR\nA --> B\n```";
    const messages = [];
    const controller = createEditorController({
      host: { postMessage: (message) => messages.push(message) },
      store: createTabStateStore(),
    });
    controller.hydrate(activation(markdown));
    controller.view.dispatch({
      changes: { from: markdown.indexOf("-->"), insert: " " },
    });

    const button = controller.view.dom.querySelector("[data-mermaid-edit-action]");
    expect(button?.disabled).toBe(false);
    button.click();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(messages.map((message) => message.type)).toEqual(["document.changed"]);
    controller.dispose();
    digest.mockRestore();
  });

  it("scans a CodeMirror document once per document state for gutter markers", () => {
    // Break caught: each visible gutter line rescans the full Markdown document.
    const scanBlocks = vi.fn(findMermaidBlocks);
    const state = EditorState.create({
      doc: [
        "heading",
        "```mermaid",
        "flowchart LR",
        "A --> B",
        "```",
        ...Array.from({ length: 40 }, (_, index) => `line ${index}`),
      ].join("\n"),
      extensions: [createMermaidGutter(vi.fn(), scanBlocks)],
    });
    const view = new EditorView({ state });

    expect(scanBlocks).toHaveBeenCalledTimes(1);
    view.dispatch({ changes: { from: view.state.doc.length, insert: "\nlast" } });
    expect(scanBlocks).toHaveBeenCalledTimes(2);
    view.destroy();
  });
});
