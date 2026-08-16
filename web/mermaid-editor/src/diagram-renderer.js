import mermaid from "mermaid";
import { analyzeMermaidSource } from "@markup-view-mini/mermaid-safe/analyzer";

let initialized = false;

export async function renderDiagram(source) {
  const analysis = analyzeMermaidSource(source);
  if (!analysis.supported) return { ok: false, reason: analysis.reason, svg: "" };
  if (!initialized) {
    mermaid.initialize({ startOnLoad: false, securityLevel: "strict" });
    initialized = true;
  }
  try {
    const id = `mermaid-${Date.now()}`;
    const result = await mermaid.render(id, source);
    return { ok: true, reason: null, svg: result.svg };
  } catch (error) {
    return { ok: false, reason: error instanceof Error ? error.message : "render-failed", svg: "" };
  }
}
