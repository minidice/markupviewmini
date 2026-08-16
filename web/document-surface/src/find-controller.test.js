import { afterEach, describe, expect, it } from "vitest";
import { mountDocumentSurface } from "./editor-app.js";
import { createFindController } from "./find-controller.js";

const WINDOW_ID = "11111111-1111-4111-8111-111111111111";
const TAB_ID = "22222222-2222-4222-8222-222222222222";
const REQUEST_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

function activationEnvelope(revision, text) {
  return {
    version: 1,
    type: "document.activate",
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: revision,
    payload: {
      path: "C:\\docs\\readme.md",
      text,
      mode: "read",
      line: null,
      anchor: null,
      assetBaseUrl: "https://document-assets.local/",
    },
  };
}

function findEnvelope(type, revision, overrides = {}) {
  return {
    version: 1,
    type,
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: revision,
    payload: {},
    ...overrides,
  };
}

function createWebView() {
  let receiveMessage;
  return {
    webview: {
      addEventListener(type, listener) {
        if (type === "message") receiveMessage = listener;
      },
      postMessage() {},
    },
    receive(message) {
      return receiveMessage({ data: message });
    },
  };
}

function createController(html, renderedStates = []) {
  document.body.innerHTML = `<main>${html}</main>`;
  const root = document.querySelector("main");
  const controller = createFindController(root, {
    render(state) {
      renderedStates.push({ ...state });
    },
  });
  return { controller, root };
}

afterEach(() => {
  document.body.replaceChildren();
});

