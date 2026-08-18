import { t } from "../../shared/i18n/index.js";
import { findNodeElement, resolveNodeId, resolveSubgraphId } from "./canvas-view.js";
import {
  addConnectedNode,
  connectEndpoints,
  removeEdge,
  removeNode,
  removeSubgraph,
  setNodeLabel,
} from "@markup-view-mini/mermaid-safe/model";

const DRAG_THRESHOLD_PX = 4;
const TEXT_ENTRY_TAGS = new Set(["INPUT", "TEXTAREA", "SELECT"]);

/**
 * 글자를 받는 칸인가. Del·Backspace는 이런 칸의 것이지 캔버스의 것이 아니다.
 *
 * 어느 칸이 어디 붙어 있는지로 따지지 않는다 - 칸이 늘 때마다 목록에 적어 넣는
 * 방식이면 빠뜨린 칸에서 이름을 고치려고 지운 순간 노드가 통째로 사라진다.
 */
function isTextEntry(target) {
  if (target == null) return false;
  if (TEXT_ENTRY_TAGS.has(target.tagName)) return true;
  return target.isContentEditable === true || target.getAttribute?.("contenteditable") === "true";
}

/**
 * 빈 영역 드래그로 화면을 옮긴다. 노드 위 드래그는 연결이므로 건드리지 않는다.
 *
 * 누르자마자 setPointerCapture를 걸면 안 된다. 포인터가 잡히면 뒤따르는 click이
 * 눌린 요소가 아니라 캡처한 요소로 가 버려서, 선을 클릭해도 선택되지 않는다.
 * 실제로 움직이기 시작한 뒤에야 잡는다 - 클릭은 클릭대로 통과시키고, 드래그 중
 * 포인터가 캔버스를 벗어나도 놓치지 않기 위해서다.
 */
export function bindCanvasPan(viewport, canvas) {
  const onPointerDown = (event) => {
    if (event.target?.closest?.("g.node") != null) return;

    const startX = event.clientX;
    const startY = event.clientY;
    let lastX = startX;
    let lastY = startY;
    let panning = false;

    const onMove = (moveEvent) => {
      if (!panning) {
        if (Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) < DRAG_THRESHOLD_PX) return;
        panning = true;
        viewport.setPointerCapture?.(moveEvent.pointerId);
      }
      canvas.panBy(moveEvent.clientX - lastX, moveEvent.clientY - lastY);
      lastX = moveEvent.clientX;
      lastY = moveEvent.clientY;
    };

    const onUp = (upEvent) => {
      viewport.removeEventListener("pointermove", onMove);
      viewport.removeEventListener("pointerup", onUp);
      if (panning) viewport.releasePointerCapture?.(upEvent.pointerId);
    };

    viewport.addEventListener("pointermove", onMove);
    viewport.addEventListener("pointerup", onUp);
  };

  viewport.addEventListener("pointerdown", onPointerDown);
  return () => viewport.removeEventListener("pointerdown", onPointerDown);
}

function toOverlayPoint(viewport, clientX, clientY) {
  const bounds = viewport.getBoundingClientRect();
  return { x: clientX - bounds.left, y: clientY - bounds.top };
}

function nodeRectInOverlay(viewport, element) {
  const bounds = viewport.getBoundingClientRect();
  const box = element.getBoundingClientRect();
  return {
    left: box.left - bounds.left,
    top: box.top - bounds.top,
    width: box.width,
    height: box.height,
  };
}

/**
 * 캔버스 위의 직접 조작을 붙인다. 이 파일은 DOM 이벤트를 받아 commit에 넘길
 * 변형 함수를 만드는 일만 한다 - 소스 텍스트를 직접 건드리지 않는다.
 *
 * overlay/commit 등이 없으면 그 기능만 조용히 빠진다(선택만 붙는다).
 */
