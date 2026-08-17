import { analyzeMermaidSource } from "@markup-view-mini/mermaid-safe/analyzer";
import { serializeFlowchart } from "@markup-view-mini/mermaid-safe/serializer";
import { setDirection, setNodeColor, setNodeLabel, setNodeShape } from "@markup-view-mini/mermaid-safe/model";
import { bindCanvasInteractions, bindCanvasPan } from "./canvas-interactions.js";
import { createCanvasView } from "./canvas-view.js";
import { renderDiagram } from "./diagram-renderer.js";
import { renderInspector } from "./inspector.js";
import { stepZoom } from "./zoom-control.js";

const BRIDGE_VERSION = 1;

function validOpenPayload(payload) {
  return payload != null && typeof payload.sessionId === "string" &&
    typeof payload.source === "string" && typeof payload.language === "string" &&
    Object.keys(payload).length === 3;
}

// The bridge's "supported" flag means "the host may accept a mermaid.confirm for this
// source", not "the visual canvas understands this source". Diagrams the strict flowchart
// parser rejects still confirm as plain text (see confirmableSource below) so the "시각 편집"
// action is never a dead end - only a genuinely empty source blocks confirm.
function confirmableSource(source) {
  return source.trim() !== "";
}

export function createBlockSession(bridge) {
  const post = (type, payload) => bridge.postMessage({ version: BRIDGE_VERSION, type, payload });
  const reportValidity = (session) => {
    const confirmable = confirmableSource(session.source);
    post("mermaid.validityChanged", {
      sessionId: session.sessionId,
      source: session.source,
      language: session.language,
      sourceVersion: session.sourceVersion,
      supported: confirmable,
      reason: confirmable ? "" : "empty",
    });
    return confirmable;
  };
  const session = {
    sessionId: null,
    source: "",
    language: "mermaid",
    sourceVersion: 0,
    get isOpen() { return this.sessionId !== null; },
    open(payload) {
      if (!validOpenPayload(payload)) throw new TypeError("mermaid.open requires { sessionId, source, language }");
      this.sessionId = payload.sessionId;
      this.source = payload.source;
      this.language = payload.language;
      this.sourceVersion = 0;
      reportValidity(this);
    },
    change(source) {
      if (!this.isOpen) return false;
      this.source = String(source);
      this.sourceVersion += 1;
      post("mermaid.changed", {
        sessionId: this.sessionId,
        source: this.source,
        language: this.language,
        sourceVersion: this.sourceVersion,
      });
      reportValidity(this);
      return true;
    },
    confirm() {
      if (!this.isOpen || !confirmableSource(this.source)) return false;
      post("mermaid.confirm", {
        sessionId: this.sessionId,
        source: this.source,
        language: this.language,
        sourceVersion: this.sourceVersion,
      });
      return true;
    },
    cancel() {
      if (!this.isOpen) return false;
      post("mermaid.cancel", { sessionId: this.sessionId });
      return true;
    },
  };
  post("mermaid.ready", {});
  return session;
}

export function createRenderCoordinator({ analyze = analyzeMermaidSource, render = renderDiagram, apply }) {
  let generation = 0;
  return {
    async render(source) {
      const current = ++generation;
      const analysis = analyze(source);
      if (!analysis.supported) {
        apply({ ok: false, reason: analysis.reason, svg: "" });
        return false;
      }
      const diagram = await render(source);
      if (current !== generation) return false;
      apply(diagram);
      return diagram.ok;
    },
  };
}

export function createEditorState() {
  let selection = null;
  return {
    get selection() { return selection; },
    select(next) { selection = next; },
    resetForOpen() { selection = null; },
    resetForSource() { selection = null; },
  };
}

export function bindZoomControls(root, canvas) {
  const actions = [
    ["[data-zoom-in]", () => canvas.set(stepZoom(canvas.zoom, 1))],
    ["[data-zoom-out]", () => canvas.set(stepZoom(canvas.zoom, -1))],
    ["[data-canvas-reset]", () => canvas.reset()],
  ];
  for (const [selector, handler] of actions) root.querySelector(selector).addEventListener("click", handler);
  return () => {
    for (const [selector, handler] of actions) root.querySelector(selector).removeEventListener("click", handler);
  };
}

function createWebViewBridge(webview = globalThis.chrome?.webview) {
  return {
    postMessage: (message) => webview?.postMessage(message),
    addEventListener: (type, handler) => webview?.addEventListener(type, handler),
    removeEventListener: (type, handler) => webview?.removeEventListener(type, handler),
  };
}

function sourceForRendering(model) {
  return serializeFlowchart({
    ...model,
    syntax: null,
    format: { containerPrefix: "", newline: "\n", trailingNewline: false },
  });
}

function makeRenderedNodesKeyboardAccessible(canvas) {
  for (const node of canvas.querySelectorAll("svg g.node")) {
    node.tabIndex = 0;
    node.setAttribute("role", "button");
    node.setAttribute("aria-label", `Select node ${node.textContent?.trim() || node.id}`);
  }
}

