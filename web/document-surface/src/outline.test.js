import { afterEach, describe, expect, it, vi } from "vitest";
import { mountDocumentSurface } from "./editor-app.js";
import { buildOutline } from "./outline.js";
import { goToAnchor } from "./source-map.js";
import { renderPreviewHtml } from "./preview.js";

const WINDOW_ID = "11111111-1111-4111-8111-111111111111";
const TAB_ID = "22222222-2222-4222-8222-222222222222";
const REQUEST_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

function activationEnvelope(revision, payload = {}) {
  return {
    version: 1,
    type: "document.activate",
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: revision,
    payload: {
      path: "C:\\docs\\readme.md",
      text: "",
      mode: "read",
      line: null,
      anchor: null,
      assetBaseUrl: "https://document-assets.local/",
      ...payload,
    },
  };
}

function createWebView() {
  const messages = [];
  let receiveMessage;
  return {
    messages,
    webview: {
      addEventListener(type, listener) {
        if (type === "message") receiveMessage = listener;
      },
      postMessage(message) {
        messages.push(message);
      },
    },
    receive(message) {
      return receiveMessage({ data: message });
    },
  };
}

function navigationEnvelope(type, revision, payload, overrides = {}) {
  return {
    version: 1,
    type,
    requestId: REQUEST_ID,
    windowId: WINDOW_ID,
    tabId: TAB_ID,
    documentRevision: revision,
    payload,
    ...overrides,
  };
}

afterEach(() => {
  vi.useRealTimers();
});

