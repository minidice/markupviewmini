import { homedir } from "node:os";
import {
  readFile,
  readdir,
  writeFile,
} from "node:fs/promises";
import {
  dirname,
  isAbsolute,
  relative,
  resolve,
} from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = resolve(dirname(scriptPath), "..");
const generatedManifestPath = resolve(
  repositoryRoot,
  "src/MarkUpViewMini.App/About/Resources/runtime-notices.json",
);
const manualConfigPath = resolve(
  repositoryRoot,
  "src/MarkUpViewMini.App/About/Resources/runtime-notice-sources.json",
);
const npmLicenseOverridesPath = resolve(
  repositoryRoot,
  "src/MarkUpViewMini.App/About/Resources/runtime-npm-license-overrides.json",
);
const bundleManifestPaths = [
  resolve(repositoryRoot, "web/document-surface/dist/runtime-components.json"),
  resolve(repositoryRoot, "web/mermaid-editor/dist/runtime-components.json"),
];

function normalizeLineEndings(text) {
  return text.replaceAll("\r\n", "\n").trim();
}

function isLicenseFile(name) {
  const lower = name.toLowerCase();
  if (!/^(?:license|licence|unlicense|copying|notice)(?:[-_.].*)?$/u.test(lower)) return false;
  const extension = lower.includes(".") ? lower.slice(lower.lastIndexOf(".") + 1) : "";
  return !new Set(["cjs", "js", "json", "jsx", "mjs", "ts", "tsx", "xml", "yaml", "yml"])
    .has(extension);
}

