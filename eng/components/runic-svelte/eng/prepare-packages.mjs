import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const packageDirectories = ["packages/svelte", "packages/sveltekit"];
const versionPattern =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const revisionPattern = /^[0-9a-f]{40}$/;
const registries = new Map([
  ["github", { url: "https://npm.pkg.github.com", access: "restricted" }],
  ["public", { url: "https://registry.npmjs.org", access: "public" }],
]);

export function preparePackages(root, version, revision, registryName) {
  if (!versionPattern.test(version)) {
    throw new Error(`invalid package version: ${version}`);
  }
  if (!revisionPattern.test(revision)) {
    throw new Error("revision must be a lowercase 40-character Git SHA");
  }
  const registry = registries.get(registryName);
  if (!registry) {
    throw new Error("registry must be github or public");
  }

  for (const directory of packageDirectories) {
    const manifestPath = path.join(root, directory, "package.json");
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    manifest.version = version;
    manifest.gitHead = revision;
    manifest.publishConfig = {
      ...manifest.publishConfig,
      access: registry.access,
      registry: registry.url,
    };
    manifest.runicCandidate = {
      sourceRevision: revision,
      dependencies:
        manifest.name === "@runic-artifex/sveltekit"
          ? [
              {
                ecosystem: "npm",
                package: "@runic-artifex/svelte",
                version,
              },
            ]
          : [],
    };
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [, , version, revision, registryName] = process.argv;
  preparePackages(repositoryRoot, version, revision, registryName);
}
