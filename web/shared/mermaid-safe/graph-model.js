export const DIRECTIONS = ["TD", "TB", "LR", "RL", "BT"];

export function createEmptyGraph(keyword = "flowchart", direction = "TB") {
  return {
    keyword,
    direction,
    nodes: [],
    edges: [],
    subgraphs: [],
    classDefs: [],
    classUses: [],
    comments: [],
  };
}

/** 전체 흐름 방향. 아는 값이 아니면 그대로 둔다. */
export function setDirection(graph, direction) {
  if (!DIRECTIONS.includes(direction) || graph.direction === direction) return false;
  graph.direction = direction;
  return true;
}

export function findNode(graph, id) {
  return graph.nodes.find((node) => node.id === id) ?? null;
}

/**
 * 노드를 만들거나 이미 있으면 돌려준다.
 * label/shape는 "이름만 쓴 참조"가 나중에 나온 진짜 선언을 덮어쓰지 않도록,
 * 명시된 값이 들어올 때만 채운다.
 */
export function upsertNode(graph, id, { label, shape, color } = {}) {
  let node = findNode(graph, id);
  if (node === null) {
    node = { id, label: label ?? id, shape: shape ?? "rect", color: color ?? null };
    graph.nodes.push(node);
    return node;
  }
  if (label !== undefined && label !== null) node.label = label;
  if (shape !== undefined && shape !== null) node.shape = shape;
  if (color !== undefined && color !== null) node.color = color;
  return node;
}

let edgeCounter = 0;

export function addEdge(graph, edge) {
  edgeCounter += 1;
  const created = {
    id: `edge-${edgeCounter}`,
    label: null,
    line: "solid",
    arrow: "arrow",
    arrowHead: "none",
    color: null,
    ...edge,
  };
  graph.edges.push(created);
  return created;
}

/*
 * 엣지 id(`edge-N`)는 파싱할 때마다 새로 매겨진다. 비주얼 편집은 코드를 다시 쓰고
 * 다시 파싱하므로, 편집 한 번에 선택하고 있던 엣지의 id가 사라진다.
 * 노드는 모델 id가 그대로라 이 문제가 없다.
 *
 * 그래서 다시 찾을 수 있는 이름표를 따로 만든다. 양 끝과, 같은 양 끝을 가진 것들 사이의
 * 순번이다. 라벨·선 종류·색을 고쳐도 이 셋은 변하지 않는다.
 */
export function describeEdge(graph, edgeId) {
  const target = graph.edges.find((edge) => edge.id === edgeId);
  if (target === undefined) return null;

  let occurrence = 0;
  for (const edge of graph.edges) {
    if (edge.id === edgeId) return { from: target.from, to: target.to, occurrence };
    if (edge.from === target.from && edge.to === target.to) occurrence += 1;
  }
  return null;
}

export function findEdgeByDescription(graph, description) {
  if (description == null) return null;

  let occurrence = 0;
  for (const edge of graph.edges) {
    if (edge.from !== description.from || edge.to !== description.to) continue;
    if (occurrence === description.occurrence) return edge;
    occurrence += 1;
  }
  return null;
}

const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

export function nextNodeId(graph) {
  const used = new Set(graph.nodes.map((node) => node.id));
  for (const letter of LETTERS) if (!used.has(letter)) return letter;
  for (let suffix = 1; ; suffix += 1) {
    for (const letter of LETTERS) {
      const candidate = `${letter}${suffix}`;
      if (!used.has(candidate)) return candidate;
    }
  }
}

function subgraphContaining(graph, nodeId) {
  return graph.subgraphs.find((group) => group.children.includes(nodeId)) ?? null;
}

export function addConnectedNode(graph, fromId, { label, shape } = {}) {
  if (findNode(graph, fromId) === null) return null;

  const id = nextNodeId(graph);
  const created = upsertNode(graph, id, { label: label ?? id, shape: shape ?? "rect" });
  addEdge(graph, { from: fromId, to: id });

  // 새 노드는 출발 노드와 같은 묶음에 넣는다. 그러지 않으면 묶음 밖으로 튀어나온다.
  const group = subgraphContaining(graph, fromId);
  if (group !== null) group.children.push(id);

  return created;
}

export function connectNodes(graph, fromId, toId) {
  if (fromId === toId) return null;
  if (findNode(graph, fromId) === null || findNode(graph, toId) === null) return null;
  return addEdge(graph, { from: fromId, to: toId });
}

/**
 * 선의 양 끝은 노드일 수도 묶음일 수도 있다. Mermaid는 묶음 이름을 노드처럼 쓴다.
 * 묶음 전체를 잇고 싶을 때(단계에서 단계로) 쓴다.
 */
export function connectEndpoints(graph, fromId, toId) {
  const exists = (id) =>
    findNode(graph, id) !== null || graph.subgraphs.some((group) => group.id === id);

  if (fromId === toId) return null;
  if (!exists(fromId) || !exists(toId)) return null;
  return addEdge(graph, { from: fromId, to: toId });
}

export function removeNode(graph, id) {
  const index = graph.nodes.findIndex((node) => node.id === id);
  if (index < 0) return false;

  graph.nodes.splice(index, 1);
  graph.edges = graph.edges.filter((edge) => edge.from !== id && edge.to !== id);
  graph.classUses = graph.classUses.filter((use) => use.nodeId !== id);
  for (const group of graph.subgraphs) {
    group.children = group.children.filter((child) => child !== id);
  }
  graph.comments = graph.comments.filter(
    (comment) => !(comment.anchorKind === "node" && comment.anchorId === id));
  return true;
}

