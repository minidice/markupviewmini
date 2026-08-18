import { describe, expect, it } from "vitest";
import { parseFlowchart } from "./flowchart-parser.js";
import { serializeFlowchart } from "./flowchart-serializer.js";
import { setNodeLabel } from "./graph-model.js";

function parseOk(code) {
  const result = parseFlowchart(code);
  expect(result.ok, result.ok ? "" : `파싱 실패: ${result.reason} ${result.detail ?? ""}`).toBe(true);
  return result.graph;
}

/** 엣지 id는 파싱할 때마다 새로 매겨지므로 왕복 비교 전에 순번으로 바꾼다. */
function normalizeIds(graph) {
  return {
    ...graph,
    edges: graph.edges.map((edge, index) => ({ ...edge, id: `e${index}` })),
    comments: graph.comments.map((comment) => ({ ...comment, anchorId: null })),
  };
}

describe("serializeFlowchart", () => {
  it("writes the header with the original keyword and direction", () => {
    const graph = parseOk("graph LR\n  A --> B");

    expect(serializeFlowchart(graph).split("\n")[0]).toBe("graph LR");
  });

  it("indents statements with two spaces", () => {
    expect(serializeFlowchart(parseOk("flowchart TD\n  A[가] --> B")))
      .toBe("flowchart TD\n  A[가]\n  B[B]\n  A --> B\n");
  });

  it("writes every node shape back in its own syntax", () => {
    const graph = parseOk("flowchart TD\n  A([가]) --> B{{나}}");
    const code = serializeFlowchart(graph);

    expect(code).toContain("A([가])");
    expect(code).toContain("B{{나}}");
  });

  it("writes an expanded shape back in the @{ } form", () => {
    const code = serializeFlowchart(parseOk('flowchart TD\n  A@{ shape: cyl, label: "저장소" }\n  A --> B'));

    expect(code).toContain('A@{ shape: cyl, label: "저장소" }');
  });

  it("always writes edge labels in the pipe form", () => {
    expect(serializeFlowchart(parseOk("flowchart TD\n  A -- 예 --> B"))).toContain("A -->|예| B");
  });

  it("writes line styles and arrow ends", () => {
    const graph = parseOk("flowchart TD\n  A -.-> B\n  B ==> C\n  C --o D\n  D <--> E\n  E ~~~ F");
    const code = serializeFlowchart(graph);

    expect(code).toContain("A -.-> B");
    expect(code).toContain("B ==> C");
    expect(code).toContain("C --o D");
    expect(code).toContain("D <--> E");
    expect(code).toContain("E ~~~ F");
  });

  /*
   * 끝에 화살표가 없으면 링크를 닫을 글자가 하나 더 필요하다.
   * `A -- B`는 mermaid가 링크로 읽지 않아 그 줄이 통째로 사라지고,
   * 그림과 모델의 개수가 어긋나 비주얼 편집이 잠긴다.
   */
  it("closes an arrowless link with a third character", () => {
    const graph = parseOk("flowchart TD\n  A --- B\n  B === C\n  C -.- D");
    const code = serializeFlowchart(graph);

    expect(code).toContain("A --- B");
    expect(code).toContain("B === C");
    expect(code).toContain("C -.- D");
    expect(code).not.toContain("A -- B");
    expect(code).not.toContain("B == C");
  });

  it("round-trips every link style", () => {
    const source = "flowchart TD\n  A --- B\n  B -.-> C\n  C ==> D\n  D --o E\n  E --x F\n  F <--> G\n  G ~~~ H\n  H === I\n  I -.- J";
    const first = parseOk(source);

    const second = parseOk(serializeFlowchart(first));

    expect(second.edges.map((edge) => [edge.line, edge.arrow, edge.arrowHead]))
      .toEqual(first.edges.map((edge) => [edge.line, edge.arrow, edge.arrowHead]));
  });

  it("writes subgraphs with their title, direction, and members", () => {
    const code = serializeFlowchart(parseOk([
      "flowchart TD",
      "  subgraph ci [빌드]",
      "    direction LR",
      "    A --> B",
      "  end",
    ].join("\n")));

    expect(code).toContain("  subgraph ci [빌드]");
    expect(code).toContain("    direction LR");
    expect(code).toContain("  end");
  });

  it("recomputes linkStyle indexes from the current edge order", () => {
    const graph = parseOk([
      "flowchart TD",
      "  A --> B",
      "  B --> C",
      "  C --> D",
      "  linkStyle 2 stroke:#d32f2f,stroke-width:2px",
    ].join("\n"));

    graph.edges.splice(0, 1); // 첫 선을 지운다

    expect(serializeFlowchart(graph)).toContain("linkStyle 1 stroke:#d32f2f,stroke-width:2px");
  });

  it("writes no style statement for a default colour", () => {
    const graph = parseOk("flowchart TD\n  A --> B\n  style A fill:#e8f5e9,stroke:#2e7d32,color:#1b5e20");
    graph.nodes[0].color = null;

    expect(serializeFlowchart(graph)).not.toContain("style A");
  });

  it("writes comments before the statement they belong to", () => {
    const code = serializeFlowchart(parseOk("flowchart TD\n  %% 시작\n  A --> B"));

    expect(code).toContain("  %% 시작\n  A --> B");
  });

  it("writes classDef and class statements back", () => {
    const code = serializeFlowchart(parseOk([
      "flowchart TD",
      "  A --> B",
      "  classDef warn fill:#f96,stroke:#333",
      "  class A warn",
    ].join("\n")));

    expect(code).toContain("  classDef warn fill:#f96,stroke:#333");
    expect(code).toContain("  class A warn");
  });
});

describe("serializeFlowchart — 마크다운 컨테이너 들여쓰기·줄바꿈 (MarkUpViewMini 전용)", () => {
  // Break caught: 마크다운 목록/인용문 안에 들여써진 mermaid 블록을 편집하면, 헤더 줄이
  // 들여쓰기 0으로 다시 쓰여 목록/인용문 구조 밖으로 빠져나간다.
  it("re-indents every line, including the header, with the original container prefix", () => {
    const graph = parseOk("  flowchart LR\n  A --> B");
    setNodeLabel(graph, "A", "Changed");

    const code = serializeFlowchart(graph);

    expect(code).toBe("  flowchart LR\n    A[Changed]\n    B[B]\n    A --> B\n");
    expect(code.split("\n").filter((line) => line !== "").every((line) => line.startsWith("  "))).toBe(true);
  });

  it("restores the original CRLF newline style", () => {
    const graph = parseOk("flowchart LR\r\nA --> B");
    setNodeLabel(graph, "A", "Changed");

    const code = serializeFlowchart(graph);

    expect(code).toBe("flowchart LR\r\n  A[Changed]\r\n  B[B]\r\n  A --> B\r\n");
  });

  it("round-trips an indented block through parse → serialize → parse", () => {
    const first = parseOk("> flowchart LR\r\n> A[가] --> B[나]");
    const second = parseOk(serializeFlowchart(first));

    expect(normalizeIds(second)).toEqual(normalizeIds(first));
  });
});

describe("왕복", () => {
  it.each([
    "flowchart TD\n  A --> B",
    "graph LR\n  A[가] -->|예| B{나}\n  B -.->|아니오| C((다))",
    "flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end\n  B --> C",
    "flowchart TD\n  A & B --> C",
  ])("parse → serialize → parse keeps the same graph for %s", (code) => {
    const first = parseOk(code);
    const second = parseOk(serializeFlowchart(first));

    expect(normalizeIds(second)).toEqual(normalizeIds(first));
  });
});
