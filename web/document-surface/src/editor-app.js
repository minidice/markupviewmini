import {
  applyHostMessage,
  bindLinkInterception,
  createSurfaceState,
  isUuid,
  makeEnvelope,
} from "./bridge.js";
import { buildOutline } from "./outline.js";
import { createFindController } from "./find-controller.js";
import { createEditFindController } from "./edit-find-controller.js";
import { createEditorController } from "./editor-controller.js";
import { renderPreview } from "./preview.js";
import {
  cancelNavigationHighlight,
  goToAnchor,
  goToSourceLine,
} from "./source-map.js";
import { createTabStateStore } from "./tab-state-store.js";
import { focusMermaidAction } from "./mermaid-blocks.js";

function contextFor(state) {
  return {
    windowId: state.windowId,
    tabId: state.tabId,
    documentRevision: Math.max(0, state.revision),
  };
}

export function readBootstrapContext(search = globalThis.location?.search ?? "") {
  const parameters = new URLSearchParams(search);
  const windowId = parameters.get("windowId");
  const tabId = parameters.get("tabId");
  return isUuid(windowId) && isUuid(tabId) ? { windowId, tabId } : null;
}

function defaultScrollIntoView(options) {
  this.scrollIntoView?.(options);
}

function readNavigationMessage(state, message) {
  const payload = message?.payload;
  if (
    message?.version !== 1
    || !isUuid(message.requestId)
    || message.requestId !== state.requestId
    || message.windowId !== state.windowId
    || message.tabId !== state.tabId
    || message.documentRevision !== state.revision
    || !Number.isSafeInteger(state.activationToken)
    || state.activationToken < 1
    || typeof payload !== "object"
    || payload === null
    || Array.isArray(payload)
  ) return null;

  if (message.type === "navigation.goToLine"
    && Object.keys(payload).length === 1
    && Number.isSafeInteger(payload.line)
    && payload.line > 0) {
    return { kind: "line", value: payload.line };
  }
  if (message.type === "navigation.goToAnchor"
    && Object.keys(payload).length === 1
    && typeof payload.anchor === "string"
    && payload.anchor.trim() !== "") {
    return { kind: "anchor", value: payload.anchor };
  }
  return null;
}

function readFindMessage(state, message) {
  if (
    message?.version !== 1
    || !["find.open", "find.next", "find.previous", "find.close"].includes(message.type)
    || !isUuid(message.requestId)
    || message.requestId !== state.requestId
    || message.windowId !== state.windowId
    || message.tabId !== state.tabId
    || message.documentRevision !== state.revision
    || !Number.isSafeInteger(state.activationToken)
    || state.activationToken < 1
    || typeof message.payload !== "object"
    || message.payload === null
    || Array.isArray(message.payload)
    || Object.keys(message.payload).length !== 0
  ) return null;
  return message.type;
}

function createFindBar(root) {
  const bar = root.ownerDocument.createElement("section");
  bar.className = "document-find-bar";
  bar.dataset.findBar = "";
  bar.hidden = true;
  bar.setAttribute("aria-label", "Find in document");
  bar.innerHTML = [
    '<input type="search" data-find-query aria-label="Find text" autocomplete="off" spellcheck="false">',
    '<output data-find-count aria-live="polite">0 / 0</output>',
    '<button type="button" data-find-previous aria-label="Previous match">↑</button>',
    '<button type="button" data-find-next aria-label="Next match">↓</button>',
    '<label><input type="checkbox" data-find-match-case> Match case</label>',
    '<label><input type="checkbox" data-find-whole-word> Whole word</label>',
    '<label><input type="checkbox" data-find-regex> Regex</label>',
    '<span class="document-find-error" data-find-error role="alert" hidden></span>',
    '<button type="button" data-find-close aria-label="Close find">×</button>',
  ].join("");
  root.prepend(bar);

  const query = bar.querySelector("[data-find-query]");
  const count = bar.querySelector("[data-find-count]");
  const matchCase = bar.querySelector("[data-find-match-case]");
  const wholeWord = bar.querySelector("[data-find-whole-word]");
  const regex = bar.querySelector("[data-find-regex]");
  const error = bar.querySelector("[data-find-error]");
  let controller = null;

  const search = () => controller?.search(query.value, {
    matchCase: matchCase.checked,
    wholeWord: wholeWord.checked,
    useRegex: regex.checked,
  });
  query.addEventListener("input", search);
  matchCase.addEventListener("change", search);
  wholeWord.addEventListener("change", search);
  regex.addEventListener("change", search);
  bar.querySelector("[data-find-previous]").addEventListener("click", () => {
    controller?.previousMatch();
  });
  bar.querySelector("[data-find-next]").addEventListener("click", () => {
    controller?.nextMatch();
  });
  bar.querySelector("[data-find-close]").addEventListener("click", () => {
    controller?.closeFind();
    bar.hidden = true;
  });

  return {
    bind(value) {
      controller = value;
    },
    open() {
      bar.hidden = false;
      query.focus();
      query.select();
    },
    close() {
      bar.hidden = true;
    },
    isOpen() {
      return !bar.hidden;
    },
    current() {
      return {
        query: query.value,
        matchCase: matchCase.checked,
        wholeWord: wholeWord.checked,
        useRegex: regex.checked,
      };
    },
    restore(find = {}) {
      this.render({
        query: typeof find.query === "string" ? find.query : "",
        matchCase: find.matchCase === true,
        wholeWord: find.wholeWord === true,
        useRegex: find.useRegex === true,
        activeIndex: -1,
        total: 0,
        error: null,
      });
    },
    render(state) {
      if (query.value !== state.query) query.value = state.query;
      matchCase.checked = state.matchCase;
      wholeWord.checked = state.wholeWord;
      regex.checked = state.useRegex;
      count.textContent = state.total > 0 ? `${state.activeIndex + 1} / ${state.total}` : "0 / 0";
      error.textContent = state.error ?? "";
      error.hidden = state.error === null;
      query.setAttribute("aria-invalid", state.error === null ? "false" : "true");
    },
    dispose() {
      controller = null;
      bar.remove();
    },
  };
}

