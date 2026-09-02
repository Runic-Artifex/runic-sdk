#!/usr/bin/env node

import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";

const registry = "https://npm.pkg.github.com";
const expected = new Set(["@runic-artifex/application-bridge", "@runic-artifex/angular"]);
const [, , suppliedDirectory, tag = "ci"] = process.argv;
const token = process.env.NODE_AUTH_TOKEN;

if (!suppliedDirectory || !token || !new Set(["ci", "release-staging"]).has(tag)) {
  throw new Error("usage: NODE_AUTH_TOKEN=... node eng/publish-github-npm.mjs <directory> [ci|release-staging]");
}

function sha256(value) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

function manifest(tarball) {
  const result = spawnSync("tar", ["-xOf", tarball, "package/package.json"], { encoding: "utf8" });
  if (result.status !== 0) throw new Error(`cannot read ${tarball}: ${result.stderr}`);
  return JSON.parse(result.stdout);
}

async function publishedBytes(name, version) {
  const metadata = await fetch(`${registry}/${encodeURIComponent(name)}`, {
    headers: { Accept: "application/json", Authorization: `Bearer ${token}` },
  });
  if (metadata.status === 404) return undefined;
  if (!metadata.ok) throw new Error(`registry metadata request failed: ${metadata.status}`);
  const tarballUrl = (await metadata.json()).versions?.[version]?.dist?.tarball;
  if (!tarballUrl) return undefined;
  const response = await fetch(tarballUrl, { headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) throw new Error(`registry tarball request failed: ${response.status}`);
  return Buffer.from(await response.arrayBuffer());
}

const directory = path.resolve(suppliedDirectory);
const tarballs = fs.readdirSync(directory)
  .filter((entry) => entry.endsWith(".tgz"))
  .map((entry) => path.join(directory, entry));
if (tarballs.length !== expected.size) throw new Error(`Expected ${expected.size} npm candidates, found ${tarballs.length}.`);

for (const tarball of tarballs) {
  const packageManifest = manifest(tarball);
  if (!expected.delete(packageManifest.name)) throw new Error(`Unexpected npm candidate ${packageManifest.name}.`);
  const localBytes = fs.readFileSync(tarball);
  const existing = await publishedBytes(packageManifest.name, packageManifest.version);
  if (existing) {
    if (sha256(existing) !== sha256(localBytes)) {
      throw new Error(`immutable coordinate collision for ${packageManifest.name}@${packageManifest.version}`);
    }
    console.log(`reused: ${packageManifest.name}@${packageManifest.version}`);
    continue;
  }

  const authDirectory = fs.mkdtempSync(path.join(os.tmpdir(), "runic-npm-auth-"));
  const userConfig = path.join(authDirectory, ".npmrc");
  fs.writeFileSync(userConfig, `//npm.pkg.github.com/:_authToken=${token}\n`, { mode: 0o600 });
  try {
    // Bun 1.4.0 ignores tarball arguments here; npm uploads the exact Bun-built archive without repacking it.
    const result = spawnSync(
      "npm",
      ["publish", tarball, "--registry", registry, "--tag", tag, "--access", "restricted"],
      { encoding: "utf8", env: { ...process.env, NPM_CONFIG_USERCONFIG: userConfig } },
    );
    if (result.status !== 0) throw new Error(`publish failed: ${result.stdout}${result.stderr}`);
  } finally {
    fs.rmSync(authDirectory, { recursive: true, force: true });
  }
  console.log(`published: ${packageManifest.name}@${packageManifest.version}`);
}
