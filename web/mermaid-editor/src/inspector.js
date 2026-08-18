import {
  groupNodes,
  moveNodeToSubgraph,
  removeEdge,
  removeNode,
  removeSubgraph,
  setEdgeArrow,
  setEdgeArrowHead,
  setEdgeColor,
  setEdgeLabel,
  setEdgeLine,
  setNodeColor,
  setNodeLabel,
  setNodeShape,
  setSubgraphDirection,
  setSubgraphTitle,
  subgraphOfNode,
} from "@markup-view-mini/mermaid-safe/model";
import { t } from "../../shared/i18n/index.js";
import { PALETTE_SHAPE_IDS, shapeLabel } from "./node-shapes.js";
import { PALETTE, paletteLabel } from "./palette.js";

/*
 * 모양·선·화살표는 글자가 아니라 그림으로 고른다. 이 편집기를 쓰는 사람은
 * mermaid 문법을 모르는 사람이라, "stadium"이라고 적어 두면 아무 도움이 안 된다.
 * 아이콘은 40x28 좌표계의 인라인 SVG다 - 외부 파일이나 아이콘 폰트를 만들지
 * 않는다(편집기는 CSP `default-src 'none'` 아래에서 로컬로만 뜬다).
 */
const SHAPE_GLYPHS = {
  rect: '<rect x="6" y="6" width="28" height="16" />',
  round: '<rect x="6" y="6" width="28" height="16" rx="6" />',
  stadium: '<rect x="6" y="6" width="28" height="16" rx="8" />',
  diamond: '<path d="M20 4 L36 14 L20 24 L4 14 Z" />',
  circle: '<circle cx="20" cy="14" r="9" />',
  doublecircle: '<g><circle cx="20" cy="14" r="9" /><circle cx="20" cy="14" r="6" /></g>',
  cylinder: '<path d="M8 9 v10 a12 4 0 0 0 24 0 v-10 a12 4 0 0 0 -24 0 a12 4 0 0 0 24 0" />',
  hexagon: '<path d="M11 6 H29 L35 14 L29 22 H11 L5 14 Z" />',
  parallelogram: '<path d="M11 6 H36 L29 22 H4 Z" />',
  parallelogramAlt: '<path d="M4 6 H29 L36 22 H11 Z" />',
  trapezoid: '<path d="M11 6 H29 L36 22 H4 Z" />',
  trapezoidAlt: '<path d="M4 6 H36 L29 22 H11 Z" />',
  subroutine: '<g><rect x="6" y="6" width="28" height="16" /><path d="M11 6 v16 M29 6 v16" /></g>',
  asymmetric: '<path d="M6 6 H30 L34 14 L30 22 H6 Z" />',
};

const LINE_GLYPHS = {
  solid: { glyph: '<path d="M4 14 H36" />', labelKey: "mermaid.line.solid" },
  dotted: { glyph: '<path d="M4 14 H36" stroke-dasharray="4 4" />', labelKey: "mermaid.line.dotted" },
  thick: { glyph: '<path d="M4 14 H36" stroke-width="4" />', labelKey: "mermaid.line.thick" },
  invisible: {
    glyph: '<path d="M4 14 H36" stroke-dasharray="1 5" opacity="0.4" />',
    labelKey: "mermaid.line.invisible",
  },
};

const ARROW_GLYPHS = {
  arrow: { glyph: '<g><path d="M4 14 H28" /><path d="M28 9 L36 14 L28 19 Z" /></g>', labelKey: "mermaid.arrow.arrow" },
  none: { glyph: '<path d="M4 14 H36" />', labelKey: "mermaid.arrow.none" },
  circle: { glyph: '<g><path d="M4 14 H28" /><circle cx="32" cy="14" r="4" /></g>', labelKey: "mermaid.arrow.circle" },
  cross: { glyph: '<g><path d="M4 14 H26" /><path d="M28 10 L36 18 M36 10 L28 18" /></g>', labelKey: "mermaid.arrow.cross" },
};

// 모델에서 arrowHead는 "시작 쪽 끝"이다. 있으면 양쪽 화살표(`A <--> B`)가 된다.
const EDGE_DIRECTION_GLYPHS = {
  none: { glyph: '<g><path d="M4 14 H28" /><path d="M28 9 L36 14 L28 19 Z" /></g>', labelKey: "mermaid.edgeDirection.none" },
  arrow: {
    glyph: '<g><path d="M12 14 H28" /><path d="M28 9 L36 14 L28 19 Z" /><path d="M12 9 L4 14 L12 19 Z" /></g>',
    labelKey: "mermaid.edgeDirection.arrow",
  },
};

const DIRECTION_CHOICES = [
  { value: "", labelKey: "mermaid.direction.inherit" },
  { value: "TB", labelKey: "mermaid.direction.TB" },
  { value: "LR", labelKey: "mermaid.direction.LR" },
  { value: "RL", labelKey: "mermaid.direction.RL" },
  { value: "BT", labelKey: "mermaid.direction.BT" },
];

function section(title) {
  const element = document.createElement("section");
  element.className = "inspector-section";
  const heading = document.createElement("h2");
  heading.textContent = title;
  element.append(heading);
  return element;
}

