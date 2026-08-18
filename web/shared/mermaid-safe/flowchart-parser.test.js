import { describe, expect, it } from "vitest";
import { parseFlowchart } from "./flowchart-parser.js";

function parseOk(code) {
  const result = parseFlowchart(code);
  expect(result.ok, result.ok ? "" : `파싱 실패: ${result.reason}`).toBe(true);
  return result.graph;
}

describe("parseFlowchart — 헤더", () => {
  it("reads the keyword and the direction", () => {
    const graph = parseOk("flowchart TD\n  A --> B");

    expect(graph.keyword).toBe("flowchart");
    expect(graph.direction).toBe("TD");
  });

  it("keeps the graph keyword when the document uses it", () => {
    expect(parseOk("graph LR\n  A --> B").keyword).toBe("graph");
    expect(parseOk("graph LR\n  A --> B").direction).toBe("LR");
  });

  it("accepts every direction", () => {
    for (const direction of ["TD", "TB", "LR", "RL", "BT"]) {
      expect(parseOk(`flowchart ${direction}\n  A --> B`).direction).toBe(direction);
    }
  });

  it("defaults to TB when the header has no direction", () => {
    expect(parseOk("flowchart\n  A --> B").direction).toBe("TB");
  });

  it("skips blank lines and comments before the header", () => {
    expect(parseOk("\n%% 메모\n\nflowchart TD\n  A --> B").direction).toBe("TD");
  });

  it("rejects a document that is not a flowchart", () => {
    const result = parseFlowchart("sequenceDiagram\n  A->>B: hi");

    expect(result.ok).toBe(false);
    expect(result.reason).toBe("not-a-flowchart");
  });

  it("rejects an empty document", () => {
    expect(parseFlowchart("   \n\n").ok).toBe(false);
  });
});

describe("parseFlowchart — 노드와 연결", () => {
  it("reads a bare edge and creates both nodes", () => {
    const graph = parseOk("flowchart TD\n  A --> B");

    expect(graph.nodes).toEqual([
      { id: "A", label: "A", shape: "rect", color: null },
      { id: "B", label: "B", shape: "rect", color: null },
    ]);
    expect(graph.edges).toHaveLength(1);
    expect(graph.edges[0]).toMatchObject({
      from: "A", to: "B", label: null, line: "solid", arrow: "arrow", arrowHead: "none",
    });
  });

  it("reads a square label", () => {
    const graph = parseOk("flowchart TD\n  A[시작] --> B[끝]");

    expect(graph.nodes[0]).toMatchObject({ id: "A", label: "시작", shape: "rect" });
    expect(graph.nodes[1]).toMatchObject({ id: "B", label: "끝", shape: "rect" });
  });

  it("reads an edge label written with pipes", () => {
    const graph = parseOk("flowchart TD\n  A -->|예| B");

    expect(graph.edges[0].label).toBe("예");
  });

  it("reads an edge label written in the middle of the arrow", () => {
    const graph = parseOk("flowchart TD\n  A -- 아니오 --> B");

    expect(graph.edges[0]).toMatchObject({ label: "아니오", line: "solid", arrow: "arrow" });
  });

  it("keeps declaration order of nodes", () => {
    const graph = parseOk("flowchart TD\n  B --> C\n  A --> B");

    expect(graph.nodes.map((node) => node.id)).toEqual(["B", "C", "A"]);
  });

  it("lets a later declaration supply the label for a node first seen bare", () => {
    const graph = parseOk("flowchart TD\n  A --> B\n  A[시작]");

    expect(graph.nodes.find((node) => node.id === "A").label).toBe("시작");
    expect(graph.nodes).toHaveLength(2);
  });

  it("gives every edge a stable unique id", () => {
    const graph = parseOk("flowchart TD\n  A --> B\n  A --> B");

    expect(graph.edges).toHaveLength(2);
    expect(graph.edges[0].id).not.toBe(graph.edges[1].id);
  });

  it("reads line styles and arrow ends", () => {
    const graph = parseOk([
      "flowchart TD",
      "  A --- B",
      "  B -.-> C",
      "  C ==> D",
      "  D ~~~ E",
      "  E --o F",
      "  F --x G",
      "  G <--> H",
    ].join("\n"));

    expect(graph.edges.map((edge) => [edge.line, edge.arrow, edge.arrowHead])).toEqual([
      ["solid", "none", "none"],
      ["dotted", "arrow", "none"],
      ["thick", "arrow", "none"],
      ["invisible", "none", "none"],
      ["solid", "circle", "none"],
      ["solid", "cross", "none"],
      ["solid", "arrow", "arrow"],
    ]);
  });

  it("accepts longer dashes", () => {
    expect(parseOk("flowchart TD\n  A ----> B").edges[0]).toMatchObject({
      line: "solid", arrow: "arrow",
    });
  });

  it("reads a quoted label containing brackets", () => {
    const graph = parseOk('flowchart TD\n  A["a]b"] --> B');

    expect(graph.nodes[0].label).toBe("a]b");
  });

  it("keeps a line break marker inside a label", () => {
    expect(parseOk("flowchart TD\n  A[첫 줄<br/>둘째 줄] --> B").nodes[0].label)
      .toBe("첫 줄<br/>둘째 줄");
  });
});

