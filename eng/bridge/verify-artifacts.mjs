import { resolve } from "node:path";
import { root, run } from "../run.mjs";

const cli = resolve(
  root,
  "packages/web/application-bridge-tooling/dist/esm/cli.js",
);
for (const fixture of ["setup", "counter", "refresh", "portable-core"]) {
  const directory = `specs/application/protocol/application-bridge/${fixture}`;
  run("bun", [
    cli,
    "check",
    "--authority",
    "effect",
    "--source",
    `${directory}/application.bridge.ts`,
    "--ir",
    `${directory}/generated/bridge.ir.json`,
    "--facade",
    `${directory}/generated/application.bridge.generated.ts`,
  ]);
}
await import("./verify-application-bridge-templates.mjs");
