export function buildOutline(root) {
  if (!root?.querySelectorAll) return [];

  return [...root.querySelectorAll("h1, h2, h3, h4, h5, h6")]
    .map((heading) => ({
      level: Number(heading.tagName.slice(1)),
      text: heading.textContent,
      anchor: heading.id,
      sourceLine: Number(heading.dataset.sourceStart),
    }))
    .filter((item) => item.anchor && Number.isSafeInteger(item.sourceLine) && item.sourceLine > 0);
}