describe("parseFlowchart — 노드 모양", () => {
  it.each([
    ["A[사각]", "rect", "사각"],
    ["A(둥근)", "round", "둥근"],
    ["A([스타디움])", "stadium", "스타디움"],
    ["A[[서브루틴]]", "subroutine", "서브루틴"],
    ["A[(원통)]", "cylinder", "원통"],
    ["A((원))", "circle", "원"],
    ["A(((두겹원)))", "doublecircle", "두겹원"],
    ["A>비대칭]", "asymmetric", "비대칭"],
    ["A{마름모}", "diamond", "마름모"],
    ["A{{육각형}}", "hexagon", "육각형"],
    ["A[/평행사변형/]", "parallelogram", "평행사변형"],
    ["A[\\평행사변형역\\]", "parallelogramAlt", "평행사변형역"],
    ["A[/사다리꼴\\]", "trapezoid", "사다리꼴"],
    ["A[\\사다리꼴역/]", "trapezoidAlt", "사다리꼴역"],
  ])("reads %s as %s", (declaration, shape, label) => {
    const node = parseOk(`flowchart TD\n  ${declaration} --> B`).nodes[0];

    expect(node.shape).toBe(shape);
    expect(node.label).toBe(label);
  });
});

describe("parseFlowchart — 확장 모양 문법", () => {
  it("reads shape and label from the @{ } form", () => {
    const graph = parseOk('flowchart TD\n  A@{ shape: manual-input, label: "손 입력" }\n  A --> B');

    expect(graph.nodes[0]).toMatchObject({ id: "A", shape: "manual-input", label: "손 입력" });
  });

  it("reads an unquoted label", () => {
    expect(parseOk("flowchart TD\n  A@{ shape: rounded, label: 확인 }\n  A --> B").nodes[0])
      .toMatchObject({ shape: "rounded", label: "확인" });
  });

  it("uses the id as the label when only a shape is given", () => {
    expect(parseOk("flowchart TD\n  A@{ shape: cyl }\n  A --> B").nodes[0])
      .toMatchObject({ shape: "cyl", label: "A" });
  });

  it("rejects keys other than shape and label", () => {
    const result = parseFlowchart('flowchart TD\n  A@{ shape: rect, icon: "x" }\n  A --> B');

    expect(result.ok).toBe(false);
    expect(result.reason).toBe("unsupported-syntax");
  });
});

