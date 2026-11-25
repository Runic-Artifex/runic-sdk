// The Views TypeScript emitter: the generated clients, contracts, declaration maps, mocks and named types of one producer
// unit, from its contract IR (specs/application/contract-ir.md). Internal until the build runs it (R7 G6); the .NET
// generator writes the same files today, byte for byte.
import { CONTRACT_IR_FORMAT, type ContractIr, type View } from "./ir.js";
import { Emitter } from "./graph.js";
import { CONTRACT_SUFFIX } from "./declmap.js";
import { Modules, NAMED_TYPE_IMPORTS } from "./modules.js";
import { emitView } from "./view.js";
import { SharedParts, emitSharedClient } from "./shared.js";
import { isAbsolutePath, resolvePath } from "./paths.js";
import { lowerFirst, ordinal, sibling } from "./text.js";

export interface GenerateOptions {
  /** The absolute directory the files are written to; declaration maps name sources relative to it. */
  readonly outputDirectory: string;
  /** The absolute directory of the IR document, which relative `sources` paths start from; default `outputDirectory`. */
  readonly irDirectory?: string;
}

const NAMED_TYPES = "types";
// Library mode (#1318): the barrels of the clients and of the mocks. Application mode: the check of imported contracts.
const CLIENTS_MODULE = "runic-clients";
const MOCKS_MODULE = "runic-mocks";
const CHECK_MODULE = "runic-client-modules";
const CHECK_IMPORT = `import type {} from ${sibling(`${CHECK_MODULE}.js`)};`;

/**
 * The files of the generated clients of `ir`, by path relative to the output directory. Throws a ContractIrDiagnostic
 * for an IR that the emitter cannot write a client for, and an Error for one that breaks the format.
 */
export function generate(ir: ContractIr, options: GenerateOptions): Map<string, string> {
  if (ir.format !== CONTRACT_IR_FORMAT) throw new Error(`Not a Views contract IR of format ${CONTRACT_IR_FORMAT}: ${ir.format}`);
  if (!isAbsolutePath(options.outputDirectory)) throw new Error("outputDirectory must be absolute.");
  const irDirectory = options.irDirectory ?? options.outputDirectory;
  const sources = Object.fromEntries(Object.entries(ir.sources).map(([id, location]) =>
    [id, { ...location, file: resolvePath(irDirectory, location.file) }]));
  const clientModules = new Map((ir.clientModules ?? []).map(module => [module.unit, module.specifier]));
  const specifierOf = (unit: string) => {
    const specifier = clientModules.get(unit);
    if (specifier === undefined) throw new Error(`The owner ${unit} is not in clientModules.`);
    return specifier;
  };
  // Application mode: the modules a client library owns, which the application imports from it.
  const owners = new Map<string, string>();
  for (const view of ir.views) if (view.owner !== undefined) owners.set(view.route, specifierOf(view.owner));
  for (const client of ir.sharedClients ?? []) if (client.owner !== undefined) owners.set(lowerFirst(client.name), specifierOf(client.owner));

  const emitter = new Emitter(ir);
  const modules = new Modules(owners);
  const sharedById = new Map((ir.sharedClients ?? []).map(client => [client.id, client]));
  const parts = new Map<string, SharedParts>();
  for (const view of ir.views) {
    const shared = view.sharedClients === undefined ? null : new SharedParts();
    if (shared !== null) parts.set(view.id, shared);
    const sharedReferences = (view.sharedClients ?? []).map(id => {
      const client = sharedById.get(id);
      if (!client) throw new Error(`${view.id}: the shared client ${id} is not in sharedClients.`);
      return { module: lowerFirst(client.name), reference: `${client.name}PageReference` };
    });
    // A View a client library owns is checked like any other, but the library writes its modules.
    modules.discarding = emitter.named.discarding = view.owner !== undefined;
    try { emitView({ emitter, modules, shared, sharedReferences }, view); }
    finally { modules.discarding = emitter.named.discarding = false; }
  }
  const views = new Map(ir.views.map(view => [view.id, view]));
  for (const client of ir.sharedClients ?? []) emitSharedClient(client, views, parts, modules);

  // TypeScriptModules.WriteAll: the named types, then each module with its named-type imports.
  const named = emitter.named;
  named.complete(modules.pending.map(module => module.text));
  const texts = new Map(modules.pending.map(module => [module.path, module.text]));
  const files = new Map<string, string>();
  for (const { path, text } of modules.pending) {
    const contract = path.endsWith(CONTRACT_SUFFIX);
    const used = named.used(text);
    // A client module also re-exports the named types of its contract (#952).
    const exported = contract ? text : text + (texts.get(path.replace(/\.ts$/, CONTRACT_SUFFIX)) ?? "");
    const reexported = named.used(exported);
    const values = named.usedValues(exported);
    const lines: string[] = [];
    // A module that imports a client library's module brings in the check of the library's contracts.
    if (!contract && modules.importsLibrary(text)) lines.push(CHECK_IMPORT);
    if (used.length > 0) lines.push(`import type { ${used.join(", ")} } from ${sibling(`${NAMED_TYPES}.js`)};`);
    if (!contract && reexported.length > 0) lines.push(`export type { ${reexported.join(", ")} } from ${sibling(`${NAMED_TYPES}.js`)};`);
    if (!contract && values.length > 0) lines.push(`export { ${values.join(", ")} } from ${sibling(`${NAMED_TYPES}.js`)};`);
    const resolved = named.resolve(text).replaceAll(NAMED_TYPE_IMPORTS + "\n", lines.length === 0 ? "" : lines.join("\n") + "\n");
    if (!contract) { files.set(path, resolved); continue; }
    const { text: declarations, map } = modules.marks.complete(resolvePath(options.outputDirectory, path), resolved, sources);
    files.set(path, declarations);
    if (map !== null) files.set(path + ".map", map);
  }
  if (named.any) files.set(`${NAMED_TYPES}.ts`, "// <auto-generated />\n" + named.module());
  if (ir.producer.clientLibrary) writeLibrary(ir, files, named.any);
  writeCheck(ir, files);
  return files;
}