export function mountMermaidEditor(root, options = {}) {
  const analyze = options.analyze ?? analyzeMermaidSource;
  const serialize = options.serialize ?? serializeFlowchart;
  const renderDiagramSource = options.render ?? renderDiagram;
  const bridge = options.bridge ?? createWebViewBridge();
  const session = createBlockSession(bridge);
  const source = root.querySelector("[data-source]");
  const viewport = root.querySelector("[data-canvas-viewport]");
  const canvas = root.querySelector("[data-canvas]");
  const status = root.querySelector("[data-status]");
  const inspector = root.querySelector("[data-inspector]");
  const confirm = root.querySelector("[data-confirm]");
  const cancel = root.querySelector("[data-cancel]");
  const canvasView = createCanvasView(viewport, canvas);
  const state = createEditorState();
  let active = true;
  let open = false;
  const analysis = () => analyze(session.source);
  const directionControls = [...root.querySelectorAll("[data-direction]")];
  const updateDirections = () => {
    const current = analysis();
    for (const control of directionControls) {
      control.disabled = !open || !current.supported;
      control.setAttribute("aria-pressed", String(
        current.supported && current.model.direction === control.dataset.direction,
      ));
    }
  };
  const inspectorHandlers = {
    onLabel: (value) => mutate("label", (model) => setNodeLabel(model, state.selection, value)),
    onShape: (value) => mutate("shape", (model) => setNodeShape(model, state.selection, value)),
    onColor: (value) => mutate("color", (model) => setNodeColor(model, state.selection, value)),
  };
  const updateInspector = (focus = null) => {
    renderInspector(inspector, analysis(), state.selection, inspectorHandlers);
    if (focus !== null) root.querySelector(focus)?.focus();
  };
  const coordinator = createRenderCoordinator({
    analyze,
    render: renderDiagramSource,
    apply: (diagram) => {
      if (!active) return;
      if (diagram.ok) {
        status.textContent = "";
        canvas.innerHTML = diagram.svg;
        makeRenderedNodesKeyboardAccessible(canvas);
      } else {
        status.textContent = "이 다이어그램은 미리보기를 표시할 수 없습니다. 텍스트로만 편집할 수 있습니다.";
        canvas.replaceChildren();
      }
    },
  });
  const render = () => {
    const current = analysis();
    return coordinator.render(current.supported ? sourceForRendering(current.model) : session.source);
  };
  const unbindPan = bindCanvasPan(viewport, canvasView);
  const unbindZoom = bindZoomControls(root, canvasView);
  const unbindCanvas = bindCanvasInteractions({
    surface: canvas,
    isEnabled: () => analysis().supported,
    select: (next) => { state.select(next); updateInspector(); },
  });
  const setControls = (enabled) => {
    source.disabled = !enabled;
    confirm.disabled = !enabled || !confirmableSource(session.source);
    cancel.disabled = !enabled;
    updateDirections();
  };
  setControls(false);
  const mutate = (focusKind, change) => {
    if (!active) return false;
    const current = analysis();
    if (!current.supported || !change(current.model)) return false;
    const next = serialize(current.model);
    if (!analyze(next).supported) {
      status.textContent = "Visual editing is locked: serializer-round-trip-failed";
      return false;
    }
    source.value = next;
    session.change(next);
    setControls(true);
    const focus = focusKind === "label" ? "[data-node-label]"
      : focusKind === "direction" ? `[data-direction=\"${current.model.direction}\"]`
        : `[data-node-${focusKind}=\"${current.model.nodes.find((node) => node.id === state.selection)?.[focusKind] ?? "default"}\"]`;
    updateInspector(focus);
    render();
    return true;
  };
  const receiveOpen = (payload) => {
    session.open(payload);
    open = true;
    state.resetForOpen();
    source.value = session.source;
    setControls(true);
    updateInspector();
    render();
  };
  const sourceInput = () => {
    state.resetForSource();
    session.change(source.value);
    setControls(true);
    updateInspector();
    render();
  };
  const confirmClick = () => session.confirm();
  const cancelClick = () => session.cancel();
  const directionClick = (event) => mutate(
    "direction",
    (model) => setDirection(model, event.currentTarget.dataset.direction),
  );
  const message = (event) => {
    const message = event.data;
    if (message?.version === BRIDGE_VERSION && message.type === "mermaid.open") receiveOpen(message.payload);
  };
  source.addEventListener("input", sourceInput);
  confirm.addEventListener("click", confirmClick);
  cancel.addEventListener("click", cancelClick);
  for (const control of directionControls) control.addEventListener("click", directionClick);
  bridge.addEventListener?.("message", message);
  return () => {
    active = false;
    source.removeEventListener("input", sourceInput);
    confirm.removeEventListener("click", confirmClick);
    cancel.removeEventListener("click", cancelClick);
    for (const control of directionControls) control.removeEventListener("click", directionClick);
    bridge.removeEventListener?.("message", message);
    unbindCanvas();
    unbindZoom();
    unbindPan();
  };
}

const root = typeof document === "undefined" ? null : document.querySelector("#mermaid-app");
if (root) mountMermaidEditor(root);
