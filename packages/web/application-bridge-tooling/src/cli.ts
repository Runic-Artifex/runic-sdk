#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import {
  ApplicationBridgeCompilerError,
  checkApplicationBridge,
  compareApplicationBridgeIr,
  generateApplicationBridge,
  watchApplicationBridge,
  validateApplicationBridgeIr,
  type ApplicationBridgeCompilerOptions,
} from "./compiler.js";
import type { BridgeIr } from "./model.js";

const args = process.argv.slice(2);
const command = args.shift();
const parsed = parseArguments(args);
const options = parsed.options;

try {
  if (command === "generate") {
    const result = await generateApplicationBridge(options);
    process.stdout.write(`${result.changed ? "Generated" : "Current"}: ${result.irPath}\n`);
  } else if (command === "check") {
    const result = await checkApplicationBridge(options);
    process.stdout.write(`Current: ${result.irPath}\n`);
  } else if (command === "watch") {
    const watcher = await watchApplicationBridge(options, (result) => {
      if (result instanceof Error) process.stderr.write(`${result.message}\n`);
      else process.stdout.write(`Generated: ${result.irPath}\n`);
    });
    for (const signal of ["SIGINT", "SIGTERM"] as const) {
      process.once(signal, () => { watcher.close(); process.exitCode = 0; });
    }
    await new Promise<void>(() => undefined);
  } else if (command === "diff") {
    const paths = parsed.positionals;
    if (paths.length !== 2) usage();
    const baseline = validateApplicationBridgeIr(JSON.parse(await readFile(paths[0]!, "utf8"))) as BridgeIr;
    const candidate = validateApplicationBridgeIr(JSON.parse(await readFile(paths[1]!, "utf8"))) as BridgeIr;
    const result = compareApplicationBridgeIr(baseline, candidate);
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
    if (result.classification === "breaking") process.exitCode = 2;
  } else {
    usage();
  }
} catch (error) {
  const failure = error instanceof ApplicationBridgeCompilerError || error instanceof Error
    ? error.message
    : String(error);
  process.stderr.write(`${failure}\n`);
  process.exitCode = 1;
}

function parseArguments(values: readonly string[]): Readonly<{
  options: ApplicationBridgeCompilerOptions;
  positionals: readonly string[];
}> {
  const result: { root?: string; source?: string; ir?: string; facade?: string } = {};
  const positionals: string[] = [];
  for (let index = 0; index < values.length; index++) {
    const name = values[index];
    if (name !== "--root" && name !== "--source" && name !== "--ir" && name !== "--facade") {
      if (name?.startsWith("--")) usage();
      if (name !== undefined) positionals.push(name);
      continue;
    }
    const value = values[++index];
    if (value === undefined) usage();
    result[name.slice(2) as keyof typeof result] = value;
  }
  return { options: result, positionals };
}

function usage(): never {
  throw new ApplicationBridgeCompilerError(
    "RTKAB1008",
    "Usage: runic-bridge <generate|check|watch|diff> [--source PATH] [--ir PATH] [--facade PATH]",
  );
}
