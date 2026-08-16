export const DIRECTIONS = ["TD", "TB", "LR", "RL", "BT"];
const NODE_SHAPES = new Set(["rect", "round", "diamond"]);
const NODE_COLOURS = new Set(["default", "blue", "green"]);

export function createEmptyGraph(keyword = "flowchart", direction = "TB", format = null) {
  return { keyword, direction, nodes: [], edges: [], subgraphs: [], comments: [], format };
}

export function findNode(graph, id) {
  return graph.nodes.find((node) => node.id === id) ?? null;
}

export function upsertNode(graph, id, { label, shape, color } = {}) {
  const existing = findNode(graph, id);
  if (existing !== null) {
    if (label != null) existing.label = label;
    if (shape != null) existing.shape = shape;
    if (color != null) existing.color = color;
    return existing;
  }
  const node = { id, label: label ?? id, shape: shape ?? "rect", color: color ?? null };
  graph.nodes.push(node);
  return node;
}

export function addEdge(graph, edge) {
  const created = { id: `edge-${graph.edges.length + 1}`, label: null, line: "solid", arrow: "arrow", ...edge };
  graph.edges.push(created);
  return created;
}

export function setDirection(graph, direction) {
  if (!DIRECTIONS.includes(direction) || graph.direction === direction) return false;
  graph.direction = direction;
  return true;
}

export function setNodeLabel(graph, id, label) {
  const node = findNode(graph, id);
  if (node === null || typeof label !== "string" || label === "" || /[\r\n"]/u.test(label)) return false;
  node.label = label;
  return true;
}

export function setNodeShape(graph, id, shape) {
  const node = findNode(graph, id);
  if (node === null || !NODE_SHAPES.has(shape)) return false;
  node.shape = shape;
  return true;
}

export function setNodeColor(graph, id, color) {
  const node = findNode(graph, id);
  if (node === null || !NODE_COLOURS.has(color)) return false;
  node.color = color === "default" ? null : color;
  return true;
}
