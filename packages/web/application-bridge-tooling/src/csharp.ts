import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { dirname, resolve } from "node:path";
import type { ApplicationBridgeCompilation, ApplicationBridgeCompilerOptions } from "./compiler.js";
import { ApplicationBridgeCompilerError, validateApplicationBridgeIr } from "./compiler.js";
import type { BridgeIr, BridgeIrNode } from "./model.js";

const exec = promisify(execFile);

export async function compileCSharp(options: Extract<ApplicationBridgeCompilerOptions, { authority: "csharp" }>): Promise<ApplicationBridgeCompilation> {
  const root = resolve(options.root ?? process.cwd());
  const project = resolve(root, options.project);
  // The override is a path to the same internal tool command, useful for source checkouts.
  const tool = process.env["RUNIC_BRIDGE_INSPECTOR"];
  const args = tool === undefined ? ["runic", "__bridge-inspect", project] : [tool, "__bridge-inspect", project];
  let stdout: string;
  try {
    ({ stdout } = await exec(process.env["DOTNET_HOST_PATH"] ?? "dotnet", args, { cwd: dirname(project), maxBuffer: 32 * 1024 * 1024, env: process.env }));
  } catch (error) {
    const failure = error as { stderr?: string; message?: string };
    throw new ApplicationBridgeCompilerError("RTKAB2001", failure.stderr?.trim() || failure.message || "C# bridge inspection failed.", project);
  }
  const inspected = JSON.parse(stdout) as { ir: unknown; dependencies: string[] };
  const ir = validateApplicationBridgeIr(inspected.ir);
  return { ir, irPath: resolve(root, options.ir ?? "../Contract/bridge.ir.json"), facadePath: resolve(root, options.facade ?? "src/application.bridge.generated.ts"), irText: serialize(ir), facadeText: renderCSharpFacade(ir), dependencies: inspected.dependencies };
}

