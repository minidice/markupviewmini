import { addEdge, createEmptyGraph, DIRECTIONS, upsertNode } from "./graph-model.js";
import { paletteIdForNodeStyle } from "./palette.js";

const HEADER = /^(flowchart|graph)(?:\s+(TD|TB|LR|RL|BT))?\s*$/;
const NODE_ID = /^[A-Za-z0-9_][A-Za-z0-9_.-]*/;
const LINK = /^(-->|---|==>|===|-\.->|~~~)(?:\|([^|]*)\|)?/;
const STYLE = /^style\s+([A-Za-z0-9_][A-Za-z0-9_.-]*)\s+fill:(#[a-f\d]{6}),stroke:(#[a-f\d]{6}),color:(#[a-f\d]{6})$/iu;

export function failure(reason, detail = null) {
  return { ok: false, reason, detail };
}

function readNode(text, start) {
  const idMatch = text.slice(start).match(NODE_ID);
  if (idMatch === null) return null;
  const id = idMatch[0];
  let end = start + id.length;
  const open = text[end];
  if (open !== "[" && open !== "(" && open !== "{") {
    return { id, label: null, shape: null, explicit: false, end };
  }
  const close = open === "[" ? "]" : open === "(" ? ")" : "}";
  const quoted = text[end + 1] === '"';
  const quoteAt = quoted ? text.indexOf('"', end + 2) : -1;
  const closeAt = quoted
    ? (quoteAt >= 0 && text[quoteAt + 1] === close ? quoteAt + 1 : -1)
    : text.indexOf(close, end + 1);
  if (closeAt < 0) return null;
  const rawLabel = quoted
    ? text.slice(end + 2, quoteAt)
    : text.slice(end + 1, closeAt);
  end = closeAt + 1;
  return {
    id,
    label: rawLabel.trim(),
    shape: open === "(" ? "round" : open === "{" ? "diamond" : "rect",
    explicit: true,
    end,
  };
}

function recordNode(graph, node, lineIndex, lineOffset, start) {
  const occurrence = {
    lineIndex,
    lineOffset,
    start: lineOffset + start,
    end: lineOffset + node.end,
  };
  graph.syntax.firstReferences[node.id] ??= occurrence;
  if (node.explicit) {
    if (graph.syntax.declarations[node.id] !== undefined) {
      return failure("ambiguous-node-declaration", node.id);
    }
    graph.syntax.declarations[node.id] = occurrence;
  }
  upsertNode(graph, node.id, node);
  return { ok: true };
}

function parseStatement(graph, rawLine, lineIndex, lineOffset) {
  let cursor = 0;
  const first = readNode(rawLine, cursor);
  if (first === null) return failure("unsupported-syntax", rawLine);
  const firstRecorded = recordNode(graph, first, lineIndex, lineOffset, cursor);
  if (!firstRecorded.ok) return firstRecorded;
  let previous = first.id;
  cursor = first.end;

  while (cursor < rawLine.length) {
    while (/\s/.test(rawLine[cursor] ?? "")) cursor += 1;
    if (cursor === rawLine.length) break;
    const link = rawLine.slice(cursor).match(LINK);
    if (link === null) return failure("unsupported-syntax", rawLine);
    cursor += link[0].length;
    while (/\s/.test(rawLine[cursor] ?? "")) cursor += 1;
    const next = readNode(rawLine, cursor);
    if (next === null) return failure("unsupported-syntax", rawLine);
    const nextRecorded = recordNode(graph, next, lineIndex, lineOffset, cursor);
    if (!nextRecorded.ok) return nextRecorded;
    addEdge(graph, {
      from: previous,
      to: next.id,
      label: link[2] === undefined ? null : link[2],
      line: link[1] === "~~~" ? "invisible" : link[1].includes("=", 0) ? "thick" : link[1].includes(".", 0) ? "dotted" : "solid",
      arrow: link[1] === "---" || link[1] === "===" || link[1] === "~~~" ? "none" : "arrow",
    });
    previous = next.id;
    cursor = next.end;
  }
  return { ok: true };
}

function longestCommonPrefix(lines) {
  if (lines.length === 0) return "";
  let prefix = lines[0];
  for (const line of lines.slice(1)) {
    let index = 0;
    while (index < prefix.length && index < line.length && prefix[index] === line[index]) index += 1;
    prefix = prefix.slice(0, index);
  }
  return prefix;
}

function readFormat(source) {
  const newline = source.match(/\r\n|\r|\n/u)?.[0] ?? "\n";
  const trailingNewline = /(?:\r\n|\r|\n)$/u.test(source);
  const physicalLines = [];
  let start = 0;
  for (const match of source.matchAll(/\r\n|\r|\n/gu)) {
    physicalLines.push({ text: source.slice(start, match.index), newline: match[0] });
    start = match.index + match[0].length;
  }
  if (start < source.length || physicalLines.length === 0) {
    physicalLines.push({ text: source.slice(start), newline: "" });
  }
  const mixedNewlines = new Set(
    physicalLines.map((line) => line.newline).filter((lineNewline) => lineNewline !== ""),
  ).size > 1;
  const common = longestCommonPrefix(physicalLines.map((line) => line.text).filter((line) => line.trim() !== ""));
  const containerPrefix = common.match(/^(?:(?:[ \t]*>[ \t]*)+|[ \t]+)/u)?.[0] ?? "";
  const logicalLines = physicalLines.map((line) => line.text === "" || containerPrefix === ""
    ? line.text
    : line.text.slice(containerPrefix.length));
  return {
    logicalLines,
    format: { containerPrefix, newline, trailingNewline, physicalLines },
    mixedNewlines,
  };
}

export function parseFlowchart(source) {
  const { logicalLines: lines, format, mixedNewlines } = readFormat(String(source ?? ""));
  if (mixedNewlines) return failure("mixed-newlines");
  let index = 0;
  const headerComments = [];
  while (index < lines.length && (lines[index].trim() === "" || lines[index].trim().startsWith("%%"))) {
    if (lines[index].trim().startsWith("%%{")) return failure("unsupported-syntax", lines[index].trim());
    if (lines[index].trim().startsWith("%%")) {
      const marker = lines[index].indexOf("%%");
      headerComments.push(lines[index].slice(marker + 2));
    }
    index += 1;
  }
  if (index === lines.length) return failure("empty");
  const header = lines[index].trim().match(HEADER);
  if (header === null) return failure("not-a-flowchart", lines[index]);
  const graph = createEmptyGraph(header[1], DIRECTIONS.includes(header[2]) ? header[2] : "TB", format);
  graph.syntax = {
    header: {
      lineIndex: index,
      lineOffset: format.containerPrefix.length + lines[index].search(/\S/u),
      keywordLength: header[1].length,
      directionOffset: header[2] === undefined ? null : lines[index].trim().indexOf(header[2]),
    },
    declarations: Object.create(null),
    firstReferences: Object.create(null),
    styles: Object.create(null),
    original: null,
  };
  graph.comments.push(...headerComments.map((text) => ({ anchorKind: "header", anchorId: null, text })));
  index += 1;

  for (; index < lines.length; index += 1) {
    const logicalLine = lines[index];
    const line = logicalLine.trim();
    const lineOffset = format.containerPrefix.length + logicalLine.search(/\S/u);
    if (line === "") continue;
    if (line.startsWith("%%")) {
      if (line.startsWith("%%{")) return failure("unsupported-syntax", line);
      graph.comments.push({ anchorKind: "trailing", anchorId: null, text: line.slice(2) });
      continue;
    }
    const style = line.match(STYLE);
    if (style !== null) {
      const node = graph.nodes.find((candidate) => candidate.id === style[1]);
      const color = paletteIdForNodeStyle({ fill: style[2], stroke: style[3], color: style[4] });
      if (node === undefined || color === null) return failure("unsupported-colour", line);
      if (graph.syntax.styles[style[1]] !== undefined) {
        return failure("ambiguous-node-style", style[1]);
      }
      graph.syntax.styles[style[1]] = { lineIndex: index, start: lineOffset, end: lineOffset + line.length };
      node.color = color;
      continue;
    }
    if (/^(subgraph|end|direction|style|linkStyle|classDef|class)\b/.test(line)) {
      return failure("unsupported-syntax", line);
    }
    const parsed = parseStatement(graph, line, index, lineOffset);
    if (!parsed.ok) return parsed;
  }
  graph.syntax.original = {
    direction: graph.direction,
    nodes: Object.fromEntries(graph.nodes.map((node) => [node.id, {
      label: node.label,
      shape: node.shape,
      color: node.color,
    }])),
  };
  return { ok: true, graph };
}
