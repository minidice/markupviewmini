import { build } from "esbuild";
import { writeRuntimeComponentManifest } from "../document-surface/build-support.mjs";

const check = process.argv.includes("--check");
const buildResults = await Promise.all([
  build({ entryPoints: ["src/editor-app.js"], bundle: true, format: "iife", platform: "browser", write: !check, outfile: "dist/editor.js", minify: true, metafile: true }),
  build({ entryPoints: ["src/styles.css"], bundle: true, write: !check, outfile: "dist/editor.css", minify: true, metafile: true }),
]);

if (!check) {
  await writeRuntimeComponentManifest(
    ".",
    buildResults.map(({ metafile }) => metafile),
    "dist/runtime-components.json",
  );
}
