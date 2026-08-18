import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeAll, describe, expect, it } from "vitest";

const packageRoot = process.cwd();

beforeAll(() => {
  execFileSync(process.execPath, ["build.mjs"], { cwd: packageRoot });
});

function installBuiltStyles() {
  const style = document.createElement("style");
  style.dataset.testBuiltStyles = "true";
  style.textContent = readFileSync(join(packageRoot, "dist", "editor.css"), "utf8");
  document.head.append(style);
}

function parsePage() {
  return new DOMParser().parseFromString(readFileSync("index.html", "utf8"), "text/html");
}

/** Split a grid-template-columns value into tracks without splitting inside minmax(...). */
function trackCount(value) {
  let depth = 0;
  let tracks = 0;
  let inTrack = false;
  for (const character of value) {
    if (character === "(") depth += 1;
    else if (character === ")") depth -= 1;
    if (depth === 0 && /\s/u.test(character)) {
      inTrack = false;
    } else if (!inTrack) {
      inTrack = true;
      tracks += 1;
    }
  }
  return tracks;
}

afterEach(() => {
  document.querySelectorAll("style[data-test-built-styles]").forEach((style) => style.remove());
  document.body.replaceChildren();
});

describe("mermaid editor layout", () => {
  it("places the tool pane beside the canvas rather than nested under it", () => {
    // Break caught: the inspector used to live inside the canvas section, so it rendered
    // *below* the diagram - every selection pushed the controls down the page and the canvas
    // lost width it never got back. It has to be its own column next to the canvas.
    const page = parsePage();
    const grid = page.querySelector(".workspace-grid");
    const toolPane = page.querySelector("[data-inspector]");

    expect(toolPane.parentElement).toBe(grid);
    expect(page.querySelector(".canvas-pane [data-inspector]")).toBeNull();

    const columns = [...grid.children];
    expect(columns).toHaveLength(3);
    expect(columns.at(-1)).toBe(toolPane);
  });

  it("keeps the source textarea reachable at every width", () => {
    // Break caught: hiding the code pane on narrow screens (as the reference editor does)
    // makes limited mode - where the textarea is the ONLY way to edit - completely unusable.
    const css = readFileSync(join(packageRoot, "dist", "editor.css"), "utf8");

    expect(css).not.toMatch(/\.code-pane\s*\{[^}]*display:\s*none/u);
  });

  it("gives the built stylesheet three columns and a self-scrolling tool pane", () => {
    installBuiltStyles();
    document.body.innerHTML =
      '<div class="workspace-grid"><section></section><section></section>'
      + '<aside class="tool-pane" data-inspector></aside></div>';

    const grid = getComputedStyle(document.querySelector(".workspace-grid"));
    const toolPane = getComputedStyle(document.querySelector(".tool-pane"));

    expect(grid.display).toBe("grid");
    expect(trackCount(grid.gridTemplateColumns)).toBe(3);
    // Its own scrollport - otherwise a long inspector grows the page and drags the canvas away.
    expect(toolPane.overflow).toBe("auto");
  });
});
