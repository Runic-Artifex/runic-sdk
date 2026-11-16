#!/usr/bin/env node
// After .github/actions/native-server-libraries restored the cached libraries and downloaded this run's artifacts
// (artifacts/native/runic-server/downloaded/runic-server-<rid>), moves the downloaded ones into place and fails unless
// every runtime identifier the packages ship has its shared and its static library.
import { existsSync, mkdirSync, readdirSync, renameSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(new URL("../..", import.meta.url)), "artifacts/native/runic-server");
export const runtimes = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "win-x64", "win-arm64", "osx-arm64"];
export const libraries = rid => rid.startsWith("win-") ? ["runic_server.dll", "runic_server.lib"]
  : rid.startsWith("osx-") ? ["librunic_server.dylib", "librunic_server.a"] : ["librunic_server.so", "librunic_server.a"];

const downloaded = join(root, "downloaded");
if (existsSync(downloaded)) {
  const entries = readdirSync(downloaded, { withFileTypes: true });
  if (entries.length && entries.every(entry => entry.isDirectory() && entry.name.startsWith("runic-server-"))) {
    for (const { name } of entries) {
      const rid = name.replace(/^runic-server-/, "");
      rmSync(join(root, rid), { recursive: true, force: true });
      renameSync(join(downloaded, name), join(root, rid));
    }
  } else if (entries.length) {
    // download-artifact extracts a pattern's only match straight into the path, without its runic-server-<rid>
    // directory: the files belong to the one runtime identifier this run built (RUNIC_SERVER_BUILT).
    const built = JSON.parse(process.env.RUNIC_SERVER_BUILT || "[]");
    if (built.length !== 1) {
      console.error(`::error title=Native server libraries::${entries.map(entry => entry.name).join(", ")} were downloaded without their runic-server-<rid> directories, but this run built ${JSON.stringify(built)}, not one runtime identifier.`);
      process.exit(1);
    }
    const target = join(root, built[0]);
    rmSync(target, { recursive: true, force: true });
    mkdirSync(target, { recursive: true });
    for (const { name } of entries) renameSync(join(downloaded, name), join(target, name));
  }
  rmSync(downloaded, { recursive: true, force: true });
}
const missing = runtimes.flatMap(rid => libraries(rid).filter(file => !existsSync(join(root, rid, file))).map(file => `${rid}/${file}`));
if (missing.length) {
  console.error(`::error title=Native server libraries missing::${missing.join(", ")}: the packages need every runtime identifier's library.`);
  process.exit(1);
}
console.log(`Native server libraries for ${runtimes.join(", ")}.`);