function readEditorCommand(state, message) {
  if (
    state.mode !== "edit"
    || message?.version !== 1
    || !["editor.undo", "editor.redo"].includes(message.type)
    || !isUuid(message.requestId)
    || message.requestId !== state.requestId
    || message.windowId !== state.windowId
    || message.tabId !== state.tabId
    || message.documentRevision !== state.revision
    || typeof message.payload !== "object"
    || message.payload === null
    || Array.isArray(message.payload)
    || Object.keys(message.payload).length !== 0
  ) return null;
  return message.type;
}

function createEditorWorkspace(root, preview) {
  const workspace = root.ownerDocument.createElement("section");
  workspace.className = "document-workspace";
  workspace.dataset.documentWorkspace = "";
  const editor = root.ownerDocument.createElement("section");
  editor.className = "document-editor";
  editor.dataset.editor = "";
  editor.hidden = true;
  preview.before(workspace);
  workspace.append(editor, preview);
  return {
    workspace,
    editor,
    dispose() {
      workspace.replaceWith(preview);
    },
  };
}

function createEditError(root) {
  const error = root.ownerDocument.createElement("p");
  error.className = "document-edit-error";
  error.dataset.editError = "";
  error.setAttribute("role", "alert");
  error.hidden = true;
  root.prepend(error);
  return {
    render(value) {
      error.textContent = value?.code === "edit-limit-exceeded"
        ? "Edit limit exceeded. Use at most 64 MiB of inserted text and 10,000 changed ranges at once."
        : "";
      error.hidden = value === null;
    },
    dispose() {
      error.remove();
    },
  };
}