export function renderCSharpFacade(ir: BridgeIr): string {
  const entries = Object.entries(ir.wire.definitions).filter(([id]) => !id.startsWith("error:") || !builtInErrors.has(id.slice(6)));
  const counts = new Map<string, number>();
  for (const [id] of entries) counts.set(id.split(":")[1]!, (counts.get(id.split(":")[1]!) ?? 0) + 1);
  const names = new Map(entries.map(([id]) => [id, id.split(":")[1]! + (counts.get(id.split(":")[1]!)! > 1 ? id.split(":")[0]! : "")]));
  const name = (id: string): string => { const value = names.get(id); if (value === undefined) throw new Error(`Missing wire definition ${id}`); return value; };
  const type = (node: BridgeIrNode): string => {
    switch (node.kind) {
      case "string": return "string";
      case "number": case "integer": return "number";
      case "boolean": return "boolean";
      case "null": return "null";
      case "literal": return JSON.stringify(node.value);
      case "ref": return name(node.name);
      case "array": return `ReadonlyArray<${type(node.items)}>`;
      case "union": return node.members.map(type).join(" | ");
      case "object": return `{ ${Object.entries(node.properties).map(([key, p]) => `readonly ${JSON.stringify(key)}${p.optional ? "?" : ""}: ${type(p.type)};`).join(" ")} }`;
      default: throw new Error(`Unexpected C# wire node ${node.kind}`);
    }
  };
  const schema = (node: BridgeIrNode): string => {
    let value: string;
    switch (node.kind) {
      case "string": value = node.format === "uuid" ? `Schema.String.check(Schema.isPattern(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/))` : "Schema.String"; break;
      case "number": value = "Schema.Finite"; break;
      case "integer": value = "Schema.Int.check(Schema.isBetween({ minimum: -9007199254740991, maximum: 9007199254740991 }))"; break;
      case "boolean": value = "Schema.Boolean"; break;
      case "null": value = "Schema.Null"; break;
      case "literal": value = node.value === null ? "Schema.Null" : `Schema.Literal(${JSON.stringify(node.value)})`; break;
      case "ref": return `Schema.suspend(() => ${name(node.name)})`;
      case "array": value = `Schema.Array(${schema(node.items)})`; break;
      case "union": value = `Schema.Union([${node.members.map(schema).join(", ")}])`; break;
      case "object": value = `Schema.Struct({ ${Object.entries(node.properties).map(([key, p]) => `${JSON.stringify(key)}: ${p.optional ? `Schema.optionalKey(${schema(p.type)})` : schema(p.type)}`).join(", ")} })`; break;
      default: throw new Error(`Unexpected C# wire node ${node.kind}`);
    }
    if ("constraints" in node && node.constraints !== undefined) {
      const constraints = node.constraints;
      const filters: string[] = [];
      const functions = { minimum: "isGreaterThanOrEqualTo", maximum: "isLessThanOrEqualTo", exclusiveMinimum: "isGreaterThan", exclusiveMaximum: "isLessThan", multipleOf: "isMultipleOf", minLength: "isMinLength", maxLength: "isMaxLength", minItems: "isMinLength", maxItems: "isMaxLength" } as const;
      for (const [key, fn] of Object.entries(functions)) {
        const bound = constraints[key as keyof typeof functions];
        if (bound !== undefined) filters.push(`Schema.check(Schema.${fn}(${bound}))`);
      }
      if (constraints.pattern !== undefined) filters.push(`Schema.check(Schema.isPattern(new RegExp(${JSON.stringify(constraints.pattern)})))`);
      if (constraints.uniqueItems) filters.push("Schema.check(Schema.makeFilter(items => new Set(items.map(canonicalValue)).size === items.length))");
      if (filters.length) value += `.pipe(${filters.join(", ")})`;
    }
    return value;
  };
  const lines = ["// <auto-generated />", 'import { Schema } from "effect";', 'import { bridge, defineApplicationBridgeContract, materializeApplicationBridgeContract } from "@runic-artifex/application-bridge";', ""];
  if (JSON.stringify(ir.wire).includes('"uniqueItems":true')) lines.push('const canonicalValue = (value: unknown): string => JSON.stringify(value, (_key, item: unknown) => item !== null && typeof item === "object" && !Array.isArray(item) ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item);');
  for (const [id, node] of entries) {
    const exportName = name(id);
    lines.push(`export type ${exportName} = ${type(node)};`, `export const ${exportName}: Schema.Codec<${exportName}> = ${schema(node)}.annotate({ identifier: ${JSON.stringify(id.split(":")[1])} });`, `export type ${exportName}Encoded = Schema.Codec.Encoded<typeof ${exportName}>;`, "");
  }
  lines.push("const definition = defineApplicationBridgeContract({", `  protocol: ${JSON.stringify(ir.wire.protocol)},`, `  csharp: ${JSON.stringify(ir.csharp)},`, `  snapshot: ${name(ir.wire.snapshot)},`, "  commands: [");
  for (const command of ir.wire.commands) lines.push(`    bridge.command(${name(`command:${command.name}`)}, { receipt: ${name(`receipt:${command.receipt}`)}, startsOperation: ${command.startsOperation}, cancellable: ${command.cancellable}, advancesRevision: ${command.advancesRevision} }),`);
  lines.push("  ],", `  events: [${ir.wire.events.map(tag => name(`event:${tag}`)).join(", ")}],`, `  errors: [${ir.wire.errors.filter(tag => !builtInErrors.has(tag)).map(tag => name(`error:${tag}`)).join(", ")}],`, "});", `export const applicationBridge = materializeApplicationBridgeContract(definition, ${JSON.stringify(ir.fingerprint.value)});`, `export type ${ir.csharp.contractName}Command = Schema.Schema.Type<typeof applicationBridge.command>;`, `export type ${ir.csharp.contractName}Receipt = Schema.Schema.Type<typeof applicationBridge.receipt>;`, `export type ${ir.csharp.contractName}Event = Schema.Schema.Type<typeof applicationBridge.event>;`, "export default applicationBridge;", "");
  return lines.join("\n");
}
const builtInErrors = new Set(["CommandRejected", "OperationCancelled", "OperationFailed", "OperationTimedOut", "ProtocolDecodeError", "ProtocolVersionMismatch", "StaleRevision", "TransportClosed", "TransportUnavailable"]);
function serialize(value: unknown): string { return `${JSON.stringify(value, (_key, item: unknown) => item !== null && typeof item === "object" && !Array.isArray(item) ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item, 2)}\n`; }
