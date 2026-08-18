import { describe, expect, it } from "vitest";
import { parseFlowchart } from "./flowchart-parser.js";
import {
  addConnectedNode,
  connectEndpoints,
  connectNodes,
  describeEdge,
  findEdgeByDescription,
  groupNodes,
  moveNodeToSubgraph,
  nextNodeId,
  removeEdge,
  removeNode,
  setDirection,
  setEdgeColor,
  setEdgeLabel,
  setEdgeLine,
  setNodeColor,
  setNodeLabel,
  setNodeShape,
  setSubgraphDirection,
  setSubgraphTitle,
  subgraphOfNode,
} from "./graph-model.js";
import { serializeFlowchart } from "./flowchart-serializer.js";

function graphOf(code) {
  const result = parseFlowchart(code);
  expect(result.ok).toBe(true);
  return result.graph;
}

describe("nextNodeId", () => {
  it("uses single letters first", () => {
    expect(nextNodeId(graphOf("flowchart TD\n  A --> B"))).toBe("C");
  });

  it("skips ids already in use", () => {
    expect(nextNodeId(graphOf("flowchart TD\n  A --> C"))).toBe("B");
  });

  it("moves to numbered ids after Z", () => {
    const graph = graphOf("flowchart TD\n  A --> B");
    graph.nodes = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("").map((id) => ({
      id, label: id, shape: "rect", color: null,
    }));

    expect(nextNodeId(graph)).toBe("A1");
  });
});

describe("addConnectedNode", () => {
  it("adds a node and an edge from the source", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    const created = addConnectedNode(graph, "A");

    expect(created.id).toBe("C");
    expect(graph.nodes.map((node) => node.id)).toEqual(["A", "B", "C"]);
    expect(graph.edges.at(-1)).toMatchObject({ from: "A", to: "C" });
  });

  it("puts the new node in the same subgraph as its source", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end");

    const created = addConnectedNode(graph, "A");

    expect(graph.subgraphs[0].children).toContain(created.id);
  });

  it("does nothing when the source is unknown", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(addConnectedNode(graph, "없음")).toBeNull();
    expect(graph.nodes).toHaveLength(2);
  });
});

describe("removeNode", () => {
  it("removes the node and every edge touching it", () => {
    const graph = graphOf("flowchart TD\n  A --> B\n  B --> C\n  A --> C");

    removeNode(graph, "B");

    expect(graph.nodes.map((node) => node.id)).toEqual(["A", "C"]);
    expect(graph.edges.map((edge) => [edge.from, edge.to])).toEqual([["A", "C"]]);
  });

  it("removes the node from its subgraph and from class assignments", () => {
    const graph = graphOf([
      "flowchart TD",
      "  subgraph s1 [묶음]",
      "    A --> B",
      "  end",
      "  classDef warn fill:#f96",
      "  class A warn",
    ].join("\n"));

    removeNode(graph, "A");

    expect(graph.subgraphs[0].children).toEqual(["B"]);
    expect(graph.classUses).toEqual([]);
  });
});

describe("removeEdge", () => {
  it("keeps the colour of the other edges", () => {
    const graph = graphOf([
      "flowchart TD",
      "  A --> B",
      "  B --> C",
      "  C --> D",
      "  linkStyle 2 stroke:#d32f2f,stroke-width:2px",
    ].join("\n"));

    removeEdge(graph, graph.edges[0].id);

    expect(graph.edges).toHaveLength(2);
    expect(graph.edges.at(-1).color).toBe("red");
  });
});

describe("속성 변경", () => {
  it("changes a label, a shape, and a colour", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    setNodeLabel(graph, "A", "시작");
    setNodeShape(graph, "A", "stadium");
    setNodeColor(graph, "A", "green");

    expect(graph.nodes[0]).toMatchObject({ label: "시작", shape: "stadium", color: "green" });
  });

  it("treats the default colour as no colour", () => {
    const graph = graphOf("flowchart TD\n  A --> B");
    setNodeColor(graph, "A", "green");

    setNodeColor(graph, "A", "default");

    expect(graph.nodes[0].color).toBeNull();
  });

  it("changes a line style and an edge colour", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    setEdgeLine(graph, graph.edges[0].id, "dotted");
    setEdgeColor(graph, graph.edges[0].id, "red");

    expect(graph.edges[0]).toMatchObject({ line: "dotted", color: "red" });
  });
});

