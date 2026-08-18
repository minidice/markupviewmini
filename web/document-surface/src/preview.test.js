import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it, vi } from "vitest";
import {
  createMarkdownRenderer,
  MERMAID_CONFIG,
  renderPreview,
  renderPreviewHtml,
} from "./preview.js";

describe("markdown preview", () => {
  it("locks security-sensitive Mermaid configuration against document directives", () => {
    expect(MERMAID_CONFIG.securityLevel).toBe("strict");
    expect(MERMAID_CONFIG.secure).toEqual(expect.arrayContaining([
      "securityLevel",
      "htmlLabels",
      "theme",
      "themeCSS",
      "themeVariables",
      "fontFamily",
      "flowchart",
    ]));
  });

  it("disables Mermaid html labels on both the current and deprecated config keys", () => {
    // Mermaid 11 reads the top-level `htmlLabels` flag for most label-layout decisions and
    // only falls back to `flowchart.htmlLabels` in a few places, defaulting to `true` (i.e.
    // foreignObject + HTML labels) when the top-level flag is missing. sanitizeMermaidSvg
    // forbids foreignObject, so any drift here silently strips every node label from the
    // rendered diagram while leaving the shapes intact. Both flags must stay false together.
    expect(MERMAID_CONFIG.htmlLabels).toBe(false);
    expect(MERMAID_CONFIG.flowchart.htmlLabels).toBe(false);
  });

  it("sanitizes active raw HTML, event handlers, and javascript links", async () => {
    const html = await renderPreviewHtml([
      "<section><strong>kept</strong>",
      '<script>alert(1)</script>',
      '<iframe src="https://example.com"></iframe>',
      '<object data="file:///C:/secret"></object>',
      '<img src="image.png" onerror="alert(1)">',
      '<a href="javascript:alert(1)" onclick="alert(2)">bad</a>',
      "</section>",
    ].join("\n"));

    expect(html).toContain("<section>");
    expect(html).toContain("<strong>kept</strong>");
    expect(html).not.toMatch(/<(?:script|iframe|object)\b/iu);
    expect(html).not.toMatch(/\son\w+=/iu);
    expect(html).not.toContain("javascript:");
  });

  it("preserves javascript destinations in code examples while blocking active links", async () => {
    const html = await renderPreviewHtml([
      "`[inline](javascript:alert(1))`",
      "",
      "~~~markdown",
      "[fenced](javascript:alert(2))",
      "~~~",
      "",
      "[active](javascript:alert(3))",
    ].join("\n"));
    const root = document.createElement("div");
    root.innerHTML = html;

    expect(root.querySelector("p code")?.textContent)
      .toBe("[inline](javascript:alert(1))");
    expect(root.querySelector("pre code")?.textContent)
      .toContain("[fenced](javascript:alert(2))");
    expect(root.textContent).toContain("[active](javascript:alert(3))");
    expect(root.querySelector("a[href]")).toBeNull();
  });

  it("preserves safe Markdown, KaTeX, and Mermaid output after sanitizing", async () => {
    const html = await renderPreviewHtml(
      "# Heading\n\n[Safe](https://example.com) and $x^2$\n\n~~~mermaid\nflowchart LR\nA --> B\n~~~",
    );

    expect(html).toContain("<h1");
    expect(html).toContain('href="https://example.com"');
    expect(html).toContain("katex");
    expect(html).toContain("data-mermaid-source");
    expect(html).toContain("A --&gt; B");
  });

  it("emits inert Mermaid placeholders", () => {
    const root = document.createElement("div");
    root.innerHTML = createMarkdownRenderer().render("~~~mermaid\nflowchart LR\nA --> B\n~~~");

    expect(root.querySelector("[data-mermaid-source]")?.textContent).toContain("A --> B");
    expect(root.querySelector("svg")).toBeNull();
  });

  it("preserves a normal Mermaid SVG through final insertion", async () => {
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40" role="graphics-document">',
            '<style>.node { fill: #fff; stroke: #333; }</style>',
            '<defs><marker id="arrow" markerWidth="10" markerHeight="10" refX="5" refY="5" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z"></path></marker></defs>',
            '<g class="node"><rect x="1" y="1" width="40" height="20" style="fill:#fff;stroke:#333"></rect><text x="5" y="15">Safe</text></g>',
            '<path d="M 41 11 L 100 11" marker-end="url(#arrow)"></path>',
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    expect(container.querySelector("svg")?.getAttribute("viewBox")).toBe("0 0 120 40");
    expect(container.querySelector("rect")?.getAttribute("style")).toContain("fill");
    expect(container.querySelector("text")?.textContent).toBe("Safe");
    expect(container.querySelector('path[marker-end="url(#arrow)"]')).not.toBeNull();
    expect(container.querySelector("style")?.textContent).toContain("stroke:");
  });

  it("sanitizes malicious Mermaid SVG and theme-style output before final insertion", async () => {
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40" onload="alert(1)" style="background-image:url(data:text/html,unsafe)">',
            '<style>@import url(https://example.com/theme.css); .node { background-image: url(file:///C:/secret.png); fill: red; }</style>',
            '<a href="javascript:alert(1)"><text>Unsafe link</text></a>',
            '<foreignObject><iframe src="https://example.com"></iframe></foreignObject>',
            '<image href="file:///C:/secret.png" onerror="alert(2)"></image>',
            '<rect class="node" onclick="alert(3)" style="fill:url(//example.com/pattern.svg);stroke:red" width="40" height="20"></rect>',
            '<script>alert(4)</script>',
            "</svg>",
          ].join(""),
          bindFunctions(root) {
            root.querySelector("svg")?.setAttribute("onload", "alert(5)");
          },
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });
    const html = container.innerHTML;

    expect(container.querySelector("svg")).not.toBeNull();
    expect(container.textContent).toContain("Unsafe link");
    expect(container.querySelector("script, foreignObject, iframe, image, a")).toBeNull();
    expect(container.querySelector("[onload], [onclick], [onerror], [href], [xlink\\:href]")).toBeNull();
    expect(html).not.toMatch(/(?:https?:|file:|data:|javascript:|\/\/|@import|url\s*\()/iu);
  });

  it("keeps every safe theme rule when one rule uses a comma selector or an unsupported property", async () => {
    // Mermaid's real base theme stylesheet has 50+ rules, several using comma-separated
    // selectors ("A, B { ... }") and CSSOM-expanded longhand properties this sanitizer's
    // allowlist doesn't cover (animation-*, border-*, etc). Losing an unsupported rule is
    // fine; losing the ENTIRE stylesheet because of it is not — every unstyled node/edge then
    // falls back to the SVG default fill (black), which is exactly what happened before this
    // fix: the whole <style> block was discarded the moment any single rule failed validation.
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40">',
            "<style>",
            ".node rect, .node circle, .node polygon { fill: rgb(232, 245, 233); stroke: rgb(46, 125, 50); }",
            ".unsupported-only { animation-name: spin; animation-duration: 1s; }",
            "</style>",
            '<g class="node"><polygon points="0,0 10,10 0,20"></polygon></g>',
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    const styleText = container.querySelector("style")?.textContent ?? "";
    expect(styleText).toContain("fill: rgb(232, 245, 233)");
    expect(styleText).toContain(".node rect");
    expect(styleText).toContain(".node circle");
    expect(styleText).toContain(".node polygon");
    expect(styleText).not.toContain("animation");
  });

  it("gives fill-less edge-label background rects a fill and recenters them on their own height", async () => {
    // Mermaid's SVG-text-mode edge labels (htmlLabels:false) ship the background rect that's
    // meant to sit behind a "Y"/"N"-style link label with no fill anywhere — not stripped by
    // this sanitizer, mermaid's own render output already omits it. An SVG rect with no fill
    // defaults to solid black, so without this patch every edge label renders as an opaque box.
    // Mermaid also positions this rect assuming alphabetic-baseline text metrics (e.g.
    // y="-1" height="23", not centered on 0) — once the label text is re-centered on
    // dominant-baseline below, that mismatch pokes the text out above the rect, so this
    // recenters the rect on the same local origin instead.
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 20">',
            '<g class="edgeLabel"><rect class="background" x="0" y="0" width="10" height="10"></rect><text>Y</text></g>',
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA -->|Y| B\n~~~", { container, mermaidAdapter });

    const backgroundRect = container.querySelector("rect.background");
    expect(backgroundRect?.getAttribute("fill")).toBe("white");
    expect(backgroundRect?.getAttribute("y")).toBe("-5");
  });

  it("leaves an explicitly styled background rect's fill alone", async () => {
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 20">',
            '<rect class="background" fill="red" x="0" y="0" width="10" height="10"></rect>',
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    expect(container.querySelector("rect.background")?.getAttribute("fill")).toBe("red");
  });

  it("centers word-wrapped label text that mermaid renders without a text-anchor", async () => {
    // For a short, single-chunk label mermaid emits text-anchor="middle" on both the <text>
    // and its wrapping "row" tspan, so the label straddles its x="0" anchor point. But once a
    // label is long enough to word-wrap into multiple inner tspans, mermaid's SVG-text-mode
    // renderer (htmlLabels:false) stops emitting text-anchor at all — confirmed in mermaid's
    // own raw, unsanitized output, not something this sanitizer strips. Left un-anchored, SVG
    // defaults to "start" (left-aligned), so the label still starts at the node's horizontal
    // center but grows rightward past the shape's edge instead of being centered on it.
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40">',
            '<g class="node">',
            '<rect x="-60" y="-17" width="120" height="34"></rect>',
            '<text y="-10.1"><tspan class="row" x="0" y="-0.1em" dy="1.1em">',
            '<tspan class="text-inner-tspan">일반</tspan><tspan class="text-inner-tspan"> 공격</tspan><tspan class="text-inner-tspan"> 판정</tspan>',
            "</tspan></text>",
            "</g>",
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    const text = container.querySelector("text");
    const rowTspan = container.querySelector('tspan[x="0"]');
    expect(text?.getAttribute("text-anchor")).toBe("middle");
    expect(rowTspan?.getAttribute("text-anchor")).toBe("middle");
  });

  it("vertically centers label text on the font's central baseline instead of its alphabetic one", async () => {
    // Mermaid positions a label's baseline at roughly the node's vertical center (e.g.
    // y="-0.1em" on the row tspan), which only reads as centered if the rendering font's
    // ascent and descent happen to be symmetric around that baseline. Most fonts' ascent is
    // taller than their descent, so the glyphs drawn end up sitting mostly above the baseline —
    // visibly shifting the label upward off-center. dominant-baseline="central" anchors to the
    // font's own central metric instead, so this holds regardless of which font renders it.
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 60 34">',
            '<g class="node">',
            '<rect x="-30" y="-17" width="60" height="34"></rect>',
            '<text y="-10.1"><tspan class="row" x="0" y="-0.1em">시작</tspan></text>',
            "</g>",
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    const text = container.querySelector("text");
    const rowTspan = container.querySelector('tspan[x="0"]');
    expect(text?.getAttribute("dominant-baseline")).toBe("central");
    expect(rowTspan?.getAttribute("dominant-baseline")).toBe("central");
  });

  it("nudges central-baseline text down to close the residual gap to the shape's true center", async () => {
    // Break caught: even after anchoring to the central baseline, the rendered glyphs still sit
    // measurably above a shape's geometric center for the fonts this renders with (measured in
    // Chromium: ~0.83em regardless of shape size, for Korean + Latin fallback) - central-baseline
    // centers on the font's central-baseline table, which isn't identical to the ink/line-box
    // center for every font/script. Without this dy nudge every label reads slightly high.
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 60 34">',
            '<g class="node">',
            '<rect x="-30" y="-17" width="60" height="34"></rect>',
            '<text y="-10.1"><tspan class="row" x="0" y="-0.1em">시작</tspan></text>',
            "</g>",
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    const text = container.querySelector("text");
    const rowTspan = container.querySelector('tspan[x="0"]');
    expect(text?.getAttribute("dy")).toBe("0.83em");
    expect(rowTspan?.getAttribute("dy")).toBe("0.83em");
  });

  it("does not override an existing dy when the label already carries dominant-baseline", async () => {
    // The compensating nudge is only calibrated for the "central" baseline we force in; a
    // label mermaid already annotates with its own baseline must be left untouched.
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 60 34">',
            '<g class="node">',
            '<rect x="-30" y="-17" width="60" height="34"></rect>',
            '<text dominant-baseline="middle" dy="0.2em" x="0">시작</text>',
            "</g>",
            "</svg>",
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });

    const text = container.querySelector("text");
    expect(text?.getAttribute("dominant-baseline")).toBe("middle");
    expect(text?.getAttribute("dy")).toBe("0.2em");
  });

  it("never attaches renderer-controlled CSS to the live document head", async () => {
    const container = document.createElement("div");
    const attachedStyles = [];
    const observer = new MutationObserver((records) => {
      attachedStyles.push(...records
        .flatMap((record) => [...record.addedNodes])
        .filter((node) => node.nodeName === "STYLE"));
    });
    observer.observe(document.head, { childList: true });
    const mermaidAdapter = {
      async render() {
        return {
          svg: '<svg id="old" viewBox="0 0 10 10"><style>#old + .outside { color: red; }</style><text>Safe</text></svg>',
        };
      },
    };

    try {
      await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });
      attachedStyles.push(...observer.takeRecords()
        .flatMap((record) => [...record.addedNodes])
        .filter((node) => node.nodeName === "STYLE"));
      expect(container.querySelector("svg")).not.toBeNull();
      expect(attachedStyles).toEqual([]);
    } finally {
      observer.disconnect();
    }
  });

  it("rejects Mermaid stylesheet selectors and rules that can escape the SVG root", async () => {
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg id="old" viewBox="0 0 10 10">',
            '<style>#old + .outside { color: red; }</style>',
            '<style>#old ~ .outside { color: red; }</style>',
            '<style>#old, body { color: red; }</style>',
            '<style>#old:is(.node, body) { color: red; }</style>',
            '<style>:root { color: red; }</style>',
            '<style>html { color: red; }</style>',
            '<style>body { color: red; }</style>',
            '<style>@media all { #old { color: red; } }</style>',
            '<style>@import "unsafe.css";</style>',
            '<style>@namespace svg url(http://www.w3.org/2000/svg);</style>',
            '<style>@keyframes unsafe { from { opacity: 0; } to { opacity: 1; } }</style>',
            '<style>@font-face { font-family: unsafe; src: local(unsafe); }</style>',
            '<style>@page { margin: 0; }</style>',
            '<style>@property --unsafe { syntax: "*"; inherits: false; initial-value: red; }</style>',
            '<style>.node { background: url(https://example.com/a.svg); }</style>',
            '<g class="node"><rect width="5" height="5"></rect></g>',
            '</svg>',
          ].join(""),
        };
      },
    };

    await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });
    const css = [...container.querySelectorAll("style")].map((style) => style.textContent).join("\n");

    expect(css).toBe("");
    expect(container.querySelector("svg .node rect")).not.toBeNull();
  });

  it("retains only root-contained Mermaid stylesheet selectors", async () => {
    const container = document.createElement("div");
    const outside = document.createElement("span");
    outside.className = "node label outside";
    document.body.append(container, outside);
    const mermaidAdapter = {
      async render() {
        return {
          svg: [
            '<svg id="old" viewBox="0 0 10 10">',
            '<style>#old { fill: red; } #old .node > rect { stroke: #333; } .label { color: #111; }</style>',
            '<g class="node"><rect width="5" height="5"></rect><text class="label">Safe</text></g>',
            '</svg>',
          ].join(""),
        };
      },
    };

    try {
      await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });
      const rootId = container.querySelector("svg")?.id;
      const css = container.querySelector("style")?.textContent ?? "";

      expect(css).toContain(`#${rootId} {`);
      expect(css).toContain(`#${rootId} .node > rect {`);
      expect(css).toContain(`#${rootId} .label {`);
      expect(css).not.toContain("#old");
      expect(getComputedStyle(outside).color).not.toBe("rgb(17, 17, 17)");
    } finally {
      container.remove();
      outside.remove();
    }
  });

  it("uses a per-render nonce so duplicate author IDs cannot share Mermaid CSS scope", async () => {
    const randomUUID = vi.spyOn(crypto, "randomUUID")
      .mockReturnValue("c7d1ef05-1a85-4bcc-9dc2-08428579f85b");
    const container = document.createElement("div");
    const mermaidAdapter = {
      async render() {
        return {
          svg: '<svg id="old"><style>#old { color: rgb(17, 17, 17); }</style><text>Diagram</text></svg>',
        };
      },
    };

    try {
      await renderPreview([
        '<span id="markdown-diagram-1">Author content</span>',
        "",
        "~~~mermaid",
        "flowchart LR",
        "A --> B",
        "~~~",
      ].join("\n"), { container, mermaidAdapter });

      const author = container.querySelector("#markdown-diagram-1");
      const svg = container.querySelector("svg");
      expect(svg?.id).toBe("markdown-diagram-c7d1ef05-1a85-4bcc-9dc2-08428579f85b");
      expect(container.querySelector("style")?.textContent).toContain(`#${svg.id}`);
      expect(getComputedStyle(author).color).not.toBe("rgb(17, 17, 17)");
    } finally {
      randomUUID.mockRestore();
    }
  });

  it("keeps safe Mermaid SVG usable when detached stylesheet parsing is unavailable", async () => {
    const container = document.createElement("div");
    const cssStyleSheet = globalThis.CSSStyleSheet;
    const mermaidAdapter = {
      async render() {
        return {
          svg: '<svg viewBox="0 0 10 10"><style>.node { fill: red; }</style><rect class="node" width="5" height="5" fill="#fff"></rect><text>Safe</text></svg>',
        };
      },
    };

    vi.stubGlobal("CSSStyleSheet", undefined);
    try {
      await renderPreview("~~~mermaid\nflowchart LR\nA --> B\n~~~", { container, mermaidAdapter });
      expect(container.querySelector("style")).toBeNull();
      expect(container.querySelector("rect")?.getAttribute("fill")).toBe("#fff");
      expect(container.querySelector("text")?.textContent).toBe("Safe");
    } finally {
      vi.stubGlobal("CSSStyleSheet", cssStyleSheet);
    }
  });

  it("renders valid KaTeX and isolates malformed math", async () => {
    const html = await renderPreviewHtml("Inline $x^2$\n\nBroken $\\notacommand{$ still here");

    expect(html).toContain("katex");
    expect(html).toContain("data-render-error");
    expect(html).toContain("still here");
  });

  it("syntax-highlights fenced code", () => {
    const html = createMarkdownRenderer().render("~~~js\nconst answer = 42;\n~~~");

    expect(html).toContain('<code class="hljs language-js">');
    expect(html).toContain("hljs-keyword");
  });

  it("resolves relative images only for a saved file base directory", async () => {
    const html = await renderPreviewHtml("![logo](images/logo.png)", {
      assetBaseUrl: "https://document-assets.local/",
    });

    expect(html).toContain('src="https://document-assets.local/images/logo.png"');
  });

  it("preserves a valid encoded filename under the document asset origin", async () => {
    const html = await renderPreviewHtml("![logo](images/my%20logo.png)", {
      assetBaseUrl: "https://document-assets.local/",
    });

    expect(html).toContain('src="https://document-assets.local/images/my%20logo.png"');
    expect(html).not.toContain("data-local-image");
  });

  it("preserves a single-encoded UTF-8 Unicode filename", async () => {
    const html = await renderPreviewHtml("![logo](images/%ED%95%9C%EA%B8%80.png)", {
      assetBaseUrl: "https://document-assets.local/",
    });

    expect(html).toContain('src="https://document-assets.local/images/%ED%95%9C%EA%B8%80.png"');
  });

  it.each([
    "../secret.png",
    "%2e%2e/secret.png",
    "%252e%252e/secret.png",
    "%5c%5cserver/share.png",
    "%255c%255cserver/share.png",
    "%2fsecret.png",
    "%252fsecret.png",
    "%252f%252fserver/share.png",
    "images%2flogo.png",
    "images%2Flogo.png",
    "images%5clogo.png",
    "images/my%2520logo.png",
    "images/%25ED%2595%259C%25EA%25B8%2580.png",
    "images/percent%2525name.png",
    "%43%3a/secret.png",
    "%2543%253a/secret.png",
    "images/%zz.png",
    "images/%C0%AF.png",
    "images/%2500.png",
    "images/%250a.png",
    "https%253a%252f%252fexample.com/tracker.png",
    "/absolute.png",
    "https://example.com/tracker.png",
  ])("rejects escaping absolute and untrusted Markdown image %s", async (source) => {
    const html = await renderPreviewHtml(`![blocked](${source})`, {
      assetBaseUrl: "https://document-assets.local/",
    });

    const root = document.createElement("div");
    root.innerHTML = html;
    expect(root.querySelector("img")?.hasAttribute("src") ?? false).toBe(false);
  });

  it("rejects raw HTML that forges the local-image trust marker", async () => {
    const html = await renderPreviewHtml([
      '<img id="empty-forgery" data-local-image src="https://document-assets.local/secret.png">',
      '<img id="nonce-forgery" data-local-image="guessed-token" src="https://document-assets.local/secret.png">',
    ].join("\n"), { assetBaseUrl: "https://document-assets.local/" });
    const root = document.createElement("div");
    root.innerHTML = html;

    expect(root.querySelector("#empty-forgery")?.hasAttribute("src")).toBe(false);
    expect(root.querySelector("#nonce-forgery")?.hasAttribute("src")).toBe(false);
    expect(root.querySelector("[data-local-image]")).toBeNull();
  });

  it("applies tag-specific URI and inline-style resource restrictions", async () => {
    const html = await renderPreviewHtml([
      '<a id="safe-web" href="https://example.com/docs">safe</a>',
      '<a id="encoded-script" href="java&#x0a;script:alert(1)">bad</a>',
      '<img id="remote" src="https://example.com/tracker.png">',
      '<img id="protocol-relative" src="//example.com/tracker.png">',
      '<img id="unsafe-file" src="file:///C:/secret/hidden.png">',
      '<img id="relative" src="images/raw.png">',
      '<img id="data-image" src="data:image/png;base64,iVBORw0KGgo=">',
      '<span id="hostile-style" style="color:red;background-image:url(https://example.com/a.png)">x</span>',
      "![local](images/logo.png)",
      "",
      "$x^2$",
    ].join("\n"), { assetBaseUrl: "https://document-assets.local/" });
    const root = document.createElement("div");
    root.innerHTML = html;

    expect(root.querySelector("#safe-web")?.getAttribute("href"))
      .toBe("https://example.com/docs");
    expect(root.querySelector("#encoded-script")?.hasAttribute("href")).toBe(false);
    expect(root.querySelector("#remote")?.hasAttribute("src")).toBe(false);
    expect(root.querySelector("#protocol-relative")?.hasAttribute("src")).toBe(false);
    expect(root.querySelector("#unsafe-file")?.hasAttribute("src")).toBe(false);
    expect(root.querySelector("#relative")?.hasAttribute("src")).toBe(false);
    expect(root.querySelector("#data-image")?.getAttribute("src"))
      .toBe("data:image/png;base64,iVBORw0KGgo=");
    expect(root.querySelector('img[alt="local"]')?.getAttribute("src"))
      .toBe("https://document-assets.local/images/logo.png");
    expect(root.querySelector("#hostile-style")?.hasAttribute("style")).toBe(false);
    expect(root.querySelector(".katex [style]")).not.toBeNull();
    expect([...root.querySelectorAll("[style]")].some((element) => /url\s*\(/iu.test(element.style.cssText)))
      .toBe(false);
  });

  it("ships a restrictive local-only content security policy", () => {
    const page = new DOMParser().parseFromString(
      readFileSync(resolve(process.cwd(), "index.html"), "utf8"),
      "text/html",
    );
    const policy = page.querySelector('meta[http-equiv="Content-Security-Policy"]')?.content;

    expect(policy).toContain("default-src 'none'");
    expect(policy).toContain("script-src 'self'");
    expect(policy).toContain("connect-src 'none'");
    expect(policy).toContain("object-src 'none'");
    expect(policy).toContain("frame-src 'none'");
    expect(policy).toContain("img-src 'self' data: https://document-assets.local");
    expect(policy).not.toContain("file:");
    expect(policy).not.toMatch(/https?:\/\/(?!document-assets\.local(?:[\s;]|$))/iu);
  });
});

describe("extended markdown syntax", () => {
  it("renders footnotes, task lists, marks, scripts, emoji, and definition lists", async () => {
    const html = await renderPreviewHtml(
      "Term\n: Definition\n\n- [x] done\n\n==mark== H~2~O 2^10^ :rocket:\n\nReference[^n]\n\n[^n]: Note",
    );
    const root = document.createElement("div");
    root.innerHTML = html;

    expect(root.querySelector("dt")?.textContent).toBe("Term");
    expect(html).toContain('type="checkbox"');
    expect(html).toContain("<mark>mark</mark>");
    expect(html).toContain("H<sub>2</sub>O");
    expect(html).toContain("2<sup>10</sup>");
    expect(html).toContain("🚀");
    expect(html).toContain("footnote-ref");
  });

  it("gives headings safe explicit link targets", async () => {
    const html = await renderPreviewHtml("## 설치 방법 {#workflow}");

    expect(html).toContain('id="workflow"');
    expect(html).not.toContain("{#workflow}");
    expect(html).not.toContain("onclick");
  });
});
