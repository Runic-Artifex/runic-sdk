#!/usr/bin/env node
// Startup cost of an embedded Runic Assets archive (S1, D053.25), Linux only (the program reads /proc).
//
//   node tests/benchmarks/asset-archive/measure.mjs [output-directory]
//
// Writes a synthetic 6 MiB frontend (a Vite-like build: an entry document, an entry bundle, a vendor bundle and a
// stylesheet that the first page loads, lazy chunks, images and fonts), packs it with the built Runic.Assets packer
// (tools/Runic.Assets.Packer, Debug), publishes AssetArchiveStartup with NativeAOT and runs it RUNIC_MEASURE_RUNS
// times (default 21) after one warm-up. Prints the medians of the in-process times from the process's start of
// AssetArchive.ReadEmbedded: the load, the load plus the first page's assets, and every asset; the process's wall
// time; its peak RSS; and whether it loaded OpenSSL.
import { execFileSync } from "node:child_process";
import { mkdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "../../..");
const out = resolve(process.argv[2] ?? join(root, "artifacts/asset-archive-measure"));
const runs = Number(process.env.RUNIC_MEASURE_RUNS ?? "21");
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

// Deterministic content: minified-JavaScript-like text compresses about as a real bundle does (≈5:1); images and
// fonts are already compressed (stored).
let seed = 0x2545f491;
const random = () => ((seed = (Math.imul(seed ^ (seed >>> 15), 0x2c1b3c6d) + 0x6d2b79f5) >>> 0) / 2 ** 32);
const words = Array.from({ length: 384 }, (_, index) => `${"abcdefghijklmnopqrstuvwxyz"[index % 26]}${Math.floor(random() * 1e6).toString(36)}`);
const script = bytes => {
  let text = "";
  while (text.length < bytes) {
    const name = words[Math.floor(random() * words.length)];
    text += `function ${name}(${words[Math.floor(random() * words.length)]}){return ${words[Math.floor(random() * words.length)]}+${Math.floor(random() * 1000)}}`;
  }
  return text.slice(0, bytes);
};
const binary = bytes => Buffer.from(Array.from({ length: bytes }, () => Math.floor(random() * 256)));
const KiB = 1024;
const frontend = join(out, "frontend");
const files = {
  "index.html": `<!doctype html><script type="module" src="./assets/index-Cf3tzbYH.js"></script><link rel="modulepreload" href="./assets/vendor-B9x2kQzA.js"><link rel="stylesheet" href="./assets/index-D4kx9Q1b.css">`,
  "assets/index-Cf3tzbYH.js": script(1536 * KiB),
  "assets/vendor-B9x2kQzA.js": script(2048 * KiB),
  "assets/index-D4kx9Q1b.css": script(256 * KiB),
  ...Object.fromEntries(Array.from({ length: 8 }, (_, index) => [`assets/route${index}-A${index}b2c3d4.js`, script(192 * KiB)])),
  ...Object.fromEntries(Array.from({ length: 4 }, (_, index) => [`assets/image${index}-E${index}f6g7h8.png`, binary(128 * KiB)])),
  "assets/font-I9j0k1l2.woff2": binary(128 * KiB),
};
let frontendBytes = 0;
for (const [path, content] of Object.entries(files)) {
  mkdirSync(dirname(join(frontend, path)), { recursive: true });
  writeFileSync(join(frontend, path), content);
  frontendBytes += Buffer.byteLength(content);
}

const archive = join(out, "frontend.runic-assets");
execFileSync("dotnet", [join(root, "tools/Runic.Assets.Packer/bin/Debug/net10.0/Runic.Assets.Packer.dll"), frontend, archive, "--trusted-generated-output"],
  { stdio: ["ignore", "ignore", "inherit"] });
const published = join(out, "aot");
execFileSync("dotnet", ["publish", join(here, "AssetArchiveStartup.csproj"), "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
  `-p:RunicAssetsBenchmarkArchive=${archive}`, "-o", published, "-nologo", "-v:q"], { cwd: root, stdio: ["ignore", "ignore", "inherit"] });

const executable = join(published, "AssetArchiveStartup");
const once = () => {
  const started = process.hrtime.bigint();
  const result = JSON.parse(execFileSync(executable, { encoding: "utf8" }));
  return { ...result, wallMs: Number(process.hrtime.bigint() - started) / 1e6 };
};
once();
const results = Array.from({ length: runs }, once);
const pick = key => Math.round(median(results.map(result => result[key])) * 100) / 100;
const summary = {
  frontendMiB: Math.round(frontendBytes / 1048576 * 100) / 100,
  archiveMiB: Math.round(statSync(archive).size / 1048576 * 100) / 100,
  assets: results[0].assets,
  runs,
  loadMs: pick("loadMs"),
  firstPageMs: pick("firstPageMs"),
  allMs: pick("allMs"),
  wallMs: pick("wallMs"),
  peakMiB: Math.round(median(results.map(result => result.peakKiB)) / 1024 * 10) / 10,
  openssl: results.some(result => result.openssl),
};
writeFileSync(join(out, "results.json"), `${JSON.stringify(summary, null, 2)}\n`);
console.log(JSON.stringify(summary));
