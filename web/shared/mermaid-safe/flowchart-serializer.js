import { PALETTE_BY_ID } from "./palette.js";

function declaration(node) {
  const label = /[\[\](){}|]/.test(node.label) ? `"${node.label}"` : node.label;
  if (node.shape === "round") return `${node.id}(${label})`;
  if (node.shape === "diamond") return `${node.id}{${label}}`;
  return `${node.id}[${label}]`;
}

function link(edge) {
  if (edge.line === "invisible") return "~~~";
  if (edge.line === "dotted") return "-.->";
  const line = edge.line === "thick" ? "==" : edge.line === "dotted" ? "-." : "--";
  const suffix = edge.arrow === "none" ? line.at(-1) : ">";
  const label = edge.label == null || edge.label === "" ? "" : `|${edge.label}|`;
  return `${line}${suffix}${label}`;
}

function styleDeclaration(node) {
  const colour = PALETTE_BY_ID.get(node.color);
  return `style ${node.id} fill:${colour.fill},stroke:${colour.stroke},color:${colour.text}`;
}

function serializeFromSyntax(graph) {
  const physicalLines = graph.format.physicalLines.map((line) => ({ ...line }));
  const edits = new Map();
  const insertions = new Map();
  const edit = (lineIndex, start, end, value) => {
    const lineEdits = edits.get(lineIndex) ?? [];
    lineEdits.push({ start, end, value });
    edits.set(lineIndex, lineEdits);
  };

  if (graph.direction !== graph.syntax.original.direction) {
    const header = graph.syntax.header;
    if (header.directionOffset === null) {
      const at = header.lineOffset + header.keywordLength;
      edit(header.lineIndex, at, at, ` ${graph.direction}`);
    } else {
      const start = header.lineOffset + header.directionOffset;
      edit(header.lineIndex, start, start + graph.syntax.original.direction.length, graph.direction);
    }
  }

  for (const node of graph.nodes) {
    const original = graph.syntax.original.nodes[node.id];
    if (original === undefined) continue;
    if (node.label !== original.label || node.shape !== original.shape) {
      const occurrence = graph.syntax.declarations[node.id] ?? graph.syntax.firstReferences[node.id];
      edit(occurrence.lineIndex, occurrence.start, occurrence.end, declaration(node));
    }

    if (node.color === original.color) continue;
    const existingStyle = graph.syntax.styles[node.id];
    if (existingStyle !== undefined) {
      edit(
        existingStyle.lineIndex,
        existingStyle.start,
        existingStyle.end,
        node.color === null ? "" : styleDeclaration(node),
      );
      continue;
    }
    if (node.color !== null) {
      const occurrence = graph.syntax.declarations[node.id] ?? graph.syntax.firstReferences[node.id];
      const lines = insertions.get(occurrence.lineIndex) ?? [];
      const physicalPrefix = graph.format.physicalLines[occurrence.lineIndex].text
        .slice(0, occurrence.lineOffset);
      lines.push(`${physicalPrefix}${styleDeclaration(node)}`);
      insertions.set(occurrence.lineIndex, lines);
    }
  }

  for (const [lineIndex, lineEdits] of edits) {
    lineEdits.sort((left, right) => right.start - left.start);
    for (const replacement of lineEdits) {
      const line = physicalLines[lineIndex];
      line.text = `${line.text.slice(0, replacement.start)}${replacement.value}${line.text.slice(replacement.end)}`;
    }
  }

  const output = [];
  const defaultNewline = graph.format.newline ?? "\n";
  for (let index = 0; index < physicalLines.length; index += 1) {
    const line = physicalLines[index];
    const added = insertions.get(index) ?? [];
    if (added.length === 0) {
      output.push(`${line.text}${line.newline}`);
      continue;
    }
    const separator = line.newline || defaultNewline;
    output.push(`${line.text}${separator}`);
    added.forEach((inserted, addedIndex) => {
      const finalInserted = addedIndex === added.length - 1;
      output.push(`${inserted}${finalInserted ? line.newline : separator}`);
    });
  }
  return output.join("");
}

export function serializeFlowchart(graph) {
  if (graph.syntax?.original != null && Array.isArray(graph.format?.physicalLines)) {
    return serializeFromSyntax(graph);
  }
  const headerComments = graph.comments.filter((comment) => comment.anchorKind === "header");
  const trailingComments = graph.comments.filter((comment) => comment.anchorKind === "trailing");
  const lines = [
    ...headerComments.map((comment) => `%%${comment.text}`),
    `${graph.keyword} ${graph.direction}`,
    ...graph.nodes.map((node) => `  ${declaration(node)}`),
    ...graph.edges.map((edge) => `  ${edge.from} ${link(edge)} ${edge.to}`),
    ...graph.nodes.filter((node) => node.color !== null).map((node) => {
      return `  ${styleDeclaration(node)}`;
    }),
    ...trailingComments.map((comment) => `  %%${comment.text}`),
  ];
  const format = graph.format ?? { containerPrefix: "", newline: "\n", trailingNewline: true };
  const serialized = lines.map((line) => `${format.containerPrefix}${line}`).join(format.newline);
  return format.trailingNewline ? `${serialized}${format.newline}` : serialized;
}
