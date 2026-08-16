export function bindCanvasPan(viewport, canvas) {
  let previous = null;
  const move = (event) => {
    if (previous === null) return;
    canvas.panBy(event.clientX - previous.x, event.clientY - previous.y);
    previous = { x: event.clientX, y: event.clientY };
  };
  const up = () => { previous = null; };
  const down = (event) => {
    if (event.target.closest("svg g.node")) return;
    previous = { x: event.clientX, y: event.clientY };
  };
  viewport.addEventListener("pointerdown", down);
  viewport.addEventListener("pointermove", move);
  viewport.addEventListener("pointerup", up);
  viewport.addEventListener("pointerleave", up);
  return () => {
    viewport.removeEventListener("pointerdown", down);
    viewport.removeEventListener("pointermove", move);
    viewport.removeEventListener("pointerup", up);
    viewport.removeEventListener("pointerleave", up);
  };
}

export function bindCanvasInteractions({ surface, isEnabled, select }) {
  const nodeId = (target) => {
    const node = target.closest("g.node");
    if (node === null) return null;
    const svgId = node.closest("svg")?.id ?? "";
    const prefix = svgId === "" ? "" : `${svgId}-flowchart-`;
    return prefix !== "" && node.id.startsWith(prefix)
      ? node.id.slice(prefix.length).replace(/-\d+$/u, "")
      : node.id;
  };
  const click = (event) => {
    if (!isEnabled()) return;
    select(nodeId(event.target));
  };
  const keydown = (event) => {
    if (!isEnabled() || (event.key !== "Enter" && event.key !== " ")) return;
    const id = nodeId(event.target);
    if (id === null) return;
    event.preventDefault();
    select(id);
  };
  surface.addEventListener("click", click);
  surface.addEventListener("keydown", keydown);
  return () => {
    surface.removeEventListener("click", click);
    surface.removeEventListener("keydown", keydown);
  };
}
