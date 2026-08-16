import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeAll, describe, expect, it } from "vitest";

const packageRoot = process.cwd();

beforeAll(() => {
  execFileSync(process.execPath, ["build.mjs"], { cwd: packageRoot });
});

async function installBuiltStyles() {
  const style = document.createElement("style");
  style.dataset.testBuiltStyles = "true";
  style.textContent = readFileSync(join(packageRoot, "dist", "editor.css"), "utf8");
  document.head.append(style);
}

afterEach(() => {
  document.querySelectorAll("style[data-test-built-styles]").forEach((style) => style.remove());
  document.body.replaceChildren();
});

describe("document surface presentation", () => {
  it("visibly distinguishes the temporary source-navigation target in the built stylesheet", async () => {
    // Break caught: navigation can add its timed class while the shipped stylesheet gives users no visible target cue.
    await installBuiltStyles();
    document.body.innerHTML = '<main id="preview"><h2 class="is-navigation-target">Target</h2></main>';

    const targetStyle = getComputedStyle(document.querySelector(".is-navigation-target"));

    expect(targetStyle.backgroundColor).not.toBe("rgba(0, 0, 0, 0)");
    expect(targetStyle.boxShadow).toContain("2px");
  });

  it("wraps and scrolls the find bar controls in the built stylesheet", async () => {
    // Break caught: a single nonwrapping row overflows the minimum document width and leaves later controls unreachable.
    await installBuiltStyles();
    document.body.innerHTML = '<div class="document-find-bar"><input data-find-query><button>Next</button></div>';

    const barStyle = getComputedStyle(document.querySelector(".document-find-bar"));

    expect(barStyle.flexWrap).toBe("wrap");
    expect(barStyle.overflowY).toBe("auto");
  });

  it("lays out the editor and accepted preview as a bounded split workspace", async () => {
    // Break caught: adding the editor as a direct column child stacks it above the preview instead of splitting.
    await installBuiltStyles();
    document.body.innerHTML = [
      '<main class="document-surface">',
      '  <section class="document-workspace" data-document-workspace>',
      '    <section class="document-editor" data-editor></section>',
      '    <article id="preview" data-preview></article>',
      "  </section>",
      "</main>",
    ].join("");

    const workspaceStyle = getComputedStyle(document.querySelector("[data-document-workspace]"));
    const editorStyle = getComputedStyle(document.querySelector("[data-editor]"));
    const previewStyle = getComputedStyle(document.querySelector("[data-preview]"));

    expect(workspaceStyle.display).toBe("flex");
    expect(workspaceStyle.minHeight).toBe("0px");
    expect(editorStyle.overflow).toBe("hidden");
    expect(editorStyle.flexBasis).toBe("var(--editor-split-ratio, 50%)");
    expect(previewStyle.flexBasis).toContain("var(--editor-split-ratio, 50%)");
  });

  it("reserves a non-clipped focus outline for editor and find controls", async () => {
    await installBuiltStyles();
    document.body.innerHTML = [
      '<section class="document-find-bar"><button>Next</button></section>',
      '<section class="document-editor"><div class="cm-editor"><div class="cm-content"></div></div></section>',
    ].join("");

    const button = document.querySelector("button");
    button.focus();

    expect(button.matches(":focus")).toBe(true);
    expect(getComputedStyle(button).outlineOffset).toBe("2px");
  });

  it("shows a strong focus outline on the exact Mermaid action that opened the modal", async () => {
    // Break caught: focus returns to the originating action after modal close but has no visible indication.
    await installBuiltStyles();
    document.body.innerHTML = '<button class="mermaid-edit-action">시각 편집</button>';
    const button = document.querySelector("button");

    button.focus();

    expect(document.activeElement).toBe(button);
    expect(getComputedStyle(button).outline).toContain("2px solid");
    expect(getComputedStyle(button).outlineOffset).toBe("2px");
  });
});
