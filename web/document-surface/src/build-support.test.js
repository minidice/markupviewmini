import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import {
  verifyCopiedDist,
  verifyDistManifest,
  writeDistManifest,
  writeRuntimeComponentManifest,
} from "../build-support.mjs";

describe("document surface build manifest", () => {
  const temporaryDirectories = [];

  afterEach(() => {
    for (const directory of temporaryDirectories.splice(0)) {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  function createDist() {
    const directory = mkdtempSync(join(tmpdir(), "markup-view-mini-dist-"));
    temporaryDirectories.push(directory);
    mkdirSync(join(directory, "fonts"));
    writeFileSync(join(directory, "editor.js"), "javascript");
    writeFileSync(join(directory, "editor.css"), "css");
    writeFileSync(join(directory, "fonts", "KaTeX_Main-Regular.woff2"), "font");
    return directory;
  }

  it("records and verifies every generated JavaScript CSS and font asset", async () => {
    const source = createDist();
    const manifest = join(source, "manifest.txt");

    await writeDistManifest(source, manifest);

    await expect(verifyDistManifest(source, manifest)).resolves.toEqual([
      "editor.css",
      "editor.js",
      "fonts/KaTeX_Main-Regular.woff2",
    ]);
  });

  it("rejects missing and stale destination assets", async () => {
    const source = createDist();
    const manifest = join(source, "manifest.txt");
    await writeDistManifest(source, manifest);
    const destination = mkdtempSync(join(tmpdir(), "markup-view-mini-copy-"));
    temporaryDirectories.push(destination);
    cpSync(source, destination, { recursive: true });
    writeFileSync(join(destination, "stale.js"), "stale");

    await expect(verifyCopiedDist(source, destination, manifest))
      .rejects.toThrow(/stale\.js/u);

    rmSync(join(destination, "stale.js"));
    rmSync(join(destination, "fonts", "KaTeX_Main-Regular.woff2"));
    await expect(verifyCopiedDist(source, destination, manifest))
      .rejects.toThrow(/KaTeX_Main-Regular\.woff2/u);
  });

  it("records the exact package and version closure consumed by esbuild", async () => {
    const root = mkdtempSync(join(tmpdir(), "markup-view-mini-components-"));
    temporaryDirectories.push(root);
    const direct = join(root, "node_modules", "example");
    const nested = join(direct, "node_modules", "nested");
    mkdirSync(nested, { recursive: true });
    writeFileSync(join(direct, "package.json"), JSON.stringify({ name: "example", version: "1.2.3" }));
    writeFileSync(join(nested, "package.json"), JSON.stringify({ name: "nested", version: "4.5.6" }));
    mkdirSync(join(root, "node_modules", "test-only"));
    writeFileSync(
      join(root, "node_modules", "test-only", "package.json"),
      JSON.stringify({ name: "test-only", version: "9.9.9" }),
    );
    const manifest = join(root, "runtime-components.json");

    await writeRuntimeComponentManifest(root, [{
      inputs: {
        "src/app.js": {},
        "node_modules/example/index.js": {},
        "node_modules/example/node_modules/nested/index.js": {},
      },
    }], manifest);

    expect(JSON.parse(await import("node:fs/promises").then(({ readFile }) => readFile(manifest, "utf8"))))
      .toEqual({
        schemaVersion: 1,
        packages: [
          { name: "example", packagePath: "node_modules/example", version: "1.2.3" },
          {
            name: "nested",
            packagePath: "node_modules/example/node_modules/nested",
            version: "4.5.6",
          },
        ],
      });
  });
});