// MarkUpViewMini 전용: 라벨이 직렬화 문법과 충돌하는 문자를 담으면 재구성한 mermaid가
// 깨진다(따옴표가 감싸는 따옴표와 충돌, `|`가 엣지 라벨 구분자와 충돌, 개행이 줄 구조를
// 깬다). MinisTool의 원본 setter는 이 검증이 없어서 포팅하면서 추가했다.
describe("라벨 검증 (MarkUpViewMini 전용)", () => {
  it.each([
    ["빈 문자열", ""],
    ["큰따옴표 포함", 'a"b'],
    ["개행 포함", "a\nb"],
    ["캐리지리턴 포함", "a\rb"],
  ])("rejects a node label with %s", (_name, label) => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(setNodeLabel(graph, "A", label)).toBe(false);
    expect(graph.nodes[0].label).toBe("A");
  });

  it("accepts a node label containing other syntax characters (they get quoted on serialize)", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(setNodeLabel(graph, "A", "a[b]c")).toBe(true);
    expect(serializeFlowchart(graph)).toContain('A["a[b]c"]');
  });

  it.each([
    ["파이프 포함", "a|b"],
    ["개행 포함", "a\nb"],
  ])("rejects an edge label with %s", (_name, label) => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(setEdgeLabel(graph, graph.edges[0].id, label)).toBe(false);
  });

  it("treats an empty edge label as no label", () => {
    const graph = graphOf("flowchart TD\n  A -->|하나| B");

    expect(setEdgeLabel(graph, graph.edges[0].id, "")).toBe(true);
    expect(graph.edges[0].label).toBeNull();
  });
});

describe("엣지 이름표", () => {
  /*
   * 비주얼 편집은 코드를 다시 쓰고 다시 파싱한다. 그때 엣지 id가 새로 매겨지므로
   * id만 들고 있으면 편집 한 번에 선택이 풀린다.
   */
  it("survives a re-parse so the selection is not lost after an edit", () => {
    const before = graphOf("flowchart TD\n  A --> B\n  B --> C");
    const description = describeEdge(before, before.edges[1].id);

    const after = graphOf("flowchart TD\n  A --> B\n  B -.->|실패| C");
    const found = findEdgeByDescription(after, description);

    expect(found).not.toBeNull();
    expect(found.id).not.toBe(before.edges[1].id); // id는 실제로 바뀐다
    expect(found).toMatchObject({ from: "B", to: "C", line: "dotted", label: "실패" });
  });

  it("keeps duplicate edges between the same nodes apart", () => {
    const graph = graphOf("flowchart TD\n  A --> B\n  A --> B\n  A --> B");

    expect(describeEdge(graph, graph.edges[0].id).occurrence).toBe(0);
    expect(describeEdge(graph, graph.edges[1].id).occurrence).toBe(1);
    expect(describeEdge(graph, graph.edges[2].id).occurrence).toBe(2);
    expect(findEdgeByDescription(graph, { from: "A", to: "B", occurrence: 2 }).id)
      .toBe(graph.edges[2].id);
  });

  it("does not confuse the two directions of a pair", () => {
    const graph = graphOf("flowchart TD\n  A --> B\n  B --> A");

    expect(describeEdge(graph, graph.edges[1].id)).toEqual({ from: "B", to: "A", occurrence: 0 });
  });

  it("reports nothing for an edge that is gone", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(describeEdge(graph, "edge-없음")).toBeNull();
    expect(findEdgeByDescription(graph, { from: "A", to: "없음", occurrence: 0 })).toBeNull();
    expect(findEdgeByDescription(graph, null)).toBeNull();
  });
});

describe("서브그래프 속성", () => {
  it("changes the title and the inner direction", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end");

    setSubgraphTitle(graph, "s1", "빌드 단계");
    setSubgraphDirection(graph, "s1", "LR");

    expect(graph.subgraphs[0]).toMatchObject({ title: "빌드 단계", direction: "LR" });
  });

  it("treats an empty direction as no direction", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    direction LR\n    A --> B\n  end");

    setSubgraphDirection(graph, "s1", "");

    expect(graph.subgraphs[0].direction).toBeNull();
  });

  it("refuses a title that would break the header syntax", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end");

    expect(setSubgraphTitle(graph, "s1", "대괄호 [있음]")).toBe(false);
    expect(graph.subgraphs[0].title).toBe("묶음");
  });

  it("does nothing for an unknown subgraph", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(setSubgraphTitle(graph, "없음", "제목")).toBe(false);
    expect(setSubgraphDirection(graph, "없음", "LR")).toBe(false);
  });
});

describe("setDirection", () => {
  it("changes the flow direction", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(setDirection(graph, "LR")).toBe(true);
    expect(serializeFlowchart(graph).split("\n")[0]).toBe("flowchart LR");
  });

  it("ignores an unknown direction or one already set", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(setDirection(graph, "옆으로")).toBe(false);
    expect(setDirection(graph, "TD")).toBe(false);
    expect(graph.direction).toBe("TD");
  });

  it("keeps the graph keyword the document used", () => {
    const graph = graphOf("graph LR\n  A --> B");
    setDirection(graph, "BT");

    expect(serializeFlowchart(graph).split("\n")[0]).toBe("graph BT");
  });
});

