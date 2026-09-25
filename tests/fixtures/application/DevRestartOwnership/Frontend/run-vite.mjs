import { spawn } from "node:child_process";

const vite = process.env.RUNIC_PROBE_VITE_BIN;
if (!vite) throw new Error("RUNIC_PROBE_VITE_BIN is required for this test fixture.");

const child = spawn(process.execPath, [vite, ...process.argv.slice(2)], {
  env: process.env,
  stdio: "inherit",
});
const stop = signal => child.kill(signal);
process.on("SIGINT", () => stop("SIGINT"));
process.on("SIGTERM", () => stop("SIGTERM"));
child.on("exit", (code, signal) => process.exitCode = code ?? (signal ? 1 : 0));
