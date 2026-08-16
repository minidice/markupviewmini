import { readFile, readdir, writeFile } from "node:fs/promises";
import { relative, resolve, sep } from "node:path";

function toManifestPath(path) {
  return path.split(sep).join("/");
}

async function listFiles(root, directory = root) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) files.push(...await listFiles(root, path));
    else if (entry.isFile()) files.push(toManifestPath(relative(root, path)));
  }
  return files.sort((left, right) => left.localeCompare(right, "en"));
}

function parseManifest(text) {
  const files = text.split(/\r?\n/u).filter(Boolean);
  if (files.some((file) => file.startsWith("/") || file.includes("\\")
    || file.split("/").some((segment) => segment === ".."))) {
    throw new Error("The document-surface manifest contains an unsafe path.");
  }
  if (new Set(files).size !== files.length) {
    throw new Error("The document-surface manifest contains a duplicate path.");
  }
  return files;
}

function assertSameFiles(expected, actual, description) {
  const expectedSet = new Set(expected);
  const actualSet = new Set(actual);
  const missing = expected.filter((file) => !actualSet.has(file));
  const stale = actual.filter((file) => !expectedSet.has(file));
  if (missing.length || stale.length) {
    throw new Error([
      `${description} does not match the document-surface manifest.`,
      missing.length ? `Missing: ${missing.join(", ")}` : null,
      stale.length ? `Stale: ${stale.join(", ")}` : null,
    ].filter(Boolean).join(" "));
  }
}

export async function writeDistManifest(distDirectory, manifestPath) {
  const manifestAbsolute = resolve(manifestPath);
  const files = (await listFiles(resolve(distDirectory)))
    .filter((file) => resolve(distDirectory, file) !== manifestAbsolute);
  await writeFile(manifestAbsolute, `${files.join("\n")}\n`, "utf8");
  return files;
}

function packageRootFromInput(inputPath) {
  const segments = inputPath.replaceAll("\\", "/").split("/");
  const nodeModulesIndex = segments.lastIndexOf("node_modules");
  if (nodeModulesIndex < 0 || nodeModulesIndex === segments.length - 1) return null;
  const packageSegmentCount = segments[nodeModulesIndex + 1].startsWith("@") ? 2 : 1;
  if (nodeModulesIndex + packageSegmentCount >= segments.length) return null;
  return segments.slice(0, nodeModulesIndex + packageSegmentCount + 1).join("/");
}

export async function writeRuntimeComponentManifest(bundleRoot, metafiles, manifestPath) {
  const root = resolve(bundleRoot);
  const packageRoots = new Set(
    metafiles
      .flatMap((metafile) => Object.keys(metafile.inputs))
      .map(packageRootFromInput)
      .filter(Boolean),
  );
  const packagesByIdentity = new Map();
  for (const packagePath of [...packageRoots].sort((left, right) => left.localeCompare(right, "en"))) {
    const absolutePackagePath = resolve(root, packagePath);
    const relativePackagePath = toManifestPath(relative(root, absolutePackagePath));
    if (relativePackagePath.startsWith("../") || relativePackagePath === "..") {
      throw new Error(`Bundle package path escapes its root: ${packagePath}`);
    }
    const packageJson = JSON.parse(await readFile(resolve(absolutePackagePath, "package.json"), "utf8"));
    if (!packageJson.name || !packageJson.version) {
      throw new Error(`Bundle package metadata is incomplete: ${packagePath}`);
    }
    const identity = `${packageJson.name}@${packageJson.version}`;
    const candidate = {
      name: packageJson.name,
      packagePath: relativePackagePath,
      version: packageJson.version,
    };
    const existing = packagesByIdentity.get(identity);
    if (!existing || candidate.packagePath.localeCompare(existing.packagePath, "en") < 0) {
      packagesByIdentity.set(identity, candidate);
    }
  }
  const packages = [...packagesByIdentity.values()].sort((left, right) =>
    left.name.localeCompare(right.name, "en")
      || left.version.localeCompare(right.version, "en")
      || left.packagePath.localeCompare(right.packagePath, "en"));
  const manifest = { schemaVersion: 1, packages };
  await writeFile(resolve(manifestPath), `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  return manifest;
}

export async function verifyDistManifest(distDirectory, manifestPath) {
  const root = resolve(distDirectory);
  const manifestAbsolute = resolve(manifestPath);
  const expected = parseManifest(await readFile(manifestAbsolute, "utf8"));
  const actual = (await listFiles(root))
    .filter((file) => resolve(root, file) !== manifestAbsolute);
  assertSameFiles(expected, actual, "Generated dist");
  if (!expected.includes("editor.js") || !expected.includes("editor.css")
    || !expected.some((file) => file.startsWith("fonts/"))) {
    throw new Error("Generated dist must include JavaScript, CSS, and local fonts.");
  }
  if (expected.some((file) => file.endsWith(".map"))) {
    throw new Error("Generated dist must not include source maps.");
  }
  return expected;
}

export async function verifyCopiedDist(sourceDirectory, destinationDirectory, manifestPath) {
  const sourceRoot = resolve(sourceDirectory);
  const destinationRoot = resolve(destinationDirectory);
  const manifestName = toManifestPath(relative(sourceRoot, resolve(manifestPath)));
  const assets = await verifyDistManifest(sourceRoot, manifestPath);
  const expected = [...assets, manifestName].sort((left, right) => left.localeCompare(right, "en"));
  const actual = await listFiles(destinationRoot);
  assertSameFiles(expected, actual, "Copied dist");
  for (const file of expected) {
    const [source, destination] = await Promise.all([
      readFile(resolve(sourceRoot, file)),
      readFile(resolve(destinationRoot, file)),
    ]);
    if (!source.equals(destination)) {
      throw new Error(`Copied dist differs from its source: ${file}`);
    }
  }
  return expected;
}
