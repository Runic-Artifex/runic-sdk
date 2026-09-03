import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { watch, type FSWatcher } from "node:fs";
import { dirname, extname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { Option, Schema, SchemaAST } from "effect";
import type { BridgeIr, BridgeIrConstraints, BridgeIrNode } from "./model.js";

const defaultLimits = Object.freeze({
  maxFrameBytes: 262_144,
  maxDepth: 32,
  maxStringBytes: 65_536,
  maxCollectionItems: 4_096,
  maxPendingCommands: 64,
});

const builtInErrorNames = [
  "CommandRejected",
  "OperationCancelled",
  "OperationFailed",
  "OperationTimedOut",
  "ProtocolDecodeError",
  "ProtocolVersionMismatch",
  "StaleRevision",
  "TransportClosed",
  "TransportUnavailable",
] as const;

// These standard Effect transformations are total over their encoded schema:
// they normalize values but do not reject any additional wire input.
const totalStandardTransformations = new Set([
  "Capitalize",
  "Lowercase",
  "Trim",
  "Uncapitalize",
  "Uppercase",
]);

export interface ApplicationBridgeCompilerOptions {
  readonly root?: string;
  readonly source?: string;
  readonly ir?: string;
  readonly facade?: string;
}

export interface ApplicationBridgeCompilation {
  readonly ir: BridgeIr;
  readonly irPath: string;
  readonly facadePath: string;
  readonly irText: string;
  readonly facadeText: string;
  readonly dependencies: readonly string[];
}

export class ApplicationBridgeCompilerError extends Error {
  readonly code: string;
  readonly schemaPath?: string;

  constructor(code: string, message: string, schemaPath?: string) {
    super(`${code}: ${message}${schemaPath === undefined ? "" : ` at ${schemaPath}`}`);
    this.name = "ApplicationBridgeCompilerError";
    this.code = code;
    if (schemaPath !== undefined) this.schemaPath = schemaPath;
  }
}

export async function compileApplicationBridge(
  options: ApplicationBridgeCompilerOptions = {},
): Promise<ApplicationBridgeCompilation> {
  const paths = resolvePaths(options);
  const loaded = await loadDefinition(paths.source);
  const definition = validateDefinition(loaded.value);
  const definitions: Record<string, BridgeIrNode> = {};
  const documentation: Record<string, string> = {};
  const roots = new Map<object, string>();

  const addDefinition = (name: string, schema: WireSchema, path: string): void => {
    const existingName = roots.get(schema);
    if (existingName !== undefined && existingName !== name) {
      throw new ApplicationBridgeCompilerError("RTKAB1005", `Schema is declared as both '${existingName}' and '${name}'.`, path);
    }
    roots.set(schema, name);
    assertPortableSource(schema.ast, path, new Set());
    const existing = definitions[name];
    definitions[name] = { kind: "null" };
    const node = lowerWithoutName(schema.ast, path, definitions, new Set());
    if (existing !== undefined && serialize(existing) !== serialize(node)) {
      throw new ApplicationBridgeCompilerError("RTKAB1005", `Two different schemas declare '${name}'.`, path);
    }
    definitions[name] = node;
    collectDocumentation(schema.ast, name, documentation, new Set());
  };

  const snapshotName = identifier(definition.snapshot.ast) ?? `${definition.csharp.contractName}Snapshot`;
  const snapshotId = `type:${snapshotName}`;
  addDefinition(snapshotId, definition.snapshot, "$.snapshot");
  const commands: Array<{
    name: string;
    receipt: string;
    startsOperation: boolean;
    cancellable: boolean;
    advancesRevision: boolean;
  }> = [];
  const receiptNames = new Set<string>();
  for (let index = 0; index < definition.commands.length; index++) {
    const command = definition.commands[index]!;
    const commandPath = `$.commands[${index}]`;
    const name = taggedName(command.schema, `${commandPath}.schema`);
    const receipt = taggedName(command.receipt, `${commandPath}.receipt`);
    addDefinition(`command:${name}`, command.schema, `${commandPath}.schema`);
    addDefinition(`receipt:${receipt}`, command.receipt, `${commandPath}.receipt`);
    if (command.cancellable && !command.startsOperation) {
      throw new ApplicationBridgeCompilerError("RTKAB1006", "Only an operation-starting command can be cancellable.", commandPath);
    }
    if (command.startsOperation && !hasRequiredStringProperty(definitions[`receipt:${receipt}`]!, "operationId", definitions)) {
      throw new ApplicationBridgeCompilerError(
        "RTKAB1006",
        "An operation-starting command receipt must contain a required string 'operationId'.",
        `${commandPath}.receipt`,
      );
    }
    receiptNames.add(receipt);
    commands.push({
      name,
      receipt,
      startsOperation: command.startsOperation,
      cancellable: command.cancellable,
      advancesRevision: command.advancesRevision,
    });
  }
  const events = definition.events.map((schema, index) => {
    const name = taggedName(schema, `$.events[${index}]`);
    addDefinition(`event:${name}`, schema, `$.events[${index}]`);
    return name;
  });
  const domainErrors = (definition.errors ?? []).map((schema, index) => {
    const name = taggedName(schema, `$.errors[${index}]`);
    if ((builtInErrorNames as readonly string[]).includes(name)) {
      throw new ApplicationBridgeCompilerError("RTKAB1005", `'${name}' is a built-in bridge error.`, `$.errors[${index}]`);
    }
    addDefinition(`error:${name}`, schema, `$.errors[${index}]`);
    return name;
  });
  for (const name of builtInErrorNames) definitions[`error:${name}`] = builtInError(name);

  const commandNames = new Set(commands.map((command) => command.name));
  const initializeTag = valueTag(definition.initialize);
  if (commands.length > 0 && (initializeTag === undefined || !commandNames.has(initializeTag))) {
    throw new ApplicationBridgeCompilerError("RTKAB1006", "The initialize value must be one of the declared commands.", "$.initialize");
  }
  if (commandNames.size !== commands.length || events.length !== new Set(events).size ||
      domainErrors.length !== new Set(domainErrors).size) {
    throw new ApplicationBridgeCompilerError("RTKAB1005", "Command, event, and error tags must be unique.");
  }

  const wire = {
    protocol: definition.protocol,
    envelopeVersion: 1 as const,
    limits: { ...defaultLimits, ...definition.limits },
    ...(initializeTag === undefined ? {} : { initialize: initializeTag }),
    snapshot: snapshotId,
    definitions: Object.fromEntries(Object.entries(definitions).sort(([left], [right]) => compare(left, right))),
    commands: commands.sort((left, right) => compare(left.name, right.name)),
    events: [...events].sort(compare),
    errors: [...builtInErrorNames, ...domainErrors].sort(compare),
  };
  const contractFingerprint = sha256(serialize(wire));
  const ir: BridgeIr = {
    format: "runic.application-bridge-ir",
    formatVersion: 1,
    fingerprint: { algorithm: "sha256", scope: "wire", value: contractFingerprint },
    wire,
    csharp: definition.csharp,
    documentation: Object.fromEntries(Object.entries(documentation).sort(([left], [right]) => compare(left, right))),
  };
  const sourceImport = relative(dirname(paths.facade), paths.source)
    .split(sep).join("/")
    .replace(new RegExp(`${escapeRegExp(extname(paths.source))}$`), ".js");
  const importPath = sourceImport.startsWith(".") ? sourceImport : `./${sourceImport}`;
  const facadeText = [
    "// <auto-generated />",
    `import definition from ${JSON.stringify(importPath)};`,
    "import { materializeApplicationBridgeContract } from \"@runic-artifex/application-bridge\";",
    "",
    `export const applicationBridge = materializeApplicationBridgeContract(definition, ${JSON.stringify(contractFingerprint)});`,
    "export default applicationBridge;",
    "",
  ].join("\n");
  return {
    ir,
    irPath: paths.ir,
    facadePath: paths.facade,
    irText: serialize(ir),
    facadeText,
    dependencies: loaded.dependencies,
  };
}

export async function generateApplicationBridge(
  options: ApplicationBridgeCompilerOptions = {},
): Promise<ApplicationBridgeCompilation & { readonly changed: boolean }> {
  const compilation = await compileApplicationBridge(options);
  const [irChanged, facadeChanged] = await Promise.all([
    differs(compilation.irPath, compilation.irText),
    differs(compilation.facadePath, compilation.facadeText),
  ]);
  await Promise.all([
    ...(irChanged ? [replaceFile(compilation.irPath, compilation.irText)] : []),
    ...(facadeChanged ? [replaceFile(compilation.facadePath, compilation.facadeText)] : []),
  ]);
  const changed = irChanged || facadeChanged;
  return { ...compilation, changed };
}

export async function checkApplicationBridge(
  options: ApplicationBridgeCompilerOptions = {},
): Promise<ApplicationBridgeCompilation> {
  const compilation = await compileApplicationBridge(options);
  const stale = [];
  if (await differs(compilation.irPath, compilation.irText)) stale.push(compilation.irPath);
  if (await differs(compilation.facadePath, compilation.facadeText)) stale.push(compilation.facadePath);
  if (stale.length > 0) {
    throw new ApplicationBridgeCompilerError("RTKAB1007", `Generated artifacts are stale: ${stale.join(", ")}`);
  }
  return compilation;
}

export async function watchApplicationBridge(
  options: ApplicationBridgeCompilerOptions = {},
  onResult?: (result: ApplicationBridgeCompilation | ApplicationBridgeCompilerError) => void,
): Promise<{ close(): void }> {
  let watchers: FSWatcher[] = [];
  let timer: NodeJS.Timeout | undefined;
  let closed = false;
  const refresh = async (): Promise<void> => {
    try {
      const result = await generateApplicationBridge(options);
      if (closed) return;
      for (const watcher of watchers) watcher.close();
      watchers = result.dependencies.map((dependency) => watch(dependency, schedule));
      onResult?.(result);
    } catch (error) {
      const failure = error instanceof ApplicationBridgeCompilerError
        ? error
        : new ApplicationBridgeCompilerError("RTKAB1000", error instanceof Error ? error.message : String(error));
      if (watchers.length === 0) {
        const source = resolvePaths(options).source;
        watchers = [watch(source, schedule)];
      }
      onResult?.(failure);
    }
  };
  const schedule = (): void => {
    if (timer !== undefined) clearTimeout(timer);
    timer = setTimeout(() => void refresh(), 50);
  };
  await refresh();
  return {
    close() {
      closed = true;
      if (timer !== undefined) clearTimeout(timer);
      for (const watcher of watchers) watcher.close();
    },
  };
}

export function compareApplicationBridgeIr(baseline: BridgeIr, candidate: BridgeIr): Readonly<{
  classification: "compatible" | "additive" | "breaking";
  diagnostics: readonly string[];
}> {
  if (baseline.fingerprint.value === candidate.fingerprint.value) {
    return { classification: "compatible", diagnostics: [] };
  }
  const diagnostics: string[] = [];
  let breaking = false;
  let additive = false;
  if (baseline.wire.protocol.identity !== candidate.wire.protocol.identity ||
      baseline.wire.protocol.version !== candidate.wire.protocol.version) {
    diagnostics.push("Protocol identity or version changed.");
    breaking = true;
  }

  if (baseline.wire.envelopeVersion !== candidate.wire.envelopeVersion) {
    diagnostics.push("Envelope version changed.");
    breaking = true;
  }
  if (serialize(baseline.wire.limits) !== serialize(candidate.wire.limits)) {
    diagnostics.push("Protocol limits changed.");
    breaking = true;
  }
  if (baseline.wire.snapshot !== candidate.wire.snapshot) {
    diagnostics.push("Snapshot declaration changed.");
    breaking = true;
  }
  if (baseline.wire.initialize !== candidate.wire.initialize) {
    diagnostics.push("Initialization command changed.");
    breaking = true;
  }

  const beforeCommands = new Map(baseline.wire.commands.map((item) => [item.name, item]));
  const afterCommands = new Map(candidate.wire.commands.map((item) => [item.name, item]));
  const removedCommands = [...beforeCommands.keys()].filter((name) => !afterCommands.has(name)).sort(compare);
  const addedCommands = [...afterCommands.keys()].filter((name) => !beforeCommands.has(name)).sort(compare);
  const changedCommands = [...beforeCommands].filter(([name, item]) => {
    const after = afterCommands.get(name);
    return after !== undefined && serialize(item) !== serialize(after);
  }).map(([name]) => name).sort(compare);
  if (removedCommands.length > 0) {
    diagnostics.push(`Commands removed: ${removedCommands.join(", ")}.`);
    breaking = true;
  }
  if (changedCommands.length > 0) {
    diagnostics.push(`Commands changed: ${changedCommands.join(", ")}.`);
    breaking = true;
  }
  if (addedCommands.length > 0) {
    diagnostics.push(`Commands added: ${addedCommands.join(", ")}.`);
    additive = true;
  }

  for (const [label, before, after] of [
    ["Events", baseline.wire.events, candidate.wire.events],
    ["Errors", baseline.wire.errors, candidate.wire.errors],
  ] as const) {
    const beforeSet = new Set(before);
    const afterSet = new Set(after);
    const removed = [...beforeSet].filter((name) => !afterSet.has(name)).sort(compare);
    const added = [...afterSet].filter((name) => !beforeSet.has(name)).sort(compare);
    if (removed.length > 0) {
      diagnostics.push(`${label} removed: ${removed.join(", ")}.`);
      breaking = true;
    }
    if (added.length > 0) {
      diagnostics.push(`${label} added: ${added.join(", ")}.`);
      additive = true;
    }
  }

  const beforeDefinitions = new Set(Object.keys(baseline.wire.definitions));
  const afterDefinitions = new Set(Object.keys(candidate.wire.definitions));
  const removedDefinitions = [...beforeDefinitions].filter((name) => !afterDefinitions.has(name)).sort(compare);
  const addedDefinitions = [...afterDefinitions].filter((name) => !beforeDefinitions.has(name)).sort(compare);
  const changedDefinitions = [...beforeDefinitions].filter((name) =>
    afterDefinitions.has(name) &&
    serialize(baseline.wire.definitions[name]) !== serialize(candidate.wire.definitions[name])).sort(compare);
  if (removedDefinitions.length > 0) {
    diagnostics.push(`Wire declarations removed: ${removedDefinitions.join(", ")}.`);
    breaking = true;
  }
  if (changedDefinitions.length > 0) {
    diagnostics.push(`Wire declarations changed: ${changedDefinitions.join(", ")}.`);
    breaking = true;
  }
  if (addedDefinitions.length > 0) additive = true;

  return {
    classification: breaking ? "breaking" : additive ? "additive" : "compatible",
    diagnostics,
  };
}

export function validateApplicationBridgeIr(value: unknown): BridgeIr {
  const candidate = value as Partial<BridgeIr> | null;
  if (candidate === null || typeof candidate !== "object" ||
      candidate.format !== "runic.application-bridge-ir" || candidate.formatVersion !== 1 ||
      candidate.fingerprint?.algorithm !== "sha256" || candidate.fingerprint.scope !== "wire" ||
      !/^[a-f0-9]{64}$/.test(candidate.fingerprint.value) ||
      candidate.wire === undefined || candidate.csharp === undefined || candidate.documentation === undefined) {
    throw new ApplicationBridgeCompilerError("RTKAB1003", "The input is not Runic Application Bridge IR version 1.");
  }
  const actual = sha256(serialize(candidate.wire));
  if (actual !== candidate.fingerprint.value) {
    throw new ApplicationBridgeCompilerError("RTKAB1003", "The IR fingerprint does not match its canonical wire semantics.");
  }
  return candidate as BridgeIr;
}

interface WireSchema { readonly ast: SchemaAST.AST }
interface Definition {
  readonly protocol: { readonly identity: string; readonly version: number };
  readonly csharp: { readonly namespace: string; readonly contractName: string };
  readonly limits?: Partial<typeof defaultLimits>;
  readonly snapshot: WireSchema;
  readonly commands: readonly {
    readonly schema: WireSchema;
    readonly receipt: WireSchema;
    readonly startsOperation: boolean;
    readonly cancellable: boolean;
    readonly advancesRevision: boolean;
  }[];
  readonly events: readonly WireSchema[];
  readonly errors?: readonly WireSchema[];
  readonly initialize: unknown;
}

function validateDefinition(value: unknown): Definition {
  const candidate = value as Partial<Definition> | null;
  if (candidate === null || typeof candidate !== "object" ||
      typeof candidate.protocol?.identity !== "string" || candidate.protocol.identity.length === 0 ||
      !Number.isSafeInteger(candidate.protocol.version) || Number(candidate.protocol.version) < 1 ||
      typeof candidate.csharp?.namespace !== "string" || typeof candidate.csharp.contractName !== "string" ||
      !isSchema(candidate.snapshot) || !Array.isArray(candidate.commands) ||
      !Array.isArray(candidate.events)) {
    throw new ApplicationBridgeCompilerError("RTKAB1001", "The default export is not a valid Application Bridge definition.");
  }
  for (const command of candidate.commands) {
    if (!isSchema(command?.schema) || !isSchema(command?.receipt) ||
        typeof command.startsOperation !== "boolean" || typeof command.cancellable !== "boolean" ||
        typeof command.advancesRevision !== "boolean") {
      throw new ApplicationBridgeCompilerError("RTKAB1001", "A command declaration is invalid.");
    }
  }
  if (!candidate.events.every(isSchema) || !(candidate.errors ?? []).every(isSchema)) {
    throw new ApplicationBridgeCompilerError("RTKAB1001", "An event or error declaration is invalid.");
  }
  return candidate as Definition;
}

function isSchema(value: unknown): value is WireSchema {
  return value !== null && (typeof value === "object" || typeof value === "function") && "ast" in value;
}

async function loadDefinition(source: string): Promise<{ value: unknown; dependencies: readonly string[] }> {
  const bridgeRuntime = fileURLToPath(import.meta.resolve("@runic-artifex/application-bridge"));
  const result = await build({
    absWorkingDir: dirname(source),
    entryPoints: [source],
    bundle: true,
    alias: { "@runic-artifex/application-bridge": bridgeRuntime },
    format: "esm",
    platform: "node",
    target: "node24",
    write: false,
    metafile: true,
    sourcemap: "inline",
    logLevel: "silent",
  }).catch((error: unknown) => {
    throw new ApplicationBridgeCompilerError("RTKAB1002", error instanceof Error ? error.message : String(error), source);
  });
  const output = result.outputFiles[0];
  if (output === undefined) throw new ApplicationBridgeCompilerError("RTKAB1002", "The contract module produced no output.", source);
  // The bundled bytes already make the module URL content-addressed. Appending a
  // fragment is unnecessary and causes Bun to treat it as part of the base64 payload.
  const module = await import(`data:text/javascript;base64,${Buffer.from(output.contents).toString("base64")}`)
    .catch((error: unknown) => {
      throw new ApplicationBridgeCompilerError("RTKAB1002", error instanceof Error ? error.message : String(error), source);
    });
  const dependencies = Object.keys(result.metafile.inputs)
    .map((input) => resolve(dirname(source), input))
    .filter((input) => !input.includes(`${sep}node_modules${sep}`));
  return { value: module.default, dependencies: [...new Set([source, ...dependencies])].sort(compare) };
}

function lower(
  ast: SchemaAST.AST,
  path: string,
  definitions: Record<string, BridgeIrNode>,
  active: Set<SchemaAST.AST>,
): BridgeIrNode {
  if (active.has(ast)) {
    const name = identifier(ast);
    if (name === undefined) throw unsupported("Anonymous recursive schemas are not portable.", path);
    return { kind: "ref", name: `type:${name}` };
  }
  if (ast._tag === "Suspend") return lowerWithoutName(ast, path, definitions, active);
  const named = identifier(ast);
  const namedId = named === undefined ? undefined : `type:${named}`;
  if (namedId !== undefined && definitions[namedId] === undefined) {
    active.add(ast);
    definitions[namedId] = { kind: "null" };
    const value = lowerWithoutName(ast, path, definitions, active);
    definitions[namedId] = value;
    active.delete(ast);
    return { kind: "ref", name: namedId };
  }
  return lowerWithoutName(ast, path, definitions, active);
}

function lowerWithoutName(
  ast: SchemaAST.AST,
  path: string,
  definitions: Record<string, BridgeIrNode>,
  active: Set<SchemaAST.AST>,
): BridgeIrNode {
  switch (ast._tag) {
    case "StringKeyword": return { kind: "string" };
    case "NumberKeyword": return { kind: "number" };
    case "BooleanKeyword": return { kind: "boolean" };
    case "Literal": {
      if (typeof ast.literal === "bigint") throw unsupported("BigInt literals are not JSON values.", path);
      return ast.literal === null ? { kind: "null" } : { kind: "literal", value: ast.literal };
    }
    case "Enums":
      return { kind: "union", members: ast.enums.map(([, value]) => {
        if (typeof value === "bigint") throw unsupported("BigInt enum values are not JSON values.", path);
        return { kind: "literal", value };
      }) };
    case "Refinement": {
      const node = lowerWithoutName(ast.from, path, definitions, active);
      const annotation = option(SchemaAST.getJSONSchemaAnnotation(ast));
      if (annotation === undefined) throw unsupported("Executable refinements require portable constraint metadata.", path);
      if (option(SchemaAST.getSchemaIdAnnotation(ast)) === Schema.PatternSchemaId) {
        const pattern = ast.annotations[Schema.PatternSchemaId] as { readonly regex?: RegExp } | undefined;
        if (pattern?.regex?.flags !== "") {
          throw unsupported("Regular-expression flags are not portable; express the intended character classes explicitly.", path);
        }
      }
      return constrain(node, annotation, path);
    }
    case "TupleType": {
      const constraints = undefined;
      if (ast.elements.length === 0 && ast.rest.length === 1) {
        return { kind: "array", items: lower(ast.rest[0]!.type, `${path}[]`, definitions, active) };
      }
      if (ast.rest.length > 1) throw unsupported("Tuple post-rest elements are not supported in V1.", path);
      const firstOptional = ast.elements.findIndex((element) => element.isOptional);
      if (firstOptional >= 0 && ast.elements.slice(firstOptional).some((element) => !element.isOptional)) {
        throw unsupported("Optional tuple elements must be trailing.", path);
      }
      if (firstOptional >= 0 && ast.rest.length > 0) {
        throw unsupported("A tuple cannot combine optional elements with a rest element in V1.", path);
      }
      return {
        kind: "tuple",
        elements: ast.elements.map((element, index) => ({
          type: lower(element.type, `${path}[${index}]`, definitions, active),
          optional: element.isOptional,
        })),
        ...(ast.rest.length === 0 ? {} : { rest: lower(ast.rest[0]!.type, `${path}[]`, definitions, active) }),
        ...(constraints === undefined ? {} : { constraints }),
      };
    }
    case "TypeLiteral": {
      const propertyEntries: Array<[string, { type: BridgeIrNode; optional: boolean }]> = ast.propertySignatures.map((property) => {
        if (typeof property.name !== "string") throw unsupported("Symbol property names are not JSON object keys.", path);
        let propertyAst = property.type;
        if (property.isOptional && propertyAst._tag === "Union") {
          const present = propertyAst.types.filter((member) => member._tag !== "UndefinedKeyword");
          if (present.length === 1) propertyAst = present[0]!;
        }
        return [property.name, {
          type: lower(propertyAst, `${path}.${property.name}`, definitions, active),
          optional: property.isOptional,
        }];
      });
      propertyEntries.sort(([left], [right]) => compare(left, right));
      const properties = Object.fromEntries(propertyEntries);
      if (ast.indexSignatures.length === 0) return { kind: "object", properties };
      if (ast.indexSignatures.length !== 1) throw unsupported("Only one string record index is supported.", path);
      const index = ast.indexSignatures[0]!;
      const keyPattern = recordPattern(index.parameter, `${path}.*`);
      const values = lower(index.type, `${path}.*`, definitions, active);
      if (Object.keys(properties).length === 0) return { kind: "record", ...(keyPattern === undefined ? {} : { keyPattern }), values };
      throw unsupported("Objects cannot mix declared properties with a record index in V1.", path);
    }
    case "Union": {
      const members = ast.types.filter((member) => member._tag !== "UndefinedKeyword")
        .map((member, index) => lower(member, `${path}|${index}`, definitions, active));
      if (members.length === 0) throw unsupported("Undefined is not a JSON wire value.", path);
      return members.length === 1 ? members[0]! : { kind: "union", members };
    }
    case "Suspend": {
      const name = identifier(ast);
      if (name === undefined) throw unsupported("Recursive schemas require an identifier annotation.", path);
      const nameId = `type:${name}`;
      if (definitions[nameId] !== undefined) return { kind: "ref", name: nameId };
      definitions[nameId] = { kind: "null" };
      definitions[nameId] = lower(ast.f(), path, definitions, active);
      return { kind: "ref", name: nameId };
    }
    case "NeverKeyword": throw unsupported("Never cannot be transported as JSON.", path);
    case "UndefinedKeyword": throw unsupported("Undefined is not a JSON wire value.", path);
    case "Declaration": throw unsupported("Effect declarations require an explicit Runic wire adapter.", path);
    case "Transformation": return lower(ast.from, path, definitions, active);
    case "TemplateLiteral": throw unsupported("Template literal schemas are outside the V1 portable core.", path);
    case "AnyKeyword":
    case "UnknownKeyword":
    case "ObjectKeyword": throw unsupported("Unbounded values are not guaranteed to be JSON-safe.", path);
    case "BigIntKeyword": throw unsupported("BigInt is not a JSON wire value; transform it to a constrained string.", path);
    case "SymbolKeyword":
    case "UniqueSymbol": throw unsupported("Symbols are not JSON wire values.", path);
    case "VoidKeyword": throw unsupported("Void is not a JSON wire value.", path);
  }
}

function constrain(node: BridgeIrNode, raw: object, path: string): BridgeIrNode {
  const value = raw as Record<string, unknown>;
  const supported = new Set([
    "type", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
    "minLength", "maxLength", "pattern", "minItems", "maxItems", "uniqueItems",
  ]);
  const unsupportedKeys = Object.keys(value).filter((key) => !supported.has(key));
  if (unsupportedKeys.length > 0) throw unsupported(`Unsupported constraint '${unsupportedKeys[0]}'.`, path);
  let constrained = node;
  if (value.type === "integer") {
    if (node.kind !== "number") throw unsupported("The integer refinement must refine a number.", path);
    constrained = { kind: "integer" };
  } else if (value.type !== undefined) {
    throw unsupported(`Unsupported refinement type '${String(value.type)}'.`, path);
  }
  if (typeof value.pattern === "string") validatePattern(value.pattern, path);
  const constraints = Object.fromEntries(Object.entries(value).filter(([key]) => key !== "type")) as BridgeIrConstraints;
  if (Object.keys(constraints).length === 0) return constrained;
  if (constrained.kind === "string" || constrained.kind === "number" || constrained.kind === "integer" ||
      constrained.kind === "array" || constrained.kind === "tuple") {
    return { ...constrained, constraints: { ...constrained.constraints, ...constraints } };
  }
  throw unsupported("This constraint cannot be applied to the encoded wire node.", path);
}

function assertPortableSource(ast: SchemaAST.AST, path: string, seen: Set<SchemaAST.AST>): void {
  if (seen.has(ast)) return;
  seen.add(ast);
  switch (ast._tag) {
    case "Transformation": {
      const transformationName = identifier(ast);
      if (ast.transformation._tag !== "TypeLiteralTransformation" &&
          (transformationName === undefined || !totalStandardTransformations.has(transformationName))) {
        throw unsupported("Custom and effectful transformations require a Runic wire adapter.", path);
      }
      assertPortableSource(ast.from, path, seen);
      return;
    }
    case "Refinement": {
      if (ast.from._tag === "Transformation") {
        throw unsupported("A post-transformation refinement is not represented by the encoded wire schema.", path);
      }
      assertPortableSource(ast.from, path, seen);
      return;
    }
    case "TupleType":
      for (const element of ast.elements) assertPortableSource(element.type, path, seen);
      for (const rest of ast.rest) assertPortableSource(rest.type, path, seen);
      return;
    case "TypeLiteral":
      for (const property of ast.propertySignatures) assertPortableSource(property.type, `${path}.${String(property.name)}`, seen);
      for (const index of ast.indexSignatures) assertPortableSource(index.type, `${path}.*`, seen);
      return;
    case "Union":
      for (const member of ast.types) assertPortableSource(member, path, seen);
      return;
    case "Suspend": assertPortableSource(ast.f(), path, seen); return;
    default: return;
  }
}

function taggedName(schema: WireSchema, path: string): string {
  let ast = SchemaAST.encodedBoundAST(schema.ast);
  while (ast._tag === "Refinement") ast = ast.from;
  if (ast._tag !== "TypeLiteral") throw unsupported("Commands, receipts, events, and errors must be tagged structs.", path);
  const tag = ast.propertySignatures.find((property) => property.name === "_tag")?.type;
  if (tag?._tag !== "Literal" || typeof tag.literal !== "string" || tag.literal.length === 0) {
    throw unsupported("The schema must have one literal string '_tag' property.", path);
  }
  return tag.literal;
}

function recordPattern(ast: SchemaAST.Parameter, path: string): string | undefined {
  if (ast._tag === "StringKeyword") return undefined;
  if (ast._tag === "Refinement") {
    const annotation = option(SchemaAST.getJSONSchemaAnnotation(ast));
    const pattern = annotation === undefined ? undefined : (annotation as Record<string, unknown>).pattern;
    if (typeof pattern === "string") {
      validatePattern(pattern, path);
      return pattern;
    }
  }
  throw unsupported("Record keys must be strings with an optional portable pattern.", path);
}

function validatePattern(pattern: string, path: string): void {
  if (/\(\?|\\[1-9]|\\[pP]\{/.test(pattern)) {
    throw unsupported("Regular expressions may not use lookarounds, named groups, backreferences, or Unicode properties.", path);
  }
  try { new RegExp(pattern); }
  catch { throw unsupported("The regular expression is invalid.", path); }
}

function builtInError(name: string): BridgeIrNode {
  return {
    kind: "object",
    properties: {
      _tag: { type: { kind: "literal", value: name }, optional: false },
      message: { type: { kind: "string" }, optional: false },
      retryable: { type: { kind: "boolean" }, optional: false },
    },
  };
}

function hasRequiredStringProperty(
  node: BridgeIrNode,
  name: string,
  definitions: Readonly<Record<string, BridgeIrNode>>,
): boolean {
  const resolved = node.kind === "ref" ? definitions[node.name] : node;
  if (resolved?.kind !== "object") return false;
  const property = resolved.properties[name];
  if (property === undefined || property.optional) return false;
  const propertyType = property.type.kind === "ref" ? definitions[property.type.name] : property.type;
  return propertyType?.kind === "string";
}

function identifier(ast: SchemaAST.AST): string | undefined {
  return option(SchemaAST.getIdentifierAnnotation(ast)) ?? option(SchemaAST.getJSONIdentifier(ast));
}

function collectDocumentation(
  ast: SchemaAST.AST,
  path: string,
  documentation: Record<string, string>,
  seen: Set<SchemaAST.AST>,
): void {
  if (seen.has(ast)) return;
  seen.add(ast);
  const description = option(SchemaAST.getDescriptionAnnotation(ast));
  if (description !== undefined) documentation[path] = description;
  switch (ast._tag) {
    case "Transformation":
      collectDocumentation(ast.from, path, documentation, seen);
      return;
    case "Refinement":
      collectDocumentation(ast.from, path, documentation, seen);
      return;
    case "TypeLiteral":
      for (const property of ast.propertySignatures) {
        if (typeof property.name !== "string") continue;
        const propertyPath = `${path}.${property.name}`;
        const propertyDescription = option(SchemaAST.getDescriptionAnnotation(property)) ??
          option(SchemaAST.getDescriptionAnnotation(property.type));
        if (propertyDescription !== undefined) documentation[propertyPath] = propertyDescription;
        collectDocumentation(property.type, propertyPath, documentation, seen);
      }
      return;
    case "TupleType":
      for (let index = 0; index < ast.elements.length; index++) {
        collectDocumentation(ast.elements[index]!.type, `${path}[${index}]`, documentation, seen);
      }
      for (const rest of ast.rest) collectDocumentation(rest.type, `${path}[]`, documentation, seen);
      return;
    case "Union":
      for (let index = 0; index < ast.types.length; index++) {
        collectDocumentation(ast.types[index]!, `${path}|${index}`, documentation, seen);
      }
      return;
    case "Suspend":
      collectDocumentation(ast.f(), path, documentation, seen);
      return;
    default:
      return;
  }
}

function valueTag(value: unknown): string | undefined {
  return value !== null && typeof value === "object" && "_tag" in value && typeof value._tag === "string"
    ? value._tag
    : undefined;
}

function option<A>(value: Option.Option<A>): A | undefined {
  return Option.isSome(value) ? value.value : undefined;
}

function unsupported(message: string, path: string): ApplicationBridgeCompilerError {
  return new ApplicationBridgeCompilerError("RTKAB1004", message, path);
}

function resolvePaths(options: ApplicationBridgeCompilerOptions): { source: string; ir: string; facade: string } {
  const root = resolve(options.root ?? process.cwd());
  return {
    source: resolve(root, options.source ?? "src/application.bridge.ts"),
    ir: resolve(root, options.ir ?? "../Contract/bridge.ir.json"),
    facade: resolve(root, options.facade ?? "src/application.bridge.generated.ts"),
  };
}

function canonical(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonical);
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort(compare).map((key) => [key, canonical((value as Record<string, unknown>)[key])]));
  }
  return value;
}

function serialize(value: unknown): string {
  return `${JSON.stringify(canonical(value), null, 2)}\n`;
}

function sha256(value: string): string {
  return createHash("sha256").update(value).digest("hex");
}

async function differs(path: string, expected: string): Promise<boolean> {
  return await readFile(path, "utf8").catch(() => undefined) !== expected;
}

async function replaceFile(path: string, contents: string): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  const temporary = `${path}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporary, contents, "utf8");
  try { await rename(temporary, path); }
  catch (error) {
    await rm(temporary, { force: true });
    throw error;
  }
}

function compare(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
