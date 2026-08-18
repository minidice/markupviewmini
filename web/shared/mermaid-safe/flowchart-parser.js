import { addEdge, createEmptyGraph, DIRECTIONS, upsertNode } from "./graph-model.js";
import { CLASSIC_SHAPES } from "./node-shapes.js";
import { paletteIdForLinkStroke, paletteIdForNodeStyle } from "./palette.js";

const HEADER = /^(flowchart|graph)(?:\s+(TD|TB|LR|RL|BT))?\s*$/;
const NODE_ID = /^[A-Za-z0-9_][A-Za-z0-9_.-]*/;
const SUBGRAPH = /^subgraph\s+(.+)$/;
const SUBGRAPH_TITLE = /^(\S+)\s*\[(.*)\]$/;
const DIRECTION_LINE = /^direction\s+(TD|TB|LR|RL|BT)$/;
const STYLE_LINE = /^style\s+(\S+)\s+(.+)$/;
const LINK_STYLE_LINE = /^linkStyle\s+(\d+)\s+(.+)$/;
const CLASS_DEF_LINE = /^classDef\s+(\S+)\s+(.+)$/;
const CLASS_LINE = /^class\s+(\S+)\s+(\S+)$/;

// 링크 토큰. 앞머리 화살표(선택) + 몸통 + 뒷머리 화살표(선택) + |라벨|(선택)
const LINK = /^(<|o|x)?(-\.+-|-{2,}|={2,}|~{3,})(>|o|x)?(?:\|([^|]*)\|)?/;

// 뒷머리는 `>`, 앞머리는 `<`로 쓴다. 둘 다 같은 "화살표"를 뜻한다.
const ARROW_BY_TOKEN = { ">": "arrow", "<": "arrow", o: "circle", x: "cross" };

export function failure(reason, detail = null) {
  return { ok: false, reason, detail };
}

/**
 * mermaid 블록은 마크다운 목록·인용문 안에 들여써질 수 있다 — document-surface의
 * findMermaidBlocks가 그 들여쓰기를 포함해서 블록 소스를 넘긴다. 여기서 공통 접두사를
 * 벗겨서 파싱하고, 시리얼라이저가 다시 씌운다. 안 그러면 편집 한 번으로 헤더 줄
 * (직렬화기가 항상 들여쓰기 0으로 쓴다)이 목록/인용문 구조 밖으로 빠져나간다.
 */
const CONTAINER_PREFIX = /^(?:(?:[ \t]*>[ \t]*)+|[ \t]+)/u;

function longestCommonPrefix(strings) {
  if (strings.length === 0) return "";
  let prefix = strings[0];
  for (const value of strings.slice(1)) {
    let index = 0;
    while (index < prefix.length && index < value.length && prefix[index] === value[index]) index += 1;
    prefix = prefix.slice(0, index);
  }
  return prefix;
}

function stripContainerPrefix(lines) {
  const nonBlank = lines.filter((line) => line.trim() !== "");
  const common = longestCommonPrefix(nonBlank);
  const containerPrefix = common.match(CONTAINER_PREFIX)?.[0] ?? "";
  if (containerPrefix === "") return { containerPrefix: "", lines };
  return {
    containerPrefix,
    lines: lines.map((line) =>
      (line.startsWith(containerPrefix) ? line.slice(containerPrefix.length) : line)),
  };
}

/** 줄바꿈 종류가 섞여 있으면 재구성 시 하나로 뭉개져 손실 없는 편집을 보장할 수 없다. */
function splitByNewline(source) {
  const text = String(source ?? "");
  const matches = text.match(/\r\n|\r|\n/gu) ?? [];
  if (new Set(matches).size > 1) return null;
  return { lines: text.split(/\r\n|\r|\n/u), newline: matches[0] ?? "\n" };
}

