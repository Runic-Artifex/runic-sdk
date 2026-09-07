import { existsSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { delimiter, dirname, join } from "node:path";

let cached;
// bun run --bun can put a node -> bun shim first on PATH. Compatibility checks
// must explicitly find a real Node executable, including when launched by Bun.
export function nodeCompatibility() {
  if (cached) return cached;
  const name = process.platform === "win32" ? "node.exe" : "node";
  const candidates = process.env.RUNIC_NODE_EXECUTABLE
    ? [process.env.RUNIC_NODE_EXECUTABLE]
    : (process.env.PATH ?? "").split(delimiter).filter(Boolean).map(path => join(path, name));
  for (const executable of candidates) {
    if (!existsSync(executable)) continue;
    const probe = spawnSync(executable, ["-p", "Boolean(process.versions.bun)"], {
      encoding: "utf8", timeout: 5000,
    });
    if (!probe.error && probe.status === 0 && probe.stdout.trim() === "false") {
      return cached = { executable, env: { ...process.env,
        PATH: `${dirname(executable)}${delimiter}${process.env.PATH ?? ""}` } };
    }
  }
  throw new Error("npm/pnpm compatibility verification requires Node; set RUNIC_NODE_EXECUTABLE to its executable.");
}
