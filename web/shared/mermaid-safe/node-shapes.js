/**
 * 파서·직렬화·모양 팔레트가 함께 쓰는 단일 출처.
 * 순서가 중요하다. 여는 토큰이 긴 것을 먼저 두어야
 * "[[x]]"가 rect + 라벨 "[x]"로 잘못 읽히지 않는다.
 */
export const CLASSIC_SHAPES = [
  { id: "doublecircle", open: "(((", close: ")))" },
  { id: "subroutine", open: "[[", close: "]]" },
  { id: "cylinder", open: "[(", close: ")]" },
  { id: "circle", open: "((", close: "))" },
  { id: "hexagon", open: "{{", close: "}}" },
  { id: "stadium", open: "([", close: "])" },
  { id: "trapezoid", open: "[/", close: "\\]" },
  { id: "parallelogram", open: "[/", close: "/]" },
  { id: "trapezoidAlt", open: "[\\", close: "/]" },
  { id: "parallelogramAlt", open: "[\\", close: "\\]" },
  { id: "asymmetric", open: ">", close: "]" },
  { id: "rect", open: "[", close: "]" },
  { id: "round", open: "(", close: ")" },
  { id: "diamond", open: "{", close: "}" },
];

export const CLASSIC_SHAPE_BY_ID = new Map(CLASSIC_SHAPES.map((shape) => [shape.id, shape]));