export function mountDocumentSurface(root, webview = globalThis.chrome?.webview, options = {}) {
  const bootstrapContext = options.bootstrapContext ?? readBootstrapContext(options.locationSearch);
  if (!bootstrapContext) return null;
  const state = createSurfaceState(bootstrapContext);
  const preview = root.querySelector("[data-preview]") ?? root;
  if (preview !== root) preview.tabIndex = -1;
  const editorWorkspace = preview === root ? null : createEditorWorkspace(root, preview);
  const tabStore = createTabStateStore();
  const findBar = createFindBar(root);
  const editError = createEditError(root);
  const renderDocument = options.renderDocument ?? renderPreview;
  const scrollIntoView = options.scrollIntoView ?? defaultScrollIntoView;
  let modeFindController = null;
  let renderGeneration = 0;
  let renderInFlight = false;
  let pendingNavigation = null;
  let disposed = false;

  const post = (message) => webview?.postMessage(message);
  const unbindLinks = bindLinkInterception(root, () => contextFor(state), post);

  const renderSurface = async (text, revision) => {
    const generation = ++renderGeneration;
    const requestId = state.requestId;
    const tabId = state.tabId;
    const context = { windowId: state.windowId, tabId, documentRevision: revision };
    const staging = document.createElement("div");
    renderInFlight = true;
    preview.replaceChildren();

    try {
      await renderDocument(text, {
        container: staging,
        assetBaseUrl: state.assetBaseUrl,
        onMermaidEditRequested: (payload) => post(makeEnvelope(
          "mermaid.editRequested",
          context,
          payload,
          requestId,
        )),
      });
      if (disposed || generation !== renderGeneration
        || tabId !== state.tabId || revision !== state.revision) return;
      renderInFlight = false;
      preview.replaceChildren(...staging.childNodes);
      const navigation = pendingNavigation;
      pendingNavigation = null;
      if (navigation?.activationToken === state.activationToken) {
        if (navigation.kind === "line") goToSourceLine(preview, navigation.value, scrollIntoView);
        else goToAnchor(preview, navigation.value, scrollIntoView);
      } else if (state.anchor) goToAnchor(preview, state.anchor, scrollIntoView);
      else if (state.line !== null) goToSourceLine(preview, state.line, scrollIntoView);
      if (findBar.isOpen()) modeFindController.openFind();
      post(makeEnvelope("document.outline", context, { items: buildOutline(preview) }, requestId));
      post(makeEnvelope("document.rendered", context, {}, requestId));
    } catch {
      if (disposed || generation !== renderGeneration
        || tabId !== state.tabId || revision !== state.revision) return;
      renderInFlight = false;
      preview.replaceChildren();
      post(makeEnvelope("surface.error", context, { code: "render-failed" }, requestId));
    }
  };

  const editorController = createEditorController({
    host: { postMessage: (message) => webview?.postMessage(message) },
    store: tabStore,
    renderAccepted: (text, { tabId, revision }) => {
      if (disposed || tabId !== state.tabId) return;
      state.text = text;
      state.revision = revision;
      state.revisionsByTab.set(tabId, revision);
      void renderSurface(text, revision);
    },
    onViewChanged: (tabId, hints) => {
      if (tabId === state.tabId && hints) {
        const entry = tabStore.captureHints(tabId, hints);
        post(makeEnvelope(
          "document.uiHintsChanged",
          contextFor(state),
          {
            selection: hints.selection,
            scrollTop: hints.scrollTop,
            splitRatio: entry?.splitRatio ?? 0.5,
            find: {
              matchCase: entry?.find?.matchCase === true,
              wholeWord: entry?.find?.wholeWord === true,
              useRegex: entry?.find?.useRegex === true,
            },
          },
          state.requestId,
        ));
      }
      if (tabId === state.tabId && state.mode === "edit" && findBar.isOpen()) {
        modeFindController?.openFind();
      }
    },
    onEditError: (value) => editError.render(value),
    limits: options.editorLimits,
  });
  if (editorWorkspace) editorController.mount(editorWorkspace.editor);
  const readFindController = createFindController(preview, findBar);
  const editFindController = createEditFindController(() => editorController.view, findBar);
  const activeFindController = () => state.mode === "edit"
    ? editFindController
    : readFindController;
  modeFindController = {
    openFind() {
      const find = tabStore.activate(state.tabId)?.find ?? findBar.current();
      return activeFindController().search(find.query, find);
    },
    closeFind() {
      readFindController.closeFind();
      editFindController.closeFind();
    },
    search(query, find = {}) {
      const retained = { query, ...find };
      const entry = tabStore.captureHints(state.tabId, { find: retained });
      if (entry) {
        post(makeEnvelope("document.uiHintsChanged", contextFor(state), {
          selection: {
            anchor: entry.editorState.selection.main.anchor,
            head: entry.editorState.selection.main.head,
          },
          scrollTop: entry.scrollTop,
          splitRatio: entry.splitRatio,
          find: {
            matchCase: entry.find.matchCase,
            wholeWord: entry.find.wholeWord,
            useRegex: entry.find.useRegex,
          },
        }, state.requestId));
      }
      return activeFindController().search(query, find);
    },
    nextMatch() {
      return activeFindController().nextMatch();
    },
    previousMatch() {
      return activeFindController().previousMatch();
    },
  };
  findBar.bind(modeFindController);

  const onMessage = async (event) => {
    if (disposed) return;
    if (applyHostMessage(state, event.data)) {
      const editorState = editorController.hydrate(event.data);
      if (editorWorkspace) {
        editorWorkspace.editor.hidden = state.mode !== "edit";
        editorWorkspace.workspace.style.setProperty(
          "--editor-split-ratio",
          String((editorState?.splitRatio ?? 0.5) * 100) + "%",
        );
      }
      cancelNavigationHighlight();
      modeFindController.closeFind();
      findBar.close();
      findBar.restore(editorState?.find);
      pendingNavigation = null;
      await renderSurface(state.text, state.revision);
      return;
    }

    const focusPayload = event.data?.payload;
    if (event.data?.version === 1
      && event.data.type === "mermaid.focusRequested"
      && event.data.requestId === state.requestId
      && event.data.windowId === state.windowId
      && event.data.tabId === state.tabId
      && event.data.documentRevision === state.revision
      && typeof focusPayload === "object"
      && focusPayload !== null
      && !Array.isArray(focusPayload)
      && Object.keys(focusPayload).length === 2
      && typeof focusPayload.actionId === "string"
      && typeof focusPayload.actionOrigin === "string") {
      if (focusMermaidAction(root, focusPayload.actionId, focusPayload.actionOrigin)) {
        post({ ...event.data, type: "mermaid.focusCompleted" });
      }
      return;
    }

    if (editorController.handleHostMessage(event.data)) return;

    const editorCommand = readEditorCommand(state, event.data);
    if (editorCommand === "editor.undo") {
      editorController.undo();
      return;
    }
    if (editorCommand === "editor.redo") {
      editorController.redo();
      return;
    }

    const modePayload = event.data?.payload;
    if (event.data?.version === 1
      && event.data.type === "document.setEditorPreferences"
      && event.data.requestId === state.requestId
      && event.data.windowId === state.windowId
      && event.data.tabId === state.tabId
      && event.data.documentRevision === state.revision
      && typeof modePayload === "object"
      && modePayload !== null
      && !Array.isArray(modePayload)
      && Object.keys(modePayload).length === 2
      && Number.isFinite(modePayload.splitRatio)
      && modePayload.splitRatio >= 0.1
      && modePayload.splitRatio <= 0.9
      && typeof modePayload.find === "object"
      && modePayload.find !== null
      && !Array.isArray(modePayload.find)
      && Object.keys(modePayload.find).length === 3
      && ["matchCase", "wholeWord", "useRegex"].every(
        (key) => typeof modePayload.find[key] === "boolean",
      )) {
      const entry = tabStore.captureHints(state.tabId, {
        splitRatio: modePayload.splitRatio,
        find: { ...modePayload.find, query: findBar.current().query },
      });
      if (editorWorkspace && entry) {
        editorWorkspace.workspace.style.setProperty(
          "--editor-split-ratio",
          String(entry.splitRatio * 100) + "%",
        );
      }
      findBar.restore(entry?.find);
      return;
    }

    if (event.data?.version === 1
      && event.data.type === "document.setMode"
      && event.data.requestId === state.requestId
      && event.data.windowId === state.windowId
      && event.data.tabId === state.tabId
      && event.data.documentRevision === state.revision
      && typeof modePayload === "object"
      && modePayload !== null
      && !Array.isArray(modePayload)
      && Object.keys(modePayload).length === 1
      && ["read", "edit"].includes(modePayload.mode)) {
      const previousFindController = activeFindController();
      state.mode = modePayload.mode;
      editorController.clearEditError();
      tabStore.captureHints(state.tabId, { mode: state.mode });
      if (editorWorkspace) editorWorkspace.editor.hidden = state.mode !== "edit";
      if (state.mode === "edit") editorController.view?.focus();
      else preview.focus();
      previousFindController.closeFind();
      if (findBar.isOpen()) modeFindController.openFind();
      post(makeEnvelope("document.modeChanged", contextFor(state), { mode: state.mode }, state.requestId));
      return;
    }

    const navigation = readNavigationMessage(state, event.data);
    if (navigation) {
      if (renderInFlight) {
        pendingNavigation = { ...navigation, activationToken: state.activationToken };
        return;
      }
      if (navigation.kind === "line") goToSourceLine(preview, navigation.value, scrollIntoView);
      else goToAnchor(preview, navigation.value, scrollIntoView);
      return;
    }

    const findMessage = readFindMessage(state, event.data);
    if (findMessage === "find.open") {
      findBar.open();
      modeFindController.openFind();
    } else if (findMessage === "find.next") {
      modeFindController.nextMatch();
    } else if (findMessage === "find.previous") {
      modeFindController.previousMatch();
    } else if (findMessage === "find.close") {
      modeFindController.closeFind();
      findBar.close();
    }
  };

  webview?.addEventListener("message", onMessage);
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    state.activationToken += 1;
    renderGeneration += 1;
    webview?.removeEventListener?.("message", onMessage);
    unbindLinks();
    readFindController.dispose();
    editFindController.dispose();
    findBar.dispose();
    editError.dispose();
    editorController.dispose();
    editorWorkspace?.dispose();
  };
  state.editorController = editorController;
  state.tabStore = tabStore;
  state.dispose = dispose;
  post(makeEnvelope("surface.ready", contextFor(state)));
  return state;
}

const root = typeof document === "undefined" ? null : document.querySelector("#document-surface");
const bootstrapContext = readBootstrapContext();
if (root && bootstrapContext) mountDocumentSurface(root, undefined, { bootstrapContext });
