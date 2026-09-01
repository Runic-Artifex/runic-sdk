import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

const registry = "https://npm.pkg.github.com";

function sha256(value) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

function packageManifest(tarball) {
  const result = spawnSync("tar", ["-xOf", tarball, "package/package.json"], {
    encoding: "utf8",
  });
  if (result.status !== 0) {
    throw new Error(
      `cannot read package manifest from ${tarball}: ${result.stderr}`,
    );
  }
  return JSON.parse(result.stdout);
}

async function metadata(name, token) {
  const response = await fetch(`${registry}/${encodeURIComponent(name)}`, {
    headers: {
      Accept: "application/json",
      Authorization: `Bearer ${token}`,
    },
  });
  if (response.status === 404) {
    return undefined;
  }
  if (!response.ok) {
    throw new Error(
      `registry metadata request failed for ${name}: ${response.status}`,
    );
  }
  return response.json();
}

async function existingTarball(packageMetadata, version, token) {
  const url = packageMetadata?.versions?.[version]?.dist?.tarball;
  if (!url) {
    return undefined;
  }
  const response = await fetch(url, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    throw new Error(`registry tarball request failed: ${response.status}`);
  }
  return Buffer.from(await response.arrayBuffer());
}

async function samePublishedTarball(manifest, localBytes, token) {
  const packageMetadata = await metadata(manifest.name, token);
  const published = await existingTarball(
    packageMetadata,
    manifest.version,
    token,
  );
  return published === undefined
    ? undefined
    : sha256(published) === sha256(localBytes);
}

async function publishOrReuse(tarball, token, tag) {
  const manifest = packageManifest(tarball);
  const localBytes = fs.readFileSync(tarball);
  const existing = await samePublishedTarball(manifest, localBytes, token);
  if (existing === true) {
    return { name: manifest.name, version: manifest.version, status: "reused" };
  }
  if (existing === false) {
    throw new Error(
      `immutable coordinate collision for ${manifest.name}@${manifest.version}: digest differs`,
    );
  }

  const result = spawnSync(
    "bun",
    [
      "publish",
      "--registry",
      registry,
      "--tag",
      tag,
      "--access",
      "restricted",
      tarball,
    ],
    {
      encoding: "utf8",
      env: { ...process.env, NODE_AUTH_TOKEN: token },
    },
  );
  if (result.status === 0) {
    return {
      name: manifest.name,
      version: manifest.version,
      status: "published",
    };
  }

  if ((await samePublishedTarball(manifest, localBytes, token)) === true) {
    return {
      name: manifest.name,
      version: manifest.version,
      status: "reused-after-race",
    };
  }
  throw new Error(
    `publish failed for ${manifest.name}: ${result.stdout}${result.stderr}`,
  );
}

const [, , packageDirectory, tag = "ci"] = process.argv;
const token = process.env.NODE_AUTH_TOKEN;
if (!packageDirectory || !token || !["ci", "release-staging"].includes(tag)) {
  throw new Error(
    "usage: NODE_AUTH_TOKEN=... node eng/publish-github-packages.mjs <directory> [ci|release-staging]",
  );
}

const tarballs = fs
  .readdirSync(packageDirectory)
  .filter((file) => file.endsWith(".tgz"))
  .sort()
  .map((file) => path.join(packageDirectory, file));
if (tarballs.length === 0) {
  throw new Error(`no package tarballs found in ${packageDirectory}`);
}
for (const tarball of tarballs) {
  const result = await publishOrReuse(tarball, token, tag);
  console.log(`${result.status}: ${result.name}@${result.version}`);
}