describe("묶음 소속", () => {
  it("moves a node into a subgraph", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end\n  C[다]");

    expect(moveNodeToSubgraph(graph, "C", "s1")).toBe(true);
    expect(graph.subgraphs[0].children).toEqual(["A", "B", "C"]);
    expect(subgraphOfNode(graph, "C").id).toBe("s1");
  });

  it("takes a node back out", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end");

    expect(moveNodeToSubgraph(graph, "A", null)).toBe(true);
    expect(graph.subgraphs[0].children).toEqual(["B"]);
    expect(subgraphOfNode(graph, "A")).toBeNull();
  });

  it("keeps a node in one subgraph only", () => {
    const graph = graphOf([
      "flowchart TD",
      "  subgraph s1 [하나]",
      "    A --> B",
      "  end",
      "  subgraph s2 [둘]",
      "    C --> D",
      "  end",
    ].join("\n"));

    moveNodeToSubgraph(graph, "A", "s2");

    expect(graph.subgraphs[0].children).toEqual(["B"]);
    expect(graph.subgraphs[1].children).toEqual(["C", "D", "A"]);
  });

  it("does nothing when the target is unknown or unchanged", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [묶음]\n    A --> B\n  end");

    expect(moveNodeToSubgraph(graph, "A", "s1")).toBe(false);
    expect(moveNodeToSubgraph(graph, "없음", "s1")).toBe(false);
    expect(moveNodeToSubgraph(graph, "A", "없음")).toBe(false);
  });
});

describe("groupNodes", () => {
  it("wraps the chosen nodes in a new subgraph", () => {
    const graph = graphOf("flowchart TD\n  A --> B\n  B --> C");

    const created = groupNodes(graph, ["A", "B"], "빌드");

    expect(created.children).toEqual(["A", "B"]);
    expect(created.title).toBe("빌드");
    expect(graph.subgraphs).toHaveLength(1);
    expect(subgraphOfNode(graph, "C")).toBeNull();
  });

  it("gives the new subgraph an id that is not taken", () => {
    const graph = graphOf("flowchart TD\n  subgraph group1 [먼저]\n    A --> B\n  end\n  C --> D");

    expect(groupNodes(graph, ["C"], "다음").id).toBe("group2");
  });

  it("moves a node out of its old subgraph", () => {
    const graph = graphOf("flowchart TD\n  subgraph s1 [하나]\n    A --> B\n  end");

    groupNodes(graph, ["A"], "새 묶음");

    expect(graph.subgraphs[0].children).toEqual(["B"]);
    expect(graph.subgraphs[1].children).toEqual(["A"]);
  });

  it("refuses a title with brackets and an empty selection", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(groupNodes(graph, ["A"], "안 됨 [x]")).toBeNull();
    expect(groupNodes(graph, [], "빈 것")).toBeNull();
    expect(graph.subgraphs).toHaveLength(0);
  });

  it("round-trips through the serializer", () => {
    const graph = graphOf("flowchart TD\n  A --> B\n  B --> C");
    groupNodes(graph, ["A", "B"], "빌드");

    const again = graphOf(serializeFlowchart(graph));

    expect(again.subgraphs[0]).toMatchObject({ id: "group1", title: "빌드" });
    expect(again.subgraphs[0].children).toEqual(["A", "B"]);
  });
});

describe("connectNodes", () => {
  it("adds an edge between two existing nodes", () => {
    const graph = graphOf("flowchart TD\n  A --> B\n  C[다]");

    connectNodes(graph, "B", "C");

    expect(graph.edges.at(-1)).toMatchObject({ from: "B", to: "C", arrow: "arrow" });
  });

  it("connects a subgraph as an endpoint", () => {
    const graph = graphOf([
      "flowchart LR",
      "  subgraph ci [CI]",
      "    A --> B",
      "  end",
      "  subgraph cd [CD]",
      "    C --> D",
      "  end",
      "  Z[시작]",
    ].join("\n"));

    expect(connectEndpoints(graph, "ci", "cd")).not.toBeNull();
    expect(connectEndpoints(graph, "Z", "ci")).not.toBeNull();
    expect(graph.edges.slice(-2).map((edge) => [edge.from, edge.to]))
      .toEqual([["ci", "cd"], ["Z", "ci"]]);
  });

  it("survives a round trip with a subgraph endpoint", () => {
    const graph = graphOf("flowchart LR\n  subgraph ci [CI]\n    A --> B\n  end\n  C[끝]");
    connectEndpoints(graph, "ci", "C");

    const again = graphOf(serializeFlowchart(graph));

    expect(again.nodes.map((node) => node.id)).toEqual(["A", "B", "C"]);
    expect(again.edges.map((edge) => [edge.from, edge.to])).toEqual([["A", "B"], ["ci", "C"]]);
  });

  it("refuses an endpoint that is neither a node nor a subgraph", () => {
    const graph = graphOf("flowchart LR\n  A --> B");

    expect(connectEndpoints(graph, "A", "없음")).toBeNull();
    expect(connectEndpoints(graph, "A", "A")).toBeNull();
  });

  it("refuses to connect a node to itself", () => {
    const graph = graphOf("flowchart TD\n  A --> B");

    expect(connectNodes(graph, "A", "A")).toBeNull();
    expect(graph.edges).toHaveLength(1);
  });
});
