#!/usr/bin/env node
// Size and startup of examples/first-window-desktop on Kestrel (Runic.Desktop.Kestrel) and on the experimental native
// server, for the host's runtime identifier on Linux, macOS and Windows (X2; measure.sh is the Linux-only X1 script,
// which adds trimmed JIT and the NativeAOT code attribution). Feeds the 1.0 default-server decision (D053.16).
//
//   node tests/benchmarks/native-server/measure.mjs [output-directory]
//
// Needs eng/native/build-runic-server.sh for the host first. RUNIC_MEASURE_MODES (default "fdd sc aot") and
// RUNIC_MEASURE_RUNS (default 15) choose what runs. Writes <output>/results.json and <output>/results.md
// (artifacts/native-server-measure/<rid> by default) and prints the table.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { arch, platform, release } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const rid = `${{ win32: "win", darwin: "osx", linux: existsSync("/etc/alpine-release") ? "linux-musl" : "linux" }[platform()]}-${arch() === "arm64" ? "arm64" : "x64"}`;
const out = resolve(process.argv[2] ?? join(root, "artifacts/native-server-measure", rid));
const modes = (process.env.RUNIC_MEASURE_MODES ?? "fdd sc aot").split(/\s+/).filter(Boolean);
const runs = process.env.RUNIC_MEASURE_RUNS ?? "15";
const project = join(root, "examples/first-window-desktop/FirstWindowDesktop.csproj");
const executableName = `FirstWindowDesktop${platform() === "win32" ? ".exe" : ""}`;
mkdirSync(out, { recursive: true });

const files = directory => readdirSync(directory, { recursive: true, withFileTypes: true })
  .filter(entry => entry.isFile()).map(entry => join(entry.parentPath, entry.name))
  // Symbols and documentation are not shipped; a .dSYM bundle is symbols too.
  .filter(file => !/\.(pdb|dbg|xml)$/i.test(file) && !/\.dSYM[\\/]/.test(file));
const sum = list => list.reduce((total, file) => total + statSync(file).size, 0);

function publish(server, mode) {
  const directory = join(out, `${server}-${mode}`);
  rmSync(directory, { recursive: true, force: true });
  const properties = [`-p:RunicDesktopServer=${server}`];
  if (mode === "fdd") properties.push("--self-contained", "false");
  if (mode === "sc") properties.push("--self-contained", "true");
  if (mode === "trimmed") properties.push("--self-contained", "true", "-p:PublishTrimmed=true");
  if (mode === "aot") properties.push("-p:PublishAot=true");
  const log = join(out, `${server}-${mode}.log`);
  try {
    writeFileSync(log, execFileSync("dotnet", ["publish", project, "-c", "Release", "-r", rid, "-o", directory, "-nologo", ...properties],
      { cwd: root, encoding: "utf8", maxBuffer: 64 << 20 }));
  } catch (error) {
    writeFileSync(log, `${error.stdout ?? ""}${error.stderr ?? ""}`);
    throw new Error(`${server} ${mode} did not publish; see ${log}`);
  }
  const published = files(directory);
  const runtimeConfig = join(directory, "FirstWindowDesktop.runtimeconfig.json");
  const frameworks = existsSync(runtimeConfig)
    ? [JSON.parse(readFileSync(runtimeConfig, "utf8")).runtimeOptions].flatMap(options => [options.framework, ...(options.frameworks ?? [])])
      .filter(Boolean).map(framework => framework.name)
    : [];
  let startup;
  try {
    startup = JSON.parse(execFileSync(process.execPath, [join(root, "tests/benchmarks/native-server/startup.mjs"), join(directory, executableName), runs],
      { cwd: root, encoding: "utf8" }));
  } catch (error) {
    startup = { failed: true, error: String(error.stderr ?? error.message).slice(0, 400) };
  }
  const row = {
    server, mode, rid,
    totalBytes: sum(published),
    executableBytes: statSync(join(directory, executableName)).size,
    aspNetCoreBytes: sum(published.filter(file => /[\\/]Microsoft\.AspNetCore\.[^\\/]*\.dll$/.test(file))),
    sharedFrameworks: frameworks,
    startup,
  };
  console.error(`${server} ${mode}: ${(row.totalBytes / 1048576).toFixed(2)} MiB, ${JSON.stringify(startup)}`);
  return row;
}

const rows = [];
for (const mode of modes) for (const server of ["Kestrel", "Native"]) rows.push(publish(server, mode));
const dotnet = execFileSync("dotnet", ["--version"], { encoding: "utf8" }).trim();
const environment = { rid, os: `${platform()} ${release()}`, dotnet, runner: process.env.ImageOS ? `${process.env.ImageOS} ${process.env.ImageVersion ?? ""}`.trim() : null, runs: Number(runs) };
writeFileSync(join(out, "results.json"), `${JSON.stringify({ environment, rows }, null, 2)}\n`);

const mib = value => (value / 1048576).toFixed(2);
const table = [
  `${rid}, ${environment.os}, .NET SDK ${dotnet}${environment.runner ? `, ${environment.runner}` : ""}; startup medians of ${runs} serve-only runs.`,
  "",
  "| Mode | Server | Publish (MiB) | Executable (MiB) | Microsoft.AspNetCore.* (MiB) | Shared frameworks | URL ready (ms) | Page served (ms) | RSS (MiB) |",
  "| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |",
  ...rows.map(row => `| ${row.mode} | ${row.server} | ${mib(row.totalBytes)} | ${mib(row.executableBytes)} | ${mib(row.aspNetCoreBytes)} | ` +
    `${row.sharedFrameworks.map(name => name.replace("Microsoft.", "")).join(", ") || "—"} | ` +
    (row.startup.failed ? "fails | | |" : `${row.startup.listeningMs} | ${row.startup.servedMs} | ${row.startup.rssMiB} |`)),
].join("\n");
writeFileSync(join(out, "results.md"), `${table}\n`);
console.log(table);
