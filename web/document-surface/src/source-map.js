let navigationTarget = null;
let navigationTargetTimer = null;

export function addSourceLineBounds(token) {
  if (!token.block || !Array.isArray(token.map) || token.nesting === -1) return;
  const sourceStart = token.map[0] + 1;
  const sourceEnd = Math.max(sourceStart, token.map[1]);
  token.attrSet("data-source-start", String(sourceStart));
  token.attrSet("data-source-end", String(sourceEnd));
}

function sourceRange(element) {
  const start = Number(element.dataset.sourceStart);
  const end = Number(element.dataset.sourceEnd);
  return Number.isSafeInteger(start)
    && Number.isSafeInteger(end)
    && start > 0
    && end >= start
    ? { start, end }
    : null;
}

export function findBlockForSourceLine(root, line) {
  if (!root?.querySelectorAll || !Number.isSafeInteger(line) || line < 1) return null;

  const blocks = [
    ...(root.matches?.("[data-source-start][data-source-end]") ? [root] : []),
    ...root.querySelectorAll("[data-source-start][data-source-end]"),
  ];
  let result = null;
  let resultSize = Number.POSITIVE_INFINITY;
  for (const block of blocks) {
    const range = sourceRange(block);
    if (!range || line < range.start || line > range.end) continue;

    const size = range.end - range.start;
    if (size < resultSize) {
      result = block;
      resultSize = size;
    }
  }
  return result;
}

export function cancelNavigationHighlight() {
  if (navigationTargetTimer !== null) clearTimeout(navigationTargetTimer);
  navigationTargetTimer = null;
  navigationTarget?.classList.remove("is-navigation-target");
  navigationTarget = null;
}

function highlightNavigationTarget(target, scrollIntoView) {
  cancelNavigationHighlight();
  if (!target) return null;

  (scrollIntoView ?? target.scrollIntoView)?.call(target, { block: "center" });
  target.classList.add("is-navigation-target");
  navigationTarget = target;
  navigationTargetTimer = setTimeout(() => {
    target.classList.remove("is-navigation-target");
    if (navigationTarget === target) navigationTarget = null;
    navigationTargetTimer = null;
  }, 1500);
  navigationTargetTimer?.unref?.();
  return target;
}

export function goToSourceLine(root, line, scrollIntoView) {
  return highlightNavigationTarget(findBlockForSourceLine(root, line), scrollIntoView);
}

export function goToAnchor(root, anchor, scrollIntoView) {
  if (!root?.querySelector || typeof anchor !== "string" || anchor.trim() === "") return null;
  let target = null;
  if (typeof globalThis.CSS?.escape === "function") {
    try {
      target = root.querySelector(`#${globalThis.CSS.escape(anchor)}`);
    } catch {
      return null;
    }
  } else {
    target = [...root.querySelectorAll("[id]")].find((element) => element.id === anchor);
  }
  return highlightNavigationTarget(target, scrollIntoView);
}

export function installSourceMapRules(markdown) {
  const renderToken = markdown.renderer.renderToken.bind(markdown.renderer);
  markdown.renderer.renderToken = (tokens, index, options) => {
    addSourceLineBounds(tokens[index]);
    return renderToken(tokens, index, options);
  };
}