describe("rendered-text find controller", () => {
  it("matches literals with case, Unicode whole-word, and regular-expression options", () => {
    const { controller } = createController("Alpha alpha alphabet 고양 고양이 123 1234");

    expect(controller.search("alpha", {
      matchCase: false,
      wholeWord: true,
      regex: false,
    })).toHaveLength(2);
    expect(controller.search("Alpha", {
      matchCase: true,
      wholeWord: true,
      regex: false,
    })).toHaveLength(1);
    expect(controller.search("고양", {
      matchCase: false,
      wholeWord: true,
      regex: false,
    })).toHaveLength(1);
    expect(controller.search("\\d{3}", {
      matchCase: false,
      wholeWord: true,
      regex: true,
    })).toHaveLength(1);
  });

  it("treats text split by inline markup and links as one logical rendered match", () => {
    const states = [];
    const { controller, root } = createController(
      '<p>Al<em>ph</em><a href="chapter.md">a</a></p>',
      states,
    );

    expect(controller.search("Alpha", { matchCase: true })).toHaveLength(1);
    expect(root.querySelectorAll("mark.document-find-match")).toHaveLength(3);
    expect([...root.querySelectorAll("mark.document-find-match")]
      .map((mark) => mark.textContent).join(""))
      .toBe("Alpha");
    expect(states.at(-1)).toMatchObject({ activeIndex: 0, total: 1 });
    expect(root.querySelector("a")?.getAttribute("href")).toBe("chapter.md");
  });

  it("exposes one aria-current representative while styling every active fragment", () => {
    const { controller, root } = createController('<p>Al<em>ph</em><a href="#">a</a></p>');

    controller.search("Alpha", {});
    const fragments = [...root.querySelectorAll("mark.document-find-match")];

    expect(fragments).toHaveLength(3);
    expect(fragments.every((mark) => mark.classList.contains("is-document-find-active")))
      .toBe(true);
    expect(fragments.filter((mark) => mark.getAttribute("aria-current") === "true"))
      .toEqual([fragments[0]]);
  });

  it("does not join matches or count structural whitespace across block boundaries", () => {
    const { controller } = createController("<p>Alpha</p>\n  <p>Beta</p>");

    expect(controller.search("AlphaBeta", {})).toEqual([]);
    expect(controller.search("\\s+", { regex: true })).toEqual([]);
  });

  it("matches a rendered space for collapsible soft-break whitespace and restores raw text", () => {
    const { controller, root } = createController("<p>Alpha\nBeta</p>");
    const originalHtml = root.innerHTML;

    expect(controller.search("Alpha Beta", {})).toHaveLength(1);
    expect(root.querySelector("mark.document-find-match")?.textContent).toBe("Alpha\nBeta");

    controller.closeFind();
    expect(root.innerHTML).toBe(originalHtml);
  });

  it("collapses whitespace across inline nodes but preserves semantic preformatted text", () => {
    const { controller, root } = createController([
      "<p>Alpha \n<em>\t Beta</em></p>",
      "<pre>Alpha\nBeta</pre>",
      '<span id="pre-wrap" style="white-space: pre-wrap">Gamma\nDelta</span>',
    ].join(""));
    const originalHtml = root.innerHTML;

    expect(controller.search("Alpha Beta", {})).toHaveLength(1);
    expect([...root.querySelectorAll("p mark.document-find-match")]
      .map((mark) => mark.textContent).join(""))
      .toBe("Alpha \n\t Beta");
    controller.closeFind();
    expect(root.innerHTML).toBe(originalHtml);

    expect(controller.search("Alpha\nBeta", { matchCase: true })).toHaveLength(1);
    expect(root.querySelector("pre mark.document-find-match")?.textContent).toBe("Alpha\nBeta");
    controller.closeFind();

    expect(controller.search("Gamma\nDelta", { matchCase: true })).toHaveLength(1);
    expect(root.querySelector("#pre-wrap mark.document-find-match")?.textContent)
      .toBe("Gamma\nDelta");
  });

  it("keeps combining marks and Unicode connector punctuation inside whole words", () => {
    const { controller } = createController("e\u0301lan e foo\u203fbar foo");

    expect(controller.search("e", { wholeWord: true })).toHaveLength(1);
    expect(controller.search("foo", { wholeWord: true })).toHaveLength(1);
  });

  it("turns invalid and zero-length regular expressions into validation state", () => {
    const states = [];
    const { controller, root } = createController("Alpha beta", states);

    expect(() => controller.search("[", { regex: true })).not.toThrow();
    expect(states.at(-1)).toMatchObject({
      query: "[",
      useRegex: true,
      activeIndex: -1,
      total: 0,
      error: expect.any(String),
    });
    expect(() => controller.search("a*", { regex: true })).not.toThrow();
    expect(states.at(-1)).toMatchObject({
      query: "a*",
      activeIndex: -1,
      total: 0,
      error: expect.any(String),
    });
    expect(root.querySelector("mark.document-find-match")).toBeNull();

    root.replaceChildren();
    expect(controller.search("^", { regex: true })).toEqual([]);
    expect(states.at(-1)).toMatchObject({
      query: "^",
      total: 0,
      error: expect.any(String),
    });
  });

  it("moves the active result in both directions with wrapping and renders counters", () => {
    const states = [];
    const { controller, root } = createController("one one", states);

    const matches = controller.search("one", {});
    expect(matches).toHaveLength(2);
    expect(states.at(-1)).toMatchObject({ activeIndex: 0, total: 2, error: null });
    expect(matches[0].getAttribute("aria-current")).toBe("true");
    expect(matches[1].hasAttribute("aria-current")).toBe(false);

    expect(controller.nextMatch()).toBe(matches[1]);
    expect(states.at(-1)).toMatchObject({ activeIndex: 1, total: 2 });
    expect(controller.nextMatch()).toBe(matches[0]);
    expect(states.at(-1)).toMatchObject({ activeIndex: 0, total: 2 });
    expect(controller.previousMatch()).toBe(matches[1]);
    expect(states.at(-1)).toMatchObject({ activeIndex: 1, total: 2 });
    expect(root.querySelectorAll('[aria-current="true"]')).toHaveLength(1);

    expect(controller.search("missing", {})).toEqual([]);
    expect(states.at(-1)).toMatchObject({ activeIndex: -1, total: 0, error: null });
    expect(controller.nextMatch()).toBeNull();
    expect(controller.previousMatch()).toBeNull();
  });

  it("scrolls initial, next, previous, and wrapped active matches into view", () => {
    const { controller } = createController("one one one");
    const scrolled = [];
    const originalScrollIntoView = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function scrollIntoView(options) {
      scrolled.push({ element: this, options });
    };

    try {
      const matches = controller.search("one", {});
      controller.nextMatch();
      controller.previousMatch();
      controller.previousMatch();

      expect(scrolled).toEqual([
        { element: matches[0], options: { block: "center" } },
        { element: matches[1], options: { block: "center" } },
        { element: matches[0], options: { block: "center" } },
        { element: matches[2], options: { block: "center" } },
      ]);
    } finally {
      if (originalScrollIntoView) Element.prototype.scrollIntoView = originalScrollIntoView;
      else delete Element.prototype.scrollIntoView;
    }
  });

  it("skips non-document and hidden subtrees while preserving rendered structure", () => {
    const { controller, root } = createController([
      '<p data-source-start="4" data-source-end="6">Alpha <a href="chapter.md#part">alpha</a></p>',
      "<script>alpha</script>",
      "<style>.alpha { color: red; }</style>",
      "<span hidden>alpha</span>",
      '<span aria-hidden="true">alpha</span>',
      '<span class="katex"><span>alpha</span></span>',
      '<div class="mermaid-diagram"><svg><text>alpha</text></svg></div>',
      '<div data-mermaid-source><code>alpha</code></div>',
      '<svg><text>alpha</text></svg>',
    ].join(""));
    const source = root.querySelector("p");
    const link = root.querySelector("a");
    const katex = root.querySelector(".katex");
    const mermaid = root.querySelector(".mermaid-diagram");
    const sourceAttributes = {
      start: source.getAttribute("data-source-start"),
      end: source.getAttribute("data-source-end"),
    };
    const href = link.getAttribute("href");
    const katexHtml = katex.innerHTML;
    const mermaidHtml = mermaid.innerHTML;

    expect(controller.search("alpha", { matchCase: false })).toHaveLength(2);
    expect(source.getAttribute("data-source-start")).toBe(sourceAttributes.start);
    expect(source.getAttribute("data-source-end")).toBe(sourceAttributes.end);
    expect(link.getAttribute("href")).toBe(href);
    expect(katex.innerHTML).toBe(katexHtml);
    expect(mermaid.innerHTML).toBe(mermaidHtml);
  });

  it("removes only owned marks on query replacement, close, and disposal", () => {
    const states = [];
    const { controller, root } = createController("<p><mark>author</mark> alpha beta</p>", states);
    const originalHtml = root.innerHTML;

    controller.search("alpha", {});
    expect(root.querySelectorAll("mark.document-find-match")).toHaveLength(1);
    controller.search("beta", {});
    expect(root.textContent).toBe("author alpha beta");
    expect(root.querySelectorAll("mark.document-find-match")).toHaveLength(1);
    expect(root.querySelector("mark:not(.document-find-match)")?.textContent).toBe("author");

    controller.closeFind();
    expect(root.innerHTML).toBe(originalHtml);
    expect(states.at(-1)).toMatchObject({ activeIndex: -1, total: 0, error: null });

    controller.search("alpha", {});
    controller.dispose();
    expect(root.innerHTML).toBe(originalHtml);
    expect(controller.search("alpha", {})).toEqual([]);
  });
});

