#!/usr/bin/env node

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const identities = [
  "@runic-artifex/application-bridge",
  "@runic-artifex/application-bridge-tooling",
];
const registry = "https://npm.pkg.github.com";
const root = path.resolve(import.meta.dirname, "..");
const temporaryRoot = process.env.RUNNER_TEMP;
const authorityPath = process.env.RUNIC_COMPATIBILITY_SET;
const token = process.env.NODE_AUTH_TOKEN;
const command = process.argv[2];
const backup = temporaryRoot && path.join(temporaryRoot, "runic-vite-ci-inputs");
const inputs = ["package.json", "bun.lock"];

if (!backup || !new Set(["prepare", "restore"]).has(command)) {
  throw new Error("usage: RUNNER_TEMP=... node eng/prepare-ci-dependency.mjs <prepare|restore>");
}

if (command === "restore") {
  for (const input of inputs) fs.copyFileSync(path.join(backup, input), path.join(root, input));
  process.exit(0);
}

if (!authorityPath || !token) {
  throw new Error("prepare requires RUNIC_COMPATIBILITY_SET and NODE_AUTH_TOKEN");
}

const authority = JSON.parse(fs.readFileSync(path.resolve(authorityPath), "utf8"));
fs.mkdirSync(backup, { recursive: true });
for (const input of inputs) fs.copyFileSync(path.join(root, input), path.join(backup, input));
const manifestPath = path.join(root, "package.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
for (const identity of identities) {
  const selectedPackage = authority.packages?.find((entry) => entry.ecosystem === "npm" && entry.identity === identity);
  const revision = authority.sources?.find((entry) => entry.repository === selectedPackage?.source)?.revision;
  if (!/^[0-9a-f]{40}$/u.test(revision ?? "")) {
    throw new Error(`compatibility authority has no exact source revision for ${identity}`);
  }

  const version = `1.0.0-ci.sha${revision.slice(0, 16)}`;
  const metadataResponse = await fetch(`${registry}/${encodeURIComponent(identity)}`, {
    headers: { Accept: "application/json", Authorization: `Bearer ${token}` },
  });
  if (!metadataResponse.ok) throw new Error(`registry metadata request failed for ${identity}: ${metadataResponse.status}`);
  const distribution = (await metadataResponse.json()).versions?.[version]?.dist;
  if (!distribution?.tarball || !distribution.integrity) {
    throw new Error(`GitHub Packages does not contain ${identity}@${version}`);
  }

  const archiveResponse = await fetch(distribution.tarball, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!archiveResponse.ok) throw new Error(`registry tarball request failed for ${identity}: ${archiveResponse.status}`);
  const archive = Buffer.from(await archiveResponse.arrayBuffer());
  const integrity = `sha512-${crypto.createHash("sha512").update(archive).digest("base64")}`;
  if (integrity !== distribution.integrity) throw new Error(`registry integrity mismatch for ${identity}@${version}`);

  const archiveName = identity.slice("@runic-artifex/".length);
  const archivePath = path.join(temporaryRoot, `${archiveName}-${version}.tgz`);
  fs.writeFileSync(archivePath, archive);
  manifest.devDependencies[identity] = `file:${archivePath}`;
  manifest.overrides = { ...(manifest.overrides ?? {}), [identity]: `file:${archivePath}` };
  console.log(`selected: ${identity}@${version}`);
}
fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