function hint(message) {
  const element = document.createElement("p");
  element.className = "inspector-hint";
  element.textContent = message;
  return element;
}

function glyphButton({ dataName, id, title, glyph, isCurrent, onPick }) {
  const control = document.createElement("button");
  control.type = "button";
  control.className = "glyph-button";
  control.dataset[dataName] = id;
  control.title = title;
  control.setAttribute("aria-label", title);
  control.setAttribute("aria-pressed", String(isCurrent));
  control.innerHTML = `<svg viewBox="0 0 40 28" aria-hidden="true" focusable="false">${glyph}</svg>`;
  control.addEventListener("click", () => onPick(id));
  return control;
}

function glyphChoices(title, dataName, glyphs, currentId, onPick) {
  const wrapper = section(title);
  const grid = document.createElement("div");
  grid.className = "glyph-grid";
  for (const [id, entry] of Object.entries(glyphs)) {
    grid.append(glyphButton({
      dataName,
      id,
      title: t(entry.labelKey),
      glyph: entry.glyph,
      isCurrent: id === currentId,
      onPick,
    }));
  }
  wrapper.append(grid);
  return wrapper;
}

function shapePalette(currentShape, onPick) {
  const wrapper = section(t("mermaid.inspector.shape"));
  const grid = document.createElement("div");
  grid.className = "glyph-grid";
  for (const shapeId of PALETTE_SHAPE_IDS) {
    grid.append(glyphButton({
      dataName: "nodeShape",
      id: shapeId,
      title: shapeLabel(shapeId),
      glyph: SHAPE_GLYPHS[shapeId] ?? SHAPE_GLYPHS.rect,
      isCurrent: shapeId === currentShape,
      onPick,
    }));
  }
  wrapper.append(grid);
  return wrapper;
}

function colorSwatches(dataName, currentColor, onPick) {
  const wrapper = section(t("mermaid.inspector.color"));
  const grid = document.createElement("div");
  grid.className = "swatch-grid";
  for (const entry of PALETTE) {
    const label = paletteLabel(entry.id);
    const control = document.createElement("button");
    control.type = "button";
    control.className = "swatch";
    control.dataset[dataName] = entry.id;
    control.title = label;
    control.setAttribute("aria-label", label);
    control.setAttribute("aria-pressed", String(entry.id === (currentColor ?? "default")));
    // fill이 null인 "기본"은 색이 아니라 "색 구문을 아예 쓰지 않음"이라는 상태다.
    if (entry.fill === null) {
      control.classList.add("swatch-default");
    } else {
      control.style.background = entry.fill;
      control.style.borderColor = entry.stroke;
    }
    control.addEventListener("click", () => onPick(entry.id));
    grid.append(control);
  }
  wrapper.append(grid);
  return wrapper;
}

function textField(title, dataName, value, onCommit) {
  const wrapper = section(title);
  const input = document.createElement("input");
  input.type = "text";
  input.value = value;
  input.dataset[dataName] = "";
  input.setAttribute("aria-label", title);
  // change에서만 커밋한다. input마다 커밋하면 글자 하나 칠 때마다 소스가 다시 쓰인다.
  input.addEventListener("change", () => onCommit(input.value));
  wrapper.append(input);
  return wrapper;
}

function wideButton(text, dataName, onClick) {
  const control = document.createElement("button");
  control.type = "button";
  control.className = "inspector-wide";
  control.dataset[dataName] = "";
  control.textContent = text;
  control.addEventListener("click", onClick);
  return control;
}

function deleteButton(text, onDelete) {
  const wrapper = document.createElement("div");
  wrapper.className = "inspector-actions";
  const control = document.createElement("button");
  control.type = "button";
  control.className = "danger";
  control.dataset.delete = "";
  control.textContent = text;
  control.addEventListener("click", onDelete);
  wrapper.append(control);
  return wrapper;
}

function title(text) {
  const heading = document.createElement("h2");
  heading.className = "inspector-title";
  heading.textContent = text;
  return heading;
}

/**
 * 노드가 속한 묶음을 고른다. 코드를 직접 고치지 않고 소속만 바꾸는 길이다.
 * 드래그로 옮기는 방식보다 단순하고, 어느 묶음에 들어 있는지도 한눈에 보인다.
 */
function subgraphMembership(graph, node, commit) {
  const wrapper = section(t("mermaid.inspector.group"));
  const select = document.createElement("select");
  select.dataset.nodeSubgraph = "";
  select.setAttribute("aria-label", t("mermaid.inspector.groupAria"));

  const none = document.createElement("option");
  none.value = "";
  none.textContent = t("mermaid.inspector.noGroup");
  select.append(none);

  for (const group of graph.subgraphs) {
    const option = document.createElement("option");
    option.value = group.id;
    option.textContent = group.title;
    select.append(option);
  }

  select.value = subgraphOfNode(graph, node.id)?.id ?? "";
  select.addEventListener("change", () => commit(
    (model) => moveNodeToSubgraph(model, node.id, select.value === "" ? null : select.value),
    "[data-node-subgraph]",
  ));
  wrapper.append(select);

  wrapper.append(wideButton(t("mermaid.inspector.wrapInNewGroup"), "groupNode", () => commit(
    (model) => groupNodes(model, [node.id], "") !== null,
    "[data-node-subgraph]",
  )));
  return wrapper;
}

