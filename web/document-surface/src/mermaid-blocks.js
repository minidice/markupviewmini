import { GutterMarker, gutter, showPanel } from "@codemirror/view";
import { analyzeMermaidSource } from "@markup-view-mini/mermaid-safe/analyzer";
import MarkdownIt from "markdown-it";

const EDIT_LABEL = "시각 편집";
const fenceParser = new MarkdownIt();
const actionDescriptions = new WeakMap();
let descriptionSequence = 0;

// These no longer describe why the action is blocked - it never is. They describe why a
// block opens in the editor's limited text-only mode (no visual canvas/inspector) instead of
// the full visual mode. See createActionButton below.
const LIMITED_MODE_REASON_MESSAGES = {
  "flowchart-required": "flowchart 다이어그램만 시각 편집을 지원합니다. 텍스트로만 편집할 수 있습니다.",
  "empty": "빈 다이어그램은 텍스트로만 편집할 수 있습니다.",
  "mixed-newlines": "줄바꿈 형식이 섞여 있어 텍스트로만 편집할 수 있습니다.",
  "unsupported-syntax": "이 구문은 시각 편집기에서 지원하지 않아 텍스트로만 편집할 수 있습니다.",
  "unsupported-colour": "지원하지 않는 색상 지정이라 텍스트로만 편집할 수 있습니다.",
  "unclosed-subgraph": "닫히지 않은 subgraph가 있어 텍스트로만 편집할 수 있습니다.",
  "render-failed": "다이어그램을 렌더링하지 못해 텍스트로만 편집할 수 있습니다.",
};

export function describeLimitedModeReason(code) {
  return LIMITED_MODE_REASON_MESSAGES[code] ?? "이 다이어그램은 텍스트로만 편집할 수 있습니다.";
}

function sourceLines(source) {
  const lines = [];
  for (let start = 0; start < source.length;) {
    let end = start;
    while (end < source.length && source[end] !== "\r" && source[end] !== "\n") end += 1;
    let breakEnd = end;
    if (source[breakEnd] === "\r") breakEnd += source[breakEnd + 1] === "\n" ? 2 : 1;
    else if (source[breakEnd] === "\n") breakEnd += 1;
    lines.push({ start, breakEnd });
    start = breakEnd;
  }
  return lines;
}

function excludeClosingLineBreak(source, contentStart, closingStart) {
  if (closingStart <= contentStart) return contentStart;
  if (source[closingStart - 1] === "\n") {
    return source[closingStart - 2] === "\r" ? closingStart - 2 : closingStart - 1;
  }
  return source[closingStart - 1] === "\r" ? closingStart - 1 : closingStart;
}

function contentLineCount(content) {
  if (content === "") return 0;
  const lines = content.split("\n").length;
  return content.endsWith("\n") ? lines - 1 : lines;
}

function isClosedFence(token) {
  return Array.isArray(token.map)
    && token.map[1] - token.map[0] === contentLineCount(token.content) + 2;
}

export function findMermaidBlocks(markdown) {
  const source = typeof markdown === "string" ? markdown : String(markdown ?? "");
  const lines = sourceLines(source);
  const blocks = [];

  const tokens = fenceParser.parse(source, {});
  for (const token of tokens) {
    if (token.type !== "fence"
      || token.info.trim() !== "mermaid"
      || !isClosedFence(token)) continue;
    const openingIndex = token.map[0];
    const closingIndex = token.map[1] - 1;
    const from = lines[openingIndex].breakEnd;
    const to = excludeClosingLineBreak(source, from, lines[closingIndex].start);
    blocks.push({
      from,
      to,
      source: source.slice(from, to),
      openingLine: openingIndex + 1,
    });
  }

  return blocks;
}

export function describeMermaidAction(source) {
  const analysis = analyzeMermaidSource(source);
  return analysis.supported
    ? { enabled: true, reason: "" }
    : { enabled: false, reason: String(analysis.reason ?? "unsupported-syntax") };
}

export async function createMermaidEditPayload(block) {
  const bytes = new TextEncoder().encode(block.source);
  const digest = await globalThis.crypto.subtle.digest("SHA-256", bytes);
  const sourceHash = [...new Uint8Array(digest)]
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("");
  return { from: block.from, to: block.to, source: block.source, sourceHash };
}

