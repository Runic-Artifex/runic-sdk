#!/usr/bin/env node
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { arch, cpus, platform, release } from "node:os";
import { basename, resolve } from "node:path";
import { performance } from "node:perf_hooks";

const shaPattern = /^[a-f0-9]{64}$/;
const revisionPattern = /^[a-f0-9]{40}$/;
export const receiptSchema = "runic.comparative-stress-observation/2";

export function percentile(values, fraction) {
  const ordered = [...values].sort((left, right) => left - right);
  return ordered[Math.max(0, Math.ceil(ordered.length * fraction) - 1)];
}

export function summarize(repetitions) {
  const latencies = repetitions.flatMap((item) => item.requestLatenciesMs);
  return {
    startupMs: repetitions.map((item) => item.startupMs),
    completionMs: repetitions.map((item) => item.completionMs),
    throughputRequestsPerSecond: repetitions.map((item) => item.throughputRequestsPerSecond),
    managedAllocatedBytes: repetitions.map((item) => item.managedAllocatedBytes),
    peakWorkingSetBytes: repetitions.map((item) => item.peakWorkingSetBytes),
    latencyMs: {
      p50: percentile(latencies, 0.50),
      p95: percentile(latencies, 0.95),
      p99: percentile(latencies, 0.99),
    },
  };
}

export function verifyReceipt(receipt, { requireCurrentHost = false } = {}) {
  const errors = [];
  if (!["runic.comparative-stress-observation/1", receiptSchema].includes(receipt?.schema)) errors.push("invalid receipt schema");
  const workload = receipt?.workload;
  if (workload?.schema !== "runic.comparative-stress-workload/1") errors.push("invalid workload schema");
  for (const key of ["payloadBytes", "warmupRequests", "repetitions", "requestsPerRepetition", "concurrency", "requestTimeoutMs", "startupTimeoutMs"]) {
    if (!Number.isInteger(workload?.[key]) || workload[key] <= 0) errors.push(`invalid workload ${key}`);
  }
  if (!shaPattern.test(receipt?.workloadSha256 ?? "")) errors.push("invalid workload digest");
  if (workload && typeof workload === "object" && receipt?.workloadSha256 !== createHash("sha256").update(JSON.stringify(workload)).digest("hex")) errors.push("workload digest mismatch");
  const host = receipt?.host;
  const supportedHost = (host?.platform === "linux" && host?.architecture === "x64")
    || (host?.platform === "win32" && host?.architecture === "x64")
    || (host?.platform === "darwin" && ["x64", "arm64"].includes(host?.architecture));
  if (!host || !supportedHost || typeof host.release !== "string" || !host.release
      || typeof host.cpuModel !== "string" || !host.cpuModel
      || !Number.isInteger(host.logicalCpuCount) || host.logicalCpuCount <= 0
      || !/^v\d+\.\d+\.\d+$/.test(host.node ?? "")
      || (receipt?.schema === receiptSchema && !/^\d+\.\d+\.\d+$/.test(host.dotnetSdk ?? ""))) errors.push("host fingerprint mismatch");
  if (requireCurrentHost && (host?.platform !== platform() || host?.architecture !== arch())) errors.push("receipt was not produced on the current host");
  if (receipt?.interpretation !== "raw-observation-only-not-a-cross-host-or-release-sla") errors.push("invalid interpretation boundary");
  if (JSON.stringify(receipt?.externalActions) !== JSON.stringify({ publications: 0, uploads: 0, releases: 0, tags: 0 })) errors.push("external-action boundary mismatch");
  const observations = receipt?.observations;
  if (!Array.isArray(observations) || observations.map((item) => item.implementation).sort().join(",") !== "cs-webui,runic-desktop") {
    errors.push("both implementations must be observed exactly once");
  } else {
    for (const observation of observations) {
      if (!revisionPattern.test(observation.revision ?? "")) errors.push(`${observation.implementation} revision is invalid`);
      if (!Array.isArray(observation.repetitions) || observation.repetitions.length !== workload?.repetitions) errors.push(`${observation.implementation} repetition count mismatch`);
      for (const repetition of observation.repetitions ?? []) {
        if (!shaPattern.test(repetition.adapterAssemblySha256 ?? "")) errors.push(`${observation.implementation} adapter assembly digest is invalid`);
        if (!shaPattern.test(repetition.implementationAssemblySha256 ?? "")) errors.push(`${observation.implementation} implementation assembly digest is invalid`);
        if (observation.implementation === "cs-webui" && !shaPattern.test(repetition.nativeLibrarySha256 ?? "")) errors.push("CS-WebUI native library digest is invalid");
        const expectedShutdown = observation.implementation === "runic-desktop" ? "graceful-dispose" : "process-exit-after-native-server-only-destroy-fault";
        if (repetition.shutdownBoundary !== expectedShutdown) errors.push(`${observation.implementation} shutdown boundary mismatch`);
        if (!Array.isArray(repetition.requestLatenciesMs) || repetition.requestLatenciesMs.length !== workload?.requestsPerRepetition) errors.push(`${observation.implementation} raw latency count mismatch`);
        for (const value of [repetition.startupMs, repetition.completionMs, repetition.throughputRequestsPerSecond, repetition.managedAllocatedBytes, repetition.peakWorkingSetBytes, ...(repetition.requestLatenciesMs ?? [])]) {
          if (!Number.isFinite(value) || value < 0) errors.push(`${observation.implementation} contains an invalid measurement`);
        }
      }
      if (JSON.stringify(observation.summary) !== JSON.stringify(summarize(observation.repetitions ?? []))) errors.push(`${observation.implementation} summary mismatch`);
    }
  }
  return errors;
}

