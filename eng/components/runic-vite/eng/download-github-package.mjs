import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const registry = "https://npm.pkg.github.com";
const packageName = "@runic-artifex/vite-plugin-runic";

async function download(version, token) {
  const metadataResponse = await fetch(
    `${registry}/${encodeURIComponent(packageName)}`,
    {
      headers: {
        Accept: "application/json",
        Authorization: `Bearer ${token}`,
      },
    },
  );
  if (!metadataResponse.ok) {
    throw new Error(
      `registry metadata request failed for ${packageName}: ${metadataResponse.status}`,
    );
  }
  const metadata = await metadataResponse.json();
  const distribution = metadata.versions?.[version]?.dist;
  if (!distribution?.tarball || !distribution.integrity) {
    throw new Error(`GitHub Packages does not contain ${packageName}@${version}`);
  }
  const tarballResponse = await fetch(distribution.tarball, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!tarballResponse.ok) {
    throw new Error(
      `registry tarball request failed for ${packageName}@${version}: ${tarballResponse.status}`,
    );
  }
  const tarball = Buffer.from(await tarballResponse.arrayBuffer());
  const integrity = `sha512-${crypto.createHash("sha512").update(tarball).digest("base64")}`;
  if (integrity !== distribution.integrity) {
    throw new Error(`registry integrity mismatch for ${packageName}@${version}`);
  }
  return tarball;
}

const [, , outputDirectory, version] = process.argv;
const token = process.env.NODE_AUTH_TOKEN;
if (!outputDirectory || !version || !token) {
  throw new Error(
    "usage: NODE_AUTH_TOKEN=... node eng/download-github-package.mjs <directory> <version>",
  );
}

fs.mkdirSync(outputDirectory, { recursive: true });
const tarball = await download(version, token);
const filename = `runic-artifex-vite-plugin-runic-${version}.tgz`;
fs.writeFileSync(path.join(outputDirectory, filename), tarball);
console.log(`downloaded: ${packageName}@${version}`);
