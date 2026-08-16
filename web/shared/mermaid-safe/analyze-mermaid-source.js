import { parseFlowchart } from "./flowchart-parser.js";

export function analyzeMermaidSource(source) {
  const result = parseFlowchart(source);
  if (!result.ok) {
    return {
      supported: false,
      reason: result.reason === "not-a-flowchart" ? "flowchart-required" : result.reason,
      model: null,
    };
  }
  return { supported: true, reason: null, model: result.graph };
}
