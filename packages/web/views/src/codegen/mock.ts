// <view>.mock.ts: MockTypeScriptEmitter over the IR.
import type { Command, View, ViewMember } from "./ir.js";
import { Emitter, arrowBody, type Graph } from "./graph.js";
import { Import, Ref } from "./bindings.js";
import { Builder, csQuote, lowerFirst, sibling, tsAccess } from "./text.js";
import { NAMED_TYPE_IMPORTS } from "./modules.js";

interface Graphs {
  values: Map<ViewMember, Graph>;
  inputs: Map<Command, Graph>;
  results: Map<Command, Graph>;
  failures: Map<Command, Graph>;
  interactions: { item: { wire: string }; input: Graph; output: Graph }[];
  requests: { item: { wire: string }; input: Graph }[];
  queries: { member: { wire: string; defaultKey?: string }; rows: Graph }[];
  /** field-paths/1: the paths below each member, nested members first, then rows. */
  fieldPaths: { member: string; paths: readonly (readonly string[])[] }[];
}

const quote = csQuote;
const key = (name: string) =>
  name.length > 0 && /[\p{L}_$]/u.test(name[0]!) && /^[\p{L}\p{Nd}_$]*$/u.test(name) && name !== "__proto__" ? name : `[${quote(name)}]`;
const encode = (expression: string, type: string) => `{ const typed = value as ${type}; return ${expression}; }`;

