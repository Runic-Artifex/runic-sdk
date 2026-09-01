import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const registry = "https://npm.pkg.github.com";
const packageNames = ["@runic-artifex/svelte", "@runic-artifex/sveltekit"];

async function download(name, version, token) {
  const metadataResponse = await fetch(
    `${registry}/${encodeURIComponent(name)}`,
    {
      headers: {
        Accept: "application/json",
        Authorization: `Bearer ${token}`,
      },
    },
  );
  if (!metadataResponse.ok) {
    throw new Error(
      `registry metadata request failed for ${name}: ${metadataResponse.status}`,
    );
  }
  const metadata = await metadataResponse.json();
  const distribution = metadata.versions?.[version]?.dist;
  if (!distribution?.tarball || !distribution.integrity) {
    throw new Error(`GitHub Packages does not contain ${name}@${version}`);
  }

  const tarballResponse = await fetch(distribution.tarball, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!tarballResponse.ok) {
    throw new Error(
      `registry tarball request failed for ${name}@${version}: ${tarballResponse.status}`,
    );
  }
  const tarball = Buffer.from(await tarballResponse.arrayBuffer());
  const integrity = `sha512-${crypto.createHash("sha512").update(tarball).digest("base64")}`;
  if (integrity !== distribution.integrity) {
    throw new Error(`registry integrity mismatch for ${name}@${version}`);
  }
  return tarball;
}

const [, , outputDirectory, version] = process.argv;
const token = process.env.NODE_AUTH_TOKEN;
if (!outputDirectory || !version || !token) {
  throw new Error(
    "usage: NODE_AUTH_TOKEN=... node eng/download-github-packages.mjs <directory> <version>",
  );
}

fs.mkdirSync(outputDirectory, { recursive: true });
for (const name of packageNames) {
  const tarball = await download(name, version, token);
  const filename = `${name.slice(1).replace("/", "-")}-${version}.tgz`;
  fs.writeFileSync(path.join(outputDirectory, filename), tarball);
  console.log(`downloaded: ${name}@${version}`);
}
