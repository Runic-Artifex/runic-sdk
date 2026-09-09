#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { Effect, Option } from "effect";
import { Argument, CliError, CliOutput, Command, Flag } from "effect/unstable/cli";
import { NodeServices } from "@effect/platform-node";
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

const version = (createRequire(import.meta.url)("@runic-artifex/application-bridge-tooling/package.json") as { version: string }).version;
const pathFlag = (name: string, description: string) => Flag.string(name).pipe(Flag.withDescription(description), Flag.withMetavar("PATH"), Flag.optional);
const inputs = {
  authority: Flag.choice("authority", ["csharp", "effect"]).pipe(Flag.withDescription("Source of truth for the bridge contract.")),
  project: pathFlag("project", "C# project to inspect (required with --authority csharp)."),
  source: pathFlag("source", "Effect contract source (required with --authority effect)."),
  root: pathFlag("root", "Root directory for generated artifacts."),
  ir: pathFlag("ir", "Destination of the intermediate representation."),
  facade: pathFlag("facade", "Destination of the generated facade."),
};

type Inputs = { authority: "csharp" | "effect"; project: Option.Option<string>; source: Option.Option<string>; root: Option.Option<string>; ir: Option.Option<string>; facade: Option.Option<string> };
function compilerOptions(input: Inputs): ApplicationBridgeCompilerOptions {
  const project = Option.getOrUndefined(input.project);
  const source = Option.getOrUndefined(input.source);
  if (input.authority === "csharp" ? project === undefined || source !== undefined : source === undefined || project !== undefined) {
    throw new ApplicationBridgeCompilerError("RTKAB1008", input.authority === "csharp"
      ? "--authority csharp requires --project PATH and does not accept --source."
      : "--authority effect requires --source PATH and does not accept --project.");
  }
  return Object.fromEntries(Object.entries({ authority: input.authority, project, source,
    root: Option.getOrUndefined(input.root), ir: Option.getOrUndefined(input.ir), facade: Option.getOrUndefined(input.facade),
  }).filter(([, value]) => value !== undefined)) as unknown as ApplicationBridgeCompilerOptions;
}

const operation = (name: "generate" | "check" | "watch", description: string) => Command.make(name, inputs,
  (input) => Effect.tryPromise(async () => {
    const options = compilerOptions(input);
    if (name === "generate") {
      const result = await generateApplicationBridge(options);
      process.stdout.write(`${result.changed ? "Generated" : "Current"}: ${result.irPath}\n`);
    } else if (name === "check") {
      const result = await checkApplicationBridge(options);
      process.stdout.write(`Current: ${result.irPath}\n`);
    } else {
      const watcher = await watchApplicationBridge(options, (result) => {
        if (result instanceof Error) process.stderr.write(`${result.message}\n`);
        else process.stdout.write(`Generated: ${result.irPath}\n`);
      });
      process.stderr.write("Watching bridge inputs. Press Ctrl+C to stop.\n");
      await new Promise<void>((resolve) => {
        const stop = () => { watcher.close(); process.removeListener("SIGINT", stop); process.removeListener("SIGTERM", stop); resolve(); };
        process.once("SIGINT", stop);
        process.once("SIGTERM", stop);
      });
    }
  }).pipe(Effect.mapError((error) => new CliError.UserError({ cause: error.cause }))),
).pipe(Command.withDescription(description), Command.withExamples([
  { command: `runic-bridge ${name} --authority effect --source ./contract.ts`, description: "Use an Effect contract." },
  { command: `runic-bridge ${name} --authority csharp --project ./MyApp.csproj`, description: "Use a C# contract." },
]));

const diff = Command.make("diff", {
  baseline: Argument.string("baseline").pipe(Argument.withDescription("Baseline IR JSON file.")),
  candidate: Argument.string("candidate").pipe(Argument.withDescription("Candidate IR JSON file.")),
}, ({ baseline, candidate }) => Effect.tryPromise(async () => {
  const before = validateApplicationBridgeIr(JSON.parse(await readFile(baseline, "utf8"))) as BridgeIr;
  const after = validateApplicationBridgeIr(JSON.parse(await readFile(candidate, "utf8"))) as BridgeIr;
  const result = compareApplicationBridgeIr(before, after);
  // This raw JSON is an established machine contract, independent of human help.
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  if (result.classification === "breaking") process.exitCode = 2;
}).pipe(Effect.mapError((error) => new CliError.UserError({ cause: error.cause })))).pipe(
  Command.withDescription("Compare two IR files. Writes JSON; exits 2 for breaking changes."),
  Command.withExamples([{ command: "runic-bridge diff baseline.json candidate.json", description: "Check compatibility." }]),
);

const app = Command.make("runic-bridge").pipe(
  Command.withDescription("Generate, validate and compare Runic Application Bridge contracts."),
  Command.withSubcommands([operation("generate", "Generate bridge artifacts."), operation("check", "Verify generated artifacts are current."), operation("watch", "Regenerate artifacts when source files change."), diff]),
);
const args = process.argv.slice(2);
// Support the same discoverable `help <command>` spelling as the .NET CLIs.
const invocation = args[0] === "help" ? [...args.slice(1), "--help"] : args.length === 0 ? ["--help"] : args;
await Effect.runPromise(Command.runWith(app, { version })(invocation).pipe(
  Effect.catch((error) => Effect.sync(() => {
    if (error._tag === "ShowHelp" && error.errors.length === 0) return;
    process.exitCode = 1;
  })),
  Effect.provide(CliOutput.layer(CliOutput.defaultFormatter({ colors: !!process.stdout.isTTY && process.env.NO_COLOR === undefined && process.env.TERM !== "dumb" }))),
  Effect.provide(NodeServices.layer),
));