function normalizeRepositoryUrl(repository, packageName) {
  let value = typeof repository === "string" ? repository : repository?.url;
  if (!value) return `https://www.npmjs.com/package/${packageName}`;
  if (/^[\w.-]+\/[\w.-]+$/u.test(value)) value = `https://github.com/${value}`;
  value = value
    .replace(/^git\+/, "")
    .replace(/^git:\/\/github\.com\//, "https://github.com/")
    .replace(/^git@github\.com:/, "https://github.com/")
    .replace(/\.git(?:#.*)?$/u, "")
    .replace(/\/$/u, "");
  return value.startsWith("https://")
    ? value
    : `https://www.npmjs.com/package/${packageName}`;
}

function formatLicenseIdentifier(license) {
  if (typeof license === "string" && license.trim()) return license.trim();
  if (Array.isArray(license)) {
    const values = license.map(formatLicenseIdentifier).filter(Boolean);
    if (values.length) return values.join(" OR ");
  }
  if (license && typeof license.type === "string") return license.type;
  throw new Error("Package metadata has no usable license identifier.");
}

async function readNoticeFiles(packageRoot, explicitFiles = null) {
  const fileNames = explicitFiles ?? (await readdir(packageRoot, { withFileTypes: true }))
    .filter((entry) => entry.isFile() && isLicenseFile(entry.name))
    .map((entry) => entry.name);
  const sorted = [...new Set(fileNames)].sort((left, right) => left.localeCompare(right, "en"));
  if (!sorted.length) throw new Error(`No license or notice file was found in ${packageRoot}.`);
  const sections = [];
  for (const fileName of sorted) {
    if (isAbsolute(fileName) || fileName.split(/[\\/]/u).includes("..")) {
      throw new Error(`Unsafe license file path: ${fileName}`);
    }
    const text = normalizeLineEndings(await readFile(resolve(packageRoot, fileName), "utf8"));
    if (!text) throw new Error(`License file is empty: ${resolve(packageRoot, fileName)}`);
    sections.push(`===== ${fileName} =====\n${text}`);
  }
  return sections.join("\n\n");
}

async function readBundleNotice(manifestPath, component, npmLicenseOverrides) {
  const surfaceRoot = resolve(dirname(manifestPath), "..");
  const packageRoot = resolve(surfaceRoot, component.packagePath);
  const pathFromSurface = relative(surfaceRoot, packageRoot).replaceAll("\\", "/");
  if (pathFromSurface === ".." || pathFromSurface.startsWith("../")) {
    throw new Error(`Bundle component escapes its package root: ${component.packagePath}`);
  }
  const packageJson = JSON.parse(await readFile(resolve(packageRoot, "package.json"), "utf8"));
  if (packageJson.name !== component.name || packageJson.version !== component.version) {
    throw new Error(`Bundle component metadata changed for ${component.name}@${component.version}.`);
  }
  const identity = `${packageJson.name}@${packageJson.version}`;
  return {
    name: packageJson.name,
    version: packageJson.version,
    licenseIdentifier: formatLicenseIdentifier(packageJson.license ?? npmLicenseOverrides[identity]),
    sourceUrl: normalizeRepositoryUrl(packageJson.repository, packageJson.name),
    noticeText: await readNoticeFiles(packageRoot),
  };
}

async function readManualNotice(component, nugetPackagesDirectory) {
  const sources = component.licenseSources ?? component.licenseFiles?.map((path) => ({
    packageId: component.packageId,
    version: component.version,
    path,
  }));
  if (!Array.isArray(sources) || !sources.length) {
    throw new Error(`Manual component has no license sources: ${component.name}@${component.version}`);
  }
  const sections = [];
  for (const source of sources) {
    const packageRoot = resolve(
      nugetPackagesDirectory,
      source.packageId.toLowerCase(),
      source.version,
    );
    sections.push(await readNoticeFiles(packageRoot, [source.path]));
  }
  return {
    name: component.name,
    version: component.version,
    licenseIdentifier: component.licenseIdentifier,
    sourceUrl: component.sourceUrl,
    noticeText: sections.join("\n\n"),
  };
}

export async function collectThirdPartyNotices({
  bundleManifestPaths: manifests,
  manualConfigPath: configPath,
  npmLicenseOverridesPath: overridesPath,
  nugetPackagesDirectory,
}) {
  const notices = new Map();
  const npmLicenseOverrides = overridesPath
    ? JSON.parse(await readFile(overridesPath, "utf8"))
    : {};
  for (const manifestPath of manifests) {
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.packages)) {
      throw new Error(`Unsupported bundle component manifest: ${manifestPath}`);
    }
    for (const component of manifest.packages) {
      const notice = await readBundleNotice(manifestPath, component, npmLicenseOverrides);
      const identity = `${notice.name}@${notice.version}`;
      const existing = notices.get(identity);
      if (existing && JSON.stringify(existing) !== JSON.stringify(notice)) {
        throw new Error(`Conflicting license data for ${identity}.`);
      }
      notices.set(identity, notice);
    }
  }

  const manualComponents = JSON.parse(await readFile(configPath, "utf8"));
  if (!Array.isArray(manualComponents)) throw new Error("Manual runtime notice sources must be an array.");
  for (const component of manualComponents) {
    const notice = await readManualNotice(component, nugetPackagesDirectory);
    const identity = `${notice.name}@${notice.version}`;
    if (notices.has(identity)) throw new Error(`Duplicate manual runtime component: ${identity}.`);
    notices.set(identity, notice);
  }

  return [...notices.values()].sort((left, right) =>
    left.name.localeCompare(right.name, "en") || left.version.localeCompare(right.version, "en"));
}

async function verifyPublishedDependencies(depsPath, configPath, notices) {
  if (!depsPath) return;
  const deps = JSON.parse(await readFile(depsPath, "utf8"));
  const manualComponents = JSON.parse(await readFile(configPath, "utf8"));
  const noticeIdentities = new Set(notices.map(({ name, version }) => `${name}@${version}`));
  const publishedLibraries = Object.entries(deps.libraries)
    .filter(([, metadata]) => metadata.type === "package" || metadata.type === "runtimepack")
    .map(([identity]) => identity);
  const isSelfContained = Object.values(deps.libraries)
    .some((metadata) => metadata.type === "runtimepack");
  for (const library of publishedLibraries) {
    const component = manualComponents.find(({ publishedLibraryPrefix }) =>
      publishedLibraryPrefix && library.startsWith(publishedLibraryPrefix));
    if (!component) throw new Error(`Published runtime dependency has no manual notice source: ${library}`);
    const version = library.slice(component.publishedLibraryPrefix.length);
    if (version !== component.version || !noticeIdentities.has(`${component.name}@${version}`)) {
      throw new Error(`Published runtime dependency notice is stale: ${library}`);
    }
  }
  for (const component of manualComponents.filter(({ publishedLibraryPrefix }) => publishedLibraryPrefix)) {
    if (!isSelfContained && component.publishedLibraryPrefix.startsWith("runtimepack.")) {
      // Framework-dependent publishes rely on an installed shared runtime instead of bundling
      // it, so the runtime's own notice has nothing to match in this deps.json.
      continue;
    }
    const expected = `${component.publishedLibraryPrefix}${component.version}`;
    if (!publishedLibraries.includes(expected)) {
      throw new Error(`Manual runtime notice does not match the published dependency closure: ${expected}`);
    }
  }
}

async function run() {
  const write = process.argv.includes("--write");
  const check = process.argv.includes("--check") || !write;
  if (write && check) throw new Error("Choose either --write or --check.");
  const depsIndex = process.argv.indexOf("--deps");
  const depsPath = depsIndex >= 0 ? resolve(process.argv[depsIndex + 1] ?? "") : null;
  const nugetPackagesDirectory = resolve(
    process.env.NUGET_PACKAGES || resolve(homedir(), ".nuget", "packages"),
  );
  const notices = await collectThirdPartyNotices({
    bundleManifestPaths,
    manualConfigPath,
    npmLicenseOverridesPath,
    nugetPackagesDirectory,
  });
  await verifyPublishedDependencies(depsPath, manualConfigPath, notices);
  const rendered = `${JSON.stringify(notices, null, 2)}\n`;
  if (write) {
    await writeFile(generatedManifestPath, rendered, "utf8");
    console.log(`Wrote ${notices.length} third-party notices.`);
    return;
  }
  const current = await readFile(generatedManifestPath, "utf8");
  if (normalizeLineEndings(current) !== normalizeLineEndings(rendered)) {
    throw new Error("runtime-notices.json is stale. Run: node scripts/generate-third-party-notices.mjs --write");
  }
  console.log(`Verified ${notices.length} third-party notices.`);
}

if (process.argv[1] && resolve(process.argv[1]) === scriptPath) {
  await run();
}