describe("document-surface find integration", () => {
  it("opens the find bar, renders validation and counters, and handles typed navigation", async () => {
    document.body.innerHTML = [
      '<main id="surface">',
      '<article data-preview></article>',
      "</main>",
    ].join("");
    const root = document.querySelector("#surface");
    const harness = createWebView();
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
    });
    await harness.receive(activationEnvelope(3, "Alpha alpha"));

    await harness.receive(findEnvelope("find.open", 3));
    const bar = root.querySelector("[data-find-bar]");
    const input = bar.querySelector("[data-find-query]");
    expect(bar.hidden).toBe(false);

    input.value = "alpha";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    expect(root.querySelectorAll("mark.document-find-match")).toHaveLength(2);
    expect(bar.querySelector("[data-find-count]").textContent).toBe("1 / 2");

    await harness.receive(findEnvelope("find.next", 3));
    expect(root.querySelectorAll("mark.document-find-match")[1].getAttribute("aria-current"))
      .toBe("true");
    await harness.receive(findEnvelope("find.previous", 3));
    expect(root.querySelectorAll("mark.document-find-match")[0].getAttribute("aria-current"))
      .toBe("true");

    const regex = bar.querySelector("[data-find-regex]");
    regex.checked = true;
    regex.dispatchEvent(new Event("change", { bubbles: true }));
    input.value = "[";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    expect(bar.querySelector("[data-find-error]").hidden).toBe(false);
    expect(bar.querySelector("[data-find-error]").textContent).not.toBe("");
    expect(root.querySelector("mark.document-find-match")).toBeNull();

    await harness.receive(findEnvelope("find.close", 3));
    expect(bar.hidden).toBe(true);
    expect(root.querySelector("mark.document-find-match")).toBeNull();
  });

  it("rejects stale find commands and clears owned marks before a new activation renders", async () => {
    document.body.innerHTML = '<main id="surface"><article data-preview></article></main>';
    const root = document.querySelector("#surface");
    const harness = createWebView();
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
    });
    await harness.receive(activationEnvelope(4, "old old"));

    await harness.receive(findEnvelope("find.open", 3));
    expect(root.querySelector("[data-find-bar]").hidden).toBe(true);
    await harness.receive(findEnvelope("find.open", 4));
    const input = root.querySelector("[data-find-query]");
    input.value = "old";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    expect(root.querySelectorAll("mark.document-find-match")).toHaveLength(2);

    const oldPreview = root.querySelector("[data-preview]");
    await harness.receive(activationEnvelope(5, "new document"));

    expect(oldPreview.querySelector("mark.document-find-match")).toBeNull();
    expect(root.querySelector("[data-find-bar]").hidden).toBe(true);
    expect(root.querySelector("[data-preview]").textContent.trim()).toBe("new document");
  });

  it("reapplies text entered while an accepted document render is still in flight", async () => {
    document.body.innerHTML = '<main id="surface"><article data-preview></article></main>';
    const root = document.querySelector("#surface");
    const harness = createWebView();
    let finishRender;
    const renderDocument = (source, { container }) => new Promise((resolve) => {
      finishRender = () => {
        container.innerHTML = `<p>${source}</p>`;
        resolve();
      };
    });
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
      renderDocument,
    });

    const activation = harness.receive(activationEnvelope(6, "Target target"));
    await harness.receive(findEnvelope("find.open", 6));
    const input = root.querySelector("[data-find-query]");
    input.value = "target";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    expect(root.querySelector("mark.document-find-match")).toBeNull();

    finishRender();
    await activation;

    expect(root.querySelectorAll("mark.document-find-match")).toHaveLength(2);
    expect(root.querySelector("[data-find-count]").textContent).toBe("1 / 2");
  });
});
