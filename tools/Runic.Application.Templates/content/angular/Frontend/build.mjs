import { spawn } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";

const root = import.meta.dirname;
const output = resolve(root, "dist");
const watch = process.argv.includes("--watch");
const configuration = process.argv.includes("--production")
  ? "production"
  : watch ? "development" : "production";
const executable = resolve(
  root,
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

const exitCode = await new Promise((complete, fail) => {
  child.once("error", fail);
  child.once("exit", complete);
});
if (exitCode !== 0 && exitCode !== null) {
  process.exitCode = exitCode;
}
