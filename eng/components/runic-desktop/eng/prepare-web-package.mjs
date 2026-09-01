import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const versionPattern =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const revisionPattern = /^[0-9a-f]{40}$/;

export function prepareWebPackage(root, version, revision) {
  if (!versionPattern.test(version)) {
    throw new Error(`invalid package version: ${version}`);
  }
  if (!revisionPattern.test(revision)) {
    throw new Error("revision must be a lowercase 40-character Git SHA");
  }

  const manifestPath = path.join(
    root,
    "web",
    "packages",
    "desktop",
    "package.json",
  );
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.version = version;
  manifest.gitHead = revision;
  manifest.publishConfig = {
    ...manifest.publishConfig,
    access: "restricted",
    registry: "https://npm.pkg.github.com",
  };
  manifest.runicCandidate = {
    sourceRevision: revision,
    dependencies: [],
  };
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [, , version, revision] = process.argv;
  prepareWebPackage(repositoryRoot, version, revision);
}
