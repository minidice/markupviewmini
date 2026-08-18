import { describe, expect, it, vi, beforeAll } from "vitest";
import { readFileSync } from "node:fs";
import { analyzeMermaidSource } from "@markup-view-mini/mermaid-safe/analyzer";
import { bindZoomControls, createBlockSession, createEditorState, createRenderCoordinator } from "./editor-app.js";
import * as EditorApp from "./editor-app.js";
import { bindCanvasInteractions, bindCanvasPan } from "./canvas-interactions.js";
import { addEdgeHitAreas, createCanvasView, mapEdgeElements } from "./canvas-view.js";
import { renderInspector } from "./inspector.js";
import { PALETTE } from "./palette.js";
import { CLASSIC_SHAPES, PALETTE_SHAPE_IDS } from "./node-shapes.js";
import { clampZoom, stepZoom } from "./zoom-control.js";
import { setLanguage } from "../../shared/i18n/index.js";

// These assertions name the Korean strings directly, so the language has to be pinned:
// the editor starts on English until the host sends its locale message.
beforeAll(() => setLanguage("ko"));

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
    surface.innerHTML = '<svg id="d"><g class="node" id="d-flowchart-A-0"></g></svg>';
    const selected = [];
    const unbind = bindCanvasInteractions({ surface, isEnabled: () => true, select: (target) => selected.push(target) });

    surface.querySelector("g").dispatchEvent(new MouseEvent("click", { bubbles: true }));

    expect(selected).toEqual([{ kind: "node", id: "A" }]);
    unbind();
  });

  it("refuses to resolve a node whose DOM id does not carry the rendered svg's prefix", () => {
    // Break caught: a lenient resolver falls back to the raw DOM id, so a stale or
    // foreign <g class="node"> resolves to a model id that means something else - and the
    // next edit silently rewrites the wrong node. Failing closed loses a click; guessing
    // loses the user's diagram.
    const surface = document.createElement("div");
    surface.innerHTML = '<svg id="d"><g class="node" id="A"></g></svg>';
    const selected = [];
    const unbind = bindCanvasInteractions({ surface, isEnabled: () => true, select: (target) => selected.push(target) });

    surface.querySelector("g").dispatchEvent(new MouseEvent("click", { bubbles: true }));

    expect(selected).toEqual([null]);
    unbind();
  });

  it("selects the edge behind its widened hit area", () => {
    // Break caught: the drawn link is ~1px, so without the transparent hit clone the edge is
    // effectively unclickable. The clone carries no id, so resolution has to hop to the real
    // path next to it.
    const surface = document.createElement("div");
    surface.innerHTML =
      '<svg id="d"><g class="edgePaths"><path id="d-L_A_B_0"></path></g></svg>';
    const realPath = surface.querySelector("path");
    addEdgeHitAreas(surface, [realPath]);
    const hit = surface.querySelector('[data-hit-for]');
    const selected = [];
    const unbind = bindCanvasInteractions({
      surface,
      isEnabled: () => true,
      getEdgeMap: () => new Map([["e1", realPath]]),
      select: (target) => selected.push(target),
    });

    expect(hit).not.toBeNull();
    hit.dispatchEvent(new MouseEvent("click", { bubbles: true }));

    expect(selected).toEqual([{ kind: "edge", id: "e1" }]);
    unbind();
  });

  it("maps drawn links to model edges by id rather than document order", () => {
    // Break caught: mermaid does not emit link paths in declaration order, so pairing them
    // positionally selects a different edge than the one clicked.
    const surface = document.createElement("div");
    surface.innerHTML = '<svg id="d"><g class="edgePaths">'
      + '<path id="d-L_B_C_1"></path><path id="d-L_A_B_0"></path></g></svg>';
    const graph = {
      nodes: [{ id: "A" }, { id: "B" }, { id: "C" }],
      edges: [{ id: "e1", from: "A", to: "B" }, { id: "e2", from: "B", to: "C" }],
    };

    const mapped = mapEdgeElements(surface, graph);

    expect(mapped.get("e1").getAttribute("id")).toBe("d-L_A_B_0");
    expect(mapped.get("e2").getAttribute("id")).toBe("d-L_B_C_1");
  });

  it("refuses to map edges when the drawing and the model disagree", () => {
    const surface = document.createElement("div");
    surface.innerHTML = '<svg id="d"><g class="edgePaths"><path id="d-L_A_B_0"></path></g></svg>';
    const graph = {
      nodes: [{ id: "A" }, { id: "B" }],
      edges: [{ id: "e1", from: "A", to: "B" }, { id: "e2", from: "B", to: "A" }],
    };

    expect(mapEdgeElements(surface, graph)).toBeNull();
  });

  describe("direct canvas manipulation", () => {
    const mountSurface = (extra = {}) => {
      const viewport = document.createElement("div");
      const surface = document.createElement("div");
      const overlay = document.createElement("div");
      viewport.append(surface, overlay);
      document.body.append(viewport);
      surface.innerHTML = '<svg id="d"><g class="node" id="d-flowchart-A-0"></g></svg>';
      const changes = [];
      const selected = [];
      const unbind = bindCanvasInteractions({
        viewport,
        surface,
        overlay,
        isEnabled: () => true,
        getGraph: () => analyzeMermaidSource("flowchart LR\nA[Hi]").model,
        commit: (change) => { changes.push(change); return true; },
        select: (target) => selected.push(target),
        ...extra,
      });
      return { viewport, surface, overlay, changes, selected, unbind };
    };

    it("adds a connected node from the + handles around a hovered node", () => {
      const { surface, overlay, changes, unbind } = mountSurface();

      surface.querySelector("g.node").dispatchEvent(new MouseEvent("pointerover", { bubbles: true }));
      const adders = [...overlay.querySelectorAll("[data-add-node]")];
      expect(adders).toHaveLength(4);
      expect(adders.every((button) => button.dataset.addNode === "A")).toBe(true);

      adders[0].click();

      // The handle must produce a real graph change, not just a click that looks alive.
      expect(changes).toHaveLength(1);
      const graph = analyzeMermaidSource("flowchart LR\nA[Hi]").model;
      expect(changes[0](graph)).toBe(true);
      expect(graph.nodes).toHaveLength(2);
      expect(graph.edges).toHaveLength(1);
      expect(graph.edges[0].from).toBe("A");
      unbind();
    });

    it("clears the handles once the pointer leaves the canvas", () => {
      const { viewport, surface, overlay, unbind } = mountSurface();
      surface.querySelector("g.node").dispatchEvent(new MouseEvent("pointerover", { bubbles: true }));
      expect(overlay.childElementCount).toBe(4);

      viewport.dispatchEvent(new MouseEvent("pointerleave", { bubbles: false }));

      expect(overlay.childElementCount).toBe(0);
      unbind();
    });

    it("renames a node through the double-click label editor", () => {
      const { surface, overlay, changes, unbind } = mountSurface();

      surface.querySelector("g.node").dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
      const editor = overlay.querySelector("[data-label-editor]");
      expect(editor.value).toBe("Hi");

      editor.value = "Bye";
      editor.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));

      expect(changes).toHaveLength(1);
      const graph = analyzeMermaidSource("flowchart LR\nA[Hi]").model;
      expect(changes[0](graph)).toBe(true);
      expect(graph.nodes[0].label).toBe("Bye");
      expect(overlay.childElementCount).toBe(0);
      unbind();
    });

    it("abandons the label editor on Escape without touching the graph", () => {
      const { surface, overlay, changes, unbind } = mountSurface();
      surface.querySelector("g.node").dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
      const editor = overlay.querySelector("[data-label-editor]");

      editor.value = "Bye";
      editor.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));

      expect(changes).toHaveLength(0);
      expect(overlay.childElementCount).toBe(0);
      unbind();
    });

    it("never deletes the selection while the user is typing in a field", () => {
      // Break caught: the inspector's own label field is a text input on the same page. A
      // Delete keystroke meant for a character used to reach the canvas handler and remove
      // the node the user was in the middle of renaming.
      const field = document.createElement("input");
      document.body.append(field);
      const { changes, unbind } = mountSurface({ getSelection: () => ({ kind: "node", id: "A" }) });

      field.dispatchEvent(new KeyboardEvent("keydown", { key: "Delete", bubbles: true }));
      field.dispatchEvent(new KeyboardEvent("keydown", { key: "Backspace", bubbles: true }));

      expect(changes).toHaveLength(0);
      unbind();
      field.remove();
    });

    it("deletes the current selection on Delete outside a text field", () => {
      const { changes, selected, unbind } = mountSurface({
        getSelection: () => ({ kind: "node", id: "A" }),
      });

      document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Delete", bubbles: true }));

      expect(changes).toHaveLength(1);
      const graph = analyzeMermaidSource("flowchart LR\nA[Hi] --> B").model;
      expect(changes[0](graph)).toBe(true);
      expect(graph.nodes.map((node) => node.id)).toEqual(["B"]);
      expect(selected.at(-1)).toBeNull();
      unbind();
    });

    it("does not connect anything when a node is clicked rather than dragged", () => {
      // Break caught: treating every pointerdown on a node as a connection start turns an
      // ordinary selection click into a stray self-edge.
      const { surface, changes, unbind } = mountSurface();
      const node = surface.querySelector("g.node");

      node.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true, clientX: 5, clientY: 5 }));
      document.dispatchEvent(new MouseEvent("pointerup", { bubbles: true, clientX: 5, clientY: 5 }));

      expect(changes).toHaveLength(0);
      unbind();
    });
  });
});