function nodePanel(graph, node, commit) {
  return [
    title(t("mermaid.inspector.nodeHeading", node.id)),
    textField(t("mermaid.inspector.text"), "nodeLabel", node.label, (value) =>
      commit((model) => setNodeLabel(model, node.id, value), "[data-node-label]")),
    subgraphMembership(graph, node, commit),
    shapePalette(node.shape, (shapeId) =>
      commit((model) => setNodeShape(model, node.id, shapeId), `[data-node-shape="${shapeId}"]`)),
    colorSwatches("nodeColor", node.color, (colorId) =>
      commit((model) => setNodeColor(model, node.id, colorId), `[data-node-color="${colorId}"]`)),
    deleteButton(t("mermaid.inspector.deleteNode"), () => commit((model) => removeNode(model, node.id))),
  ];
}

function edgePanel(edge, commit) {
  return [
    title(t("mermaid.inspector.edgeHeading", edge.from, edge.to)),
    textField(t("mermaid.inspector.text"), "edgeLabel", edge.label ?? "", (value) =>
      commit((model) => setEdgeLabel(model, edge.id, value), "[data-edge-label]")),
    glyphChoices(t("mermaid.inspector.lineType"), "edgeLine", LINE_GLYPHS, edge.line, (lineId) =>
      commit((model) => setEdgeLine(model, edge.id, lineId), `[data-edge-line="${lineId}"]`)),
    glyphChoices(t("mermaid.inspector.arrowEnd"), "edgeArrow", ARROW_GLYPHS, edge.arrow, (arrowId) =>
      commit((model) => setEdgeArrow(model, edge.id, arrowId), `[data-edge-arrow="${arrowId}"]`)),
    glyphChoices(t("mermaid.inspector.direction"), "edgeDirection", EDGE_DIRECTION_GLYPHS, edge.arrowHead, (headId) =>
      commit((model) => setEdgeArrowHead(model, edge.id, headId), `[data-edge-direction="${headId}"]`)),
    colorSwatches("edgeColor", edge.color, (colorId) =>
      commit((model) => setEdgeColor(model, edge.id, colorId), `[data-edge-color="${colorId}"]`)),
    deleteButton(t("mermaid.inspector.deleteEdge"), () => commit((model) => removeEdge(model, edge.id))),
  ];
}

function subgraphPanel(group, commit) {
  const direction = section(t("mermaid.inspector.innerDirection"));
  const select = document.createElement("select");
  select.dataset.subgraphDirection = "";
  select.setAttribute("aria-label", t("mermaid.inspector.innerDirectionAria"));
  for (const choice of DIRECTION_CHOICES) {
    const option = document.createElement("option");
    option.value = choice.value;
    option.textContent = t(choice.labelKey);
    select.append(option);
  }
  select.value = group.direction ?? "";
  select.addEventListener("change", () => commit(
    (model) => setSubgraphDirection(model, group.id, select.value),
    "[data-subgraph-direction]",
  ));
  direction.append(select);

  return [
    title(t("mermaid.inspector.subgraphHeading", group.id)),
    textField(t("mermaid.inspector.groupTitle"), "subgraphTitle", group.title, (value) =>
      commit((model) => setSubgraphTitle(model, group.id, value), "[data-subgraph-title]")),
    direction,
    // 묶음만 없앤다. 안에 든 노드는 그대로 남는다.
    deleteButton(t("mermaid.inspector.deleteGroupOnly"), () => commit((model) => removeSubgraph(model, group.id))),
  ];
}

/**
 * 도구 패널을 그린다. 순수하게 유지한다 - 전역 상태를 읽지 않고, 모든 변경은
 * commit으로만 나간다. 그래서 mermaid 없이 jsdom에서 전부 시험할 수 있다.
 *
 * graph가 null이면 파서가 다루지 못하는 다이어그램이다(제한 모드). 이때는
 * 소스 칸에서 텍스트로만 편집할 수 있다.
 */
export function renderInspector(container, { graph = null, selection = null, commit } = {}) {
  container.replaceChildren();

  if (graph === null) {
    container.append(hint(t("mermaid.inspector.locked")));
    return;
  }
  if (selection === null) {
    container.append(hint(t("mermaid.inspector.empty")));
    return;
  }

  const target = selection.kind === "node"
    ? graph.nodes.find((node) => node.id === selection.id)
    : selection.kind === "edge"
      ? graph.edges.find((edge) => edge.id === selection.id)
      : graph.subgraphs.find((group) => group.id === selection.id);

  // 방금 지운 대상이 선택에 남아 있을 수 있다.
  if (target === undefined) {
    container.append(hint(t("mermaid.inspector.empty")));
    return;
  }

  if (selection.kind === "node") container.append(...nodePanel(graph, target, commit));
  else if (selection.kind === "edge") container.append(...edgePanel(target, commit));
  else container.append(...subgraphPanel(target, commit));
}
