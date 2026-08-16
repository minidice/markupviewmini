import { rm } from "node:fs/promises";
import { build } from "esbuild";
import {
  writeDistManifest,
  writeRuntimeComponentManifest,
} from "./build-support.mjs";

await rm("dist", { recursive: true, force: true });

const buildResults = await Promise.all([
  build({
    entryPoints: ["src/editor-app.js"],
    bundle: true,
    format: "iife",
    outfile: "dist/editor.js",
    platform: "browser",
    minify: true,
    metafile: true,
  }),
  build({
    entryPoints: ["src/styles.css"],
    bundle: true,
    outfile: "dist/editor.css",
    minify: true,
    metafile: true,
    assetNames: "fonts/[name]",
    loader: {
      ".ttf": "file",
      ".woff": "file",
      ".woff2": "file",
    },
  }),
]);

await writeRuntimeComponentManifest(
  ".",
  buildResults.map(({ metafile }) => metafile),
  "dist/runtime-components.json",
);
await writeDistManifest("dist", "dist/manifest.txt");