describe("parseFlowchart — 연쇄와 병렬", () => {
  it("expands a chain into one edge per hop", () => {
    const graph = parseOk("flowchart TD\n  A --> B --> C");

    expect(graph.edges.map((edge) => [edge.from, edge.to])).toEqual([["A", "B"], ["B", "C"]]);
  });

  it("keeps the label of each hop in a chain", () => {
    const graph = parseOk("flowchart TD\n  A -->|하나| B -.->|둘| C");

    expect(graph.edges[0]).toMatchObject({ label: "하나", line: "solid" });
    expect(graph.edges[1]).toMatchObject({ label: "둘", line: "dotted" });
  });

  it("expands & on the left into one edge per source", () => {
    const graph = parseOk("flowchart TD\n  A & B --> C");

    expect(graph.edges.map((edge) => [edge.from, edge.to])).toEqual([["A", "C"], ["B", "C"]]);
  });

  it("expands & on both sides into the cross product", () => {
    const graph = parseOk("flowchart TD\n  A & B --> C & D");

    expect(graph.edges.map((edge) => [edge.from, edge.to])).toEqual([
      ["A", "C"], ["A", "D"], ["B", "C"], ["B", "D"],
    ]);
  });

  /*
   * 중간 라벨(`A -- 예 --> B`) 정규화가 링크를 가로질러 삼키면 안 된다.
   * `L --- M -.-> N`에서 `--- M -.->`를 한 덩어리로 보면 M이 라벨로 먹히고
   * 노드 하나와 간선 하나가 통째로 사라진다. 그러면 그림과 모델의 개수가 어긋나
   * 비주얼 편집이 잠긴다.
   */
  it("does not swallow a chain that mixes link styles", () => {
    const graph = parseOk("flowchart TD\n  L --- M -.-> N ==> O");

    expect(graph.nodes.map((node) => node.id)).toEqual(["L", "M", "N", "O"]);
    expect(graph.edges.map((edge) => [edge.from, edge.to, edge.line, edge.label])).toEqual([
      ["L", "M", "solid", null],
      ["M", "N", "dotted", null],
      ["N", "O", "thick", null],
    ]);
  });

  it("still reads a real mid-arrow label", () => {
    const graph = parseOk("flowchart TD\n  A -- 아니오 --> B -- 예 --> C");

    expect(graph.edges.map((edge) => [edge.from, edge.to, edge.label])).toEqual([
      ["A", "B", "아니오"],
      ["B", "C", "예"],
    ]);
  });

  it("reads a dotted mid-arrow label", () => {
    const graph = parseOk("flowchart TD\n  A -. 실패 .-> B");

    expect(graph.edges[0]).toMatchObject({ line: "dotted", label: "실패", arrow: "arrow" });
  });

  it("keeps a plain three-dash link out of the mid-label rule", () => {
    const graph = parseOk("flowchart TD\n  A --- B --- C");

    expect(graph.edges.map((edge) => [edge.from, edge.to, edge.arrow])).toEqual([
      ["A", "B", "none"],
      ["B", "C", "none"],
    ]);
  });

  /*
   * 여는 토큰이 같고 닫기 토큰만 다른 모양(`[/…/]`와 `[/…\]`)이 한 줄에 이어 나오면,
   * 먼저 시도되는 쪽이 다음 노드의 닫기 토큰까지 삼킬 수 있다.
   */
  it("does not let one label swallow the next node", () => {
    const graph = parseOk("flowchart TD\n  I[/입출력/] --> J[/사다리꼴\\] --> K>깃발]");

    expect(graph.nodes.map((node) => [node.id, node.shape, node.label])).toEqual([
      ["I", "parallelogram", "입출력"],
      ["J", "trapezoid", "사다리꼴"],
      ["K", "asymmetric", "깃발"],
    ]);
    expect(graph.edges).toHaveLength(2);
  });

  it("keeps a hyphen that really belongs to the label", () => {
    expect(parseOk("flowchart TD\n  A[a--b] --> B").nodes[0].label).toBe("a--b");
  });

  it("reads shapes declared inside an & group", () => {
    const graph = parseOk("flowchart TD\n  A[가] & B(나) --> C");

    expect(graph.nodes[0]).toMatchObject({ id: "A", label: "가", shape: "rect" });
    expect(graph.nodes[1]).toMatchObject({ id: "B", label: "나", shape: "round" });
  });
});

