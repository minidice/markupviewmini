import { clampZoom } from "./zoom-control.js";

/*
 * mermaid가 그린 DOM에서 모델 id를 되찾는 규칙. 참조 구현이 mermaid 11에서 실측해
 * 남긴 내용이고, 우리도 같은 mermaid 11.x를 쓴다:
 *
 *   최상위 svg   id = 우리가 mermaid.render에 넘긴 id
 *   노드 g       class="node", id="<svg id>-flowchart-<노드 id>-<숫자>"
 *   묶음 g       class="cluster", id="<svg id>-<묶음 id>"
 *                — 노드와 달리 "-flowchart-"도 꼬리 숫자도 없다
 *   엣지 path    id="<svg id>-L_<from>_<to>_<n>"
 *
 *   ★ DOM 순서 != 모델 순서다. 위치로 짝지으면 엉뚱한 대상을 고치게 된다.
 *     반드시 id로 맞춘다.
 *   ★ 같은 양 끝을 가진 선이 여럿이면 n이 선언 순서대로 커지지만 연속하지는
 *     않는다. n을 계산하려 들지 말고 정렬해서 쓴다.
 *
 * 형식이 어긋나면 전부 null을 준다. 부르는 쪽은 null을 "편집 잠금"으로 다뤄야
 * 한다 — 엉뚱한 노드를 고치는 것보다 아무것도 못 고치는 편이 낫다.
 */

export function resolveNodeId(element) {
  const group = element?.closest?.("g.node");
  if (group == null) return null;

  const svg = group.closest("svg");
  if (svg == null || svg.id === "") return null;

  const prefix = `${svg.id}-flowchart-`;
  const domId = group.getAttribute("id") ?? "";
  if (!domId.startsWith(prefix)) return null;

  // 모델 id에도 하이픈이나 숫자가 들어갈 수 있다. 꼬리 숫자만 떼어 낸다.
  const match = domId.slice(prefix.length).match(/^(.+)-\d+$/u);
  return match === null ? null : match[1];
}

export function resolveSubgraphId(element) {
  const cluster = element?.closest?.("g.cluster");
  if (cluster == null) return null;

  const svg = cluster.closest("svg");
  if (svg == null || svg.id === "") return null;

  const prefix = `${svg.id}-`;
  const domId = cluster.getAttribute("id") ?? "";
  if (!domId.startsWith(prefix)) return null;

  const id = domId.slice(prefix.length);
  return id === "" ? null : id;
}

export function findNodeElement(surface, nodeId) {
  return [...surface.querySelectorAll("g.node")]
    .find((group) => resolveNodeId(group) === nodeId) ?? null;
}

export function findSubgraphElement(surface, subgraphId) {
  return [...surface.querySelectorAll("g.cluster")]
    .find((cluster) => resolveSubgraphId(cluster) === subgraphId) ?? null;
}

function edgeElements(surface, graph) {
  const paths = [...surface.querySelectorAll(
    "path.flowchart-link:not([data-hit-for]), g.edgePaths path:not([data-hit-for])")];
  return paths.length === graph.edges.length ? paths : null;
}

/**
 * 엣지 path를 모델 엣지 id에 짝지어 Map<엣지 id, path>로 준다.
 * 하나라도 짝지어지지 않으면 null이다 — 부르는 쪽은 편집을 잠가야 한다.
 */
export function mapEdgeElements(surface, graph) {
  const svg = surface.querySelector("svg");
  if (svg == null || svg.id === "") return null;

  const paths = edgeElements(surface, graph);
  if (paths === null) return null;

  const edgesByPair = new Map();
  for (const edge of graph.edges) {
    const pair = `${edge.from}_${edge.to}`;
    if (!edgesByPair.has(pair)) edgesByPair.set(pair, []);
    edgesByPair.get(pair).push(edge);
  }

  const mapped = new Map();
  const claimed = new Set();
  for (const [pair, edges] of edgesByPair) {
    const prefix = `${svg.id}-L_${pair}_`;
    const candidates = paths
      .map((path) => ({ path, id: path.getAttribute("id") ?? "" }))
      .filter((entry) => entry.id.startsWith(prefix))
      .map((entry) => ({ path: entry.path, order: Number(entry.id.slice(prefix.length)) }))
      .filter((entry) => Number.isInteger(entry.order))
      .sort((left, right) => left.order - right.order);

    if (candidates.length !== edges.length) return null;
    edges.forEach((edge, index) => {
      mapped.set(edge.id, candidates[index].path);
      claimed.add(candidates[index].path);
    });
  }

  // 짝지어지지 않고 남은 path가 있으면 그림과 모델이 어긋난 것이다.
  return claimed.size === paths.length ? mapped : null;
}

/** 클릭 판정용 복제의 굵기. 실제 선은 1px이라 그대로 두면 조준이 거의 불가능하다. */
const HIT_AREA_STROKE_WIDTH = 14;

/**
 * 선은 너무 얇아서 클릭하기 어렵다. 투명한 굵은 복제를 깔아 판정을 넓힌다.
 *
 * 굵기를 속성이 아니라 인라인 스타일로 준다. mermaid가 주입하는
 * `.flowchart-link { stroke-width: ... }`가 표현 속성을 이겨서, 속성으로 주면
 * 계산값이 되돌아가 판정이 전혀 넓어지지 않는다. 같은 이유로 class도 떼어 낸다.
 */
export function addEdgeHitAreas(surface, paths) {
  for (const path of paths) {
    if (path.dataset.hitAreaAdded === "true") continue;

    const hit = path.cloneNode(false);
    hit.removeAttribute("id");
    hit.removeAttribute("class");
    hit.removeAttribute("marker-end");
    hit.removeAttribute("marker-start");
    hit.dataset.hitFor = "true";
    hit.style.stroke = "transparent";
    hit.style.strokeWidth = `${HIT_AREA_STROKE_WIDTH}px`;
    hit.style.strokeDasharray = "none";
    hit.style.fill = "none";
    hit.style.pointerEvents = "stroke";

    path.parentNode.insertBefore(hit, path);
    path.dataset.hitAreaAdded = "true";
  }
}

export function createCanvasView(viewport, surface, onChange = () => {}) {
  let zoom = 1;
  let panX = 0;
  let panY = 0;
  const paint = () => {
    surface.style.transform = `translate(${panX}px, ${panY}px) scale(${zoom})`;
    onChange(zoom, panX, panY);
  };
  return {
    get zoom() { return zoom; },
    get pan() { return { x: panX, y: panY }; },
    set(nextZoom, nextPanX = panX, nextPanY = panY) {
      zoom = clampZoom(nextZoom);
      panX = nextPanX;
      panY = nextPanY;
      paint();
    },
    panBy(deltaX, deltaY) { panX += deltaX; panY += deltaY; paint(); },
    reset() { zoom = 1; panX = 0; panY = 0; paint(); },
    fit() { zoom = 1; paint(); },
    viewport,
  };
}
