import { describe, expect, it } from "vitest";
import { analyzeMermaidSource } from "./analyze-mermaid-source.js";
import { serializeFlowchart } from "./flowchart-serializer.js";
import * as GraphModel from "./graph-model.js";

// 이 파서/직렬화기는 원본 텍스트를 부분 패치하지 않고, 파싱된 모델에서 매번 mermaid
// 문법을 통째로 다시 구성한다(MinisTool 아키텍처 이식, 2026-08). 그래서 "건드리지 않은
// 줄은 원본 그대로 남는다"는 예전의 바이트 단위 트리비아 보존 보장은 더 이상 없다 -
// 빈 줄, 문장 사이 주석 위치, 줄마다 다른 들여쓰기 표현 등은 표준 형태로 재정렬된다.
// 유지되는 안전 보장은: (1) 시맨틱 손실 없음(파싱 가능한 건 다시 파싱 가능),
// (2) 블록 전체에 공통으로 걸린 마크다운 목록/인용문 들여쓰기, (3) 원래 줄바꿈 종류.
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

  it("keeps node/edge semantics through a mutation even without byte-exact trivia", () => {
    const source = [
      "flowchart LR",
      "  %% before nodes",
      "  A(Round) --> B{Decision}",
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
    const reparsed = analyzeMermaidSource(serialized);
    expect(reparsed.supported).toBe(true);
    expect(reparsed.model.nodes.find((node) => node.id === "A")).toMatchObject({
      label: "Changed", shape: "round", color: "blue",
    });
    expect(reparsed.model.nodes.find((node) => node.id === "B")).toMatchObject({
      label: "Decision", shape: "diamond",
    });
    expect(reparsed.model.edges.map(({ from, to }) => [from, to])).toEqual([
      ["A", "B"], ["A", "C"], ["B", "D"],
    ]);
    expect(serialized).toContain("%% before nodes");
    expect(serialized).toContain("%% between edges");
    expect(serialized).toContain("%% after style");
  });

  it("uses the last explicit declaration when a node is declared more than once, matching Mermaid itself", () => {
    // 예전 패치 기반 아키텍처는 "어느 선언 위치를 고쳐야 하는지" 모호해서 이런 소스를
    // 통째로 거부했다. 지금은 모델을 통째로 다시 구성하므로 그 모호함이 없다 - 실제
    // mermaid도 나중 선언이 이긴다.
    const result = analyzeMermaidSource([
      "flowchart LR",
      "  A[First] --> B",
      "  A(Round) --> C",
    ].join("\n"));

    expect(result.supported).toBe(true);
    expect(result.model.nodes.find((node) => node.id === "A")).toMatchObject({
      label: "Round", shape: "round",
    });
  });

  it("fails closed for mixed physical newline kinds that Core cannot validate losslessly", () => {
    // Break caught: the dialog reports support for a source whose replacement Core must refuse.
    const result = analyzeMermaidSource("flowchart LR\r\nA --> B\nB --> C");

    expect(result).toMatchObject({ supported: false, model: null, reason: "mixed-newlines" });
  });
});
