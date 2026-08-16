export function clampZoom(zoom) {
  return Math.min(4, Math.max(0.25, Number(zoom) || 1));
}

export function stepZoom(zoom, direction) {
  return clampZoom(zoom * (direction > 0 ? 1.2 : 1 / 1.2));
}