export function emitMock(emitter: Emitter, view: View, graphs: Graphs): string {
  const name = view.name;
  const state = `${name}MockState`;
  const t = (graph: Graph) => emitter.type(graph.root);
  const hasArgument = (command: Command) => command.input !== undefined;
  const inputType = (command: Command) => graphs.inputs.has(command) ? t(graphs.inputs.get(command)!) : command.input?.untyped ? "string" : "never";

  const defaults: [string, string][] = [];
  if (view.validation) defaults.push(["validation", "{ hasErrors: false, truncated: false, errors: [] }"]);
  if (view.memberErrors) for (const member of view.members) defaults.push([member.wire + "Errors", "[]"]);
  for (const command of view.commands) {
    if (!hasArgument(command)) defaults.push([`can${command.name}`, "true"]);
    if (command.executionState) defaults.push([`is${command.name}Executing`, "false"]);
  }
  const contentFields = view.members.filter(member => member.kind === "content" || member.kind === "content-list" || member.kind === "region-collection")
    .sort((a, b) => order(a) - order(b))
    .map(member => {
      const pages = (member as { readonly pages: readonly { readonly kind: string }[] }).pages;
      return {
        wire: member.wire,
        referenceType: pages.map(page => `${Ref("MockReference")}<${quote(page.kind)}>`).join(" | "),
        isCollection: member.kind === "content-list",
        isNullable: member.kind !== "region-collection" && (member as { nullable?: boolean }).nullable === true,
        isRegion: member.kind === "region-collection",
        close: member.kind === "region-collection" && member.pageCanClose,
      };
    });
  const valueFields = view.members.filter(member => graphs.values.has(member));
  const setters = valueFields.filter(member => (member as { settable?: boolean }).settable).map(member => ({
    method: `set${member.name}`, wire: member.wire, valueType: t(graphs.values.get(member)!),
    checked: (member as { checked?: boolean }).checked ? `write${member.name}` : null,
  }));
  const commands = view.commands.map(command => ({
    method: command.wire, member: command.wire,
    inputType: hasArgument(command) ? inputType(command) : null,
    decodeInput: hasArgument(command) ? (graphs.inputs.has(command) ? emitter.decoder(graphs.inputs.get(command)!.root, "wire") : "bridgeWire.string(wire)") : null,
    availability: hasArgument(command) ? null : `can${command.name}`,
    failureType: graphs.failures.has(command) ? t(graphs.failures.get(command)!) : null,
    encodeFailure: graphs.failures.has(command) ? emitter.encoder(graphs.failures.get(command)!.root, "typed") : null,
    keyName: command.keyed ? command.parameter! : null,
  }));
  const operations = view.commands.filter(command => command.async).map(command => {
    const failureType = graphs.failures.has(command) ? t(graphs.failures.get(command)!) : null;
    const inputT = hasArgument(command) ? inputType(command) : "void";
    const resultType = graphs.results.has(command) ? t(graphs.results.get(command)!) : "void";
    return {
      method: command.wire, member: command.wire, inputType: inputT, resultType,
      decodeInput: graphs.inputs.has(command) ? emitter.decoder(graphs.inputs.get(command)!.root, "wire") : null,
      encodeResult: graphs.results.has(command) ? emitter.encoder(graphs.results.get(command)!.root, "typed") : null,
      isStream: command.stream === true, failureType,
      encodeFailure: graphs.failures.has(command) ? emitter.encoder(graphs.failures.get(command)!.root, "typed") : null,
      typeArguments: failureType === null ? `${inputT}, ${resultType}` : `${inputT}, ${resultType}, ${failureType}`,
    };
  });
  const interactions = graphs.interactions.map(({ item, input, output }) => ({
    name: item.wire, inputType: t(input), outputType: t(output),
    encodeInput: emitter.encoder(input.root, "typed"), decodeOutput: emitter.decoder(output.root, "wire"),
  }));
  const requests = graphs.requests.map(({ item, input }) => ({ name: item.wire, inputType: t(input), encodeInput: emitter.encoder(input.root, "typed") }));
  const collections = valueFields.filter(member => (member as { collectionKey?: string }).collectionKey !== undefined).map(member => {
    const root = graphs.values.get(member)!.root;
    return {
      wire: member.wire, itemType: emitter.type(root.element!), encode: emitter.encoder(root.element!, "typed"),
      decode: emitter.decoder(root.element!, "wire"), key: `String(${tsAccess("item", (member as { collectionKey: string }).collectionKey)})`,
    };
  });
  const checkedFields = valueFields.filter(member => (member as { checked?: boolean }).checked).map(member => member.wire);
  const texts = valueFields.filter(member => (member as { text?: boolean }).text).map(member => member.wire);
  const scoped = view.commands.filter(command => command.scoped).map(command => command.wire);

  const body = new Builder();
  const optional = defaults.map(([field]) => quote(field));
  const content = contentFields.map(field => quote(field.wire));
  const queries = graphs.queries.map(({ member, rows }) => ({
    wire: member.wire, itemType: emitter.type(rows.root.element!), encode: emitter.encoder(rows.root.element!, "typed"),
    decode: emitter.decoder(rows.root.element!, "wire"), key: member.defaultKey === undefined ? null : `String(${tsAccess("item", member.defaultKey)})`,
  }));
  const omitted = [...optional, ...content, ...queries.map(query => quote(query.wire))];
  const kinds = view.kinds.map(quote).join(" | ");
  body.line(`/** The state of a mock ${name}: the client state, with content as \`{ kind, id }\` and command state optional. */`);
  body.append(`export type ${state} = `);
  body.append(omitted.length === 0 ? `${name}State` : `Omit<${name}State, ${omitted.join(" | ")}>`);
  if (optional.length > 0) body.append(` & Partial<Pick<${name}State, ${optional.join(" | ")}>>`);
  if (contentFields.length > 0) {
    body.line(" & {");
    for (const field of contentFields)
      body.line(field.isRegion
        ? `  readonly ${key(field.wire)}: ${Ref("MockRegionCollection")}<${field.referenceType}>;`
        : `  readonly ${key(field.wire)}: ${field.isCollection ? `readonly (${field.referenceType})[]` : field.referenceType}${field.isNullable ? " | null" : ""};`);
    body.append("}");
  }
  body.line(";");
  body.line();
  body.line(`export interface ${name}MockDefinition {`);
  body.line(`  /** Presents the mock as content on route \`content{id}\`; omit it for the root route \`${view.route}\`. */`);
  body.line("  readonly id?: string;");
  if (view.kinds.length > 1) body.line(`  readonly kind?: ${kinds};`);
  body.line(`  readonly state: ${state};`);
  if (setters.length > 0) {
    body.line("  /** Run before a setter or checked write applies its value; return more changes or throw to reject it. */");
    body.line("  readonly setters?: {");
    for (const setter of setters) body.line(`    readonly ${setter.method}?: ${Ref("MockSetterHandler")}<${state}, ${setter.valueType}>;`);
    body.line("  };");
  }
  if (commands.length > 0) {
    body.line("  readonly commands?: {");
    for (const command of commands)
      body.line(`    readonly ${command.method}?: ${Ref("MockCommandHandler")}<${state}${command.inputType === null ? "" : `, [input: ${command.inputType}]`}>;`);
    body.line("  };");
  }
  const withInput = commands.filter(command => command.inputType !== null);
  if (withInput.length > 0) {
    body.line("  /** Availability of commands with an argument. */");
    body.line("  readonly canExecute?: {");
    for (const command of withInput) body.line(`    readonly ${command.method}?: (state: ${state}, input: ${command.inputType}) => boolean;`);
    body.line("  };");
  }
  const keyed = commands.filter(command => command.keyName !== null);
  if (keyed.length > 0) {
    body.line("  /** Whether .NET finds an object for the key of a `[RunicCommandKey]` command; absent, every key resolves. */");
    body.line("  readonly resolveKey?: {");
    for (const command of keyed) body.line(`    readonly ${command.method}?: (state: ${state}, ${command.keyName}: ${command.inputType}) => boolean;`);
    body.line("  };");
  }
  const regions = contentFields.filter(field => field.isRegion);
  if (regions.length > 0) {
    body.line("  /** Region requests (region-collections/1); without a handler, a select or close commits. */");
    body.line("  readonly regions?: {");
    for (const field of regions) body.line(`    readonly ${key(field.wire)}?: ${Ref("MockRegionHandler")}<${state}>;`);
    body.line("  };");
  }
  if (queries.length > 0) {
    body.line("  /** The rows of each query collection, which the mock filters, sorts and serves by range. */");
    body.line("  readonly queries?: {");
    for (const query of queries) body.line(`    readonly ${key(query.wire)}?: ${Ref("MockTypedQuerySource")}<${query.itemType}>;`);
    body.line("  };");
  }
  if (operations.length > 0) {
    body.line("  /** Operation handlers; without one an operation runs its command handler and succeeds. */");
    body.line("  readonly operations?: {");
    for (const operation of operations)
      body.line(`    readonly ${operation.method}?: ${Ref("MockTypedOperationHandler")}<${state}, ${operation.typeArguments}> | "manual";`);
    body.line("  };");
  }
  body.line("}");
  body.line();
  const members: string[] = [];
  if (collections.length > 0)
    members.push(`  readonly collections: { ${collections.map(collection => `readonly ${key(collection.wire)}: ${Ref("MockTypedCollection")}<${collection.itemType}>;`).join(" ")} };`);
  if (queries.length > 0)
    members.push(`  readonly queries: { ${queries.map(query => `readonly ${key(query.wire)}: ${Ref("MockTypedQueryCollection")}<${query.itemType}>;`).join(" ")} };`);
  if (operations.length > 0)
    members.push(`  readonly operations: { ${operations.map(operation => `readonly ${operation.method}: readonly ${Ref("MockTypedOperation")}<${operation.typeArguments}>[];`).join(" ")} };`);
  if (interactions.length > 0)
    members.push(`  readonly interactions: { ${interactions.map(item => `readonly ${item.name}: ${Ref("MockTypedInteraction")}<${item.inputType}, ${item.outputType}>;`).join(" ")} };`);
  if (requests.length > 0)
    members.push(`  readonly requests: { ${requests.map(item => `readonly ${item.name}: ${Ref("MockTypedViewRequest")}<${item.inputType}>;`).join(" ")} };`);
  const baseType = `${Ref("MockTypedView")}<${state}, ${name}Client, ${kinds}>`;
  if (members.length === 0) body.line(`export interface ${name}Mock extends ${baseType} {}`);
  else {
    body.line(`export interface ${name}Mock extends ${baseType} {`);
    for (const member of members) body.line(member);
    body.line("}");
  }
  body.line();
  body.line(`const ${Ref("spec")}: ${Ref("MockTypedViewSpec")} = {`);
  body.line(`  kind: ${quote(view.kinds[0]!)},`);
  body.line(`  route: ${quote(view.route)},`);
  body.line(`  contract: ${quote(`${view.id}:${view.fingerprint}`)},`);
  if (valueFields.length === 0) body.line("  fields: {},");
  else {
    body.line("  fields: {");
    for (const member of valueFields) {
      const root = graphs.values.get(member)!.root;
      body.line(`    ${key(member.wire)}: { encode: (value: unknown) => ${encode(emitter.encoder(root, "typed"), emitter.type(root))}, decode: (wire: unknown) => ${arrowBody(emitter.decoder(root, "wire"))} },`);
    }
    body.line("  },");
  }
  body.line(defaults.length === 0 ? "  defaults: {}," : `  defaults: { ${defaults.map(([field, value]) => `${key(field)}: ${value}`).join(", ")} },`);
  if (checkedFields.length > 0) body.line(`  checkedFields: [${checkedFields.map(quote).join(", ")}],`);
  if (graphs.fieldPaths.length > 0)
    body.line(`  fieldPaths: { ${graphs.fieldPaths.map(entry => `${key(entry.member)}: [${entry.paths.map(path => `[${path.map(quote).join(", ")}]`).join(", ")}]`).join(", ")} },`);
  if (texts.length > 0) body.line(`  texts: [${texts.map(quote).join(", ")}],`);
  if (scoped.length > 0) body.line(`  scopedCommands: [${scoped.map(quote).join(", ")}],`);
  if (collections.length > 0) {
    body.line("  collections: {");
    for (const collection of collections)
      body.line(`    ${key(collection.wire)}: { encode: (value: unknown) => ${encode(collection.encode, collection.itemType)}, decode: (wire: unknown) => ${arrowBody(collection.decode)}, key: (item: ${collection.itemType}) => ${collection.key} },`);
    body.line("  },");
  }
  if (regions.length > 0) body.line(`  regions: { ${regions.map(field => `${key(field.wire)}: { close: ${field.close ? "true" : "false"} }`).join(", ")} },`);
  if (queries.length > 0) {
    body.line("  queries: {");
    for (const query of queries)
      body.line(`    ${key(query.wire)}: { encode: (value: unknown) => ${encode(query.encode, query.itemType)}, decode: (wire: unknown) => ${arrowBody(query.decode)}${query.key === null ? "" : `, key: (item: ${query.itemType}) => ${query.key}`} },`);
    body.line("  },");
  }
  if (setters.length > 0) {
    body.line("  setters: {");
    for (const setter of setters) body.line(`    ${setter.method}: { member: ${quote(setter.wire)}${setter.checked === null ? "" : `, checked: ${quote(setter.checked)}`} },`);
    body.line("  },");
  }
  if (commands.length > 0) {
    body.line("  commands: {");
    for (const command of commands)
      body.line(`    ${command.method}: { member: ${quote(command.member)}${command.decodeInput === null ? "" : `, decodeInput: (wire: unknown) => ${arrowBody(command.decodeInput)}`}${command.availability === null ? "" : `, available: ${quote(command.availability)}`}${command.encodeFailure === null ? "" : `, encodeFailure: (value: unknown) => ${encode(command.encodeFailure, command.failureType!)}`} },`);
    body.line("  },");
  }
  if (operations.length > 0) {
    body.line("  operations: {");
    for (const operation of operations)
      body.line(`    ${operation.method}: { member: ${quote(operation.member)}${operation.decodeInput === null ? "" : `, decodeInput: (wire: unknown) => ${arrowBody(operation.decodeInput)}`}${operation.encodeResult === null ? "" : `, encodeResult: (value: unknown) => ${encode(operation.encodeResult, operation.resultType)}`}${operation.isStream ? ", stream: true" : ""}${operation.encodeFailure === null ? "" : `, encodeFailure: (value: unknown) => ${encode(operation.encodeFailure, operation.failureType!)}`} },`);
    body.line("  },");
  }
  if (interactions.length > 0) {
    body.line("  interactions: {");
    for (const item of interactions)
      body.line(`    ${item.name}: { encodeInput: (value: unknown) => ${encode(item.encodeInput, item.inputType)}, decodeOutput: (wire: unknown) => ${arrowBody(item.decodeOutput)} },`);
    body.line("  },");
  }
  if (requests.length > 0) {
    body.line("  requests: {");
    for (const item of requests) body.line(`    ${item.name}: { encodeInput: (value: unknown) => ${encode(item.encodeInput, item.inputType)} },`);
    body.line("  },");
  }
  body.line("};");
  body.line();
  body.line("/**");
  body.line(` * Serves the ${name} routes from \`bridge\` with typed state and handlers, so a test`);
  body.line(" * can connect the generated client without .NET.");
  body.line(" */");
  body.line(`export function mock${name}(bridge: ${Ref("MockBridge")}, definition: ${name}MockDefinition): ${name}Mock {`);
  body.line(`  return ${Ref("mockTypedView")}(bridge, ${Ref("spec")}, definition) as ${name}Mock;`);
  body.line("}");

  const text = body.toString();
  const generatedImports = [Import("mockTypedView"), "type " + Import("MockTypedViewSpec")];
  const mockImports = ["type " + Import("MockBridge"), "type " + Import("MockTypedView")];
  if (setters.length > 0) mockImports.push("type " + Import("MockSetterHandler"));
  if (commands.length > 0) mockImports.push("type " + Import("MockCommandHandler"));
  if (operations.length > 0) mockImports.push("type " + Import("MockTypedOperation"), "type " + Import("MockTypedOperationHandler"));
  if (collections.length > 0) mockImports.push("type " + Import("MockTypedCollection"));
  if (queries.length > 0) mockImports.push("type " + Import("MockTypedQueryCollection"), "type " + Import("MockTypedQuerySource"));
  if (interactions.length > 0) mockImports.push("type " + Import("MockTypedInteraction"));
  if (requests.length > 0) mockImports.push("type " + Import("MockTypedViewRequest"));
  if (contentFields.length > 0) mockImports.push("type " + Import("MockReference"));
  if (regions.length > 0) mockImports.push("type " + Import("MockRegionCollection"), "type " + Import("MockRegionHandler"));
  const module = new Builder();
  module.line("// <auto-generated />");
  if (text.includes("bridgeWire.")) module.line('import * as bridgeWire from "@runic-artifex/views/generated/wire";');
  module.line(`import { ${generatedImports.join(", ")} } from "@runic-artifex/views/generated/mock";`);
  module.line(`import type { ${mockImports.map(item => item.slice(5)).join(", ")} } from "@runic-artifex/views/mock";`);
  module.line(`import type { ${name}Client, ${name}State } from ${sibling(`${lowerFirst(name)}.js`)};`);
  module.line(NAMED_TYPE_IMPORTS);
  module.line();
  module.append(text);
  return module.toString();
}

// contentBindings order: content slots, then ViewModel collections, then region collections.
const order = (member: ViewMember) => (member.kind === "content" ? 0 : member.kind === "content-list" ? 1 : 2);
