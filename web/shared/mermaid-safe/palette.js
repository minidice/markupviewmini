/**
 * 노드는 배경·테두리·글자색이 한 벌로 정해져 있다. 사용자가 색 조합을 고민하지 않도록.
 * 선은 stroke만 쓴다.
 * "default"는 색 구문을 아예 쓰지 않는다는 뜻이다. Mermaid 테마 색으로 돌아간다.
 */
export const PALETTE = [
  { id: "default", fill: null, stroke: null, text: null },
  { id: "gray", fill: "#f2f2f2", stroke: "#8c8c8c", text: "#333333" },
  { id: "red", fill: "#fdecea", stroke: "#d32f2f", text: "#7f1d1d" },
  { id: "orange", fill: "#fff3e0", stroke: "#ef6c00", text: "#7c3a00" },
  { id: "green", fill: "#e8f5e9", stroke: "#2e7d32", text: "#1b5e20" },
  { id: "blue", fill: "#e3f2fd", stroke: "#1565c0", text: "#0d3c74" },
  { id: "purple", fill: "#f3e8fd", stroke: "#6a1b9a", text: "#3f0f5e" },
];

export const PALETTE_BY_ID = new Map(PALETTE.map((entry) => [entry.id, entry]));

/** `style` 구문에서 읽은 fill/stroke/color 조합을 팔레트 이름으로 되돌린다. */
export function paletteIdForNodeStyle({ fill, stroke, color }) {
  const match = PALETTE.find((entry) =>
    entry.id !== "default" &&
    entry.fill.toLowerCase() === String(fill).toLowerCase() &&
    entry.stroke.toLowerCase() === String(stroke).toLowerCase() &&
    entry.text.toLowerCase() === String(color).toLowerCase());
  return match?.id ?? null;
}

/** `linkStyle` 구문에서 읽은 stroke를 팔레트 이름으로 되돌린다. */
export function paletteIdForLinkStroke(stroke) {
  const match = PALETTE.find((entry) =>
    entry.id !== "default" && entry.stroke.toLowerCase() === String(stroke).toLowerCase());
  return match?.id ?? null;
}
