import MarkdownIt from "markdown-it";
import markdownItAnchor from "markdown-it-anchor";
import markdownItAttrs from "markdown-it-attrs";
import markdownItDeflist from "markdown-it-deflist";
import markdownItFootnote from "markdown-it-footnote";
import markdownItMark from "markdown-it-mark";
import markdownItSub from "markdown-it-sub";
import markdownItSup from "markdown-it-sup";
import markdownItTaskLists from "markdown-it-task-lists";
import { full as markdownItEmoji } from "markdown-it-emoji";
import katex from "katex";
import mermaid from "mermaid";
import hljs from "highlight.js/lib/common";
import DOMPurify from "dompurify";
import { addRenderedMermaidAction, describeMermaidAction, findMermaidBlocks } from "./mermaid-blocks.js";
import { addSourceLineBounds, installSourceMapRules } from "./source-map.js";

export const MERMAID_CONFIG = {
  startOnLoad: false,
  securityLevel: "strict",
  maxTextSize: 50000,
  // Mermaid 11 deprecated `flowchart.htmlLabels` in favor of this top-level flag; several
  // internal label-layout code paths only read the top-level value and default to `true`
  // (foreignObject + HTML) when it is missing, regardless of the nested flowchart setting.
  // Both flags must stay false and in sync. HTML labels are NOT an option here: DOMPurify
  // unconditionally strips all element content inside <foreignObject> (verified — no allowlist
  // or config combination lets HTML through; this is a deliberate DOMPurify mXSS defense with
  // no override), so `htmlLabels:true` renders diamonds/boxes with every label silently empty.
  // Pure SVG <text>/<tspan> labels are the only option sanitizeMermaidSvg can actually allow.
  htmlLabels: false,
  flowchart: { htmlLabels: false },
  secure: [
    "securityLevel",
    "startOnLoad",
    "maxTextSize",
    "secure",
    "htmlLabels",
    "theme",
    "themeCSS",
    "themeVariables",
    "fontFamily",
    "flowchart",
  ],
};

mermaid.initialize(MERMAID_CONFIG);

const SAFE_TAGS = [
  "a", "abbr", "annotation", "article", "b", "blockquote", "br", "code", "dd", "del",
  "div", "dl", "dt", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img",
  "input", "kbd", "li", "mark", "math", "mfrac", "mi", "mn", "mo", "mover", "mrow",
  "mspace", "msqrt", "mstyle", "msub", "msubsup", "msup", "mtable", "mtd", "mtext",
  "mtr", "munder", "munderover", "ol", "p", "pre", "s", "section", "semantics", "span",
  "strong", "sub", "sup", "table", "tbody", "td", "th", "thead", "tr", "ul",
];

const SAFE_ATTRIBUTES = [
  "alt", "aria-describedby", "aria-hidden", "checked", "class", "data-footnote-backref",
  "data-footnote-ref", "data-mermaid-opening-line", "data-mermaid-source", "data-render-error", "data-save-document-hint",
  "data-local-image", "data-source-end", "data-source-start", "disabled", "encoding", "height", "href", "id",
  "mathvariant", "role", "src", "style", "tabindex", "title", "type", "width", "xmlns",
];

