import { spawn } from "node:child_process";

const vite = process.env.RUNIC_PROBE_VITE_BIN;
if (!vite) throw new Error("RUNIC_PROBE_VITE_BIN is required for this test fixture.");
const child = spawn(process.execPath, [vite, ...process.argv.slice(2)], { env: process.env, stdio: "inherit" });
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => child.kill(signal));
child.on("exit", (code, signal) => process.exitCode = code ?? (signal ? 1 : 0));