describe("inspector panels", () => {
  const graphWith = (overrides = {}) => ({
    nodes: [{ id: "A", label: "A", shape: "rect", color: null }],
    edges: [{ id: "e1", from: "A", to: "B", label: "", line: "solid", arrow: "arrow", arrowHead: "none", color: null }],
    subgraphs: [],
    ...overrides,
  });
  const nodeGraph = (nodeOverrides = {}) => graphWith({
    nodes: [{ id: "A", label: "A", shape: "rect", color: null, ...nodeOverrides }],
  });
  const showNode = (inspector, nodeOverrides = {}, commit = () => true) =>
    renderInspector(inspector, { graph: nodeGraph(nodeOverrides), selection: { kind: "node", id: "A" }, commit });

  it("offers a picker for exactly the shapes and colours the model round-trips", () => {
    // This used to assert the *unselected* panel printed "Shapes: rect, round, ..." - a text
    // dump that only existed because there was no real picker. What matters is that every
    // shape and colour the model supports is reachable, so assert on the controls instead.
    const inspector = document.createElement("aside");
    showNode(inspector);

    expect(CLASSIC_SHAPES.length).toBeGreaterThan(0);
    expect(PALETTE.length).toBeGreaterThan(1);
    expect([...inspector.querySelectorAll("[data-node-shape]")]
      .map((control) => control.dataset.nodeShape).sort())
      .toEqual(CLASSIC_SHAPES.map((shape) => shape.id).sort());
    expect([...inspector.querySelectorAll("[data-node-color]")]
      .map((control) => control.dataset.nodeColor))
      .toEqual(PALETTE.map((colour) => colour.id));
  });

  it("keeps the shape palette order list in step with the modeled shapes", () => {
    // Break caught: the picker order is a separate list from the parser's precedence order, so
    // the two drift - offering a shape the serializer can't emit, or silently dropping one the
    // model supports. (The reference editor this was ported from has exactly this drift.)
    expect([...PALETTE_SHAPE_IDS].sort()).toEqual(CLASSIC_SHAPES.map((shape) => shape.id).sort());
    // Common flowchart shapes first - the parser order buries rect/diamond behind doublecircle.
    expect(PALETTE_SHAPE_IDS.slice(0, 4)).toEqual(["rect", "round", "stadium", "diamond"]);
  });

  it("picks shapes by drawing them and colours by showing them", () => {
    // Break caught: rendering the raw ids ("stadium", "doublecircle") as button text is useless
    // to someone who doesn't know Mermaid syntax - which is exactly who this editor is for.
    const inspector = document.createElement("aside");
    showNode(inspector);

    const stadium = inspector.querySelector('[data-node-shape="stadium"]');
    expect(stadium.querySelector("svg")).not.toBeNull();
    expect(stadium.title).toBe("알약");
    expect(stadium.getAttribute("aria-label")).toBe("알약");

    const blue = inspector.querySelector('[data-node-color="blue"]');
    expect(blue.style.background).not.toBe("");
    expect(blue.title).toBe("파랑");

    expect(inspector.textContent).not.toContain("Colour");
    expect(inspector.textContent).not.toContain("stadium");
  });

  it("marks the node's current shape and colour as pressed", () => {
    const inspector = document.createElement("aside");
    showNode(inspector, { shape: "diamond", color: "red" });

    expect(inspector.querySelector('[data-node-shape="diamond"]').getAttribute("aria-pressed")).toBe("true");
    expect(inspector.querySelector('[data-node-shape="rect"]').getAttribute("aria-pressed")).toBe("false");
    expect(inspector.querySelector('[data-node-color="red"]').getAttribute("aria-pressed")).toBe("true");
  });

  it("shows an uncoloured node as the palette's explicit default", () => {
    // "default" is not a colour - it means emit no style line at all. It still has to read as
    // the current choice, otherwise nothing looks selected until the user picks a colour.
    const inspector = document.createElement("aside");
    showNode(inspector, { color: null });

    const fallback = inspector.querySelector('[data-node-color="default"]');
    expect(fallback.getAttribute("aria-pressed")).toBe("true");
    expect(fallback.classList.contains("swatch-default")).toBe(true);
    expect(fallback.style.background).toBe("");
  });

  it("edits every edge property the model carries", () => {
    // Break caught: the editor had no edge panel at all - line style, arrow ends, direction
    // and colour were reachable only by hand-editing Mermaid syntax.
    const inspector = document.createElement("aside");
    const changes = [];
    renderInspector(inspector, {
      graph: graphWith(),
      selection: { kind: "edge", id: "e1" },
      commit: (change) => { changes.push(change); return true; },
    });

    expect(inspector.querySelector("[data-edge-label]")).not.toBeNull();
    expect([...inspector.querySelectorAll("[data-edge-line]")].map((c) => c.dataset.edgeLine))
      .toEqual(["solid", "dotted", "thick", "invisible"]);
    expect([...inspector.querySelectorAll("[data-edge-arrow]")].map((c) => c.dataset.edgeArrow))
      .toEqual(["arrow", "none", "circle", "cross"]);
    expect([...inspector.querySelectorAll("[data-edge-direction]")].map((c) => c.dataset.edgeDirection))
      .toEqual(["none", "arrow"]);
    expect([...inspector.querySelectorAll("[data-edge-color]")]).toHaveLength(PALETTE.length);
    // The edge's own current values, not the node defaults.
    expect(inspector.querySelector('[data-edge-line="solid"]').getAttribute("aria-pressed")).toBe("true");
    expect(inspector.querySelector('[data-edge-direction="none"]').getAttribute("aria-pressed")).toBe("true");

    inspector.querySelector('[data-edge-line="dotted"]').click();
    expect(changes).toHaveLength(1);
  });

  it("groups a node and moves it between existing groups", () => {
    const inspector = document.createElement("aside");
    const changes = [];
    renderInspector(inspector, {
      graph: graphWith({ subgraphs: [{ id: "g1", title: "1단계", direction: null, children: [] }] }),
      selection: { kind: "node", id: "A" },
      commit: (change) => { changes.push(change); return true; },
    });

    const membership = inspector.querySelector("[data-node-subgraph]");
    expect([...membership.options].map((option) => option.textContent)).toEqual(["묶음 없음", "1단계"]);

    inspector.querySelector("[data-group-node]").click();
    expect(changes).toHaveLength(1);
  });

  it("edits a selected group's title and inner direction", () => {
    const inspector = document.createElement("aside");
    renderInspector(inspector, {
      graph: graphWith({ subgraphs: [{ id: "g1", title: "1단계", direction: "LR", children: ["A"] }] }),
      selection: { kind: "subgraph", id: "g1" },
      commit: () => true,
    });

    expect(inspector.querySelector("[data-subgraph-title]").value).toBe("1단계");
    expect(inspector.querySelector("[data-subgraph-direction]").value).toBe("LR");
    expect(inspector.querySelector("[data-delete]").textContent).toBe("묶음만 해제");
  });

  it("falls back to the empty hint when the selected target no longer exists", () => {
    // Break caught: deleting a node leaves the stale selection behind, and the panel throws
    // instead of returning to its resting state.
    const inspector = document.createElement("aside");
    renderInspector(inspector, {
      graph: graphWith(),
      selection: { kind: "node", id: "gone" },
      commit: () => true,
    });

    expect(inspector.querySelector(".inspector-hint")).not.toBeNull();
    expect(inspector.querySelector("[data-node-shape]")).toBeNull();
  });

  it("tells the user where to edit when the diagram is not visually editable", () => {
    const inspector = document.createElement("aside");
    renderInspector(inspector, { graph: null, selection: null, commit: () => true });

    expect(inspector.textContent).toContain("소스 칸");
    expect(inspector.querySelector("[data-node-shape]")).toBeNull();
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

  it("keeps an edge selected across the reparse that every edit triggers", async () => {
    // Break caught: edge ids (`edge-N`) are handed out by a module-level counter, so EVERY
    // parse renames every edge. Re-parsing per call - even just to read the graph - made the
    // id in hand stale the moment it was taken: the edge highlighted on the canvas, but the
    // panel could not find it and fell back to the empty hint. The graph has to be parsed
    // once per source revision, with the selection re-attached by description at that seam.
    const root = document.createElement("main");
    root.innerHTML = `
      <textarea data-source></textarea><div data-status></div>
      <button data-direction="LR">LR</button>
      <button data-zoom-out></button><button data-zoom-in></button><button data-canvas-reset></button>
      <div data-canvas-viewport><div data-canvas></div></div><aside data-inspector></aside>
      <button data-cancel></button><button data-confirm></button>`;
    document.body.append(root);
    let receiveMessage;
    const bridge = {
      postMessage: vi.fn(),
      addEventListener: (type, handler) => { if (type === "message") receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({
      ok: true,
      reason: "",
      svg: '<svg id="d">'
        + '<g class="node" id="d-flowchart-A-0"></g><g class="node" id="d-flowchart-B-1"></g>'
        + '<g class="edgePaths"><path id="d-L_A_B_0"></path></g></svg>',
    }));

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-edge", source: "flowchart LR\nA --> B", language: "mermaid" },
    } });
    await vi.waitFor(() => expect(root.querySelector("[data-hit-for]")).not.toBeNull());

    root.querySelector("[data-hit-for]").dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(root.querySelector("[data-edge-line]")).not.toBeNull();

    root.querySelector('[data-edge-line="dotted"]').click();

    await vi.waitFor(() => expect(root.querySelector("[data-source]").value).toContain("-.->"));
    // Still the same edge after the rewrite - not knocked back to the empty hint.
    expect(root.querySelector("[data-edge-line]")).not.toBeNull();
    expect(root.querySelector('[data-edge-line="dotted"]').getAttribute("aria-pressed")).toBe("true");

    dispose();
    root.remove();
  });

  it("relabels itself when the host names a different language", async () => {
    // Break caught: the locale arriving after the page has rendered leaves every control in
    // whatever language the bundle happened to default to. The host is the only thing that
    // knows the user's choice, so its message has to re-label what is already on screen -
    // including the static markup, which carries keys rather than text.
    const page = new DOMParser().parseFromString(readFileSync("index.html", "utf8"), "text/html");
    const root = page.querySelector("#mermaid-app");
    document.body.append(root);
    let receiveMessage;
    const bridge = {
      postMessage: vi.fn(),
      addEventListener: (type, handler) => { if (type === "message") receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({
      ok: true,
      reason: "",
      svg: '<svg id="d"><g class="node" id="d-flowchart-A-0"></g></svg>',
    }));

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-locale", source: "flowchart LR\nA[Read] --> B", language: "mermaid" },
    } });
    await vi.waitFor(() => expect(root.querySelector("g.node")).not.toBeNull());
    root.querySelector("g.node").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));

    // Markup shipped with keys only, so it is already Korean from the beforeAll above.
    expect(root.querySelector("[data-confirm]").textContent).toBe("적용");
    expect(root.querySelector('[data-node-shape="stadium"]').title).toBe("알약");

    receiveMessage({ data: {
      version: 1,
      type: "mermaid.locale",
      payload: { language: "en" },
    } });

    expect(root.querySelector("[data-confirm]").textContent).toBe("Apply");
    expect(root.querySelector('[data-node-shape="stadium"]').title).toBe("Stadium");
    expect(root.querySelector('[data-direction="LR"]').title).toBe("Left to right (LR)");

    setLanguage("ko");
    dispose();
    root.remove();
  });

  it("keeps a grouped diagram visually editable so grouping is not a one-way door", async () => {
    // Break caught: wrapping nodes in a subgraph writes syntax the analyzer must still accept.
    // If it did not, the first grouping would silently drop the whole diagram into text-only
    // mode - and the user would have no way back short of undoing it by hand.
    const root = document.createElement("main");
    root.innerHTML = `
      <textarea data-source></textarea><div data-status></div>
      <button data-direction="LR">LR</button>
      <button data-zoom-out></button><button data-zoom-in></button><button data-canvas-reset></button>
      <div data-canvas-viewport><div data-canvas></div></div><aside data-inspector></aside>
      <button data-cancel></button><button data-confirm></button>`;
    document.body.append(root);
    let receiveMessage;
    const bridge = {
      postMessage: vi.fn(),
      addEventListener: (type, handler) => { if (type === "message") receiveMessage = handler; },
      removeEventListener: vi.fn(),
    };
    const render = vi.fn(async () => ({
      ok: true,
      reason: "",
      svg: '<svg id="d"><g class="node" id="d-flowchart-A-0"></g></svg>',
    }));

    const dispose = EditorApp.mountMermaidEditor(root, { bridge, render });
    receiveMessage({ data: {
      version: 1,
      type: "mermaid.open",
      payload: { sessionId: "s-group", source: "flowchart LR\nA[Read] --> B[Edit]", language: "mermaid" },
    } });
    await vi.waitFor(() => expect(root.querySelector("g.node")).not.toBeNull());
    root.querySelector("g.node").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));

    root.querySelector("[data-group-node]").click();

    await vi.waitFor(() => expect(root.querySelector("[data-source]").value).toContain("subgraph"));
    const grouped = root.querySelector("[data-source]").value;
    expect(grouped).toContain("end");
    expect(analyzeMermaidSource(grouped)).toMatchObject({ supported: true });
    // Still the full visual panel, not the text-only fallback hint.
    expect(root.querySelector("[data-node-shape]")).not.toBeNull();

    dispose();
    root.remove();
  });
});