const contractOf = (view: View) => `${view.id}:${view.fingerprint}`;

// Library mode: runic-clients.ts, a barrel of the client modules, the shared client modules and the named types with
// runicClientContracts; runic-mocks.ts, a barrel of the mock modules, apart so that production imports never reach the
// mock runtime.
function writeLibrary(ir: ContractIr, files: Map<string, string>, namedTypes: boolean): void {
  const clients = ir.views.filter(view => view.owner === undefined).sort((a, b) => ordinal(a.route, b.route));
  const sharedModules = (ir.sharedClients ?? []).filter(client => client.owner === undefined).map(client => lowerFirst(client.name)).sort(ordinal);
  const barrel = [
    "// <auto-generated />",
    "// The generated clients of this library (RunicApplicationFrontendClientLibrary), for applications and",
    "// components that import them. The mocks are in runic-mocks.ts.",
    ...[...clients.map(view => view.route), ...sharedModules].map(module => `export * from ${sibling(`${module}.js`)};`),
    ...(namedTypes ? [`export * from ${sibling(`${NAMED_TYPES}.js`)};`] : []),
    "",
    "/**",
    " * The contract of each generated client: its ViewModel and the fingerprint of the ViewModel's Bridge contract,",
    " * computed from the assembly this module was generated from.",
    " */",
    "export const runicClientContracts = {",
    ...clients.map(view => `  ${view.route}: "${contractOf(view)}",`),
    "} as const;",
  ];
  files.set(`${CLIENTS_MODULE}.ts`, barrel.join("\n") + "\n");
  const mocks = [
    "// <auto-generated />",
    "// The generated mocks of this library's clients (RunicApplicationFrontendClientLibrary), for tests and",
    "// previews. Production code imports runic-clients.ts.",
    ...clients.map(view => `export * from ${sibling(`${view.route}.mock.js`)};`),
  ];
  files.set(`${MOCKS_MODULE}.ts`, mocks.join("\n") + "\n");
}

// Application mode: runic-client-modules.ts, for each View a client library owns a type that is an error unless the
// contract the library's module exports is the one this unit compiles against.
function writeCheck(ir: ContractIr, files: Map<string, string>): void {
  const owned = ir.views.filter(view => view.owner !== undefined).sort((a, b) => ordinal(a.route, b.route));
  if (owned.length === 0) return;
  const specifiers = new Map((ir.clientModules ?? []).map(module => [module.unit, module.specifier]));
  const aliases = new Map<string, string>();
  const taken = new Set(["RunicSameContract", "RunicClientModuleCheck"]);
  for (const unit of [...new Set(owned.map(view => view.owner!))].sort(ordinal)) {
    let alias = identifier(unit);
    for (let suffix = 2; taken.has(alias); suffix++) alias = identifier(unit) + suffix;
    taken.add(alias);
    aliases.set(unit, alias);
  }
  const check = [
    "// <auto-generated />",
    "// The clients that RunicApplicationFrontendClientModule imports from client libraries. A type error here means that a",
    "// library's module was generated from another build of the assembly than this application compiles against:",
    "// rebuild the library and its package.",
    ...[...aliases].map(([unit, alias]) => `import type { runicClientContracts as ${alias} } from "${specifiers.get(unit)}";`),
    "",
    "type RunicSameContract<Package extends Application, Application extends string> = Package;",
    "",
    "/** Each imported client's contract in its library's module, which must be the one this application's Bridge carries. */",
    "export type RunicClientModuleCheck = [",
    ...owned.map(view => `  RunicSameContract<typeof ${aliases.get(view.owner!)}.${view.route}, "${contractOf(view)}">,`),
    "];",
  ];
  files.set(`${CHECK_MODULE}.ts`, check.join("\n") + "\n");
}

function identifier(name: string): string {
  let result = [...name].map(character => (/[A-Za-z0-9_$]/.test(character) ? character : "_")).join("");
  if (result.length === 0 || /^[0-9]/.test(result)) result = "_" + result;
  return result;
}

export { CONTRACT_IR_FORMAT };
export { ContractIrDiagnostic } from "./diagnostics.js";
export type * from "./ir.js";