const SAFE_URI_PATTERN = /^(?:(?:https?|mailto|file):|[./#]|[a-z\d._~-]+(?:[/?#]|$))/iu;

const KATEX_STYLE_PROPERTIES = new Set([
  "border-bottom-width",
  "font-size",
  "height",
  "left",
  "margin-left",
  "margin-right",
  "min-width",
  "padding-left",
  "position",
  "top",
  "vertical-align",
  "width",
]);

const SAFE_STYLE_VALUE = /^(?:-?(?:\d+(?:\.\d+)?|\.\d+)(?:em|ex|px|%)?|0|absolute|relative)$/iu;
const CONTROL_CHARACTERS = /[\u0000-\u0020\u007f-\u009f]/gu;
const CONTROL_CHARACTER = /[\u0000-\u0020\u007f-\u009f]/u;
const CSS_CONTROL_CHARACTER = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u;
const ASSET_CONTROL_CHARACTER = /[\u0000-\u001f\u007f-\u009f]/u;

const SAFE_MERMAID_TAGS = [
  "circle", "clipPath", "defs", "desc", "ellipse", "g", "line", "marker", "mask",
  "path", "polygon", "polyline", "rect", "style", "svg", "text", "title", "tspan",
];

const SAFE_MERMAID_ATTRIBUTES = [
  "alignment-baseline", "aria-describedby", "aria-labelledby", "aria-roledescription", "class",
  "clip-path", "cx", "cy", "d", "dominant-baseline", "fill", "fill-opacity", "font-family",
  "font-size", "font-style", "font-weight", "height", "id", "marker-end", "marker-mid",
  "marker-start", "markerHeight", "markerUnits", "markerWidth", "mask", "orient", "points",
  "preserveAspectRatio", "r", "refX", "refY", "role", "rx", "ry", "stroke",
  "stroke-dasharray", "stroke-linecap", "stroke-linejoin", "stroke-opacity", "stroke-width",
  "style", "text-anchor", "transform", "viewBox", "width", "x", "x1", "x2", "y", "y1", "y2",
];

const SAFE_MERMAID_STYLE_PROPERTIES = new Set([
  "alignment-baseline", "background-color", "color", "display", "dominant-baseline", "fill",
  "fill-opacity", "font-family", "font-size", "font-style", "font-weight", "line-height",
  "margin", "margin-bottom", "margin-left", "margin-right", "margin-top", "max-width",
  "opacity", "overflow", "padding", "shape-rendering", "stroke", "stroke-dasharray",
  "stroke-linecap", "stroke-linejoin", "stroke-opacity", "stroke-width", "text-align",
  "text-anchor", "white-space",
]);

const LOCAL_FRAGMENT_ATTRIBUTES = new Set([
  "clip-path", "fill", "marker-end", "marker-mid", "marker-start", "mask", "stroke",
]);

const MERMAID_SELECTOR_COMPOUND = "(?:(?:\\*|[a-z][a-z\\d-]*)(?:[.#][a-z_][a-z\\d_-]*)*|(?:[.#][a-z_][a-z\\d_-]*)+)";
const SAFE_MERMAID_SELECTOR = new RegExp(
  `^${MERMAID_SELECTOR_COMPOUND}(?:(?:\\s+|\\s*>\\s*)${MERMAID_SELECTOR_COMPOUND})*$`,
  "iu",
);
const SAFE_CSS_IDENTIFIER = /^[a-z_][a-z\d_-]*$/iu;
const GLOBAL_MERMAID_ELEMENT = /(?:^|[\s>])(?:html|body)(?=$|[.#\s>])/iu;

function hasUnsafeCssResource(value) {
  return CSS_CONTROL_CHARACTER.test(value)
    || /(?:\\|@|url\s*\(|expression\s*\(|behavior\s*:|-moz-binding|(?:https?|file|data|javascript):|\/\/)/iu.test(value);
}

function safeMermaidDeclarations(style) {
  const declarations = [];
  for (const property of [...style]) {
    const value = style.getPropertyValue(property).trim();
    if (SAFE_MERMAID_STYLE_PROPERTIES.has(property) && !hasUnsafeCssResource(value)) {
      declarations.push(`${property}: ${value}`);
    }
  }
  return declarations.join("; ");
}

function mermaidDeclarationsFromText(css) {
  const probe = document.createElement("span");
  probe.setAttribute("style", css);
  return safeMermaidDeclarations(probe.style);
}

function detachedMermaidStyleRules(css) {
  if (typeof CSSStyleSheet !== "function") return null;
  try {
    const sheet = new CSSStyleSheet();
    if (typeof sheet.replaceSync !== "function") return null;
    sheet.replaceSync(css);
    return [...sheet.cssRules];
  } catch {
    return null;
  }
}

function escapeRegularExpression(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

function scopedMermaidSelector(selector, rootId, originalRootId) {
  const trimmed = selector.trim();
  if (!SAFE_MERMAID_SELECTOR.test(trimmed) || GLOBAL_MERMAID_ELEMENT.test(trimmed)) return null;

  if (SAFE_CSS_IDENTIFIER.test(originalRootId)) {
    const rootPrefix = new RegExp(
      `^#${escapeRegularExpression(originalRootId)}(?=$|[.#\\s>])`,
      "iu",
    );
    if (rootPrefix.test(trimmed)) return trimmed.replace(rootPrefix, `#${rootId}`);
  }
  return `#${rootId} ${trimmed}`;
}

// Mermaid's theme stylesheet targets several shape elements per rule (e.g.
// ".node rect, .node circle, .node ellipse"). Each comma-separated compound selector is
// scoped/validated independently; the whole rule is rejected if any part is unsafe.
function scopedMermaidSelectorList(selectorText, rootId, originalRootId) {
  const parts = selectorText.split(",").map((part) => part.trim()).filter(Boolean);
  if (parts.length === 0) return null;
  const scoped = [];
  for (const part of parts) {
    const result = scopedMermaidSelector(part, rootId, originalRootId);
    if (!result) return null;
    scoped.push(result);
  }
  return scoped.join(", ");
}

function sanitizeMermaidStyleSheet(css, rootId, originalRootId) {
  if (!css || hasUnsafeCssResource(css)) return "";
  const rules = detachedMermaidStyleRules(css);
  if (!rules?.length) return "";

  // A single rule this sanitizer doesn't understand (an unsupported selector shape, an
  // @keyframes/@media block, or a declaration list with no allowlisted properties left after
  // filtering) must only drop THAT rule. Earlier this bailed out of the whole stylesheet on the
  // first such rule, so mermaid's base theme (56+ rules covering every default node/edge color)
  // silently vanished entirely whenever any one rule used a shape or property this allowlist
  // hadn't seen yet — every unstyled node then fell back to the SVG default fill (black).
  const sanitizedRules = [];
  for (const rule of rules) {
    if (rule.type !== 1) continue;
    const selector = scopedMermaidSelectorList(rule.selectorText, rootId, originalRootId);
    const declarations = safeMermaidDeclarations(rule.style);
    if (!selector || !declarations) continue;
    sanitizedRules.push(`${selector} { ${declarations}; }`);
  }
  return sanitizedRules.join("\n");
}

function sanitizeMermaidSvg(svg, rootId) {
  const sanitized = DOMPurify.sanitize(svg, {
    ALLOWED_TAGS: SAFE_MERMAID_TAGS,
    ALLOWED_ATTR: SAFE_MERMAID_ATTRIBUTES,
    ALLOW_ARIA_ATTR: false,
    ALLOW_DATA_ATTR: false,
    FORBID_TAGS: ["a", "foreignObject", "iframe", "image", "object", "script", "use"],
  });
  const container = document.createElement("div");
  container.innerHTML = sanitized;
  const root = container.querySelector("svg");
  if (!root) throw new Error("Mermaid did not return a safe SVG root.");

  const originalRootId = root.id;
  root.id = rootId;
  root.removeAttribute("xmlns");
  for (const style of root.querySelectorAll("style")) {
    const safeCss = sanitizeMermaidStyleSheet(style.textContent, rootId, originalRootId);
    if (safeCss) style.textContent = safeCss;
    else style.remove();
  }
  const elements = [root, ...root.querySelectorAll("*")];
  for (const element of elements) {
    if (element.hasAttribute("style")) {
      const safeStyle = mermaidDeclarationsFromText(element.getAttribute("style"));
      if (safeStyle) element.setAttribute("style", safeStyle);
      else element.removeAttribute("style");
    }
    for (const attribute of [...element.attributes]) {
      const name = attribute.name;
      const value = attribute.value;
      if (/^on/iu.test(name)) {
        element.removeAttribute(name);
      } else if (LOCAL_FRAGMENT_ATTRIBUTES.has(name) && /url\s*\(/iu.test(value)) {
        if (!/^url\(#[a-z\d_.:-]+\)$/iu.test(value.trim())) element.removeAttribute(name);
      } else if (hasUnsafeCssResource(value)) {
        element.removeAttribute(name);
      }
    }
  }
  // Edge-label background rects (the pill mermaid draws behind a "Y"/"N"-style link label so
  // it doesn't cross the line) ship from mermaid with no fill at all in SVG-text mode — not
  // stripped by us, mermaid's own output already omits it here. An SVG rect with no fill
  // defaults to solid black, so without this every edge label renders as an opaque black box.
  // This rect's own vertical position also assumes mermaid's alphabetic-baseline text metrics
  // (e.g. y="-1" for a 23-tall rect — not centered on the label group's local origin). Once
  // the label text below is re-centered on dominant-baseline instead, that mismatch leaves the
  // text poking out above the rect. Re-center the rect on the same local origin using only its
  // own height, matching the centered convention every other label already uses.
  for (const backgroundRect of root.querySelectorAll("rect.background")) {
    const inlineStyle = backgroundRect.getAttribute("style") ?? "";
    const hasFill = backgroundRect.hasAttribute("fill") || /(?:^|;)\s*fill\s*:/iu.test(inlineStyle);
    if (!hasFill) backgroundRect.setAttribute("fill", "white");
    const height = Number.parseFloat(backgroundRect.getAttribute("height") ?? "");
    if (Number.isFinite(height) && height > 0) backgroundRect.setAttribute("y", String(-height / 2));
  }
  // Every label mermaid lays out around a node/edge is meant to be horizontally centered on
  // its `x="0"` anchor point (that's what mermaid itself does for a short, single-chunk
  // label — both the <text> and its "row" tspan get text-anchor="middle"). But once a label
  // is long enough to word-wrap into multiple inner tspans, mermaid's SVG-text-mode renderer
  // stops emitting text-anchor at all on the outer <text>/row-tspan pair, so it falls back to
  // SVG's default "start" (left) anchor: the text still starts at the node's horizontal
  // center but now grows rightward past the shape's edge instead of straddling the center.
  // This isn't sanitizer stripping — it's missing from mermaid's own unsanitized output too.
  for (const textElement of root.querySelectorAll("text, tspan[x]")) {
    if (!textElement.hasAttribute("text-anchor")) textElement.setAttribute("text-anchor", "middle");
    // Mermaid positions each label's baseline at roughly the node's vertical center (e.g.
    // y="-0.1em" on the row tspan), which only looks centered if the rendering font's ascent
    // and descent happen to be near-symmetric around that baseline. Most fonts' ascent is
    // noticeably taller than their descent, so the glyphs actually drawn end up mostly above
    // the baseline — shifting the visible label upward off-center, worse the more of the
    // node's vertical space is taken up by ascent-heavy glyphs (CJK text included). Anchoring
    // to the font's central baseline instead of the alphabetic one fixes this regardless of
    // which font ends up rendering the text.
    if (!textElement.hasAttribute("dominant-baseline")) {
      textElement.setAttribute("dominant-baseline", "central");
    }
  }
  return root.outerHTML;
}

function safeNavigationDestination(value) {
  if (!value) return false;
  const compact = value.replace(CONTROL_CHARACTERS, "");
  if (/^\/\//u.test(compact)) return false;
  const scheme = /^([a-z][a-z\d+.-]*):/iu.exec(compact)?.[1]?.toLowerCase();
  return !scheme || ["file", "http", "https", "mailto"].includes(scheme);
}

const DOCUMENT_ASSET_BASE_URL = "https://document-assets.local/";

function safeImageSource(element, value, assetBaseUrl, imageTrustToken) {
  if (!value || CONTROL_CHARACTER.test(value)) return false;
  if (/^data:image\/(?:avif|bmp|gif|jpeg|png|webp);base64,[a-z\d+/]+=*$/iu.test(value)) {
    return true;
  }
  if (!imageTrustToken
    || element.getAttribute("data-local-image") !== imageTrustToken
    || assetBaseUrl !== DOCUMENT_ASSET_BASE_URL) {
    return false;
  }
  try {
    const base = new URL(assetBaseUrl);
    const resource = new URL(value);
    return base.href === DOCUMENT_ASSET_BASE_URL
      && resource.protocol === "https:"
      && resource.origin === base.origin;
  } catch {
    return false;
  }
}

function restrictInlineStyle(element) {
  if (!element.closest(".katex")) {
    element.removeAttribute("style");
    return;
  }
  for (const property of [...element.style]) {
    const value = element.style.getPropertyValue(property).trim();
    if (!KATEX_STYLE_PROPERTIES.has(property) || !SAFE_STYLE_VALUE.test(value)) {
      element.style.removeProperty(property);
    }
  }
  if (!element.style.cssText) element.removeAttribute("style");
}

function sanitizeRenderedFragment(html, options) {
  const sanitized = DOMPurify.sanitize(html, {
    ALLOWED_TAGS: SAFE_TAGS,
    ALLOWED_ATTR: SAFE_ATTRIBUTES,
    ALLOW_ARIA_ATTR: false,
    ALLOW_DATA_ATTR: false,
    ALLOWED_URI_REGEXP: SAFE_URI_PATTERN,
    FORBID_TAGS: ["script", "iframe", "object"],
  });
  const container = document.createElement("div");
  container.innerHTML = sanitized;
  for (const element of container.querySelectorAll("[href]")) {
    if (element.tagName !== "A" || !safeNavigationDestination(element.getAttribute("href"))) {
      element.removeAttribute("href");
    }
  }
  for (const element of container.querySelectorAll("[src]")) {
    if (element.tagName !== "IMG"
      || !safeImageSource(
        element,
        element.getAttribute("src"),
        options.assetBaseUrl,
        options.imageTrustToken,
      )) {
      element.removeAttribute("src");
    }
  }
  for (const element of container.querySelectorAll("[style]")) restrictInlineStyle(element);
  for (const element of container.querySelectorAll("[data-local-image]")) {
    element.removeAttribute("data-local-image");
  }
  return container.innerHTML;
}

function escapeHtml(source) {
  return new MarkdownIt().utils.escapeHtml(source);
}

function renderError(tagName, kind, source) {
  const tag = tagName === "span" ? "span" : "div";
  return `<${tag} class="render-error" data-render-error="${kind}"><code>${escapeHtml(source)}</code></${tag}>`;
}

function renderMath(source, displayMode) {
  try {
    return katex.renderToString(source, {
      displayMode,
      throwOnError: true,
      trust: false,
    });
  } catch {
    return renderError(displayMode ? "div" : "span", "latex", source);
  }
}

function installMathRules(markdown) {
  markdown.inline.ruler.before("escape", "math_inline", (state, silent) => {
    if (state.src[state.pos] !== "$" || state.src[state.pos + 1] === "$") return false;

    let end = state.pos + 1;
    while ((end = state.src.indexOf("$", end)) !== -1) {
      let backslashes = 0;
      for (let index = end - 1; index >= 0 && state.src[index] === "\\"; index -= 1) {
        backslashes += 1;
      }
      if (backslashes % 2 === 0) break;
      end += 1;
    }

    if (end === -1 || end === state.pos + 1) return false;
    if (!silent) {
      const token = state.push("math_inline", "math", 0);
      token.content = state.src.slice(state.pos + 1, end);
    }
    state.pos = end + 1;
    return true;
  });

  markdown.block.ruler.before("fence", "math_block", (state, startLine, endLine, silent) => {
    const start = state.bMarks[startLine] + state.tShift[startLine];
    const opening = state.src.slice(start, state.eMarks[startLine]);
    if (!opening.startsWith("$$")) return false;

    let content = opening.slice(2);
    let nextLine = startLine;
    let closed = content.endsWith("$$");
    if (closed) content = content.slice(0, -2);

    while (!closed && ++nextLine < endLine) {
      const lineStart = state.bMarks[nextLine] + state.tShift[nextLine];
      const line = state.src.slice(lineStart, state.eMarks[nextLine]);
      if (line.endsWith("$$")) {
        content += `${content ? "\n" : ""}${line.slice(0, -2)}`;
        closed = true;
      } else {
        content += `${content ? "\n" : ""}${line}`;
      }
    }

    if (!closed) return false;
    if (silent) return true;

    const token = state.push("math_block", "math", 0);
    token.block = true;
    token.content = content.trim();
    token.map = [startLine, nextLine + 1];
    state.line = nextLine + 1;
    return true;
  });

  markdown.renderer.rules.math_inline = (tokens, index) => renderMath(tokens[index].content, false);
  markdown.renderer.rules.math_block = (tokens, index, options, environment, renderer) => {
    const token = tokens[index];
    addSourceLineBounds(token);
    return `<div${renderer.renderAttrs(token)}>${renderMath(token.content, true)}</div>\n`;
  };
}

function isRelativeImage(url) {
  return Boolean(url) && !/^(?:[\\/]|%5c)/i.test(url) && !/^[a-z][a-z\d+.-]*:/i.test(url);
}

function decodeSingleAssetReference(value) {
  if (/%(?![0-9a-f]{2})/iu.test(value) || /%(?:2f|5c)/iu.test(value)) return null;
  try {
    const decoded = decodeURIComponent(value);
    return decoded.includes("%") ? null : decoded;
  } catch {
    return null;
  }
}

function isSafeRelativeImage(url) {
  if (!isRelativeImage(url) || ASSET_CONTROL_CHARACTER.test(url) || /[?#]/u.test(url)) return false;
  const decoded = decodeSingleAssetReference(url);
  if (!decoded
    || ASSET_CONTROL_CHARACTER.test(decoded)
    || decoded.includes("\\")
    || decoded.startsWith("/")
    || decoded.includes(":")
    || /^[a-z][a-z\d+.-]*:/iu.test(decoded)
    || decoded.split("/").some((segment) => segment === "..")) return false;
  return true;
}

function documentAssetBase(value) {
  if (typeof value !== "string" || value.trim() === "") return null;
  try {
    const base = new URL(value);
    return base.href === DOCUMENT_ASSET_BASE_URL ? base : null;
  } catch {
    return null;
  }
}

function installImageRule(markdown, options) {
  const defaultImage = markdown.renderer.rules.image;
  const assetBase = documentAssetBase(options.assetBaseUrl);
  const trustToken = typeof options.imageTrustToken === "string" ? options.imageTrustToken : null;

  markdown.renderer.rules.image = (tokens, index, ruleOptions, environment, renderer) => {
    const token = tokens[index];
    const source = token.attrGet("src");
    if (isSafeRelativeImage(source)) {
      if (!assetBase || !trustToken) {
        const label = token.content || source;
        return `<span class="save-document-hint" data-save-document-hint>${markdown.utils.escapeHtml(label)}</span>`;
      }
      token.attrSet("src", new URL(source, assetBase).href);
      token.attrSet("data-local-image", trustToken);
    } else if (isRelativeImage(source)) {
      const label = token.content || source;
      return `<span class="save-document-hint" data-save-document-hint>${markdown.utils.escapeHtml(label)}</span>`;
    }
    return defaultImage(tokens, index, ruleOptions, environment, renderer);
  };
}

function installFenceRule(markdown) {
  markdown.renderer.rules.fence = (tokens, index, options, environment, renderer) => {
    const token = tokens[index];
    addSourceLineBounds(token);
    const attributes = renderer.renderAttrs(token);
    const language = token.info.trim().split(/\s+/u, 1)[0].toLowerCase();
    if (language === "mermaid") {
      token.attrJoin("class", "mermaid-placeholder");
      token.attrSet("data-mermaid-opening-line", String(token.map[0] + 1));
      token.attrSet("data-mermaid-source", "");
      return `<pre${renderer.renderAttrs(token)}><code>${markdown.utils.escapeHtml(token.content)}</code></pre>\n`;
    }
    const safeLanguage = language.replaceAll(/[^a-z0-9_-]/giu, "");
    if (!language || !hljs.getLanguage(language)) {
      const languageClass = safeLanguage ? ` class="language-${safeLanguage}"` : "";
      return `<pre${attributes}><code${languageClass}>${markdown.utils.escapeHtml(token.content)}</code></pre>\n`;
    }
    const highlighted = hljs.highlight(token.content, { language }).value;
    return `<pre${attributes}><code class="hljs language-${safeLanguage}">${highlighted}</code></pre>\n`;
  };

  markdown.renderer.rules.code_block = (tokens, index, options, environment, renderer) => {
    const token = tokens[index];
    addSourceLineBounds(token);
    return `<pre${renderer.renderAttrs(token)}><code>${markdown.utils.escapeHtml(token.content)}</code></pre>\n`;
  };

  markdown.renderer.rules.html_block = (tokens, index, options, environment, renderer) => {
    const token = tokens[index];
    addSourceLineBounds(token);
    token.attrJoin("class", "raw-html-block");
    return `<div${renderer.renderAttrs(token)}>${token.content}</div>\n`;
  };
}

function slugifyHeading(title) {
  return title.trim().toLowerCase().replace(/\s+/gu, "-").replace(/[^\p{L}\p{N}_-]/gu, "");
}

export function createMarkdownRenderer(options = {}) {
  const markdown = new MarkdownIt({
    html: options.allowRawHtml === true,
    linkify: true,
    typographer: false,
  });
  markdown
    .use(markdownItFootnote)
    .use(markdownItDeflist)
    .use(markdownItMark)
    .use(markdownItSub)
    .use(markdownItSup)
    .use(markdownItEmoji)
    .use(markdownItTaskLists, { enabled: true, label: true })
    .use(markdownItAttrs, { allowedAttributes: ["id"] })
    .use(markdownItAnchor, { slugify: slugifyHeading });
  installMathRules(markdown);
  installImageRule(markdown, options);
  installFenceRule(markdown);
  installSourceMapRules(markdown);
  return markdown;
}

function errorElement(kind, source, openingLine = null) {
  const element = document.createElement("div");
  element.className = "render-error";
  element.dataset.renderError = kind;
  if (openingLine !== null) element.dataset.mermaidOpeningLine = openingLine;
  const code = document.createElement("code");
  code.textContent = source;
  element.append(code);
  return element;
}

async function validateMermaidPlaceholders(container) {
  const placeholders = [...container.querySelectorAll("[data-mermaid-source]")];
  for (const placeholder of placeholders) {
    const source = placeholder.textContent;
    try {
      const result = await mermaid.parse(source, { suppressErrors: true });
      if (!result) placeholder.replaceWith(errorElement(
        "mermaid",
        source,
        placeholder.dataset.mermaidOpeningLine,
      ));
    } catch {
      placeholder.replaceWith(errorElement(
        "mermaid",
        source,
        placeholder.dataset.mermaidOpeningLine,
      ));
    }
  }
}

export async function renderPreviewHtml(markdown, options = {}) {
  const container = document.createElement("div");
  const imageTrustToken = globalThis.crypto.randomUUID();
  const renderOptions = { ...options, allowRawHtml: true, imageTrustToken };
  const renderer = createMarkdownRenderer(renderOptions);
  const rendered = renderer.render(markdown ?? "");
  container.innerHTML = sanitizeRenderedFragment(rendered, renderOptions);
  await validateMermaidPlaceholders(container);
  return container.innerHTML;
}

export async function renderPreview(markdown, options = {}) {
  const container = options.container ?? document.createElement("div");
  const mermaidAdapter = options.mermaidAdapter ?? mermaid;
  const blocks = findMermaidBlocks(markdown);
  container.innerHTML = await renderPreviewHtml(markdown, options);

  const placeholders = [...container.querySelectorAll("[data-mermaid-source], [data-render-error=\"mermaid\"]")];
  for (const placeholder of placeholders) {
    const source = placeholder.textContent;
    const block = blocks.find(
      (candidate) => candidate.openingLine === Number(placeholder.dataset.mermaidOpeningLine),
    );
    if (placeholder.dataset.renderError === "mermaid") {
      const action = block === undefined ? null : describeMermaidAction(block.source);
      addRenderedMermaidAction(
        placeholder,
        block,
        options.onMermaidEditRequested,
        action?.enabled === false ? action.reason : "render-failed",
      );
      continue;
    }
    try {
      const id = `markdown-diagram-${globalThis.crypto.randomUUID()}`;
      const { svg } = await mermaidAdapter.render(
        id,
        source,
      );
      placeholder.className = "mermaid-diagram";
      placeholder.innerHTML = sanitizeMermaidSvg(svg, id);
      addRenderedMermaidAction(
        placeholder,
        block,
        options.onMermaidEditRequested,
      );
    } catch {
      const error = errorElement("mermaid", source, placeholder.dataset.mermaidOpeningLine);
      placeholder.replaceWith(error);
      const action = block === undefined ? null : describeMermaidAction(block.source);
      addRenderedMermaidAction(
        error,
        block,
        options.onMermaidEditRequested,
        action?.enabled === false ? action.reason : "render-failed",
      );
    }
  }

  return container.innerHTML;
}