export function bindCanvasInteractions({
  viewport,
  surface,
  overlay,
  isEnabled,
  getGraph,
  getEdgeMap,
  getSelection,
  commit,
  select,
}) {
  const clearOverlay = () => overlay?.replaceChildren();

  /*
   * 선을 클릭하면 판정용 투명 복제가 잡힌다(canvas-view.js의 addEdgeHitAreas).
   * 복제는 진짜 path 바로 앞에 꽂혀 있고 id가 없으므로, 복제를 잡았으면
   * 다음 형제가 진짜 path다. 거기서부터 모델 엣지 id를 되찾는다.
   */
  const resolveEdgeId = (target) => {
    const clicked = target?.closest?.("path");
    if (clicked == null) return null;
    const path = clicked.dataset?.hitFor === "true" ? clicked.nextElementSibling : clicked;
    const edgeMap = getEdgeMap?.();
    if (edgeMap == null) return null;
    for (const [edgeId, candidate] of edgeMap) {
      if (candidate === path) return edgeId;
    }
    return null;
  };

  // 노드가 묶음 안에 들어 있으므로 노드를 먼저 본다.
  const resolveTarget = (target) => {
    const nodeId = resolveNodeId(target);
    if (nodeId !== null) return { kind: "node", id: nodeId };
    const edgeId = resolveEdgeId(target);
    if (edgeId !== null) return { kind: "edge", id: edgeId };
    const subgraphId = resolveSubgraphId(target);
    if (subgraphId !== null) return { kind: "subgraph", id: subgraphId };
    return null;
  };

  // 드래그 연결의 끝점은 노드일 수도 묶음일 수도 있다. mermaid가 묶음 이름을
  // 노드처럼 쓰므로 묶음 테두리에서 끌면 단계 전체를 이을 수 있다.
  const resolveEndpoint = (element) => resolveNodeId(element) ?? resolveSubgraphId(element);

  const click = (event) => {
    if (!isEnabled()) return;
    // 빈 곳을 누르면 null - 선택을 푼다.
    select(resolveTarget(event.target));
  };

  const keydown = (event) => {
    if (!isEnabled() || (event.key !== "Enter" && event.key !== " ")) return;
    const target = resolveTarget(event.target);
    if (target === null) return;
    event.preventDefault();
    select(target);
  };

  // 1. 노드 위 `+` 버튼 - 사방에 띄우고, 누르면 연결된 새 노드를 만든다.
  const showAddButtons = (nodeId) => {
    if (overlay == null || commit == null) return;
    const element = findNodeElement(surface, nodeId);
    if (element === null) return;

    clearOverlay();
    const rect = nodeRectInOverlay(viewport, element);
    const centreX = rect.left + rect.width / 2;
    const centreY = rect.top + rect.height / 2;
    const gap = 14;
    const places = [
      { x: centreX, y: rect.top - gap },
      { x: centreX, y: rect.top + rect.height + gap },
      { x: rect.left - gap, y: centreY },
      { x: rect.left + rect.width + gap, y: centreY },
    ];

    for (const place of places) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "add-node-button";
      button.dataset.addNode = nodeId;
      button.textContent = "+";
      button.setAttribute("aria-label", t("mermaid.canvas.addConnected", nodeId));
      button.style.left = `${place.x - 10}px`;
      button.style.top = `${place.y - 10}px`;
      button.addEventListener("click", (event) => {
        event.stopPropagation();
        clearOverlay();
        commit((graph) => addConnectedNode(graph, nodeId) !== null);
      });
      overlay.append(button);
    }
  };

  const pointerover = (event) => {
    if (!isEnabled()) return;
    const nodeId = resolveNodeId(event.target);
    if (nodeId !== null) showAddButtons(nodeId);
  };

  // 2. 더블클릭 라벨 편집
  const dblclick = (event) => {
    if (!isEnabled() || overlay == null || commit == null) return;
    const nodeId = resolveNodeId(event.target);
    if (nodeId === null) return;

    const node = getGraph?.()?.nodes.find((candidate) => candidate.id === nodeId);
    if (node === undefined) return;
    const element = findNodeElement(surface, nodeId);
    if (element === null) return;

    clearOverlay();
    const rect = nodeRectInOverlay(viewport, element);
    const input = document.createElement("input");
    input.type = "text";
    input.className = "label-editor";
    input.dataset.labelEditor = nodeId;
    input.value = node.label;
    input.style.left = `${rect.left}px`;
    input.style.top = `${rect.top + rect.height / 2 - 12}px`;
    input.style.width = `${Math.max(rect.width, 80)}px`;

    let settled = false;
    const finish = (apply) => {
      if (settled) return;
      settled = true;
      const label = input.value.trim();
      clearOverlay();
      if (apply && label !== "" && label !== node.label) {
        commit((graph) => setNodeLabel(graph, nodeId, label));
      }
    };

    input.addEventListener("keydown", (keyEvent) => {
      if (keyEvent.key === "Enter") finish(true);
      else if (keyEvent.key === "Escape") finish(false);
      else return;
      keyEvent.preventDefault();
    });
    input.addEventListener("blur", () => finish(true));

    overlay.append(input);
    input.focus();
    input.select();
  };

  // 3. 드래그 연결
  const pointerdown = (event) => {
    if (!isEnabled() || overlay == null || commit == null) return;
    const fromId = resolveEndpoint(event.target);
    if (fromId === null) return;

    // 캔버스 이동(빈 영역 드래그)과 겹치지 않게 여기서 막는다.
    event.stopPropagation();

    const start = toOverlayPoint(viewport, event.clientX, event.clientY);
    let guide = null;

    const onMove = (moveEvent) => {
      const point = toOverlayPoint(viewport, moveEvent.clientX, moveEvent.clientY);
      const deltaX = point.x - start.x;
      const deltaY = point.y - start.y;
      const distance = Math.hypot(deltaX, deltaY);
      if (distance < DRAG_THRESHOLD_PX) return;

      if (guide === null) {
        clearOverlay();
        guide = document.createElement("div");
        guide.className = "connect-guide";
        overlay.append(guide);
      }
      guide.style.left = `${start.x}px`;
      guide.style.top = `${start.y}px`;
      guide.style.width = `${distance}px`;
      guide.style.transform = `rotate(${Math.atan2(deltaY, deltaX)}rad)`;
    };

    const onUp = (upEvent) => {
      document.removeEventListener("pointermove", onMove);
      document.removeEventListener("pointerup", onUp);
      if (guide === null) return; // 끌지 않았다면 그냥 선택으로 둔다.
      clearOverlay();

      const dropped = document.elementFromPoint?.(upEvent.clientX, upEvent.clientY);
      const toId = resolveEndpoint(dropped);
      if (toId === null || toId === fromId) return;
      commit((graph) => connectEndpoints(graph, fromId, toId) !== null);
    };

    document.addEventListener("pointermove", onMove);
    document.addEventListener("pointerup", onUp);
  };

  // 4. 삭제 - 글자를 받는 칸에 들어간 키는 가로채지 않는다.
  const documentKeydown = (event) => {
    if (event.key !== "Delete" && event.key !== "Backspace") return;
    if (!isEnabled() || commit == null) return;
    if (isTextEntry(event.target)) return;

    const current = getSelection?.();
    if (current == null) return;

    event.preventDefault();
    commit((graph) => {
      if (current.kind === "node") return removeNode(graph, current.id);
      if (current.kind === "edge") return removeEdge(graph, current.id);
      return removeSubgraph(graph, current.id);
    });
    select(null);
  };

  surface.addEventListener("click", click);
  surface.addEventListener("keydown", keydown);
  surface.addEventListener("pointerover", pointerover);
  surface.addEventListener("dblclick", dblclick);
  surface.addEventListener("pointerdown", pointerdown);
  viewport?.addEventListener("pointerleave", clearOverlay);
  document.addEventListener("keydown", documentKeydown);

  return () => {
    surface.removeEventListener("click", click);
    surface.removeEventListener("keydown", keydown);
    surface.removeEventListener("pointerover", pointerover);
    surface.removeEventListener("dblclick", dblclick);
    surface.removeEventListener("pointerdown", pointerdown);
    viewport?.removeEventListener("pointerleave", clearOverlay);
    document.removeEventListener("keydown", documentKeydown);
    clearOverlay();
  };
}
