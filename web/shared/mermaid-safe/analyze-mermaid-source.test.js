import { describe, expect, it } from "vitest";
import { analyzeMermaidSource } from "./analyze-mermaid-source.js";
import { serializeFlowchart } from "./flowchart-serializer.js";
import * as GraphModel from "./graph-model.js";

describe("analyzeMermaidSource", () => {
  it("locks unsupported syntax without returning a partial graph", () => {
    const result = analyzeMermaidSource(`sequenceDiagram
A->>B: hi`);
    expect(result.supported).toBe(false);
    expect(result.model).toBeNull();
    expect(result.reason).toMatch(/flowchart/i);
  });

  it("round-trips a supported flowchart without semantic loss", () => {
    const source = `flowchart LR
  A[Read] --> B[Edit]`;
    const parsed = analyzeMermaidSource(source);
    expect(parsed.supported).toBe(true);
    expect(analyzeMermaidSource(serializeFlowchart(parsed.model)).supported).toBe(true);
  });

  it("fails closed for Mermaid directives instead of treating them as comments", () => {
    const result = analyzeMermaidSource(`%%{init: { "theme": "dark" }}%%
flowchart LR
A --> B`);

    expect(result).toMatchObject({ supported: false, model: null });
    expect(result.reason).toBe("unsupported-syntax");
  });

  it.each([
    ["dotted", "A -.-> B", "-.->"],
    ["invisible", "A ~~~ B", "~~~"],
  ])("round-trips %s edges with their Mermaid token", (_name, statement, token) => {
    const parsed = analyzeMermaidSource(`flowchart LR\n${statement}`);

    expect(parsed.supported).toBe(true);
    const serialized = serializeFlowchart(parsed.model);
    expect(serialized).toContain(`A ${token} B`);
    expect(analyzeMermaidSource(serialized)).toMatchObject({ supported: true });
  });

  it("preserves the physical container prefix, newline, and comments through a model mutation", () => {
    const source = [
      "  %% before",
      "  flowchart LR",
      "  A[Read] --> B[Edit]",
      "  %% after",
    ].join("\r\n");
    const parsed = analyzeMermaidSource(source);

    expect(parsed.supported).toBe(true);
    expect(GraphModel.setNodeLabel(parsed.model, "A", "Write")).toBe(true);
    const serialized = serializeFlowchart(parsed.model);

    expect(serialized).toContain("  %% before\r\n");
    expect(serialized).toContain("  %% after");
    expect(serialized.split("\r\n").every((line) => line === "" || line.startsWith("  "))).toBe(true);
    expect(serialized).not.toMatch(/(?<!\r)\n/u);
    expect(analyzeMermaidSource(serialized)).toMatchObject({ supported: true });
  });

  it("serializes only the approved node-first visual mutations", () => {
    const parsed = analyzeMermaidSource(`%% keep
flowchart LR
A[Read] --> B[Edit]`);

    expect(GraphModel.setNodeLabel(parsed.model, "A", "Write")).toBe(true);
    expect(GraphModel.setNodeShape(parsed.model, "A", "round")).toBe(true);
    expect(GraphModel.setNodeColor(parsed.model, "A", "blue")).toBe(true);
    expect(GraphModel.setDirection(parsed.model, "RL")).toBe(true);

    const serialized = serializeFlowchart(parsed.model);
    expect(serialized).toContain("flowchart RL");
    expect(serialized).toContain("A(Write)");
    expect(serialized).toContain("style A fill:#e3f2fd,stroke:#1565c0,color:#0d3c74");
    expect(serialized).toContain("%% keep");
    expect(analyzeMermaidSource(serialized)).toMatchObject({ supported: true });
  });

  it("preserves explicit repeated-reference semantics and exact trivia around an intended mutation", () => {
    // Break caught: a naked edge reference resets an earlier round/diamond declaration and
    // canonical serialization moves comments and deletes blank lines.
    const source = [
      "flowchart LR",
      "  %% before nodes",
      "  A(Round) --> B{Decision}",
      "",
      "  %% between edges",
      "  A --> C",
      "  B --> D",
      "  style A fill:#e3f2fd,stroke:#1565c0,color:#0d3c74",
      "  %% after style",
    ].join("\n");
    const parsed = analyzeMermaidSource(source);

    expect(parsed.supported).toBe(true);
    expect(parsed.model.nodes.find((node) => node.id === "A")).toMatchObject({
      label: "Round", shape: "round", color: "blue",
    });
    expect(parsed.model.nodes.find((node) => node.id === "B")).toMatchObject({
      label: "Decision", shape: "diamond",
    });
    expect(GraphModel.setNodeLabel(parsed.model, "A", "Changed")).toBe(true);

    const serialized = serializeFlowchart(parsed.model);
    expect(serialized).toBe([
      "flowchart LR",
      "  %% before nodes",
      "  A(Changed) --> B{Decision}",
      "",
      "  %% between edges",
      "  A --> C",
      "  B --> D",
      "  style A fill:#e3f2fd,stroke:#1565c0,color:#0d3c74",
      "  %% after style",
    ].join("\n"));
    const reparsed = analyzeMermaidSource(serialized);
    expect(reparsed.model.nodes.find((node) => node.id === "A")).toMatchObject({
      label: "Changed", shape: "round", color: "blue",
    });
    expect(reparsed.model.nodes.find((node) => node.id === "B")).toMatchObject({
      label: "Decision", shape: "diamond",
    });
    expect(reparsed.model.edges.map(({ from, to }) => [from, to])).toEqual([
      ["A", "B"], ["A", "C"], ["B", "D"],
    ]);
  });

  it("fails closed when repeated explicit declarations make lossless mutation ambiguous", () => {
    // Break caught: visual mutation silently chooses between conflicting declaration sites.
    const result = analyzeMermaidSource([
      "flowchart LR",
      "  A[First] --> B",
      "  A(Round) --> C",
    ].join("\n"));

    expect(result).toMatchObject({ supported: false, model: null, reason: "ambiguous-node-declaration" });
  });

  it.each([
    ["list", "    ", "\t"],
    ["nested list", "        ", "\t\t"],
    ["blockquote", ">   ", ">\t"],
  ])("preserves mixed equivalent tab/space %s prefixes and CRLF for an inserted style", (
    _name,
    headerPrefix,
    statementPrefix,
  ) => {
    // Break caught: a character-LCP prefix collapses to empty and a new style escapes the
    // Markdown container or normalizes CRLF to LF.
    const source = [
      `${headerPrefix}flowchart LR`,
      `${statementPrefix}A(Round) --> B{Decision}`,
      `${headerPrefix}A --> C`,
    ].join("\r\n");
    const parsed = analyzeMermaidSource(source);

    expect(parsed.supported).toBe(true);
    expect(GraphModel.setNodeColor(parsed.model, "B", "blue")).toBe(true);
    expect(serializeFlowchart(parsed.model)).toBe([
      `${headerPrefix}flowchart LR`,
      `${statementPrefix}A(Round) --> B{Decision}`,
      `${statementPrefix}style B fill:#e3f2fd,stroke:#1565c0,color:#0d3c74`,
      `${headerPrefix}A --> C`,
    ].join("\r\n"));
  });

  it("fails closed for mixed physical newline kinds that Core cannot validate losslessly", () => {
    // Break caught: the dialog reports support for a source whose replacement Core must refuse.
    const result = analyzeMermaidSource("flowchart LR\r\nA --> B\nB --> C");

    expect(result).toMatchObject({ supported: false, model: null, reason: "mixed-newlines" });
  });

  it.each([
    ["list", "    flowchart LR\n  A(Round) --> B", "    flowchart RL\n  A(Round) --> B"],
    ["nested list", "        flowchart LR\n    A(Round) --> B", "        flowchart RL\n    A(Round) --> B"],
    ["blockquote", ">   flowchart LR\n> A(Round) --> B", ">   flowchart RL\n> A(Round) --> B"],
    ["mixed list", "        flowchart LR\r\n\tA(Round) --> B", "        flowchart RL\r\n\tA(Round) --> B"],
    ["mixed blockquote", ">     flowchart LR\r\n>\tA(Round) --> B", ">     flowchart RL\r\n>\tA(Round) --> B"],
  ])("preserves %s structural context while editing an extra-indented header token", (
    _name,
    source,
    expected,
  ) => {
    const parsed = analyzeMermaidSource(source);

    expect(parsed.supported).toBe(true);
    expect(GraphModel.setDirection(parsed.model, "RL")).toBe(true);
    expect(serializeFlowchart(parsed.model)).toBe(expected);
  });
});
