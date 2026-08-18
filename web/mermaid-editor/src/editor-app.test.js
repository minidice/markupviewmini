import { describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { analyzeMermaidSource } from "@markup-view-mini/mermaid-safe/analyzer";
import { bindZoomControls, createBlockSession, createEditorState, createRenderCoordinator } from "./editor-app.js";
import * as EditorApp from "./editor-app.js";
import { bindCanvasInteractions, bindCanvasPan } from "./canvas-interactions.js";
import { createCanvasView } from "./canvas-view.js";
import { renderInspector } from "./inspector.js";
import { PALETTE } from "./palette.js";
import { CLASSIC_SHAPES } from "./node-shapes.js";
import { clampZoom, stepZoom } from "./zoom-control.js";

describe("block session host protocol", () => {
  it("uses bridge version 1 and the exact open payload", () => {
    const posted = [];
    const session = createBlockSession({ postMessage: (message) => posted.push(message) });

    session.open({ sessionId: "s-1", source: `flowchart LR
A --> B`, language: "mermaid" });

    expect(posted).toEqual([
      { version: 1, type: "mermaid.ready", payload: {} },
      {
        version: 1,
        type: "mermaid.validityChanged",
        payload: {
          sessionId: "s-1",
          source: "flowchart LR\nA --> B",
          language: "mermaid",
          sourceVersion: 0,
          supported: true,
          reason: "",
        },
      },
    ]);
    expect(session.sessionId).toBe("s-1");
    expect(session.source).toBe(`flowchart LR
A --> B`);
    expect(session.language).toBe("mermaid");
  });
});

describe("block session state", () => {
  it("does not emit change, confirm, or cancel before a valid open", () => {
    const posted = [];
    const session = createBlockSession({ postMessage: (message) => posted.push(message) });

    expect(session.change("flowchart LR")).toBe(false);
    expect(session.confirm()).toBe(false);
    expect(session.cancel()).toBe(false);
    expect(posted).toEqual([{ version: 1, type: "mermaid.ready", payload: {} }]);
  });

  it("reports non-flowchart source as confirmable (limited text mode) but refuses empty confirm", () => {
    // Break caught: a diagram the visual editor's strict flowchart parser can't understand
    // (any other Mermaid diagram type, malformed flowchart syntax, ...) used to be locked out
    // of Confirm entirely - a dead end. "supported" on this bridge message means "the host may
    // accept a confirm", not "the visual canvas understands it", so it stays true for any
    // non-empty source; only a genuinely empty source should block confirm.
    const posted = [];
    const session = createBlockSession({ postMessage: (message) => posted.push(message) });
    session.open({ sessionId: "s-1", source: "sequenceDiagram\nA->>B: hi", language: "mermaid" });

    expect(posted.at(-1)).toEqual({
      version: 1,
      type: "mermaid.validityChanged",
      payload: {
        sessionId: "s-1",
        source: "sequenceDiagram\nA->>B: hi",
        language: "mermaid",
        sourceVersion: 0,
        supported: true,
        reason: "",
      },
    });
    expect(session.confirm()).toBe(true);
    expect(posted.at(-1).type).toBe("mermaid.confirm");

    session.change("   ");
    expect(posted.at(-1)).toEqual({
      version: 1,
      type: "mermaid.validityChanged",
      payload: {
        sessionId: "s-1",
        source: "   ",
        language: "mermaid",
        sourceVersion: 1,
        supported: false,
        reason: "empty",
      },
    });
    expect(session.confirm()).toBe(false);
    expect(posted.filter((message) => message.type === "mermaid.confirm")).toHaveLength(1);
  });

  it("increments one source version per change and binds confirm to the current version", () => {
    // Break caught: an asynchronously delivered validity report can authorize an older source.
    const posted = [];
    const session = createBlockSession({ postMessage: (message) => posted.push(message) });
    session.open({ sessionId: "s-1", source: "flowchart LR\nA --> B", language: "mermaid" });

    session.change("flowchart LR\nA --> C");
    session.change("flowchart LR\nA --> D");
    expect(session.confirm()).toBe(true);

    expect(posted.filter((message) => message.type === "mermaid.changed")
      .map((message) => message.payload.sourceVersion)).toEqual([1, 2]);
    expect(posted.filter((message) => message.type === "mermaid.validityChanged")
      .map((message) => message.payload.sourceVersion)).toEqual([0, 1, 2]);
    expect(posted.at(-1)).toMatchObject({
      type: "mermaid.confirm",
      payload: { source: "flowchart LR\nA --> D", sourceVersion: 2 },
    });
  });
});

describe("render coordinator", () => {
  it("does not apply a stale supported render after the source becomes unsupported", async () => {
    let resolveFirst;
    const applied = [];
    const coordinator = createRenderCoordinator({
      analyze: (source) => source === "bad" ? { supported: false, reason: "unsupported", model: null } : { supported: true },
      render: () => new Promise((resolve) => { resolveFirst = resolve; }),
      apply: (result) => applied.push(result),
    });

    const first = coordinator.render("flowchart LR");
    await coordinator.render("bad");
    resolveFirst({ ok: true, svg: "<svg>stale</svg>" });
    await first;

    expect(applied).toEqual([{ ok: false, reason: "unsupported", svg: "" }]);
  });
});

describe("canvas view", () => {
  it("tracks pan and zoom through one connected view state", () => {
    const changes = [];
    const surface = document.createElement("div");
    const canvas = createCanvasView(document.createElement("div"), surface, (...state) => changes.push(state));

    canvas.set(2);
    canvas.panBy(5, -3);

    expect(surface.style.transform).toBe("translate(5px, -3px) scale(2)");
    expect(changes.at(-1)).toEqual([2, 5, -3]);
  });
});

describe("canvas controls", () => {
  it("steps zoom in both directions while clamping its bounds", () => {
    expect(stepZoom(1, 1)).toBeGreaterThan(1);
    expect(stepZoom(1, -1)).toBeLessThan(1);
    expect(clampZoom(99)).toBe(4);
  });

  it("connects zoom buttons to the canvas view", () => {
    const root = document.createElement("div");
    root.innerHTML = '<button data-zoom-in></button><button data-zoom-out></button><button data-canvas-reset></button>';
    const canvas = { zoom: 1, set: vi.fn(), reset: vi.fn() };
    const unbind = bindZoomControls(root, canvas);

    root.querySelector("[data-zoom-in]").click();
    root.querySelector("[data-zoom-out]").click();
    root.querySelector("[data-canvas-reset]").click();

    expect(canvas.set).toHaveBeenCalledTimes(2);
    expect(canvas.reset).toHaveBeenCalledOnce();
    unbind();
  });

  it("pans the connected canvas on an empty-area pointer drag", () => {
    const viewport = document.createElement("div");
    const canvas = { panBy: vi.fn() };
    const unbind = bindCanvasPan(viewport, canvas);

    viewport.dispatchEvent(new MouseEvent("pointerdown", { clientX: 10, clientY: 8, bubbles: true }));
    viewport.dispatchEvent(new MouseEvent("pointermove", { clientX: 16, clientY: 3, bubbles: true }));

    expect(canvas.panBy).toHaveBeenCalledWith(6, -5);
    unbind();
  });

  it("selects a rendered node only while safe visual editing is enabled", () => {
    const surface = document.createElement("div");
    surface.innerHTML = '<svg><g class="node" id="A"></g></svg>';
    const selected = [];
    const unbind = bindCanvasInteractions({ surface, isEnabled: () => true, select: (id) => selected.push(id) });

    surface.querySelector("g").dispatchEvent(new MouseEvent("click", { bubbles: true }));

    expect(selected).toEqual(["A"]);
    unbind();
  });
});

describe("inspector assets", () => {
  it("reports the available modeled node shapes and palette", () => {
    const inspector = document.createElement("aside");
    renderInspector(inspector, { supported: true });

    expect(CLASSIC_SHAPES.length).toBeGreaterThan(0);
    expect(PALETTE.length).toBeGreaterThan(1);
    expect(inspector.textContent).toContain("Shapes:");
  });
});

describe("editor selection state", () => {
  it("clears a stale node selection when a new open or source edit replaces the diagram", () => {
    const state = createEditorState();
    state.select("old-node");
    state.resetForSource();
    expect(state.selection).toBeNull();

    state.select("another-old-node");
    state.resetForOpen();
    expect(state.selection).toBeNull();
  });
});

describe("mounted visual editor", () => {
  it("ships every supported direction including TB with exact pressed state", async () => {
    const page = new DOMParser().parseFromString(
      readFileSync("index.html", "utf8"),
      "text/html",
    );
    const root = page.querySelector("#mermaid-app");
    document.body.append(root);
    const posted = [];
    let receiveMessage;
    const bridge = {
      postMessage: (message) => posted.push(message),
      addEventListener: (_type, handler) => { receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({ ok: true, reason: "", svg: "<svg></svg>" }));

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    const controls = [...root.querySelectorAll("[data-direction]")];
    expect(controls.map((control) => control.dataset.direction)).toEqual(["TD", "TB", "LR", "BT", "RL"]);
    expect(controls.every((control) => control.getAttribute("aria-pressed") === "false")).toBe(true);

    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-direction", source: "flowchart TB\nA --> B", language: "mermaid" },
    } });
    const tb = root.querySelector('[data-direction="TB"]');
    expect(tb.getAttribute("aria-pressed")).toBe("true");
    expect(controls.filter((control) => control !== tb)
      .every((control) => control.getAttribute("aria-pressed") === "false")).toBe(true);

    root.querySelector('[data-direction="LR"]').click();
    expect(root.querySelector('[data-direction="LR"]').getAttribute("aria-pressed")).toBe("true");
    expect(tb.getAttribute("aria-pressed")).toBe("false");
    const changedSource = posted.filter((message) => message.type === "mermaid.changed").at(-1)?.payload.source;
    expect(changedSource).toContain("flowchart LR");
    expect(changedSource).toContain("A --> B");

    dispose();
    root.remove();
  });

  it("opens a non-flowchart diagram in limited text mode with Confirm enabled until cleared", async () => {
    // Break caught: opening a diagram the strict flowchart parser rejects left Confirm
    // permanently disabled - the "시각 편집" action was a dead end for any diagram type
    // other than flowchart, or any flowchart with a syntax slip. It must still let the user
    // edit and confirm raw text; only an empty source should block Confirm.
    const page = new DOMParser().parseFromString(
      readFileSync("index.html", "utf8"),
      "text/html",
    );
    const root = page.querySelector("#mermaid-app");
    document.body.append(root);
    const posted = [];
    let receiveMessage;
    const bridge = {
      postMessage: (message) => posted.push(message),
      addEventListener: (_type, handler) => { receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({ ok: false, reason: "not-a-flowchart", svg: "" }));

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-limited", source: "sequenceDiagram\nA->>B: hi", language: "mermaid" },
    } });
    await vi.waitFor(() => expect(root.querySelector("[data-status]").textContent).not.toBe(""));

    const confirm = root.querySelector("[data-confirm]");
    const source = root.querySelector("[data-source]");
    expect(confirm.disabled).toBe(false);
    expect(source.disabled).toBe(false);
    expect(root.querySelector("[data-status]").textContent).toContain("텍스트로만 편집");

    confirm.click();
    expect(posted.at(-1)).toMatchObject({
      type: "mermaid.confirm",
      payload: { source: "sequenceDiagram\nA->>B: hi" },
    });

    source.value = "";
    source.dispatchEvent(new Event("input"));
    expect(confirm.disabled).toBe(true);

    dispose();
    root.remove();
  });

  it("mutates the selected node label, shape, palette colour, and graph direction through the safe serializer", async () => {
    const root = document.createElement("main");
    root.innerHTML = `
      <textarea data-source></textarea>
      <div data-status></div>
      <button data-direction="TD">TD</button>
      <button data-direction="LR">LR</button>
      <button data-direction="RL">RL</button>
      <button data-zoom-out></button><button data-zoom-in></button><button data-canvas-reset></button>
      <div data-canvas-viewport><div data-canvas></div></div>
      <aside data-inspector></aside>
      <button data-cancel></button><button data-confirm></button>`;
    document.body.append(root);
    const posted = [];
    let receiveMessage;
    const bridge = {
      postMessage: (message) => posted.push(message),
      addEventListener: (type, handler) => {
        if (type === "message") receiveMessage = handler;
      },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({
      ok: true,
      reason: "",
      svg: '<svg id="diagram-test"><g class="node" id="diagram-test-flowchart-A-0"><rect></rect></g></svg>',
    }));

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: {
        sessionId: "s-1",
        source: "  %% keep\n  flowchart LR\n  A[Read] --> B[Edit]",
        language: "mermaid",
      },
    } });
    await vi.waitFor(() => expect(root.querySelector("g.node")).not.toBeNull());

    const renderedNode = root.querySelector("g.node");
    expect(renderedNode.tabIndex).toBe(0);
    expect(renderedNode.getAttribute("role")).toBe("button");
    renderedNode.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    const label = root.querySelector("[data-node-label]");
    expect(label?.value).toBe("Read");
    expect(label?.tabIndex).toBe(0);
    label.value = "Write";
    label.dispatchEvent(new Event("change", { bubbles: true }));
    await vi.waitFor(() => expect(root.querySelector("[data-source]").value).toContain("A[Write]"));

    const round = root.querySelector('[data-node-shape="round"]');
    expect(round?.tabIndex).toBe(0);
    round.click();
    await vi.waitFor(() => expect(root.querySelector("[data-source]").value).toContain("A(Write)"));

    const blue = root.querySelector('[data-node-color="blue"]');
    expect(blue?.tabIndex).toBe(0);
    blue.click();
    await vi.waitFor(() => expect(root.querySelector("[data-source]").value)
      .toContain("style A fill:#e3f2fd,stroke:#1565c0,color:#0d3c74"));

    const direction = root.querySelector('[data-direction="RL"]');
    expect(direction.tabIndex).toBe(0);
    direction.click();
    await vi.waitFor(() => expect(root.querySelector("[data-source]").value).toContain("flowchart RL"));

    const current = root.querySelector("[data-source]").value;
    expect(current).toContain("  %% keep");
    expect(current.split("\n").every((line) => line === "" || line.startsWith("  "))).toBe(true);
    expect(posted.filter((message) => message.type === "mermaid.changed").at(-1)?.payload.source).toBe(current);
    expect(posted.filter((message) => message.type === "mermaid.changed")).toHaveLength(4);

    dispose();
    root.remove();
  });

  it("changes only the selected node in a repeated-reference graph, keeping the rest semantically intact", async () => {
    // Break caught: production visual mutation changes repeated round/diamond nodes to
    // rectangles, or loses other nodes/edges/comments/colours entirely.
    // Note: the serializer fully reconstructs mermaid syntax from the model rather than
    // patching only the touched byte range (MinisTool architecture, ported 2026-08), so the
    // rebuilt source is no longer byte-identical to "original with one substring replaced" -
    // this asserts the semantics that must survive, not the exact text.
    const root = document.createElement("main");
    root.innerHTML = `
      <textarea data-source></textarea><div data-status></div>
      <button data-direction="TD">TD</button><button data-direction="TB">TB</button>
      <button data-direction="LR">LR</button><button data-direction="BT">BT</button>
      <button data-direction="RL">RL</button>
      <button data-zoom-out></button><button data-zoom-in></button><button data-canvas-reset></button>
      <div data-canvas-viewport><div data-canvas></div></div><aside data-inspector></aside>
      <button data-cancel></button><button data-confirm></button>`;
    document.body.append(root);
    const posted = [];
    let receiveMessage;
    const bridge = {
      postMessage: (message) => posted.push(message),
      addEventListener: (_type, handler) => { receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({
      ok: true,
      reason: "",
      svg: '<svg id="diagram-test"><g class="node" id="diagram-test-flowchart-A-0"><text>Round</text></g></svg>',
    }));
    const original = [
      "flowchart LR",
      "  %% before nodes",
      "  A(Round) --> B{Decision}",
      "",
      "  %% between edges",
      "  A --> C",
      "  B --> D",
      "  style A fill:#e3f2fd,stroke:#1565c0,color:#0d3c74",
      "  %% after style",
    ].join("\n");

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-2", source: original, language: "mermaid" },
    } });
    await vi.waitFor(() => expect(root.querySelector("g.node")).not.toBeNull());
    root.querySelector("g.node").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    const label = root.querySelector("[data-node-label]");
    label.value = "Changed";
    label.dispatchEvent(new Event("change", { bubbles: true }));

    await vi.waitFor(() => expect(root.querySelector("[data-source]").value).toContain("A(Changed)"));
    const rewritten = root.querySelector("[data-source]").value;
    expect(rewritten).not.toContain("A(Round)");
    expect(posted.filter((message) => message.type === "mermaid.changed").at(-1)?.payload.source)
      .toBe(rewritten);
    expect(rewritten).toContain("%% before nodes");
    expect(rewritten).toContain("%% between edges");
    expect(rewritten).toContain("%% after style");

    const model = analyzeMermaidSource(rewritten).model;
    expect(model.nodes.find((node) => node.id === "A")).toMatchObject({
      label: "Changed", shape: "round", color: "blue",
    });
    expect(model.nodes.find((node) => node.id === "B")).toMatchObject({
      label: "Decision", shape: "diamond",
    });
    expect(model.edges.map(({ from, to }) => [from, to])).toEqual([
      ["A", "B"], ["A", "C"], ["B", "D"],
    ]);

    dispose();
    root.remove();
  });

  it("ignores a retained inspector control after the editor is disposed", async () => {
    const root = document.createElement("main");
    root.innerHTML = `
      <textarea data-source></textarea><div data-status></div>
      <button data-direction="TD">TD</button><button data-direction="TB">TB</button>
      <button data-direction="LR">LR</button><button data-direction="BT">BT</button>
      <button data-direction="RL">RL</button>
      <button data-zoom-out></button><button data-zoom-in></button><button data-canvas-reset></button>
      <div data-canvas-viewport><div data-canvas></div></div><aside data-inspector></aside>
      <button data-cancel></button><button data-confirm></button>`;
    document.body.append(root);
    const posted = [];
    let receiveMessage;
    const bridge = {
      postMessage: (message) => posted.push(message),
      addEventListener: (_type, handler) => { receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({
      ok: true,
      reason: "",
      svg: '<svg id="diagram-test"><g class="node" id="diagram-test-flowchart-A-0"><text>Read</text></g></svg>',
    }));
    const original = "flowchart LR\nA[Read] --> B[Edit]";

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-dispose", source: original, language: "mermaid" },
    } });
    await vi.waitFor(() => expect(root.querySelector("g.node")).not.toBeNull());
    root.querySelector("g.node").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    const retainedLabel = root.querySelector("[data-node-label]");
    const source = root.querySelector("[data-source]");
    const postCount = posted.length;
    const renderCount = render.mock.calls.length;

    dispose();
    retainedLabel.value = "After dispose";
    retainedLabel.dispatchEvent(new Event("change", { bubbles: true }));
    await Promise.resolve();

    expect(source.value).toBe(original);
    expect(posted).toHaveLength(postCount);
    expect(render).toHaveBeenCalledTimes(renderCount);
    root.remove();
  });
});
