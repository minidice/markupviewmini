import { analyzeMermaidSource } from "@markup-view-mini/mermaid-safe/analyzer";
import { serializeFlowchart } from "@markup-view-mini/mermaid-safe/serializer";
import { describeEdge, findEdgeByDescription, setDirection } from "@markup-view-mini/mermaid-safe/model";
import { bindCanvasInteractions, bindCanvasPan } from "./canvas-interactions.js";
import {
  addEdgeHitAreas,
  createCanvasView,
  findNodeElement,
  findSubgraphElement,
  mapEdgeElements,
} from "./canvas-view.js";
import { renderDiagram } from "./diagram-renderer.js";
import { setLanguage, t } from "../../shared/i18n/index.js";
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
    node.setAttribute("aria-label", t("mermaid.editor.selectNode", node.textContent?.trim() || node.id));
  }
}

/**
 * Applies the catalogue to the static markup.
 *
 * The page ships with no user-visible text of its own - only `data-i18n*` keys - so there is
 * never a moment where a hard-coded language is on screen. Re-running this is how a locale
 * message relabels controls that are already rendered.
 */
export function applyTranslations(root) {
  for (const element of root.querySelectorAll("[data-i18n]")) {
    element.textContent = t(element.dataset.i18n);
  }
  for (const element of root.querySelectorAll("[data-i18n-title]")) {
    element.title = t(element.dataset.i18nTitle);
  }
  for (const element of root.querySelectorAll("[data-i18n-aria-label]")) {
    element.setAttribute("aria-label", t(element.dataset.i18nAriaLabel));
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
  // 그린 선과 모델 엣지의 짝. 하나라도 짝지어지지 않으면 null이 되고, 그러면
  // 선은 아예 고를 수 없다 - 엉뚱한 선을 고치는 것보다 낫다.
  let edgeMap = null;

  /*
   * 파싱한 그래프를 소스 기준으로 캐시한다. 단순히 편의를 위한 것이 아니다 -
   * 엣지 id(`edge-N`)는 파싱할 때마다 새로 매겨지므로(graph-model.js 참고),
   * 부를 때마다 다시 파싱하면 방금 고른 선의 id가 그 자리에서 낡아 버린다.
   * 그래프가 바뀌는 지점을 여기 하나로 모으고, 바로 그 자리에서 선택을 되붙인다.
   */
  let graph = null;
  let graphSource = null;
  const syncGraph = () => {
    if (graphSource === session.source) return graph;
    const current = analyze(session.source);
    graph = current.supported ? current.model : null;
    graphSource = session.source;
    reattachSelection();
    return graph;
  };

  const selectedElements = (model) => {
    const selection = state.selection;
    if (selection === null || model === null) return [];
    if (selection.kind === "node") return [findNodeElement(canvas, selection.id)];
    if (selection.kind === "edge") return [edgeMap?.get(selection.id) ?? null];
    return [findSubgraphElement(canvas, selection.id)];
  };

  const paintSelection = (model) => {
    for (const element of canvas.querySelectorAll("[data-selected]")) delete element.dataset.selected;
    for (const target of selectedElements(model)) {
      if (target != null) target.dataset.selected = "true";
    }
  };

  const commit = (change, focusSelector = null) => {
    if (!active) return false;
    const model = syncGraph();
    if (model === null || !change(model)) return false;
    const next = serialize(model);
    if (!analyze(next).supported) {
      status.textContent = t("mermaid.editor.revertedUnsafe");
      return false;
    }
    source.value = next;
    // 소스가 바뀌었으므로 다음 syncGraph()가 다시 파싱하고 선택을 되붙인다.
    session.change(next);
    setControls(true);
    updateInspector(focusSelector);
    render();
    return true;
  };

  const updateInspector = (focus = null) => {
    renderInspector(inspector, { graph: syncGraph(), selection: state.selection, commit });
    if (focus !== null) root.querySelector(focus)?.focus();
  };

  const directionControls = [...root.querySelectorAll("[data-direction]")];
  const updateDirections = () => {
    const model = syncGraph();
    for (const control of directionControls) {
      control.disabled = !open || model === null;
      control.setAttribute("aria-pressed", String(
        model !== null && model.direction === control.dataset.direction,
      ));
    }
  };

  /**
   * 엣지 id는 소스를 다시 파싱할 때마다 새로 매겨진다. 고르고 있던 선을 놓치지
   * 않도록 함께 들고 있던 이름표로 되찾는다. 못 찾으면 선택을 푼다.
   */
  const reattachSelection = () => {
    const selection = state.selection;
    if (selection?.kind !== "edge") return;
    if (graph === null) {
      state.select(null);
      return;
    }
    if (graph.edges.some((edge) => edge.id === selection.id)) return;

    const found = findEdgeByDescription(graph, selection.description);
    state.select(found === null ? null : { ...selection, id: found.id });
  };

  const syncCanvasMapping = () => {
    const model = syncGraph();
    edgeMap = model === null ? null : mapEdgeElements(canvas, model);
    if (edgeMap !== null) addEdgeHitAreas(canvas, [...edgeMap.values()]);
    paintSelection(model);
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
        syncCanvasMapping();
      } else {
        status.textContent = t("mermaid.editor.previewUnavailable");
        canvas.replaceChildren();
        edgeMap = null;
      }
    },
  });
  const render = () => {
    const model = syncGraph();
    return coordinator.render(model === null ? session.source : sourceForRendering(model));
  };

  const select = (next) => {
    const model = syncGraph();
    // 엣지는 다시 찾을 이름표를 함께 들고 있는다 (reattachSelection 참고).
    state.select(next?.kind === "edge" && model !== null
      ? { ...next, description: describeEdge(model, next.id) }
      : next);
    paintSelection(model);
    updateInspector();
  };

  const unbindPan = bindCanvasPan(viewport, canvasView);
  const unbindZoom = bindZoomControls(root, canvasView);
  const unbindCanvas = bindCanvasInteractions({
    viewport,
    surface: canvas,
    overlay: root.querySelector("[data-canvas-overlay]"),
    isEnabled: () => syncGraph() !== null,
    getGraph: syncGraph,
    getEdgeMap: () => edgeMap,
    getSelection: () => state.selection,
    commit,
    select,
  });
  const setControls = (enabled) => {
    source.disabled = !enabled;
    confirm.disabled = !enabled || !confirmableSource(session.source);
    cancel.disabled = !enabled;
    updateDirections();
  };
  // The markup ships keys, not text, so this is what puts any words on screen at all.
  applyTranslations(root);
  setControls(false);
  const receiveLocale = (payload) => {
    setLanguage(payload?.language);
    applyTranslations(root);
    // Re-label whatever is already on screen; nothing else about the diagram changed.
    updateInspector();
    updateDirections();
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
  const directionClick = (event) => commit(
    (model) => setDirection(model, event.currentTarget.dataset.direction),
    `[data-direction="${event.currentTarget.dataset.direction}"]`,
  );
  const message = (event) => {
    const message = event.data;
    if (message?.version !== BRIDGE_VERSION) return;
    if (message.type === "mermaid.open") receiveOpen(message.payload);
    // The host names the UI language separately from the editing session - see PostLocale in
    // MermaidEditDialog for why it is not part of mermaid.open.
    else if (message.type === "mermaid.locale") receiveLocale(message.payload);
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
