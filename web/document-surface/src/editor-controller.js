import { Compartment, StateEffect } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { redo, undo } from "@codemirror/commands";
import {
  createMermaidEditPayload,
  createMermaidGutter,
  findMermaidBlocks,
} from "./mermaid-blocks.js";

const MIB = 1024 * 1024;
export const EDITOR_LIMITS = Object.freeze({
  ordinaryInserted: 16 * MIB,
  chunkInserted: MIB,
  totalInserted: 64 * MIB,
  maxChanges: 10_000,
});

export function editWithinLimits(
  changeCount,
  insertedTotal,
  limits = EDITOR_LIMITS,
) {
  return Number.isSafeInteger(changeCount)
    && Number.isSafeInteger(insertedTotal)
    && changeCount >= 0
    && insertedTotal >= 0
    && changeCount <= limits.maxChanges
    && insertedTotal <= limits.totalInserted;
}

function boundedLimits(overrides = {}) {
  const read = (key) => Number.isSafeInteger(overrides[key]) && overrides[key] > 0
    ? Math.min(overrides[key], EDITOR_LIMITS[key])
    : EDITOR_LIMITS[key];
  return {
    ordinaryInserted: read("ordinaryInserted"),
    chunkInserted: read("chunkInserted"),
    totalInserted: read("totalInserted"),
    maxChanges: read("maxChanges"),
  };
}

function rawLineStarts(text) {
  const starts = [0];
  for (let index = 0; index < text.length;) {
    if (text[index] === "\r") {
      index += text[index + 1] === "\n" ? 2 : 1;
      starts.push(index);
    } else if (text[index] === "\n") {
      index += 1;
      starts.push(index);
    } else {
      index += 1;
    }
  }
  return starts;
}

function rawOffset(document, rawStarts, position) {
  const line = document.lineAt(position);
  const start = rawStarts[line.number - 1];
  return start === undefined ? null : start + position - line.from;
}

function normalizeInsertedText(text, preferredNewline) {
  return text.replace(/\r\n?|\n/gu, preferredNewline);
}

function readChanges(transaction, rawText, preferredNewline) {
  const starts = rawLineStarts(rawText);
  if (starts.length !== transaction.startState.doc.lines) return null;
  const changes = [];
  transaction.changes.iterChanges((fromA, toA, _fromB, _toB, inserted) => {
    changes.push({
      from: rawOffset(transaction.startState.doc, starts, fromA),
      to: rawOffset(transaction.startState.doc, starts, toA),
      insertedText: normalizeInsertedText(inserted.toString(), preferredNewline),
    });
  });
  if (changes.some((change) => change.from === null || change.to === null)) return null;
  return changes;
}

export function applyRawChanges(rawText, changes) {
  const segments = [];
  let cursor = 0;
  for (const change of changes) {
    segments.push(rawText.slice(cursor, change.from), change.insertedText);
    cursor = change.to;
  }
  segments.push(rawText.slice(cursor));
  return segments.join("");
}

function envelope(type, entry, payload) {
  return {
    version: 1,
    type,
    requestId: entry.requestId,
    windowId: entry.windowId,
    tabId: entry.tabId,
    documentRevision: entry.revision,
    payload,
  };
}

function chunkEnd(text, offset, limit) {
  let end = Math.min(offset + limit, text.length);
  const endsWithHighSurrogate = end < text.length
    && text.charCodeAt(end - 1) >= 0xD800
    && text.charCodeAt(end - 1) <= 0xDBFF
    && text.charCodeAt(end) >= 0xDC00
    && text.charCodeAt(end) <= 0xDFFF;
  if (endsWithHighSurrogate) end -= 1;
  return end > offset ? end : null;
}

function serialize(entry, changes, limits) {
  const insertedTotal = changes.reduce((total, change) => total + change.insertedText.length, 0);
  if (!editWithinLimits(changes.length, insertedTotal, limits)) return null;
  if (insertedTotal <= limits.ordinaryInserted) {
    return [envelope("document.changed", entry, {
      expectedRevision: entry.revision,
      changes,
    })];
  }

  const batchId = crypto.randomUUID();
  const messages = [envelope("document.changeBatchStart", entry, {
    batchId,
    expectedRevision: entry.revision,
    changes: changes.map(({ from, to, insertedText }) => ({
      from,
      to,
      insertedLength: insertedText.length,
    })),
  })];
  for (let changeIndex = 0; changeIndex < changes.length; changeIndex += 1) {
    const change = changes[changeIndex];
    for (let offset = 0; offset < change.insertedText.length;) {
      const end = chunkEnd(change.insertedText, offset, limits.chunkInserted);
      if (end === null) return null;
      messages.push(envelope("document.changeBatchChunk", entry, {
        batchId,
        changeIndex,
        offset,
        text: change.insertedText.slice(offset, end),
      }));
      offset = end;
    }
  }
  messages.push(envelope("document.changeBatchCommit", entry, { batchId }));
  return messages;
}