async function runAdapter(implementation, executable, revision, workload) {
  const startedAt = performance.now();
  const command = executable.endsWith(".dll") ? "dotnet" : executable;
  const args = executable.endsWith(".dll") ? [executable] : [];
  const child = spawn(command, args, {
    env: {
      ...process.env,
      RUNIC_STRESS_IMPLEMENTATION_REVISION: revision,
      RUNIC_STRESS_PAYLOAD_BYTES: String(workload.payloadBytes),
    },
    stdio: ["pipe", "pipe", "pipe"],
  });
  const exitPromise = new Promise((accept, reject) => {
    child.once("error", reject);
    child.once("exit", (exitCode, signal) => accept({ exitCode, signal }));
  });
  let stderr = "";
  child.stderr.setEncoding("utf8");
  child.stderr.on("data", (chunk) => stderr += chunk);
  const lines = createInterface({ input: child.stdout });
  const iterator = lines[Symbol.asyncIterator]();
  const ready = await nextJson(iterator, workload.startupTimeoutMs, `${implementation} startup`, stderr);
  const startupMs = performance.now() - startedAt;
  if (ready.schema !== "runic.comparative-stress-adapter-ready/1" || ready.implementation !== implementation || ready.revision !== revision) {
    child.kill();
    throw new Error(`${implementation} emitted invalid readiness metadata`);
  }
  for (let index = 0; index < workload.warmupRequests; index += 1) await request(ready.url, workload, false);
  const requestLatenciesMs = [];
  const measuredAt = performance.now();
  let next = 0;
  await Promise.all(Array.from({ length: workload.concurrency }, async () => {
    while (true) {
      const index = next++;
      if (index >= workload.requestsPerRepetition) return;
      requestLatenciesMs[index] = await request(ready.url, workload, true);
    }
  }));
  const completionMs = performance.now() - measuredAt;
  child.stdin.end("stop\n");
  const stopped = await nextJson(iterator, workload.startupTimeoutMs, `${implementation} shutdown`, stderr);
  const { exitCode, signal } = await exitPromise;
  if (exitCode !== 0 || signal !== null || stopped.schema !== "runic.comparative-stress-adapter-stopped/1" || stopped.implementation !== implementation) {
    throw new Error(`${implementation} adapter failed (${exitCode ?? signal}): ${stderr}`);
  }
  return {
    startupMs,
    completionMs,
    throughputRequestsPerSecond: workload.requestsPerRepetition / (completionMs / 1000),
    requestLatenciesMs,
    managedAllocatedBytes: stopped.managedAllocatedBytes,
    peakWorkingSetBytes: stopped.peakWorkingSetBytes,
    runtime: ready.runtime,
    adapterAssemblySha256: ready.adapterAssemblySha256,
    implementationAssemblySha256: ready.implementationAssemblySha256,
    shutdownBoundary: ready.shutdownBoundary,
    ...(ready.nativeLibrarySha256 ? { nativeLibrarySha256: ready.nativeLibrarySha256 } : {}),
  };
}