describe("parseFlowchart — 서브그래프", () => {
  it("reads a titled subgraph and its members", () => {
    const graph = parseOk([
      "flowchart TD",
      "  subgraph build [빌드]",
      "    A --> B",
      "  end",
      "  B --> C",
    ].join("\n"));

    expect(graph.subgraphs).toHaveLength(1);
    expect(graph.subgraphs[0]).toMatchObject({ id: "build", title: "빌드", direction: null });
    expect(graph.subgraphs[0].children).toEqual(["A", "B"]);
    expect(graph.edges).toHaveLength(2);
  });

  it("uses the id as the title when no title is given", () => {
    const graph = parseOk("flowchart TD\n  subgraph 빌드\n    A --> B\n  end");

    expect(graph.subgraphs[0]).toMatchObject({ id: "빌드", title: "빌드" });
  });

  it("reads a quoted title", () => {
    const graph = parseOk('flowchart TD\n  subgraph s1 ["빌드 단계"]\n    A --> B\n  end');

    expect(graph.subgraphs[0].title).toBe("빌드 단계");
  });

  it("reads the direction inside a subgraph", () => {
    const graph = parseOk([
      "flowchart TD",
      "  subgraph s1 [빌드]",
      "    direction LR",
      "    A --> B",
      "  end",
    ].join("\n"));

    expect(graph.subgraphs[0].direction).toBe("LR");
  });

  it("nests subgraphs and records the child subgraph as a member", () => {
    const graph = parseOk([
      "flowchart TD",
      "  subgraph outer [바깥]",
      "    subgraph inner [안쪽]",
      "      A --> B",
      "    end",
      "    B --> C",
      "  end",
    ].join("\n"));

    const outer = graph.subgraphs.find((group) => group.id === "outer");
    const inner = graph.subgraphs.find((group) => group.id === "inner");

    expect(inner.children).toEqual(["A", "B"]);
    expect(outer.children).toEqual(["inner", "C"]);
  });

  /*
   * Mermaid는 묶음 이름을 노드처럼 선의 끝점으로 쓴다. 파서가 그것을 노드로 만들어 버리면
   * 모델의 노드 수가 그림보다 많아져 비주얼 편집이 잠긴다.
   */
  it("treats a subgraph used as an edge end as the subgraph, not a new node", () => {
    const graph = parseOk([
      "flowchart LR",
      "  subgraph ci [CI]",
      "    A --> B",
      "  end",
      "  subgraph cd [CD]",
      "    C --> D",
      "  end",
      "  ci --> cd",
    ].join("\n"));

    expect(graph.nodes.map((node) => node.id)).toEqual(["A", "B", "C", "D"]);
    expect(graph.edges.map((edge) => [edge.from, edge.to]))
      .toEqual([["A", "B"], ["C", "D"], ["ci", "cd"]]);
  });

  it("links a node to a subgraph in either direction", () => {
    const graph = parseOk([
      "flowchart LR",
      "  subgraph ci [CI]",
      "    A --> B",
      "  end",
      "  Z[시작] --> ci",
      "  ci --> Y[끝]",
    ].join("\n"));

    expect(graph.nodes.map((node) => node.id)).toEqual(["A", "B", "Z", "Y"]);
    expect(graph.edges.map((edge) => [edge.from, edge.to]))
      .toEqual([["A", "B"], ["Z", "ci"], ["ci", "Y"]]);
  });

  it("keeps a real node that happens to be declared with a label", () => {
    // 참조로만 생긴 것과 진짜 선언은 다르다. 라벨을 준 쪽은 남겨야 한다.
    const graph = parseOk([
      "flowchart LR",
      "  subgraph ci [CI]",
      "    A --> B",
      "  end",
      "  ci2[진짜 노드] --> A",
    ].join("\n"));

    expect(graph.nodes.map((node) => node.id)).toContain("ci2");
  });

  it("rejects an unclosed subgraph", () => {
    const result = parseFlowchart("flowchart TD\n  subgraph s1 [빌드]\n    A --> B");

    expect(result.ok).toBe(false);
    expect(result.reason).toBe("unclosed-subgraph");
  });

  it("rejects a stray end", () => {
    expect(parseFlowchart("flowchart TD\n  A --> B\n  end").ok).toBe(false);
  });
});

describe("parseFlowchart — 색", () => {
  it("maps a node style back to a palette name", () => {
    const graph = parseOk([
      "flowchart TD",
      "  A --> B",
      "  style A fill:#e8f5e9,stroke:#2e7d32,color:#1b5e20",
    ].join("\n"));

    expect(graph.nodes.find((node) => node.id === "A").color).toBe("green");
    expect(graph.nodes.find((node) => node.id === "B").color).toBeNull();
  });

  it("maps a link style back to a palette name by edge index", () => {
    const graph = parseOk([
      "flowchart TD",
      "  A --> B",
      "  B --> C",
      "  linkStyle 1 stroke:#d32f2f,stroke-width:2px",
    ].join("\n"));

    expect(graph.edges[0].color).toBeNull();
    expect(graph.edges[1].color).toBe("red");
  });

  it("rejects a colour that is not in the palette", () => {
    const result = parseFlowchart([
      "flowchart TD",
      "  A --> B",
      "  style A fill:#123456,stroke:#654321,color:#000000",
    ].join("\n"));

    expect(result.ok).toBe(false);
    expect(result.reason).toBe("unsupported-colour");
  });

  it("rejects linkStyle default because it has no single edge to attach to", () => {
    const result = parseFlowchart("flowchart TD\n  A --> B\n  linkStyle default stroke:#d32f2f");

    expect(result.ok).toBe(false);
  });

  it("rejects a linkStyle index that has no edge", () => {
    expect(parseFlowchart("flowchart TD\n  A --> B\n  linkStyle 7 stroke:#d32f2f").ok).toBe(false);
  });
});

