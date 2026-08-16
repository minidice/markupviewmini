const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
export const DOCUMENT_ASSET_BASE_URL = "https://document-assets.local/";

export function isUuid(value) {
  return typeof value === "string" && UUID_PATTERN.test(value);
}

export function createSurfaceState(context = {}) {
  return {
    windowId: context.windowId ?? "",
    tabId: context.tabId ?? "",
    requestId: null,
    revision: -1,
    revisionsByTab: new Map(),
    activationToken: 0,
    path: "",
    text: "",
    dirty: false,
    mode: "read",
    line: null,
    anchor: null,
    assetBaseUrl: "",
  };
}

export function applyHostMessage(state, message) {
  const payload = message?.payload;
  const findKeys = payload?.find && typeof payload.find === "object" && !Array.isArray(payload.find)
    ? Object.keys(payload.find)
    : [];
  if (
    message?.version !== 1
    || message.type !== "document.activate"
    || !isUuid(message.requestId)
    || !isUuid(message.windowId)
    || !isUuid(message.tabId)
    || !Number.isSafeInteger(message.documentRevision)
    || message.documentRevision < 0
    || typeof payload !== "object"
    || payload === null
    || Array.isArray(payload)
    || !["read", "edit"].includes(payload.mode)
    || typeof payload.path !== "string"
    || typeof payload.text !== "string"
    || !(payload.dirty === undefined || typeof payload.dirty === "boolean")
    || !(payload.line === null || (Number.isSafeInteger(payload.line) && payload.line > 0))
    || !(payload.anchor === null || typeof payload.anchor === "string")
    || payload.assetBaseUrl !== DOCUMENT_ASSET_BASE_URL
    || !(payload.splitRatio === undefined
      || (Number.isFinite(payload.splitRatio)
        && payload.splitRatio >= 0.1
        && payload.splitRatio <= 0.9))
    || !(payload.find === undefined
      || (findKeys.length === 3
        && ["matchCase", "wholeWord", "useRegex"].every(
          (key) => findKeys.includes(key) && typeof payload.find[key] === "boolean",
        )))
    || (state.windowId && message.windowId !== state.windowId)
  ) {
    return false;
  }

  const previousRevision = state.revisionsByTab.get(message.tabId) ?? -1;
  if (message.documentRevision < previousRevision) return false;

  state.revisionsByTab.set(message.tabId, message.documentRevision);
  state.windowId = message.windowId;
  state.tabId = message.tabId;
  state.requestId = message.requestId;
  state.revision = message.documentRevision;
  state.activationToken += 1;
  state.path = payload.path;
  state.text = payload.text;
  state.dirty = payload.dirty === true;
  state.mode = payload.mode;
  state.line = payload.line;
  state.anchor = payload.anchor;
  state.assetBaseUrl = payload.assetBaseUrl;
  return true;
}

export function makeEnvelope(type, context, payload = {}, requestId = crypto.randomUUID()) {
  return {
    version: 1,
    type,
    requestId,
    windowId: context.windowId,
    tabId: context.tabId,
    documentRevision: context.documentRevision,
    payload,
  };
}

export function bindLinkInterception(root, getContext, postMessage) {
  const postLink = (event, disposition) => {
    const element = event.target instanceof Element ? event.target : event.target?.parentElement;
    const anchor = element?.closest("a[href]");
    if (!anchor || !root.contains(anchor)) return;

    event.preventDefault();
    postMessage(makeEnvelope("link.open", getContext(), {
      href: anchor.getAttribute("href"),
      disposition,
    }));
  };

  const onClick = (event) => {
    if (event.button !== 0) {
      if (event.button === 1) event.preventDefault();
      return;
    }
    postLink(event, event.ctrlKey ? "newTab" : "default");
  };

  const onAuxClick = (event) => {
    if (event.button !== 1) return;
    postLink(event, "newTab");
  };

  const onContextMenu = (event) => {
    const element = event.target instanceof Element ? event.target : event.target?.parentElement;
    const anchor = element?.closest("a[href]");
    if (!anchor || !root.contains(anchor)) return;

    event.preventDefault();
    postMessage(makeEnvelope("link.contextMenu", getContext(), {
      href: anchor.getAttribute("href"),
    }));
  };

  root.addEventListener("click", onClick);
  root.addEventListener("auxclick", onAuxClick);
  root.addEventListener("contextmenu", onContextMenu);
  return () => {
    root.removeEventListener("click", onClick);
    root.removeEventListener("auxclick", onAuxClick);
    root.removeEventListener("contextmenu", onContextMenu);
  };
}
