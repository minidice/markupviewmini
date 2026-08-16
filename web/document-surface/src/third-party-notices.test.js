import {
  mkdirSync,
  mkdtempSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { collectThirdPartyNotices } from "../../../scripts/generate-third-party-notices.mjs";

describe("third-party notice generation", () => {
  const temporaryDirectories = [];

  afterEach(() => {
    for (const directory of temporaryDirectories.splice(0)) {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  it("combines every bundled and manual package license deterministically", async () => {
    const root = mkdtempSync(join(tmpdir(), "markup-view-mini-notices-"));
    temporaryDirectories.push(root);
    const surface = join(root, "surface");
    const packageRoot = join(surface, "node_modules", "dual-license");
    mkdirSync(packageRoot, { recursive: true });
    writeFileSync(join(packageRoot, "package.json"), JSON.stringify({
      name: "dual-license",
      version: "2.3.4",
      repository: { url: "git+https://github.com/example/dual-license.git" },
    }));
    writeFileSync(join(packageRoot, "LICENSE"), `Apache License\n${"A".repeat(120)}`);
    writeFileSync(join(packageRoot, "LICENSE-MPL"), `Mozilla Public License Version 2.0\n${"M".repeat(120)}`);
    writeFileSync(join(packageRoot, "license-helper.js"), "must not be treated as a notice");
    mkdirSync(join(surface, "dist"), { recursive: true });
    const bundleManifest = join(surface, "dist", "runtime-components.json");
    writeFileSync(bundleManifest, JSON.stringify({
      schemaVersion: 1,
      packages: [{ name: "dual-license", packagePath: "node_modules/dual-license", version: "2.3.4" }],
    }));
    const npmOverrides = join(root, "npm-overrides.json");
    writeFileSync(npmOverrides, JSON.stringify({
      "dual-license@2.3.4": "(MPL-2.0 OR Apache-2.0)",
    }));

    const nugetRoot = join(root, "nuget");
    const runtimeRoot = join(nugetRoot, "runtime-pack", "1.0.0");
    mkdirSync(runtimeRoot, { recursive: true });
    writeFileSync(join(runtimeRoot, "LICENSE.TXT"), `Runtime license\n${"R".repeat(120)}`);
    const manualConfig = join(root, "manual.json");
    writeFileSync(manualConfig, JSON.stringify([{
      name: ".NET Runtime (win-x64)",
      version: "1.0.0",
      licenseIdentifier: "MIT",
      sourceUrl: "https://github.com/dotnet/runtime",
      packageId: "runtime-pack",
      licenseFiles: ["LICENSE.TXT"],
    }]));

    const notices = await collectThirdPartyNotices({
      bundleManifestPaths: [bundleManifest, bundleManifest],
      manualConfigPath: manualConfig,
      npmLicenseOverridesPath: npmOverrides,
      nugetPackagesDirectory: nugetRoot,
    });

    expect(notices.map(({ name, version }) => `${name}@${version}`)).toEqual([
      ".NET Runtime (win-x64)@1.0.0",
      "dual-license@2.3.4",
    ]);
    expect(notices[1]).toMatchObject({
      licenseIdentifier: "(MPL-2.0 OR Apache-2.0)",
      sourceUrl: "https://github.com/example/dual-license",
    });
    expect(notices[1].noticeText).toContain("===== LICENSE =====");
    expect(notices[1].noticeText).toContain("===== LICENSE-MPL =====");
    expect(notices[1].noticeText).not.toContain("license-helper.js");
  });
});
