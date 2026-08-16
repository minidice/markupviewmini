import { CLASSIC_SHAPES } from "./node-shapes.js";
import { PALETTE } from "./palette.js";

function button(label, dataName, value, selected, onEdit) {
  const control = document.createElement("button");
  control.type = "button";
  control.textContent = label;
  control.dataset[dataName] = value;
  control.setAttribute("aria-pressed", String(selected));
  control.addEventListener("click", () => onEdit?.(value));
  return control;
}

export function renderInspector(root, analysis, selection = null, handlers = {}) {
  root.replaceChildren();
  if (!analysis.supported) {
    root.textContent = `Visual editing is locked: ${analysis.reason}`;
    return;
  }
  const node = analysis.model?.nodes?.find((candidate) => candidate.id === selection);
  if (node === undefined) {
    root.textContent = `Select a node on the canvas to edit it. Shapes: ${CLASSIC_SHAPES.map((shape) => shape.id).join(", ")}. Colours: ${PALETTE.map((colour) => colour.id).join(", ")}.`;
    return;
  }

  const heading = document.createElement("h2");
  heading.textContent = `Selected ${node.id}`;
  const label = document.createElement("label");
  label.textContent = "Label";
  const input = document.createElement("input");
  input.type = "text";
  input.value = node.label;
  input.dataset.nodeLabel = "";
  input.setAttribute("aria-label", `Label for ${node.id}`);
  input.addEventListener("change", () => handlers.onLabel?.(input.value));
  label.append(input);

  const shapes = document.createElement("fieldset");
  shapes.innerHTML = "<legend>Shape</legend>";
  for (const shape of CLASSIC_SHAPES) {
    shapes.append(button(shape.id, "nodeShape", shape.id, node.shape === shape.id, handlers.onShape));
  }

  const colours = document.createElement("fieldset");
  colours.innerHTML = "<legend>Colour</legend>";
  for (const colour of PALETTE) {
    colours.append(button(
      colour.id,
      "nodeColor",
      colour.id,
      (node.color ?? "default") === colour.id,
      handlers.onColor,
    ));
  }
  root.append(heading, label, shapes, colours);
}
