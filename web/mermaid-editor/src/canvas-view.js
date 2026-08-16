import { clampZoom } from "./zoom-control.js";

export function createCanvasView(viewport, surface, onChange = () => {}) {
  let zoom = 1;
  let panX = 0;
  let panY = 0;
  const paint = () => {
    surface.style.transform = `translate(${panX}px, ${panY}px) scale(${zoom})`;
    onChange(zoom, panX, panY);
  };
  return {
    get zoom() { return zoom; },
    get pan() { return { x: panX, y: panY }; },
    set(nextZoom, nextPanX = panX, nextPanY = panY) {
      zoom = clampZoom(nextZoom);
      panX = nextPanX;
      panY = nextPanY;
      paint();
    },
    panBy(deltaX, deltaY) { panX += deltaX; panY += deltaY; paint(); },
    reset() { zoom = 1; panX = 0; panY = 0; paint(); },
    fit() { zoom = 1; paint(); },
    viewport,
  };
}
