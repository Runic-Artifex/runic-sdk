#!/usr/bin/env node

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const registry = "https://npm.pkg.github.com";
const ownPackages = ["@runic-artifex/application-bridge", "@runic-artifex/angular"];
const [, , mode, outputDirectory, candidateVersion] = process.argv;
const token = process.env.NODE_AUTH_TOKEN;

if (!new Set(["candidate", "dependencies"]).has(mode) || !outputDirectory || !token) {
  throw new Error("usage: NODE_AUTH_TOKEN=... node eng/download-github-npm.mjs <candidate|dependencies> <directory> [candidate-version]");
}

let packages;
if (mode === "candidate") {
  if (!candidateVersion) throw new Error("candidate mode requires an exact version");
  packages = ownPackages.map((identity) => ({ identity, version: candidateVersion }));
} else {
  const dependencySet = JSON.parse(
    fs.readFileSync(path.join(import.meta.dirname, "runic.ci-dependencies.json"), "utf8"),
  );
  packages = dependencySet.packages.filter((dependency) => dependency.ecosystem === "npm");
}

fs.mkdirSync(outputDirectory, { recursive: true });
for (const { identity, version } of packages) {
  const metadataResponse = await fetch(`${registry}/${encodeURIComponent(identity)}`, {
    headers: { Accept: "application/json", Authorization: `Bearer ${token}` },
  });
  if (!metadataResponse.ok) {
    throw new Error(`registry metadata request failed for ${identity}: ${metadataResponse.status}`);
  }
  const distribution = (await metadataResponse.json()).versions?.[version]?.dist;
  if (!distribution?.tarball || !distribution.integrity) {
    throw new Error(`GitHub Packages does not contain ${identity}@${version}`);
  }
  const response = await fetch(distribution.tarball, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`registry tarball request failed for ${identity}: ${response.status}`);
  const tarball = Buffer.from(await response.arrayBuffer());
  const integrity = `sha512-${crypto.createHash("sha512").update(tarball).digest("base64")}`;
  if (integrity !== distribution.integrity) {
    throw new Error(`registry integrity mismatch for ${identity}@${version}`);
  }
  const filename = `${identity.slice(1).replaceAll("/", "-")}-${version}.tgz`;
  fs.writeFileSync(path.join(outputDirectory, filename), tarball);
  console.log(`downloaded: ${identity}@${version}`);
}
