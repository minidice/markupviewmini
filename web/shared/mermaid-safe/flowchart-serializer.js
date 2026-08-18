import { CLASSIC_SHAPE_BY_ID } from "./node-shapes.js";
import { PALETTE_BY_ID } from "./palette.js";

const LINE_TOKEN = { solid: "--", dotted: "-.", thick: "==", invisible: "~~~" };
const ARROW_TOKEN = { arrow: ">", circle: "o", cross: "x", none: "" };
const HEAD_TOKEN = { arrow: "<", circle: "o", cross: "x", none: "" };

/** 라벨에 문법 기호가 있으면 따옴표로 감싼다. */
function quoteIfNeeded(label) {
  return /[[\](){}|"<>]/.test(label) && !label.includes("<br") ? `"${label}"` : label;
}

function nodeDeclaration(node) {
  const shape = CLASSIC_SHAPE_BY_ID.get(node.shape);
  if (shape === undefined) {
    // 확장 모양은 @{ } 형태로만 표현할 수 있다.
    return `${node.id}@{ shape: ${node.shape}, label: "${node.label}" }`;
  }
  return `${node.id}${shape.open}${quoteIfNeeded(node.label)}${shape.close}`;
}

/**
 * 링크 토큰을 만든다.
 *
 * 끝에 화살표가 없으면 닫을 글자가 하나 더 필요하다 — `A -- B`는 mermaid가 링크로
 * 읽지 않아 그 줄이 통째로 사라진다. `A --- B`, `A === B`여야 한다.
 * 점선은 몸통 자체가 `-.-`라 이미 닫혀 있다.
 */
function linkToken(edge) {
  if (edge.line === "invisible") return LINE_TOKEN.invisible;

  const head = HEAD_TOKEN[edge.arrowHead];
  const tail = ARROW_TOKEN[edge.arrow];
  if (edge.line === "dotted") return `${head}-.-${tail}`;

  const stem = LINE_TOKEN[edge.line];
  return `${head}${stem}${tail === "" ? stem[0] : ""}${tail}`;
}

function edgeStatement(edge) {
  const label = edge.label === null || edge.label === "" ? "" : `|${edge.label}|`;
  return `${edge.from} ${linkToken(edge)}${label} ${edge.to}`;
}

function commentsFor(graph, anchorKind, anchorId, indent) {
  return graph.comments
    .filter((comment) => comment.anchorKind === anchorKind && comment.anchorId === anchorId)
    .map((comment) => `${indent}%% ${comment.text}`);
}

/**
 * 노드마다 자기가 속한 최상위 서브그래프를 찾는다.
 * 노드 선언 순서가 Mermaid의 배치 순서라서, 서브그래프는 첫 구성원이 나오는 자리에 써야
 * 왕복해도 순서가 유지된다.
 */
function topLevelOwners(graph) {
  const childIds = new Set();
  for (const group of graph.subgraphs) {
    for (const child of group.children) childIds.add(child);
  }

  const groupById = new Map(graph.subgraphs.map((group) => [group.id, group]));
  const owners = new Map();

  const walk = (group, root) => {
    for (const childId of group.children) {
      const nested = groupById.get(childId);
      if (nested === undefined) owners.set(childId, root);
      else walk(nested, root);
    }
  };
  for (const group of graph.subgraphs) {
    if (!childIds.has(group.id)) walk(group, group);
  }

  return { owners, childIds };
}

export function serializeFlowchart(graph) {
  const lines = [];

  lines.push(...commentsFor(graph, "header", null, ""));
  lines.push(`${graph.keyword} ${graph.direction}`);

  const { owners, childIds } = topLevelOwners(graph);
  const writtenGroups = new Set();

  for (const node of graph.nodes) {
    const owner = owners.get(node.id);
    if (owner !== undefined) {
      if (!writtenGroups.has(owner.id)) {
        writtenGroups.add(owner.id);
        lines.push(...writeSubgraph(graph, owner, "  "));
      }
      continue;
    }
    lines.push(...commentsFor(graph, "node", node.id, "  "));
    lines.push(`  ${nodeDeclaration(node)}`);
  }

  // 구성원이 하나도 없는 서브그래프도 잃지 않는다.
  for (const group of graph.subgraphs) {
    if (childIds.has(group.id) || writtenGroups.has(group.id)) continue;
    writtenGroups.add(group.id);
    lines.push(...writeSubgraph(graph, group, "  "));
  }

  for (const edge of graph.edges) {
    lines.push(...commentsFor(graph, "edge", edge.id, "  "));
    lines.push(`  ${edgeStatement(edge)}`);
  }

  for (const classDef of graph.classDefs) lines.push(`  classDef ${classDef.name} ${classDef.body}`);
  for (const use of graph.classUses) lines.push(`  class ${use.nodeId} ${use.className}`);

  for (const node of graph.nodes) {
    if (node.color === null) continue;
    const colour = PALETTE_BY_ID.get(node.color);
    lines.push(`  style ${node.id} fill:${colour.fill},stroke:${colour.stroke},color:${colour.text}`);
  }

  // linkStyle의 순번은 항상 지금의 엣지 순서로 다시 계산한다.
  // 선을 지웠을 때 다른 선의 색이 엉키는 것을 이 한 줄이 막는다.
  graph.edges.forEach((edge, index) => {
    if (edge.color === null) return;
    const colour = PALETTE_BY_ID.get(edge.color);
    lines.push(`  linkStyle ${index} stroke:${colour.stroke},stroke-width:2px`);
  });

  lines.push(...commentsFor(graph, "trailing", null, "  "));

  // 원본이 마크다운 목록/인용문 안에 들여써져 있었다면(parseFlowchart가 기억해 둔
  // graph.format), 매 줄에 그 접두사를 다시 씌운다 - 안 그러면 헤더 줄이 들여쓰기 0으로
  // 나가서 목록/인용문 밖으로 빠져나간다. 원래 줄바꿈 종류도 그대로 되돌린다.
  const containerPrefix = graph.format?.containerPrefix ?? "";
  const newline = graph.format?.newline ?? "\n";
  const prefixed = containerPrefix === "" ? lines : lines.map((line) => `${containerPrefix}${line}`);
  return `${prefixed.join(newline)}${newline}`;
}

function writeSubgraph(graph, group, indent) {
  const lines = [];
  lines.push(...commentsFor(graph, "subgraph", group.id, indent));
  lines.push(`${indent}subgraph ${group.id} [${group.title}]`);
  if (group.direction !== null) lines.push(`${indent}  direction ${group.direction}`);

  for (const childId of group.children) {
    const child = graph.subgraphs.find((candidate) => candidate.id === childId);
    if (child !== undefined) {
      lines.push(...writeSubgraph(graph, child, `${indent}  `));
      continue;
    }
    const node = graph.nodes.find((candidate) => candidate.id === childId);
    if (node === undefined) continue;
    lines.push(...commentsFor(graph, "node", node.id, `${indent}  `));
    lines.push(`${indent}  ${nodeDeclaration(node)}`);
  }

  lines.push(`${indent}end`);
  return lines;
}
