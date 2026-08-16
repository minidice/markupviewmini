import { verifyCopiedDist, verifyDistManifest } from "./build-support.mjs";

const [mode, source, destinationOrManifest, manifest] = process.argv.slice(2);
if (mode === "source" && source && destinationOrManifest) {
  await verifyDistManifest(source, destinationOrManifest);
} else if (mode === "copy" && source && destinationOrManifest && manifest) {
  await verifyCopiedDist(source, destinationOrManifest, manifest);
} else {
  throw new Error("Usage: verify-dist.mjs source <dist> <manifest> | copy <source> <destination> <manifest>");
}