export function removeEdge(graph, id) {
  const index = graph.edges.findIndex((edge) => edge.id === id);
  if (index < 0) return false;

  graph.edges.splice(index, 1);
  graph.comments = graph.comments.filter(
    (comment) => !(comment.anchorKind === "edge" && comment.anchorId === id));
  return true;
}

export function removeSubgraph(graph, id) {
  const index = graph.subgraphs.findIndex((group) => group.id === id);
  if (index < 0) return false;

  // 묶음만 없애고 안의 노드는 남긴다. 노드까지 지우면 되돌리기 어렵다.
  graph.subgraphs.splice(index, 1);
  for (const group of graph.subgraphs) {
    group.children = group.children.filter((child) => child !== id);
  }
  return true;
}

function findSubgraph(graph, id) {
  return graph.subgraphs.find((group) => group.id === id) ?? null;
}

/** 노드가 속한 묶음. 어디에도 없으면 null. */
export function subgraphOfNode(graph, nodeId) {
  return graph.subgraphs.find((group) => group.children.includes(nodeId)) ?? null;
}

/**
 * 노드를 묶음으로 옮긴다. subgraphId가 null이면 어느 묶음에도 속하지 않게 한다.
 * 코드를 직접 고치지 않고 소속만 바꾸는 길이라, 드래그 없이도 묶음을 다룰 수 있다.
 */
export function moveNodeToSubgraph(graph, nodeId, subgraphId) {
  if (findNode(graph, nodeId) === null) return false;
  if (subgraphId !== null && findSubgraph(graph, subgraphId) === null) return false;

  const current = subgraphOfNode(graph, nodeId);
  if ((current?.id ?? null) === subgraphId) return false;

  for (const group of graph.subgraphs) {
    group.children = group.children.filter((child) => child !== nodeId);
  }
  if (subgraphId !== null) findSubgraph(graph, subgraphId).children.push(nodeId);
  return true;
}

/**
 * 노드들을 새 묶음으로 감싼다. 이미 다른 묶음에 있던 노드도 새 묶음으로 옮긴다.
 * 노드는 한 묶음에만 속할 수 있기 때문이다.
 */
export function groupNodes(graph, nodeIds, title) {
  const members = nodeIds.filter((id) => findNode(graph, id) !== null);
  if (members.length === 0) return null;
  if (/[[\]]/.test(title ?? "")) return null;

  const used = new Set(graph.subgraphs.map((group) => group.id));
  let id = "";
  for (let suffix = 1; ; suffix += 1) {
    id = `group${suffix}`;
    if (!used.has(id) && findNode(graph, id) === null) break;
  }

  for (const group of graph.subgraphs) {
    group.children = group.children.filter((child) => !members.includes(child));
  }
  const created = { id, title: title || id, direction: null, children: [...members] };
  graph.subgraphs.push(created);
  return created;
}

export function setSubgraphTitle(graph, id, title) {
  const group = findSubgraph(graph, id);
  if (group === null) return false;
  // 제목은 `subgraph <id> [<제목>]`로 나간다. 대괄호가 들어가면 왕복이 깨진다.
  if (/[[\]]/.test(title)) return false;
  group.title = title;
  return true;
}

export function setSubgraphDirection(graph, id, direction) {
  const group = findSubgraph(graph, id);
  if (group === null) return false;
  group.direction = DIRECTIONS.includes(direction) ? direction : null;
  return true;
}

function setNodeField(graph, id, field, value) {
  const node = findNode(graph, id);
  if (node === null) return false;
  node[field] = value;
  return true;
}

// 라벨에 CR/LF나 큰따옴표가 그대로 들어가면 직렬화 시 mermaid 문법이 깨진다
// (예: 라벨 안의 `"`가 감싸는 따옴표와 충돌). 빈 라벨도 노드 표시가 사라지므로 막는다.
export function setNodeLabel(graph, id, label) {
  if (typeof label !== "string" || label === "" || /[\r\n"]/u.test(label)) return false;
  return setNodeField(graph, id, "label", label);
}
export const setNodeShape = (graph, id, shape) => setNodeField(graph, id, "shape", shape);
export const setNodeColor = (graph, id, color) =>
  setNodeField(graph, id, "color", color === "default" ? null : color);

function setEdgeField(graph, id, field, value) {
  const edge = graph.edges.find((candidate) => candidate.id === id);
  if (edge === undefined) return false;
  edge[field] = value;
  return true;
}

// 같은 이유로 엣지 라벨도 CR/LF와 `|`(구분자)를 막는다 - `|label|` 토큰과 충돌한다.
export function setEdgeLabel(graph, id, label) {
  if (typeof label !== "string" || /[\r\n|]/u.test(label)) return false;
  return setEdgeField(graph, id, "label", label === "" ? null : label);
}
export const setEdgeLine = (graph, id, line) => setEdgeField(graph, id, "line", line);
export const setEdgeArrow = (graph, id, arrow) => setEdgeField(graph, id, "arrow", arrow);
/** 시작 쪽 끝. "arrow"면 `A <--> B`처럼 양쪽 화살표가 된다. */
export const setEdgeArrowHead = (graph, id, arrowHead) =>
  setEdgeField(graph, id, "arrowHead", arrowHead);
export const setEdgeColor = (graph, id, color) =>
  setEdgeField(graph, id, "color", color === "default" ? null : color);