/**
 * `A -- 예 --> B`를 `A -->|예| B`로 바꾼다.
 * 링크 토큰 하나만 다루면 되도록 파싱 전에 한 번 정규화한다.
 *
 * 여는 토큰은 **정확히** `--`·`==`·`-.`여야 하고 앞이 공백이어야 한다.
 * 이 조건이 없으면 `L --- M -.-> N`의 `--- M -.->`를 한 덩어리로 삼켜
 * M이 라벨로 먹히고 노드와 간선이 통째로 사라진다. `---`는 그 자체로 완결된
 * 링크지 중간 라벨의 시작이 아니다.
 */
const MID_LABEL = /(?<=\s)(--|==|-\.)(?![-=.])[ \t]+([^\n|]+?)[ \t]+(-{2,}[>ox]?|={2,}[>ox]?|-\.+-[>ox]?|\.-+[>ox]?)/g;

export function normalizeMidLabels(line) {
  return line.replace(MID_LABEL, (match, start, text, end) => {
    const link = start === "-." ? `-.${end.replace(/^\./, "")}` : end;
    return `${link}|${text.trim()}|`;
  });
}

function lineStyleOf(body) {
  if (body.startsWith("-.")) return "dotted";
  if (body.startsWith("=")) return "thick";
  if (body.startsWith("~")) return "invisible";
  return "solid";
}

/**
 * 라벨이 다음 링크를 넘어가려는 자리인지 본다.
 *
 * 이게 없으면 `I[/입출력/] --> J[/사다리꼴\]`에서 사다리꼴(`[/`…`\]`)이 먼저 시도되어
 * 다음 노드의 `\]`까지 삼킨다. 라벨 하나가 문장 전체를 먹어 노드와 간선이 사라진다.
 * 라벨은 링크를 가로지를 수 없다.
 *
 * 앞에 공백이 붙은 링크만 본다. `A[a--b]`처럼 라벨 안의 붙임표는 그대로 둔다.
 */
const LINK_AHEAD = /^\s(?:-{2,}|={2,}|-\.|~{3,})/;

/** 라벨 본문을 읽는다. 따옴표로 감싼 라벨 안의 닫기 토큰은 무시한다. */
function readLabel(text, start, closeToken) {
  let index = start;
  let quoted = false;
  while (index < text.length) {
    const character = text[index];
    if (character === '"') quoted = !quoted;
    else if (!quoted && text.startsWith(closeToken, index)) {
      const raw = text.slice(start, index).trim();
      const label = raw.startsWith('"') && raw.endsWith('"') ? raw.slice(1, -1) : raw;
      return { label, end: index + closeToken.length };
    } else if (!quoted && LINK_AHEAD.test(text.slice(index))) {
      return null;
    }
    index += 1;
  }
  return null;
}

const EXPANDED_SHAPE = /^([A-Za-z0-9_][A-Za-z0-9_.-]*)@\{([^}]*)\}/;

/** `A@{ shape: cyl, label: "저장소" }` 형태를 읽는다. shape와 label만 허용한다. */
function readExpandedShape(text, start) {
  const match = text.slice(start).match(EXPANDED_SHAPE);
  if (match === null) return null;

  const [token, id, body] = match;
  const properties = {};
  for (const entry of body.split(",")) {
    if (entry.trim() === "") continue;
    const separator = entry.indexOf(":");
    if (separator < 0) return "invalid";
    const key = entry.slice(0, separator).trim();
    const rawValue = entry.slice(separator + 1).trim();
    if (key !== "shape" && key !== "label") return "invalid";
    properties[key] = rawValue.startsWith('"') && rawValue.endsWith('"')
      ? rawValue.slice(1, -1)
      : rawValue;
  }
  if (properties.shape === undefined) return "invalid";

  return {
    id,
    label: properties.label ?? null,
    shape: properties.shape,
    end: start + token.length,
  };
}

