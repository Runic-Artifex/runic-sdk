import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";

const [lockPath, ...archives] = process.argv.slice(2);
if (!lockPath || archives.length === 0) {
  throw new Error("Usage: bind-template-candidate-integrities.mjs <lockfile> <npm-archive>...");
}

const candidates = new Map();
for (const archive of archives) {
  const manifest = JSON.parse(execFileSync("tar", ["-xOf", archive, "package/package.json"], { encoding: "utf8" }));
  candidates.set(manifest.name, {
    version: manifest.version,
    integrity: `sha512-${createHash("sha512").update(readFileSync(archive)).digest("base64")}`,
  });
}

const escapeRegExp = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

if (lockPath.endsWith("pnpm-lock.yaml")) {
  let lock = readFileSync(lockPath, "utf8");
  let bound = 0;
  for (const [name, candidate] of candidates) {
    const escapedName = escapeRegExp(name);
    const packageKey = lock.match(new RegExp(`^  '${escapedName}@([^']+)':$`, "m"));
    if (!packageKey) continue;
    const previousVersion = packageKey[1];
    lock = lock.replaceAll(`${name}@${previousVersion}`, `${name}@${candidate.version}`);
    lock = lock.replaceAll(
      `'${name}': ${previousVersion}`,
      `'${name}': ${candidate.version}`,
    );
    lock = lock.replace(
      new RegExp(`(^      '${escapedName}':\\n        specifier: )[^\\n]+(\\n        version: )[^\\n(]+`, "m"),
      `$1${candidate.version}$2${candidate.version}`,
    );
    const marker = `  '${name}@${candidate.version}':\n`;
    const start = lock.indexOf(marker);
    if (start < 0) throw new Error(`pnpm lock does not contain candidate ${name}.`);
    const end = lock.indexOf("\n  '", start + marker.length);
    const packageEntry = lock.slice(start, end < 0 ? lock.length : end);
    if (!/resolution: \{integrity: sha512-[^}]+\}/.test(packageEntry)) {
      throw new Error(`pnpm lock does not contain candidate integrity for ${name}.`);
    }
    const updatedEntry = packageEntry.replace(
      /resolution: \{integrity: sha512-[^}]+\}/,
      `resolution: {integrity: ${candidate.integrity}}`,
    );
    lock = `${lock.slice(0, start)}${updatedEntry}${lock.slice(start + packageEntry.length)}`;
    bound += 1;
  }
  if (bound === 0) throw new Error("pnpm lock does not contain any supplied candidates.");
  writeFileSync(lockPath, lock);
  process.exit(0);
}

if (lockPath.endsWith("bun.lock")) {
  let lines = readFileSync(lockPath, "utf8").split("\n");
  let bound = 0;
  for (const [name, candidate] of candidates) {
    const prefix = `    "${name}": [`;
    const lineIndex = lines.findIndex((line) => line.startsWith(prefix));
    if (lineIndex < 0) continue;
    if (!/"sha512-[^"]+"\],$/.test(lines[lineIndex])) {
      throw new Error(`Bun lock does not contain candidate integrity for ${name}.`);
    }
    const previousVersion = lines[lineIndex].match(
      new RegExp(`${escapeRegExp(name)}@([^"]+)`),
    )?.[1];
    if (!previousVersion) throw new Error(`Bun lock does not contain candidate ${name}.`);
    lines = lines.map((line) => line.replaceAll(
      `"${name}": "${previousVersion}"`,
      `"${name}": "${candidate.version}"`,
    ));
    lines[lineIndex] = lines[lineIndex]
      .replace(`${name}@${previousVersion}`, `${name}@${candidate.version}`)
      .replace(/"sha512-[^"]+"\],$/, `"${candidate.integrity}"],`);
    const importerPrefix = `        "${name}": `;
    const importerIndex = lines.findIndex((line) => line.startsWith(importerPrefix));
    if (importerIndex < 0) throw new Error(`Bun lock does not declare candidate ${name}.`);
    lines[importerIndex] = `${importerPrefix}"${candidate.version}",`;
    bound += 1;
  }
  if (bound === 0) throw new Error("Bun lock does not contain any supplied candidates.");
  writeFileSync(lockPath, lines.join("\n"));
  process.exit(0);
}

if (!lockPath.endsWith("package-lock.json")) {
  throw new Error(`Unsupported template lockfile: ${lockPath}`);
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
  for (const field of ["dependencies", "optionalDependencies"]) {
    for (const dependency of Object.keys(entry[field] ?? {})) {
      if (candidates.has(dependency)) entry[field][dependency] = candidates.get(dependency).version;
    }
  }
}
writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