export function createEditorController({
  host,
  store,
  renderAccepted = () => {},
  onViewChanged = () => {},
  onEditError = () => {},
  limits: limitOverrides,
}) {
  const limits = boundedLimits(limitOverrides);
  const queues = new Map();
  const mermaidActions = new Compartment();
  let view = null;
  let parent = null;
  let disposed = false;

  const requestMermaidEdit = async (actionPayload, candidate) => {
    const entry = store.activate(store.activeTabId);
    const queue = entry ? queues.get(entry.tabId) : null;
    const snapshot = entry ? {
      requestId: entry.requestId,
      windowId: entry.windowId,
      tabId: entry.tabId,
      revision: entry.revision,
      rawText: entry.rawText,
    } : null;
    const block = entry
      ? findMermaidBlocks(entry.rawText)
        .find((item) => item.openingLine === candidate.openingLine)
      : null;
    if (!entry
      || !queue
      || queue.awaitingResync
      || queue.pending.length !== 0
      || queue.acceptedRevision !== snapshot.revision
      || queue.acceptedRawText !== snapshot.rawText
      || !block
      || disposed) return;
    const payload = {
      ...await createMermaidEditPayload(block),
      actionId: actionPayload.actionId,
      actionOrigin: actionPayload.actionOrigin,
    };
    const current = store.activate(store.activeTabId);
    const currentQueue = current ? queues.get(current.tabId) : null;
    if (disposed
      || !current
      || !currentQueue
      || current.requestId !== snapshot.requestId
      || current.windowId !== snapshot.windowId
      || current.tabId !== snapshot.tabId
      || current.revision !== snapshot.revision
      || current.rawText !== snapshot.rawText
      || currentQueue.awaitingResync
      || currentQueue.pending.length !== 0) return;
    host.postMessage(envelope("mermaid.editRequested", snapshot, payload));
  };

  const requestMermaidReopen = async (message, entry, queue) => {
    const { from, sourceHash, actionId, actionOrigin } = message.payload;
    const snapshot = {
      requestId: entry.requestId,
      windowId: entry.windowId,
      tabId: entry.tabId,
      revision: entry.revision,
      rawText: entry.rawText,
    };
    const candidates = await Promise.all(findMermaidBlocks(entry.rawText)
      .map(async (block) => ({ block, payload: await createMermaidEditPayload(block) })));
    const matching = candidates.filter((candidate) => candidate.payload.sourceHash === sourceHash);
    const requested = candidates.find((candidate) => candidate.block.from === from)
      ?? (matching.length === 1 ? matching[0] : null);
    const current = store.activate(store.activeTabId);
    const currentQueue = current ? queues.get(current.tabId) : null;
    if (!requested
      || disposed
      || !current
      || !currentQueue
      || current.requestId !== snapshot.requestId
      || current.windowId !== snapshot.windowId
      || current.tabId !== snapshot.tabId
      || current.revision !== snapshot.revision
      || current.rawText !== snapshot.rawText
      || currentQueue.awaitingResync
      || currentQueue.pending.length !== 0
      || currentQueue.acceptedRevision !== snapshot.revision
      || currentQueue.acceptedRawText !== snapshot.rawText) return;
    host.postMessage(envelope("mermaid.editRequested", snapshot, {
      ...requested.payload,
      actionId,
      actionOrigin,
    }));
  };

  const mermaidGutter = createMermaidGutter(requestMermaidEdit);

  const installMermaidActions = (entry) => {
    if (mermaidActions.get(entry.editorState) !== undefined) return;
    entry.editorState = entry.editorState.update({
      effects: StateEffect.appendConfig.of(mermaidActions.of(mermaidGutter)),
    }).state;
  };

  const hintsForState = (editorState, scrollTop) => ({
    selection: {
      anchor: editorState.selection.main.anchor,
      head: editorState.selection.main.head,
    },
    scrollTop,
  });

  const currentHints = (targetView) => hintsForState(
    targetView.state,
    targetView.scrollDOM.scrollTop,
  );

  const reportHints = (entry, targetView) => {
    if (!entry || disposed) return;
    const hints = currentHints(targetView);
    store.captureHints(entry.tabId, {
      editorState: targetView.state,
      scrollTop: hints.scrollTop,
    });
    const queue = queues.get(entry.tabId);
    const pending = queue?.pending.at(-1);
    if (pending && !queue.awaitingResync) {
      pending.editorState = targetView.state;
      pending.hints = hints;
      return;
    }
    if (queue) {
      queue.acceptedState = targetView.state;
      queue.acceptedHints = hints;
    }
    onViewChanged(entry.tabId, hints);
  };

  const onScroll = () => {
    if (!view || disposed) return;
    reportHints(store.activate(store.activeTabId), view);
  };

  const postNext = (entry, queue) => {
    const pending = queue.pending[0];
    if (!pending || pending.sent || queue.awaitingResync) return;
    const messages = serialize({ ...entry, revision: queue.acceptedRevision }, pending.changes, limits);
    if (!messages) return;
    pending.sent = true;
    messages.forEach((message) => host.postMessage(message));
  };

  const dispatchTransactions = (transactions, targetView) => {
    const entry = store.activate(store.activeTabId);
    if (!entry || disposed) return;
    let rawText = entry.rawText;
    let queue = queues.get(entry.tabId);
    if (!queue) {
      queue = {
        acceptedRevision: entry.revision,
        acceptedRawText: entry.rawText,
        acceptedState: targetView.state,
        acceptedHints: currentHints(targetView),
        pending: [],
        awaitingResync: false,
      };
      queues.set(entry.tabId, queue);
    }
    if (queue.awaitingResync) {
      if (transactions.some((transaction) => transaction.docChanged)) return;
      targetView.update(transactions);
      store.captureHints(entry.tabId, {
        editorState: targetView.state,
        scrollTop: targetView.scrollDOM.scrollTop,
      });
      reportHints(entry, targetView);
      return;
    }
    const additions = [];

    for (const transaction of transactions) {
      if (!transaction.docChanged) continue;
      const changes = readChanges(transaction, rawText, entry.preferredNewline);
      if (!changes) return;
      const insertedTotal = changes.reduce(
        (total, change) => total + change.insertedText.length,
        0,
      );
      if (!editWithinLimits(changes.length, insertedTotal, limits)) {
        onEditError({ code: "edit-limit-exceeded" });
        return;
      }
      rawText = applyRawChanges(rawText, changes);
      additions.push({
        changes,
        rawText,
        editorState: transaction.state,
        hints: null,
        sent: false,
      });
    }

    onEditError(null);
    targetView.update(transactions);
    for (const addition of additions) {
      addition.hints = hintsForState(addition.editorState, targetView.scrollDOM.scrollTop);
    }
    store.captureHints(entry.tabId, {
      editorState: targetView.state,
      rawText,
      scrollTop: targetView.scrollDOM.scrollTop,
    });
    queue.pending.push(...additions);
    postNext(entry, queue);
    if (additions.length === 0) reportHints(entry, targetView);
  };

  const ensureView = (editorState) => {
    if (view) return;
    view = new EditorView({ state: editorState, dispatchTransactions });
    view.scrollDOM.addEventListener("scroll", onScroll, { passive: true });
    if (parent) parent.append(view.dom);
  };

  const activate = (tabId) => {
    if (!view || disposed) return store.activate(tabId);
    const entry = store.activate(tabId, view);
    if (entry) view.scrollDOM.scrollTop = entry.scrollTop;
    return entry;
  };

  const hydrate = (message) => {
    if (disposed || message?.type !== "document.activate") return null;
    const payload = message.payload ?? {};
    const entry = store.hydrate({
      tabId: message.tabId,
      windowId: message.windowId,
      requestId: message.requestId,
      revision: message.documentRevision,
      text: payload.text,
      dirty: payload.dirty,
      preferredNewline: payload.preferredNewline,
      mode: payload.mode,
      selection: payload.selection,
      scrollTop: payload.scrollTop,
      splitRatio: payload.splitRatio,
      find: payload.find,
    });
    if (!entry) return null;
    installMermaidActions(entry);
    onEditError(null);
    queues.set(entry.tabId, {
      acceptedRevision: entry.revision,
      acceptedRawText: entry.rawText,
      acceptedState: entry.editorState,
      acceptedHints: hintsForState(entry.editorState, entry.scrollTop),
      pending: [],
      awaitingResync: false,
    });
    ensureView(entry.editorState);
    return activate(entry.tabId);
  };

  const updateRevision = (tabId, revision) => {
    const entry = store.updateRevision(tabId, revision);
    if (entry) {
      const queue = queues.get(tabId);
      if (queue && queue.pending.length === 0) {
        queue.acceptedRevision = revision;
        queue.acceptedRawText = entry.rawText;
        queue.acceptedState = entry.editorState;
      }
      renderAccepted(entry.rawText, { tabId, revision });
    }
    return entry;
  };

  const ownedResponse = (message, entry) => message?.version === 1
    && message.requestId === entry.requestId
    && message.windowId === entry.windowId
    && message.tabId === entry.tabId
    && Number.isSafeInteger(message.documentRevision)
    && message.documentRevision >= 0
    && typeof message.payload === "object"
    && message.payload !== null
    && !Array.isArray(message.payload);

  const handleHostMessage = (message) => {
    if (disposed || typeof message?.tabId !== "string" || message.tabId !== store.activeTabId) {
      return false;
    }
    const entry = store.activate(message.tabId);
    const queue = queues.get(message.tabId);
    const pending = queue?.pending[0];
    if (!entry || !queue || !ownedResponse(message, entry)) return false;

    if (message.type === "mermaid.reopenRequested") {
      const keys = Object.keys(message.payload);
      const { from, sourceHash, actionId, actionOrigin } = message.payload;
      if (keys.length !== 4
        || !keys.includes("from")
        || !keys.includes("sourceHash")
        || !keys.includes("actionId")
        || !keys.includes("actionOrigin")
        || !Number.isSafeInteger(from)
        || from < 0
        || typeof sourceHash !== "string"
        || !/^[0-9a-f]{64}$/u.test(sourceHash)
        || typeof actionId !== "string"
        || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(actionId)
        || (actionOrigin !== "rendered" && actionOrigin !== "editor")
        || message.documentRevision !== queue.acceptedRevision
        || message.documentRevision !== entry.revision
        || queue.awaitingResync
        || queue.pending.length !== 0) return false;
      void requestMermaidReopen(message, entry, queue);
      return true;
    }

    if (!pending?.sent) return false;

    if (message.type === "document.changeAccepted") {
      if (Object.keys(message.payload).length !== 0
        || message.documentRevision !== queue.acceptedRevision + 1) return false;
      queue.pending.shift();
      queue.acceptedRevision = message.documentRevision;
      queue.acceptedRawText = pending.rawText;
      queue.acceptedState = pending.editorState;
      queue.acceptedHints = pending.hints;
      store.updateRevision(entry.tabId, message.documentRevision);
      renderAccepted(pending.rawText, {
        tabId: entry.tabId,
        revision: message.documentRevision,
      });
      onViewChanged(entry.tabId, queue.acceptedHints);
      postNext(entry, queue);
      return true;
    }

    const keys = Object.keys(message.payload);
    const resyncRequestId = message.payload.resyncRequestId;
    if (message.type !== "document.changeRejected"
      || keys.length !== 1
      || keys[0] !== "resyncRequestId"
      || typeof resyncRequestId !== "string"
      || message.documentRevision < queue.acceptedRevision
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(resyncRequestId)) {
      return false;
    }

    queue.pending.length = 0;
    queue.awaitingResync = true;
    queue.acceptedRevision = message.documentRevision;
    if (view && store.activeTabId === entry.tabId) {
      view.setState(queue.acceptedState);
      view.scrollDOM.scrollTop = queue.acceptedHints.scrollTop;
    }
    store.captureHints(entry.tabId, {
      editorState: queue.acceptedState,
      rawText: queue.acceptedRawText,
      scrollTop: queue.acceptedHints.scrollTop,
    });
    store.updateRevision(entry.tabId, message.documentRevision);
    host.postMessage({
      version: 1,
      type: "document.resync",
      requestId: resyncRequestId,
      windowId: entry.windowId,
      tabId: entry.tabId,
      documentRevision: message.documentRevision,
      payload: {},
    });
    return true;
  };

  const mount = (nextParent) => {
    if (disposed || !nextParent) return;
    parent = nextParent;
    if (view && view.dom.parentNode !== parent) parent.append(view.dom);
  };

  const dispose = () => {
    if (disposed) return;
    disposed = true;
    if (view) {
      view.scrollDOM.removeEventListener("scroll", onScroll);
      view.destroy();
      view.dom.remove();
    }
    store.dispose();
    queues.clear();
    parent = null;
  };

  return {
    hydrate,
    activate,
    updateRevision,
    handleHostMessage,
    mount,
    dispose,
    clearEditError() {
      onEditError(null);
    },
    undo() {
      return !disposed && view ? undo(view) : false;
    },
    redo() {
      return !disposed && view ? redo(view) : false;
    },
    get view() {
      return view;
    },
  };
}