function readNodeReference(text, start) {
  const expanded = readExpandedShape(text, start);
  if (expanded === "invalid") return null;
  if (expanded !== null) return expanded;

  const rest = text.slice(start);
  const idMatch = rest.match(NODE_ID);
  if (idMatch === null) return null;

  const id = idMatch[0];
  const cursor = start + id.length;

  // 여는 토큰이 같고 닫기 토큰만 다른 모양이 있다(`[/.../]`와 `[/...\]`).
  // 닫기 토큰을 못 찾으면 다음 후보로 넘어가야 구분된다.
  let sawOpenToken = false;
  for (const shape of CLASSIC_SHAPES) {
    if (!text.startsWith(shape.open, cursor)) continue;
    sawOpenToken = true;
    const read = readLabel(text, cursor + shape.open.length, shape.close);
    if (read === null) continue;
    return { id, label: read.label, shape: shape.id, end: read.end };
  }

  // 여는 토큰은 있는데 어느 모양으로도 닫히지 않았다면 잘못 쓴 선언이다.
  // 이름만 쓴 참조로 넘기면 뒤쪽 문자가 조용히 사라진다.
  return sawOpenToken ? null : { id, label: null, shape: null, end: cursor };
}

/** `A[가] & B(나)` 같은 그룹을 읽는다. 최소 하나의 노드 참조가 있어야 한다. */
function readNodeGroup(line, start) {
  const ids = [];
  let cursor = start;

  for (;;) {
    while (line[cursor] === " " || line[cursor] === "\t") cursor += 1;
    const reference = readNodeReference(line, cursor);
    if (reference === null) return null;
    ids.push(reference);
    cursor = reference.end;

    let lookahead = cursor;
    while (line[lookahead] === " " || line[lookahead] === "\t") lookahead += 1;
    if (line[lookahead] !== "&") return { ids, end: cursor };
    cursor = lookahead + 1;
  }
}

/** 노드는 처음 나온 서브그래프에 속한다. 바깥에서 다시 언급해도 옮겨 가지 않는다. */
function isClaimedBySubgraph(graph, id) {
  return graph.subgraphs.some((group) => group.children.includes(id));
}

/** 그룹의 노드를 모델에 넣고, 현재 서브그래프의 자식으로도 등록한다. */
function registerGroup(graph, group, container) {
  const ids = [];
  for (const reference of group.ids) {
    upsertNode(graph, reference.id, { label: reference.label, shape: reference.shape });
    if (container !== null && !isClaimedBySubgraph(graph, reference.id)) {
      container.children.push(reference.id);
    }
    ids.push(reference.id);
  }
  return ids;
}

function readStyleProperties(body) {
  const properties = {};
  for (const entry of body.split(",")) {
    const separator = entry.indexOf(":");
    if (separator < 0) return null;
    properties[entry.slice(0, separator).trim()] = entry.slice(separator + 1).trim();
  }
  return properties;
}

function parseStatement(graph, rawLine, container) {
  const line = normalizeMidLabels(rawLine.trim());

  const firstGroup = readNodeGroup(line, 0);
  if (firstGroup === null) return failure("unsupported-syntax", rawLine);

  let previousIds = registerGroup(graph, firstGroup, container);
  let cursor = firstGroup.end;

  while (cursor < line.length) {
    while (line[cursor] === " " || line[cursor] === "\t") cursor += 1;
    if (cursor >= line.length) break;

    const linkMatch = line.slice(cursor).match(LINK);
    if (linkMatch === null) return failure("unsupported-syntax", rawLine);

    const [token, head, body, tail, label] = linkMatch;
    cursor += token.length;

    const group = readNodeGroup(line, cursor);
    if (group === null) return failure("unsupported-syntax", rawLine);
    const nextIds = registerGroup(graph, group, container);
    cursor = group.end;

    for (const from of previousIds) {
      for (const to of nextIds) {
        addEdge(graph, {
          from,
          to,
          label: label ?? null,
          line: lineStyleOf(body),
          arrow: ARROW_BY_TOKEN[tail] ?? "none",
          arrowHead: ARROW_BY_TOKEN[head] ?? "none",
        });
      }
    }
    previousIds = nextIds;
  }

  return { ok: true };
}