describe("parseFlowchart — classDef과 class", () => {
  it("keeps classDef and class statements verbatim", () => {
    const graph = parseOk([
      "flowchart TD",
      "  A --> B",
      "  classDef warn fill:#f96,stroke:#333",
      "  class A warn",
    ].join("\n"));

    expect(graph.classDefs).toEqual([{ name: "warn", body: "fill:#f96,stroke:#333" }]);
    expect(graph.classUses).toEqual([{ nodeId: "A", className: "warn" }]);
  });

  it("expands a class statement that lists several nodes", () => {
    const graph = parseOk("flowchart TD\n  A --> B\n  class A,B warn\n  classDef warn fill:#f96");

    expect(graph.classUses).toEqual([
      { nodeId: "A", className: "warn" },
      { nodeId: "B", className: "warn" },
    ]);
  });
});

describe("parseFlowchart — 주석", () => {
  it("attaches a comment to the statement that follows it", () => {
    const graph = parseOk("flowchart TD\n  %% 시작 지점\n  A --> B");

    expect(graph.comments).toEqual([{ anchorKind: "edge", anchorId: graph.edges[0].id, text: "시작 지점" }]);
  });

  it("attaches a comment before the header to the header", () => {
    const graph = parseOk("%% 문서 설명\nflowchart TD\n  A --> B");

    expect(graph.comments[0]).toMatchObject({ anchorKind: "header", text: "문서 설명" });
  });

  it("keeps a comment that has nothing after it", () => {
    const graph = parseOk("flowchart TD\n  A --> B\n  %% 끝");

    expect(graph.comments.at(-1)).toMatchObject({ anchorKind: "trailing", text: "끝" });
  });
});

describe("parseFlowchart — 미지원 문법", () => {
  it.each([
    ["click", "flowchart TD\n  A --> B\n  click A \"https://example.com\""],
    ["linkStyle default", "flowchart TD\n  A --> B\n  linkStyle default stroke:#333"],
    ["알 수 없는 구문", "flowchart TD\n  A --> B\n  ??? 무엇"],
  ])("locks visual editing for %s", (_name, code) => {
    expect(parseFlowchart(code).ok).toBe(false);
  });

  it("reports which line could not be read", () => {
    const result = parseFlowchart("flowchart TD\n  A --> B\n  click A \"x\"");

    expect(result.detail).toContain("click");
  });
});

describe("parseFlowchart — 마크다운 컨테이너 들여쓰기 (MarkUpViewMini 전용)", () => {
  // MinisTool은 독립 .mmd 파일을 다루지만, MarkUpViewMini는 mermaid 블록이 마크다운
  // 목록·인용문 안에 들여써진 채로 넘어올 수 있다(document-surface의 findMermaidBlocks가
  // 그 들여쓰기를 포함해서 블록 소스를 넘긴다). 공통 접두사를 벗겨서 파싱하고
  // graph.format에 기억해 둬야 시리얼라이저가 되돌릴 수 있다.
  it("strips a common leading-space prefix and records it on graph.format", () => {
    const graph = parseOk("  flowchart LR\n  A --> B");

    expect(graph.format).toEqual({ containerPrefix: "  ", newline: "\n" });
    expect(graph.direction).toBe("LR");
  });

  it("strips a blockquote prefix", () => {
    const graph = parseOk("> flowchart LR\n> A --> B");

    expect(graph.format.containerPrefix).toBe("> ");
  });

  it("records the newline style without altering parsing", () => {
    const graph = parseOk("flowchart LR\r\nA --> B");

    expect(graph.format).toEqual({ containerPrefix: "", newline: "\r\n" });
  });

  it("fails closed for mixed newline kinds", () => {
    const result = parseFlowchart("flowchart LR\r\nA --> B\nB --> C");

    expect(result.ok).toBe(false);
    expect(result.reason).toBe("mixed-newlines");
  });

  it("does not treat a body-only indentation choice as a container prefix", () => {
    // 헤더 줄은 들여쓰기가 없으니 공통 접두사는 없다 - 이건 사용자의 스타일일 뿐이다.
    const graph = parseOk("flowchart LR\n  A --> B");

    expect(graph.format.containerPrefix).toBe("");
  });
});
