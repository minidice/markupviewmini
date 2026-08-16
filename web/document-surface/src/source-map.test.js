import { describe, expect, it } from "vitest";
import { createMarkdownRenderer, renderPreviewHtml } from "./preview.js";
import { findBlockForSourceLine } from "./source-map.js";

function expectRange(root, selector, start, end) {
  const element = root.querySelector(selector);
  expect(element, `missing ${selector}`).not.toBeNull();
  expect(element.dataset).toMatchObject({
    sourceStart: String(start),
    sourceEnd: String(end),
  });
}

describe("Markdown source map", () => {
  it("finds the narrowest rendered block containing a source line", () => {
    document.body.innerHTML = [
      '<section data-source-start="1" data-source-end="8">',
      '<p data-source-start="4" data-source-end="6">Body</p>',
      "</section>",
    ].join("");

    expect(findBlockForSourceLine(document.body, 5)?.textContent).toBe("Body");
  });

  it("adds source line bounds to rendered block elements", () => {
    const html = createMarkdownRenderer().render("# Heading\n\nParagraph");
    const root = document.createElement("div");
    root.innerHTML = html;

    expect(root.querySelector("h1")).toMatchObject({
      dataset: expect.objectContaining({ sourceStart: "1", sourceEnd: "1" }),
    });
    expect(root.querySelector("p")).toMatchObject({
      dataset: expect.objectContaining({ sourceStart: "3", sourceEnd: "3" }),
    });
  });

  it("maps multi-line fenced blocks through their complete source range", () => {
    const html = createMarkdownRenderer().render("~~~js\nconst value = 1;\n~~~");
    const root = document.createElement("div");
    root.innerHTML = html;

    expect(root.querySelector("pre")).toMatchObject({
      dataset: expect.objectContaining({ sourceStart: "1", sourceEnd: "3" }),
    });
  });

  it("never emits a source range ending before it starts", () => {
    const html = createMarkdownRenderer().render("Term\n: Definition");
    const root = document.createElement("div");
    root.innerHTML = html;

    for (const element of root.querySelectorAll("[data-source-start][data-source-end]")) {
      expect(Number(element.dataset.sourceEnd)).toBeGreaterThanOrEqual(
        Number(element.dataset.sourceStart),
      );
    }
  });

  it("maps unknown and unlabelled fence wrappers exactly", () => {
    const root = document.createElement("div");
    root.innerHTML = createMarkdownRenderer().render([
      "~~~unknown",
      "first",
      "~~~",
      "",
      "~~~",
      "second",
      "~~~",
    ].join("\n"));

    const blocks = root.querySelectorAll("pre");
    expect(blocks).toHaveLength(2);
    expect(blocks[0].dataset).toMatchObject({ sourceStart: "1", sourceEnd: "3" });
    expect(blocks[1].dataset).toMatchObject({ sourceStart: "5", sourceEnd: "7" });
  });

  it("maps indented code and definition-list wrappers exactly", () => {
    const root = document.createElement("div");
    root.innerHTML = createMarkdownRenderer().render(
      "    first\n    second\n\nTerm\n: Definition",
    );

    expectRange(root, "pre", 1, 2);
    expectRange(root, "dl", 4, 5);
    expectRange(root, "dt", 4, 4);
    expectRange(root, "dd", 5, 5);
  });

  it("maps math and Mermaid block wrappers exactly", () => {
    const root = document.createElement("div");
    root.innerHTML = createMarkdownRenderer().render(
      "$$\nx^2\n$$\n\n~~~mermaid\nflowchart LR\nA --> B\n~~~",
    );

    expectRange(root, "div[data-source-start]", 1, 3);
    expectRange(root, "pre[data-mermaid-source]", 5, 8);
  });

  it("wraps a safe raw-HTML block with exact source bounds", async () => {
    const root = document.createElement("div");
    root.innerHTML = await renderPreviewHtml("<section>\n<strong>safe</strong>\n</section>");

    expectRange(root, ".raw-html-block", 1, 3);
    expect(root.querySelector(".raw-html-block section strong")?.textContent).toBe("safe");
  });
});