async function request(url, workload, measured) {
  const startedAt = performance.now();
  const response = await fetch(url, { signal: AbortSignal.timeout(workload.requestTimeoutMs) });
  if (!response.ok) throw new Error(`stress request failed with HTTP ${response.status}`);
  const body = new Uint8Array(await response.arrayBuffer());
  if (body.length !== workload.payloadBytes || body.some((value) => value !== 82)) throw new Error("stress payload mismatch");
  return measured ? performance.now() - startedAt : 0;
}

async function nextJson(iterator, timeoutMs, label, stderr) {
  let timeoutId;
  const timeout = new Promise((_, reject) => {
    timeoutId = setTimeout(() => reject(new Error(`${label} timed out: ${stderr}`)), timeoutMs);
  });
  const item = await Promise.race([iterator.next(), timeout]).finally(() => clearTimeout(timeoutId));
  if (item.done) throw new Error(`${label} ended early: ${stderr}`);
  return JSON.parse(item.value);
}

function argumentsMap(args) {
  const result = new Map();
  for (let index = 0; index < args.length; index += 2) result.set(args[index], args[index + 1]);
  return result;
}

async function main(args) {
  const mode = args.shift();
  if (mode === "verify") {
    const receipt = JSON.parse(await readFile(resolve(args[0]), "utf8"));
    const errors = verifyReceipt(receipt);
    if (errors.length) throw new Error(errors.join("\n"));
    return;
  }
  if (mode !== "run") throw new Error("Usage: run.mjs run --workload <json> --desktop <executable> --desktop-revision <sha> --cs-webui <executable> --cs-webui-revision <sha> --dotnet-sdk <version> | verify <receipt>");
  const options = argumentsMap(args);
  const workloadPath = resolve(options.get("--workload"));
  const workloadBytes = await readFile(workloadPath);
  const workload = JSON.parse(workloadBytes);
  const definitions = [
    ["runic-desktop", resolve(options.get("--desktop")), options.get("--desktop-revision")],
    ["cs-webui", resolve(options.get("--cs-webui")), options.get("--cs-webui-revision")],
  ];
  const observations = [];
  for (const [implementation, executable, revision] of definitions) {
    const repetitions = [];
    for (let index = 0; index < workload.repetitions; index += 1) repetitions.push(await runAdapter(implementation, executable, revision, workload));
    observations.push({ implementation, revision, executable: basename(executable), repetitions, summary: summarize(repetitions) });
  }
  const receipt = {
    schema: receiptSchema,
    workload,
    workloadSha256: createHash("sha256").update(JSON.stringify(workload)).digest("hex"),
    host: { platform: platform(), architecture: arch(), release: release(), cpuModel: cpus()[0]?.model ?? "unknown", logicalCpuCount: cpus().length, node: process.version, dotnetSdk: options.get("--dotnet-sdk") },
    observations,
    interpretation: "raw-observation-only-not-a-cross-host-or-release-sla",
    externalActions: { publications: 0, uploads: 0, releases: 0, tags: 0 },
  };
  const errors = verifyReceipt(receipt, { requireCurrentHost: true });
  if (errors.length) throw new Error(errors.join("\n"));
  process.stdout.write(`${JSON.stringify(receipt, null, 2)}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(new URL(import.meta.url).pathname)) {
  main(process.argv.slice(2)).catch((error) => { console.error(error.message); process.exitCode = 1; });
}
