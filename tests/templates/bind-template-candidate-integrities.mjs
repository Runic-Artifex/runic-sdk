import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";

const [lockPath, ...archives] = process.argv.slice(2);
if (!lockPath || archives.length === 0) {
  throw new Error("Usage: bind-template-candidate-integrities.mjs <package-lock.json> <npm-archive>...");
}

const candidates = new Map();
for (const archive of archives) {
  const manifest = JSON.parse(execFileSync("tar", ["-xOf", archive, "package/package.json"], { encoding: "utf8" }));
  candidates.set(manifest.name, {
    version: manifest.version,
    integrity: `sha512-${createHash("sha512").update(readFileSync(archive)).digest("base64")}`,
  });
}

const lock = JSON.parse(readFileSync(lockPath, "utf8"));
const root = lock.packages?.[""];
if (!root) throw new Error("Template lock does not contain a root package entry.");
const declared = new Set();
for (const sectionName of ["dependencies", "devDependencies"]) {
  const section = root[sectionName] ?? {};
  for (const name of Object.keys(section).filter((item) => candidates.has(item))) {
    section[name] = candidates.get(name).version;
    declared.add(name);
  }
}
if (declared.size === 0) throw new Error("Template lock does not declare any supplied candidates.");
for (const name of declared) {
  const entry = lock.packages?.[`node_modules/${name}`];
  const candidate = candidates.get(name);
  if (!entry) throw new Error(`Template lock does not contain candidate ${name}.`);
  entry.version = candidate.version;
  entry.integrity = candidate.integrity;
}
writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
