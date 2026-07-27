import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

const root = import.meta.dirname;
const output = resolve(root, "dist");
const watch = process.argv.includes("--watch");
const configuration = process.argv.includes("--production")
  ? "production"
  : watch ? "development" : "production";
const executable = resolve(
  root,
  "..",
  "node_modules",
  ".bin",
  process.platform === "win32" ? "ng.cmd" : "ng",
);
await mkdir(output, { recursive: true });

const child = spawn(
  executable,
  ["build", "--configuration", configuration, ...(watch ? ["--watch"] : [])],
  { cwd: root, stdio: ["inherit", "pipe", "pipe"] },
);
let manifestWrite = Promise.resolve();
let bufferedOutput = "";
child.stdout.on("data", (chunk) => {
  const text = chunk.toString();
  process.stdout.write(text);
  bufferedOutput = (bufferedOutput + text).slice(-2048);
  if (watch && bufferedOutput.includes("Application bundle generation complete.")) {
    bufferedOutput = "";
    manifestWrite = manifestWrite
      .then(() => new Promise((complete) => setTimeout(complete, 500)))
      .then(writeManifest);
  }
});
child.stderr.pipe(process.stderr);

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.once(signal, () => child.kill(signal));
}

const exitCode = await new Promise((complete) => child.once("exit", complete));
if (!watch && exitCode === 0) {
  await writeManifest();
} else {
  await manifestWrite;
}
if (exitCode !== 0 && exitCode !== null) {
  process.exitCode = exitCode;
}

async function writeManifest() {
  const files = await collectFiles(output);
  const entries = {};
  for (const relativePath of files) {
    if (relativePath === "webuitoolkit.assets.json") continue;
    const bytes = await readFile(resolve(output, relativePath));
    entries[relativePath] = {
      bytes: bytes.byteLength,
      sha256: createHash("sha256").update(bytes).digest("hex"),
    };
  }

  const manifest = {
    schema: "webuitoolkit.frontend-assets/1",
    framework: "Angular",
    mode: configuration,
    entrypoints: { document: "index.html" },
    files: entries,
  };
  await writeFile(
    resolve(output, "webuitoolkit.assets.json"),
    JSON.stringify(manifest, null, 2) + "\n",
    "utf8",
  );
  console.log("[WebUIToolkit] Wrote webuitoolkit.assets.json.");
}

async function collectFiles(directory, relative = "") {
  const entries = await readdir(resolve(directory, relative), { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0)) {
    const path = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) files.push(...await collectFiles(directory, path));
    else files.push(path);
  }
  return files;
}
