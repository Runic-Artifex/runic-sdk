#!/usr/bin/env node
import { existsSync, readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const workspace = resolve(repository, "..");
const authorityRef = process.env.RUNIC_COMPATIBILITY_AUTHORITY_REF;

if (!authorityRef) {
  throw new Error("RUNIC_COMPATIBILITY_AUTHORITY_REF must identify the compatibility authority candidate.");
}

const compatibility = JSON.parse(
  readFileSync(resolve(repository, "eng/runic.compatibility-set.json"), "utf8"),
);
const candidates = [
  {
    repository: ".github",
    url: "https://github.com/Runic-Artifex/.github.git",
    revision: authorityRef,
  },
  ...compatibility.sources,
];

for (const candidate of candidates) {
  const target = resolve(workspace, candidate.repository);
  if (existsSync(target)) {
    throw new Error(`Refusing to replace existing CI candidate '${target}'.`);
  }

  execFileSync("git", ["clone", "--filter=blob:none", "--no-checkout", candidate.url, target], {
    stdio: "inherit",
  });
  const checkoutRevision = candidate.repository === ".github" && !/^[0-9a-f]{40}$/i.test(candidate.revision)
    ? `origin/${candidate.revision}`
    : candidate.revision;
  execFileSync("git", ["-C", target, "switch", "--detach", checkoutRevision], {
    stdio: "inherit",
  });

  const resolved = execFileSync("git", ["-C", target, "rev-parse", "HEAD"], {
    encoding: "utf8",
  }).trim();
  if (/^[0-9a-f]{40}$/i.test(candidate.revision) && resolved !== candidate.revision) {
    throw new Error(`Candidate '${candidate.repository}' resolved '${resolved}', expected '${candidate.revision}'.`);
  }
}
