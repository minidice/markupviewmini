import { CLASSIC_SHAPES } from "@markup-view-mini/mermaid-safe/node-shapes";
import { t } from "../../shared/i18n/index.js";

export { CLASSIC_SHAPES, CLASSIC_SHAPE_BY_ID } from "@markup-view-mini/mermaid-safe/node-shapes";

// Shape ids are Mermaid syntax names, which say nothing to someone who does not know the
// syntax ("stadium"). Looked up at call time so a language switch relabels what is on screen.
export function shapeLabel(id) {
  return t(`mermaid.shape.${id}`);
}

/**
 * The order shapes appear in the tool pane. `CLASSIC_SHAPES` is in the parser's precedence
 * order (longest opening token first), which buries the common shapes; picking wants the ones
 * people actually reach for - process, start/end, decision - at the front.
 *
 * It must stay the **same set** as `CLASSIC_SHAPES`: offering a shape the model cannot emit,
 * or hiding one it supports, are both bugs. (The reference editor drifted here.)
 */
export const PALETTE_SHAPE_IDS = [
  "rect", "round", "stadium", "diamond",
  "circle", "doublecircle", "cylinder", "hexagon",
  "parallelogram", "parallelogramAlt", "trapezoid", "trapezoidAlt",
  "subroutine", "asymmetric",
];

if (PALETTE_SHAPE_IDS.length !== CLASSIC_SHAPES.length) {
  throw new Error("PALETTE_SHAPE_IDS must cover exactly the modeled CLASSIC_SHAPES.");
}
