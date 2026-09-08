import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, existsSync, mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createServer } from "node:net";
import { root } from "../run.mjs";
import { snapshot } from "./snapshot.mjs";

export const runnerBase = "ghcr.io/catthehacker/ubuntu@sha256:62d572b92f9f32d3427b6d220ad1f9dca9c7b6ffad37d295425037dbff78abaf";
export function actArguments(args, image, artifactPath, port, directory = root) {
  return ["workflow_dispatch", "--directory", directory, "--workflows", resolve(directory, ".github/workflows/ci.yml"),
    "--platform", `ubuntu-24.04=${image}`, "--matrix", "os:ubuntu-24.04",
    "--container-architecture", "linux/amd64", "--container-daemon-socket", "-",
    "--container-options", "--init",
    "--network", "host", "--concurrent-jobs", "1", "--pull=false", "--rm",
    "--artifact-server-addr", "127.0.0.1", "--artifact-server-port", String(port),
    "--artifact-server-path", artifactPath,
    "--cache-server-addr", "127.0.0.1", "--cache-server-path", resolve(root, ".cache/act/snapshot-cache"),
    "--action-cache-path", resolve(root, ".cache/act/actions"), "--use-new-action-cache",
    "--secret-file", "/dev/null", "--env-file", "/dev/null", "--var-file", "/dev/null", "--input-file", "/dev/null",
    ...args];
}

function execute(command, args, options = {}) {
  const result = spawnSync(command, args, { cwd: root, stdio: "inherit", ...options });
  if (result.error) throw result.error;
  return result.status ?? 1;
}

async function main() {
  const args = process.argv.slice(2).filter(arg => arg !== "--");
  if (args.includes("--help") || args.includes("-h")) {
    console.log(`Usage: bun run ci [act options]
  --list                         List jobs from the real SDK workflow
  --job docs                     Run documentation verification
  --job managed --matrix suite:application
  --job customers --matrix journey:dev
  --job templates                Run template checks and their prerequisites
  --dryrun                       Validate workflow expansion
  --action-offline-mode          Reuse cached actions and runner image

Runs Linux jobs from .github/workflows/ci.yml using act and Docker/Podman.
Without a job selection, runs the Linux workflow. Windows/macOS remain native CI.
Uncommitted edits are snapshotted once; jobs run against that frozen source.
The Nix shell supplies act with the current artifact-protocol compatibility fix.
See eng/ci/README.md for prerequisites and GitHub rerun semantics.`);
    return;
  }
  if (process.platform !== "linux") throw new Error("This launcher currently supports Linux; use GitHub for Windows/macOS native jobs.");
  if (!process.env.RUNIC_CI_ACT) {
    process.exitCode = execute("nix", ["develop", "--command", "bun", "eng/ci/local.mjs", ...args]);
    return;
  }
  for (const arg of args)
    if (arg.startsWith("os:") && arg !== "os:ubuntu-24.04") throw new Error("Local CI runs the Linux matrix only.");
  const socket = `${process.env.XDG_RUNTIME_DIR ?? `/run/user/${process.getuid()}`}/podman/podman.sock`;
  if (!process.env.DOCKER_HOST && existsSync(socket)) process.env.DOCKER_HOST = `unix://${socket}`;
  const engine = process.env.RUNIC_CONTAINER_ENGINE ?? (process.env.DOCKER_HOST === `unix://${socket}` ? "podman" : "docker");
  const recipe = createHash("sha256").update(readFileSync(resolve(root, "eng/ci/runner.Containerfile"))).digest("hex").slice(0, 12);
  const image = `localhost/runic-artifex/act-ubuntu-24.04:x86_64-dff4ec57d900-${recipe}`;
  const listing = args.some(arg => ["--list", "-l", "--dryrun", "-n", "--graph", "-g"].includes(arg));
  if (!listing && execute(engine, ["image", "inspect", image], { stdio: "ignore" }) !== 0) {
    if (args.includes("--action-offline-mode")) throw new Error("Runner image is not cached; run without --action-offline-mode once.");
    const status = execute(engine, ["build", "--pull=false", "--build-arg", `RUNNER_IMAGE=${runnerBase}`,
      "--tag", image, "--file", "eng/ci/runner.Containerfile", "eng/ci"]);
    if (status !== 0) { process.exitCode = status; return; }
  }
  const artifactPath = resolve(root, "artifacts/act", `${Date.now()}-${process.pid}`);
  mkdirSync(artifactPath, { recursive: true });
  const listener = createServer();
  await new Promise((accept, reject) => { listener.once("error", reject); listener.listen(0, "127.0.0.1", accept); });
  const port = listener.address().port;
  await new Promise((accept, reject) => listener.close(error => error ? reject(error) : accept()));
  console.log(`Running the Linux SDK workflow; artifacts: ${artifactPath}`);
  const forwarded = process.env.GITHUB_TOKEN ? ["--secret", "GITHUB_TOKEN", ...args] : args;
  if (listing) {
    process.exitCode = execute(process.env.RUNIC_CI_ACT, actArguments(forwarded, image, artifactPath, port));
    return;
  }
  const snapshots = resolve(root, ".cache/act/workspaces");
  mkdirSync(snapshots, { recursive: true });
  const directory = mkdtempSync(resolve(snapshots, "run-"));
  try {
    snapshot(root, directory);
    process.exitCode = execute(process.env.RUNIC_CI_ACT, actArguments(forwarded, image, artifactPath, port, directory), { cwd: directory });
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