function createActionButton(block, onRequested, actionOrigin, actionSurface, limitedModeReason = null) {
  // A block whose source the strict flowchart parser rejects still opens - the editor falls
  // back to a text-only mode for it (see web/mermaid-editor's confirmableSource). So this
  // button is always clickable; "action.enabled" only decides whether we show a heads-up that
  // it'll open in that limited mode instead of the full visual canvas.
  const action = limitedModeReason === null
    ? describeMermaidAction(block.source)
    : { enabled: false, reason: limitedModeReason };
  const button = document.createElement("button");
  button.type = "button";
  button.className = "mermaid-edit-action";
  button.dataset.mermaidEditAction = "";
  button.dataset.mermaidActionId = globalThis.crypto.randomUUID();
  button.dataset.mermaidActionOrigin = actionOrigin;
  button.dataset.mermaidActionSurface = actionSurface;
  button.dataset.mermaidOpeningLine = String(block.openingLine);
  button.textContent = EDIT_LABEL;
  const accessibleLabel = actionSurface === "panel"
    ? `${EDIT_LABEL}, Mermaid block at line ${block.openingLine}`
    : EDIT_LABEL;
  if (action.enabled) {
    button.setAttribute("aria-label", accessibleLabel);
  } else {
    const reasonMessage = describeLimitedModeReason(action.reason);
    button.dataset.mermaidLimitedMode = "true";
    button.title = reasonMessage;
    button.setAttribute("aria-label", `${accessibleLabel}: ${reasonMessage}`);
    const description = document.createElement("span");
    description.id = `mermaid-action-reason-${++descriptionSequence}`;
    description.className = "mermaid-action-reason";
    description.textContent = reasonMessage;
    button.setAttribute("aria-describedby", description.id);
    actionDescriptions.set(button, description);
  }
  button.addEventListener("click", async () => {
    const payload = await createMermaidEditPayload(block);
    await onRequested?.({
      ...payload,
      actionId: button.dataset.mermaidActionId,
      actionOrigin,
    }, block);
  });
  return button;
}

function createGutterProxy(block) {
  // Purely a click proxy into the real (accessible-panel) action button, which carries all of
  // the enabled/limited-mode logic itself - nothing to duplicate here.
  const button = document.createElement("button");
  button.type = "button";
  button.className = "mermaid-edit-action";
  button.dataset.mermaidActionSurface = "gutter";
  button.dataset.mermaidOpeningLine = String(block.openingLine);
  button.textContent = EDIT_LABEL;
  button.tabIndex = -1;
  button.setAttribute("aria-hidden", "true");
  button.addEventListener("click", () => {
    const editor = button.closest(".cm-editor");
    const panel = editor?.querySelector(
      `[data-mermaid-action-surface="panel"][data-mermaid-opening-line="${block.openingLine}"]`,
    );
    if (panel?.isConnected) panel.click();
  });
  return button;
}

export function addRenderedMermaidAction(element, block, onRequested, limitedModeReason = null) {
  if (!element || !block) return null;
  const button = createActionButton(block, onRequested, "rendered", "rendered", limitedModeReason);
  const description = actionDescriptions.get(button);
  element.append(button);
  if (description) element.append(description);
  return button;
}

class MermaidActionMarker extends GutterMarker {
  constructor(block) {
    super();
    this.block = block;
  }

  eq(other) {
    return this.block.from === other.block.from
      && this.block.to === other.block.to
      && this.block.source === other.block.source;
  }

  toDOM() {
    return createGutterProxy(this.block);
  }
}

export function focusMermaidAction(root, actionId, actionOrigin) {
  if (!root || typeof actionId !== "string"
    || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(actionId)
    || (actionOrigin !== "rendered" && actionOrigin !== "editor")) return false;
  const action = [...root.querySelectorAll("[data-mermaid-edit-action]")]
    .find((candidate) => candidate.dataset.mermaidActionId === actionId
      && candidate.dataset.mermaidActionOrigin === actionOrigin
      && candidate.dataset.mermaidActionSurface !== "gutter"
      && candidate.getAttribute("aria-hidden") !== "true"
      && candidate.closest('[aria-hidden="true"]') === null);
  if (!action || action.disabled || action.tabIndex < 0
    || action.getAttribute("aria-disabled") === "true" || !action.isConnected) return false;
  action.focus();
  return document.activeElement === action;
}

export function createMermaidGutter(onRequested, scanBlocks = findMermaidBlocks) {
  let cachedDocument = null;
  let cachedBlocks = [];
  let blocksByOpeningLine = new Map();

  const blocksForDocument = (documentState) => {
    if (documentState !== cachedDocument) {
      cachedDocument = documentState;
      cachedBlocks = scanBlocks(documentState.toString());
      blocksByOpeningLine = new Map(cachedBlocks.map((block) => [block.openingLine, block]));
    }
    return cachedBlocks;
  };
  const blockForLine = (documentState, lineNumber) => {
    blocksForDocument(documentState);
    return blocksByOpeningLine.get(lineNumber) ?? null;
  };

  const accessiblePanel = (view) => {
    const dom = document.createElement("div");
    dom.className = "cm-mermaid-accessible-actions";
    dom.setAttribute("aria-label", "Mermaid visual edit actions");
    const render = (documentState) => {
      dom.replaceChildren();
      for (const block of blocksForDocument(documentState)) {
        const container = document.createElement("span");
        container.className = "mermaid-action-container";
        const button = createActionButton(block, onRequested, "editor", "panel");
        const description = actionDescriptions.get(button);
        container.append(button);
        if (description) container.append(description);
        dom.append(container);
      }
    };
    render(view.state.doc);
    return {
      dom,
      top: true,
      update(update) {
        if (update.docChanged) render(update.state.doc);
      },
    };
  };

  return [
    gutter({
      class: "cm-mermaid-actions",
      lineMarker(view, line) {
        const lineNumber = view.state.doc.lineAt(line.from).number;
        const block = blockForLine(view.state.doc, lineNumber);
        return block ? new MermaidActionMarker(block) : null;
      },
    }),
    showPanel.of(accessiblePanel),
  ];
}