function commentTextOf(line) {
  return line.replace(/^%%+/, "").trim();
}

/**
 * `ci --> cd`처럼 묶음을 선의 끝점으로 쓰면 문장 파서가 그것을 노드로 만든다.
 * 묶음 선언이 어디에 나오든(앞이든 뒤든) 상관없이 걷어내려면 다 읽은 뒤에 정리해야 한다.
 *
 * 라벨이나 모양을 준 적 없는 — 이름만 언급되어 생긴 — 노드만 지운다.
 * 그림에는 그런 노드가 없으므로 남겨 두면 개수가 어긋나 비주얼 편집이 잠긴다.
 */
function dropSubgraphReferenceNodes(graph) {
  for (const group of graph.subgraphs) {
    const index = graph.nodes.findIndex((node) => node.id === group.id);
    if (index < 0) continue;

    const node = graph.nodes[index];
    if (node.label !== node.id || node.shape !== "rect" || node.color !== null) continue;

    // children은 건드리지 않는다. 중첩 묶음은 부모의 children에 자식 묶음 id를 담는 것으로
    // 표현하므로, 여기서 지우면 중첩 관계가 사라진다.
    graph.nodes.splice(index, 1);
  }
}

export function parseFlowchart(source) {
  const split = splitByNewline(source);
  if (split === null) return failure("mixed-newlines");
  const { containerPrefix, lines } = stripContainerPrefix(split.lines);
  let index = 0;
  const headerComments = [];

  while (index < lines.length) {
    const line = lines[index].trim();
    if (line !== "" && !line.startsWith("%%")) break;
    // `%%{init: ...}%%` 지시자는 테마·보안 설정을 덮어쓸 수 있다. 평범한 주석처럼
    // 다뤄서 조용히 왕복시키면 사용자 모르게 재구성 문법으로 바뀌어 나갈 수 있으니
    // 아예 거부한다.
    if (line.startsWith("%%{")) return failure("unsupported-syntax", line);
    if (line.startsWith("%%")) headerComments.push(commentTextOf(line));
    index += 1;
  }
  if (index >= lines.length) return failure("empty");

  const headerMatch = lines[index].trim().match(HEADER);
  if (headerMatch === null) return failure("not-a-flowchart");

  const graph = createEmptyGraph(
    headerMatch[1],
    DIRECTIONS.includes(headerMatch[2]) ? headerMatch[2] : "TB",
  );
  graph.format = { containerPrefix, newline: split.newline };
  index += 1;

  for (const text of headerComments) {
    graph.comments.push({ anchorKind: "header", anchorId: null, text });
  }

  const stack = [];
  let subgraphCounter = 0;
  // style·linkStyle은 모든 노드·엣지가 만들어진 뒤에야 대상을 찾을 수 있다.
  // 루프에서는 모아만 두고 마지막에 적용한다.
  const pendingStyles = [];
  // 주석은 뒤따르는 문장이 만든 대상에 붙인다. 붙일 문장이 없으면 문서 끝에 남는다.
  const pendingComments = [];

  for (; index < lines.length; index += 1) {
    const line = lines[index].trim();
    if (line === "") continue;

    if (line.startsWith("%%")) {
      if (line.startsWith("%%{")) return failure("unsupported-syntax", line);
      pendingComments.push(commentTextOf(line));
      continue;
    }

    if (line === "end") {
      if (stack.length === 0) return failure("unsupported-syntax", line);
      stack.pop();
      continue;
    }

    const subgraphMatch = line.match(SUBGRAPH);
    if (subgraphMatch !== null) {
      const header = subgraphMatch[1].trim();
      const titleMatch = header.match(SUBGRAPH_TITLE);
      let id;
      let title;
      if (titleMatch === null) {
        id = header;
        title = header;
      } else {
        id = titleMatch[1];
        const raw = titleMatch[2].trim();
        title = raw.startsWith('"') && raw.endsWith('"') ? raw.slice(1, -1) : raw;
      }
      if (id === "") {
        subgraphCounter += 1;
        id = `subgraph-${subgraphCounter}`;
      }

      const group = { id, title, direction: null, children: [] };
      graph.subgraphs.push(group);
      const parent = stack[stack.length - 1] ?? null;
      if (parent !== null && !parent.children.includes(id)) parent.children.push(id);
      stack.push(group);

      for (const text of pendingComments) {
        graph.comments.push({ anchorKind: "subgraph", anchorId: group.id, text });
      }
      pendingComments.length = 0;
      continue;
    }

    const container = stack[stack.length - 1] ?? null;

    const directionMatch = line.match(DIRECTION_LINE);
    if (directionMatch !== null) {
      if (container === null) return failure("unsupported-syntax", line);
      container.direction = directionMatch[1];
      continue;
    }

    const styleMatch = line.match(STYLE_LINE);
    if (styleMatch !== null) {
      pendingStyles.push({ kind: "node", target: styleMatch[1], body: styleMatch[2], line });
      continue;
    }

    const linkStyleMatch = line.match(LINK_STYLE_LINE);
    if (linkStyleMatch !== null) {
      pendingStyles.push({
        kind: "link",
        target: Number(linkStyleMatch[1]),
        body: linkStyleMatch[2],
        line,
      });
      continue;
    }
    if (/^linkStyle\b/.test(line)) return failure("unsupported-syntax", line);

    const classDefMatch = line.match(CLASS_DEF_LINE);
    if (classDefMatch !== null) {
      graph.classDefs.push({ name: classDefMatch[1], body: classDefMatch[2] });
      continue;
    }

    const classMatch = line.match(CLASS_LINE);
    if (classMatch !== null) {
      for (const nodeId of classMatch[1].split(",").map((value) => value.trim())) {
        if (nodeId !== "") graph.classUses.push({ nodeId, className: classMatch[2] });
      }
      continue;
    }

    const nodeCountBefore = graph.nodes.length;
    const edgeCountBefore = graph.edges.length;
    const result = parseStatement(graph, line, container);
    if (!result.ok) return result;

    if (pendingComments.length > 0) {
      const anchor = graph.edges.length > edgeCountBefore
        ? { anchorKind: "edge", anchorId: graph.edges[edgeCountBefore].id }
        : graph.nodes.length > nodeCountBefore
          ? { anchorKind: "node", anchorId: graph.nodes[nodeCountBefore].id }
          : { anchorKind: "trailing", anchorId: null };
      for (const text of pendingComments) graph.comments.push({ ...anchor, text });
      pendingComments.length = 0;
    }
  }

  if (stack.length > 0) return failure("unclosed-subgraph");

  dropSubgraphReferenceNodes(graph);

  for (const text of pendingComments) {
    graph.comments.push({ anchorKind: "trailing", anchorId: null, text });
  }

  for (const style of pendingStyles) {
    const properties = readStyleProperties(style.body);
    if (properties === null) return failure("unsupported-syntax", style.line);

    if (style.kind === "node") {
      const node = graph.nodes.find((candidate) => candidate.id === style.target);
      if (node === undefined) return failure("unsupported-syntax", style.line);
      const colour = paletteIdForNodeStyle({
        fill: properties.fill,
        stroke: properties.stroke,
        color: properties.color,
      });
      if (colour === null) return failure("unsupported-colour", style.line);
      node.color = colour;
      continue;
    }

    const edge = graph.edges[style.target];
    if (edge === undefined) return failure("unsupported-syntax", style.line);
    const colour = paletteIdForLinkStroke(properties.stroke);
    if (colour === null) return failure("unsupported-colour", style.line);
    edge.color = colour;
  }

  return { ok: true, graph };
}