describe("document outline", () => {
  it("retains markdown-it-anchor IDs through preview sanitization", async () => {
    expect(await renderPreviewHtml("# Intro")).toContain('id="intro"');
  });

  it("keeps duplicate markdown-it-anchor IDs distinct after sanitization", async () => {
    const root = document.createElement("main");
    root.innerHTML = await renderPreviewHtml("# Intro\n\n# Intro");

    expect(buildOutline(root)).toEqual([
      { level: 1, text: "Intro", anchor: "intro", sourceLine: 1 },
      { level: 1, text: "Intro", anchor: "intro-1", sourceLine: 3 },
    ]);
  });

  it("posts the rendered outline using the active activation envelope", async () => {
    const root = document.createElement("main");
    root.innerHTML = '<article data-preview></article>';
    const harness = createWebView();
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
    });

    await harness.receive(activationEnvelope(4, { text: "# Intro\n\n## Details" }));

    expect(harness.messages.find((message) => message.type === "document.outline")).toEqual({
      version: 1,
      type: "document.outline",
      requestId: REQUEST_ID,
      windowId: WINDOW_ID,
      tabId: TAB_ID,
      documentRevision: 4,
      payload: {
        items: [
          { level: 1, text: "Intro", anchor: "intro", sourceLine: 1 },
          { level: 2, text: "Details", anchor: "details", sourceLine: 3 },
        ],
      },
    });
  });

  it("centers and temporarily highlights an anchor target", () => {
    vi.useFakeTimers();
    document.body.innerHTML = '<main><h1 id="intro">Intro</h1></main>';
    let element = null;
    let options = null;
    const scrollIntoView = function scrollIntoView(scrollOptions) {
      element = this;
      options = scrollOptions;
    };

    goToAnchor(document.querySelector("main"), "intro", scrollIntoView);

    expect(element).toBe(document.querySelector("#intro"));
    expect(options).toEqual({ block: "center" });
    expect(element.classList).toContain("is-navigation-target");
    vi.advanceTimersByTime(1500);
    expect(element.classList).not.toContain("is-navigation-target");
  });

  it("clears the prior highlight before highlighting a later navigation target", () => {
    vi.useFakeTimers();
    document.body.innerHTML = '<main><h1 id="intro">Intro</h1><h2 id="details">Details</h2></main>';
    const root = document.querySelector("main");

    goToAnchor(root, "intro");
    goToAnchor(root, "details");

    expect(root.querySelector("#intro").classList).not.toContain("is-navigation-target");
    expect(root.querySelector("#details").classList).toContain("is-navigation-target");
  });

  it("does not throw when CSS.escape produces an unusable selector", () => {
    const css = globalThis.CSS;
    vi.stubGlobal("CSS", { escape: () => "" });
    document.body.innerHTML = '<main><h1 id="intro">Intro</h1></main>';

    try {
      expect(() => goToAnchor(document.querySelector("main"), "intro")).not.toThrow();
      expect(goToAnchor(document.querySelector("main"), "intro")).toBeNull();
    } finally {
      vi.stubGlobal("CSS", css);
    }
  });

  it("uses CSS.escape to navigate to a special-character anchor", () => {
    const css = globalThis.CSS;
    const escape = vi.fn((value) => value.replace(":", "\\:"));
    vi.stubGlobal("CSS", { escape });
    document.body.innerHTML = '<main><h1 id="section:one">One</h1></main>';
    let target = null;

    try {
      goToAnchor(document.querySelector("main"), "section:one", function scrollIntoView() {
        target = this;
      });

      expect(escape).toHaveBeenCalledWith("section:one");
      expect(target).toBe(document.querySelector("#section\\:one"));
    } finally {
      vi.stubGlobal("CSS", css);
    }
  });

  it("rejects navigation commands outside the exact active request and payload shape", async () => {
    const root = document.createElement("main");
    root.innerHTML = '<article data-preview></article>';
    const harness = createWebView();
    let target = null;
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
      scrollIntoView(scrollOptions) {
        target = this;
      },
    });
    await harness.receive(activationEnvelope(4, { text: "# Intro" }));
    vi.useFakeTimers();

    await harness.receive(navigationEnvelope("navigation.goToAnchor", 3, { anchor: "intro" }));
    await harness.receive(navigationEnvelope("navigation.goToAnchor", 4, { anchor: "intro" }, {
      requestId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
    }));
    await harness.receive(navigationEnvelope("navigation.goToAnchor", 4, { anchor: "intro", line: 1 }));
    await harness.receive(navigationEnvelope("navigation.goToAnchor", 4, { anchor: " " }));
    await harness.receive(navigationEnvelope("navigation.goToLine", 4, { line: 1, anchor: "intro" }));
    expect(target).toBeNull();

    await harness.receive(navigationEnvelope("navigation.goToAnchor", 4, { anchor: "intro" }));

    expect(target).toBe(root.querySelector("#intro"));
    expect(target.classList).toContain("is-navigation-target");
    vi.advanceTimersByTime(1500);
    expect(target.classList).not.toContain("is-navigation-target");
  });

  it("clears a navigation highlight when a new activation replaces the document", async () => {
    vi.useFakeTimers();
    const root = document.createElement("main");
    root.innerHTML = '<article data-preview></article>';
    const harness = createWebView();
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
    });
    await harness.receive(activationEnvelope(4, { text: "# Intro" }));
    await harness.receive(navigationEnvelope("navigation.goToAnchor", 4, { anchor: "intro" }));
    const oldTarget = root.querySelector("#intro");

    await harness.receive(activationEnvelope(5, { text: "# Details" }));

    expect(oldTarget.classList).not.toContain("is-navigation-target");
  });

  it("applies the newest correlated navigation after the winning asynchronous render", async () => {
    const root = document.createElement("main");
    root.innerHTML = '<article data-preview></article>';
    const harness = createWebView();
    const pending = [];
    let target = null;
    const renderDocument = (source, { container }) => new Promise((resolve) => {
      pending.push({ source, container, resolve });
    });
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
      renderDocument,
      scrollIntoView() { target = this; },
    });

    const activation = harness.receive(activationEnvelope(5, {
      text: "# Intro\n\n## Details",
      anchor: "intro",
    }));
    await harness.receive(navigationEnvelope("navigation.goToLine", 5, { line: 3 }));
    await harness.receive(navigationEnvelope("navigation.goToAnchor", 5, { anchor: "details" }));
    pending[0].container.innerHTML = await renderPreviewHtml(pending[0].source);
    pending[0].resolve();
    await activation;

    expect(target).toBe(root.querySelector("#details"));
  });

  it("does not carry a pending navigation into a newer activation", async () => {
    const root = document.createElement("main");
    root.innerHTML = '<article data-preview></article>';
    const harness = createWebView();
    const pending = [];
    let target = null;
    const renderDocument = (source, { container }) => new Promise((resolve) => {
      pending.push({ source, container, resolve });
    });
    mountDocumentSurface(root, harness.webview, {
      bootstrapContext: { windowId: WINDOW_ID, tabId: TAB_ID },
      renderDocument,
      scrollIntoView() { target = this; },
    });

    const first = harness.receive(activationEnvelope(5, { text: "# First" }));
    await harness.receive(navigationEnvelope("navigation.goToAnchor", 5, { anchor: "first" }));
    const second = harness.receive(activationEnvelope(6, { text: "# Second", anchor: "second" }));
    pending[1].container.innerHTML = await renderPreviewHtml(pending[1].source);
    pending[1].resolve();
    await second;
    pending[0].container.innerHTML = await renderPreviewHtml(pending[0].source);
    pending[0].resolve();
    await first;

    expect(target).toBe(root.querySelector("#second"));
  });
});
