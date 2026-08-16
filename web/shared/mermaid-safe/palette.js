export const PALETTE = [
  { id: "default", fill: null, stroke: null, text: null },
  { id: "blue", fill: "#e3f2fd", stroke: "#1565c0", text: "#0d3c74" },
  { id: "green", fill: "#e8f5e9", stroke: "#2e7d32", text: "#1b5e20" },
];

export const PALETTE_BY_ID = new Map(PALETTE.map((colour) => [colour.id, colour]));

export function paletteIdForNodeStyle({ fill, stroke, color }) {
  return PALETTE.find((colour) => colour.id !== "default"
    && colour.fill === fill
    && colour.stroke === stroke
    && colour.text === color)?.id ?? null;
}
