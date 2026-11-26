// One View's client (<view>.ts), contract (<view>.contract.d.ts) and mock (<view>.mock.ts): the TypeScript half of the
// .NET generator's EmitView, over the IR.
import type {
  Command, Constraints, TypeDefinition, ContentListMember, ContentMember, FieldPath, PageRef, QueryCollectionMember, RegionCollectionMember, View,
  ViewMember, ValueMember,
} from "./ir.js";
import { Emitter, arrowBody, type Graph, type Node } from "./graph.js";
import { Import, Ref, Shorthand, mentions, declaredTopLevel, references, withoutComments } from "./bindings.js";
import { ContractIrDiagnostic, REGION_COLLECTION_NAME } from "./diagnostics.js";
import { NAMED_TYPE_IMPORTS, type Modules } from "./modules.js";
import { Builder, csQuote, dotnetNumber, lowerFirst, relaxedQuote, tsAccess, tsLiteralKey, tsPropertyName, upperFirst, isTsIdentifier } from "./text.js";
import { emitMock } from "./mock.js";
import { OWNER, PARAMETER, type SharedParts } from "./shared.js";

// D072.13: the TSDoc of development-only members, which a trimmed or NativeAOT publish leaves out.
const DEVELOPMENT_ONLY_STATE = "Development-only (`[RunicDevelopmentOnly]`): absent in a trimmed or NativeAOT publish, which leaves it out.";
const DEVELOPMENT_ONLY_CALL = "Development-only (`[RunicDevelopmentOnly]`): a trimmed or NativeAOT publish leaves the member out and rejects the call.";
const DEVELOPMENT_ONLY_COMMAND = "Development-only (`[RunicDevelopmentOnly]`): a trimmed or NativeAOT publish rejects it with a `BridgeError` of kind `rejected` and reason `unavailable`.";

const withNote = (documentation: string | undefined, note: string) =>
  documentation === undefined || documentation.trim().length === 0 ? note : documentation + "\n\n" + note;

const UPLOAD_DOCUMENTATION = "Uploads a file to the window's .NET host over HTTP (blobs/1) and resolves with the reference that a command's\n`RunicUpload` input takes. `options.onProgress` reports the bytes sent; `options.signal` aborts the upload.";
const UPLOAD_DECLARATION = 'upload(content: Blob, options?: import("@runic-artifex/views").UploadOptions): Promise<import("@runic-artifex/views").BlobRef>;';

type ContentBinding = ContentMember | ContentListMember | RegionCollectionMember;

/** A field path below a member: its wire names and the object members it passes through, with their graph nodes. */
interface Leaf { path: readonly string[]; chain: Node["members"]; constraints: Constraints | undefined }

/** What a View needs from the generation it is part of. */
export interface ViewContext {
  readonly emitter: Emitter;
  readonly modules: Modules;
  /** Records the parts of the client that implement shared clients; null when the View implements none. */
  readonly shared: SharedParts | null;
  /** The page reference types of the shared clients the View implements, with their modules, ordered by name. */
  readonly sharedReferences: readonly { module: string; reference: string }[];
}

export function emitView(context: ViewContext, view: View): void {
  const { emitter, modules } = context;
  const marks = modules.marks;
  const shortName = view.name;
  const prefix = view.route;
  const fullName = view.id;
  const members = view.members;
  const commands = view.commands;
  const values = new Map<ViewMember, Graph>();
  for (const member of members) if (member.kind === "value" || member.kind === "selection") values.set(member, emitter.graph(member.value));
  const valueMembers = members.filter((member): member is ValueMember => values.has(member));
  const queries = members.filter((member): member is QueryCollectionMember => member.kind === "query-collection")
    .map(member => ({ member, rows: emitter.graph(member.rows) }));
  const inputs = new Map(commands.filter(command => command.input && !command.input.untyped).map(command => [command, emitter.graph(command.input!)]));
  const results = new Map(commands.filter(command => command.result).map(command => [command, emitter.graph(command.result!)]));
  const failures = new Map(commands.filter(command => command.failure).map(command => [command, emitter.graph(command.failure!)]));
  const interactions = (view.interactions ?? []).map(item => ({ item, input: emitter.graph(item.input), output: emitter.graph(item.output) }));
  const requests = (view.requests ?? []).map(item => ({ item, input: emitter.graph(item.input) }));
  const hasErrors = view.memberErrors === true;
  const hasValidation = view.validation === true;
  const usesUploads = view.uploads === true;
  const checked = valueMembers.filter(member => member.checked);
  const needsCheckedWriter = checked.length > 0;
  const settable = valueMembers.filter(member => member.settable);
  const textMembers = valueMembers.filter(member => member.text);
  const textFields = valueMembers.filter(member => member.appendOnlyText);
  const incremental = valueMembers.filter(member => member.collectionKey !== undefined);
  const content = members.filter((member): member is ContentMember => member.kind === "content");
  const contentLists = members.filter((member): member is ContentListMember => member.kind === "content-list");
  const regionCollections = members.filter((member): member is RegionCollectionMember => member.kind === "region-collection");
  // contentBindings order: content slots, then ViewModel collections, then region collections.
  const contentBindings: ContentBinding[] = [...content, ...contentLists, ...regionCollections];
  const hasContent = contentBindings.length > 0;
  const scopedCommands = commands.filter(command => command.scoped).map(command => command.wire);
  const hasArgument = (command: Command) => command.input !== undefined;
  const inputType = (command: Command) => inputs.has(command) ? emitter.type(inputs.get(command)!.root) : command.input?.untyped ? "string" : "never";
  const valueType = (member: ViewMember) => emitter.type(values.get(member)!.root);
  const referenceOf = (page: PageRef) => `${upperFirst(page.kind)}PageReference`;
  const developmentOnly = (item: { developmentOnly?: true }) => item.developmentOnly === true;
  const simpleName = fullName.split(/[.+]/).pop()!;
  // field-paths/1: the paths below settable object members (nested) and below the rows of editable collections.
  const nestedPaths = new Map<ValueMember, Leaf[]>();
  const rowPaths = new Map<ValueMember, Leaf[]>();
  for (const member of valueMembers) {
    if (!member.fieldPaths) continue;
    const root = values.get(member)!.root;
    if (root.kind === "list") rowPaths.set(member, leaves(root.element!, member.fieldPaths));
    else nestedPaths.set(member, leaves(root, member.fieldPaths));
  }
  const hasFieldPaths = nestedPaths.size > 0 || rowPaths.size > 0;
  const operations = commands.filter(command => command.async).map(command => ({
    command,
    name: command.name,
    member: command.wire,
    inputType: inputs.has(command) ? emitter.type(inputs.get(command)!.root) : "never",
    resultType: results.has(command) ? emitter.type(results.get(command)!.root) : "void",
    decodeResult: results.has(command) ? emitter.decoder(results.get(command)!.root, "value") : "undefined",
    encodeInput: inputs.has(command) ? emitter.encoder(inputs.get(command)!.root, command.parameter!) : "undefined",
    hasInput: hasArgument(command),
    isStream: command.stream === true,
    inputName: command.parameter ?? "input",
    failureType: failures.has(command) ? emitter.type(failures.get(command)!.root) : undefined,
    decodeFailure: failures.has(command) ? emitter.decoder(failures.get(command)!.root, "value") : undefined,
  }));
  const typeArguments = (operation: (typeof operations)[number]) =>
    operation.failureType === undefined ? operation.resultType : `${operation.resultType}, ${operation.failureType}`;
  const shared = context.shared;
  const record: SharedParts["record"] = (owner, part) => shared?.record(owner, part);

  const ts = new Builder();
  // The page functions and page reference types of the content's modules.
  const contentImports: { module: string; functions: string[]; references: string[] }[] = [];
  const pageKinds = new Map<string, string[]>();
  for (const binding of contentBindings)
    for (const page of binding.pages) {
      const kinds = pageKinds.get(page.name) ?? [];
      if (!kinds.includes(page.kind)) kinds.push(page.kind);
      pageKinds.set(page.name, kinds);
    }
  for (const [name, kinds] of pageKinds)
    contentImports.push({ module: lowerFirst(name), functions: kinds.map(kind => `page${upperFirst(kind)}`), references: kinds.map(kind => `${upperFirst(kind)}PageReference`) });
  // region-collections/1: select{Name}(key) and close{Name}(key); RUNICBRIDGE071 when one collides with another member.
  const clientMembers = new Set(["snapshot", "subscribe", "dispose", "interactions", "fieldBaseline"]);
  for (const member of settable) clientMembers.add(`set${member.name}`);
  for (const member of checked) clientMembers.add(`write${member.name}`);
  for (const command of commands) {
    clientMembers.add(command.wire);
    clientMembers.add(`can${command.name}`);
    if (command.async) { clientMembers.add(`start${command.name}`); clientMembers.add(`recover${command.name}`); }
  }
  const regionMethods: { member: RegionCollectionMember; method: string; action: string }[] = [];
  for (const member of regionCollections)
    for (const action of member.pageCanClose ? ["select", "close"] : ["select"]) {
      const method = action + member.name;
      if (clientMembers.has(method))
        throw new ContractIrDiagnostic(REGION_COLLECTION_NAME,
          `${simpleName}.${member.name}: the region collection's client method ${method} collides with another member of the generated client. Rename the property or the other member.`,
          `${fullName}/state/${member.wire}`);
      clientMembers.add(method);
      regionMethods.push({ member, method, action });
    }

  const clientName = `${shortName}Client`;
  const dts = new Builder();
  const mapped = (kind: string | null, member: string, token: string) => marks.mark(kind === null ? fullName : `${fullName}/${kind}/${member}`, token);
  dts.line(`export interface ${shortName}State {${mapped(null, "", `${shortName}State`)}`);
  if (hasValidation) dts.line(`  readonly validation: ${Ref("BridgeValidationState")};`);
  for (const member of members) {
    let propertyType: string;
    if (member.kind === "content") propertyType = member.pages.map(referenceOf).join(" | ") + (member.nullable ? " | null" : "");
    else if (member.kind === "region-collection") propertyType = `${Ref("RegionCollection")}<` + member.pages.map(referenceOf).join(" | ") + ">";
    else if (member.kind === "content-list") propertyType = "readonly (" + member.pages.map(referenceOf).join(" | ") + ")[]" + (member.nullable ? " | null" : "");
    else if (values.has(member)) propertyType = valueType(member);
    else propertyType = Ref("QueryCollectionState");
    let documentation = member.doc;
    if (values.has(member)) documentation = Emitter.withNote(documentation, values.get(member)!.root);
    // D072.13: a development-only member is optional in every configuration, because a publish may leave it out.
    const optional = developmentOnly(member) ? "?" : "";
    if (developmentOnly(member)) documentation = withNote(documentation, DEVELOPMENT_ONLY_STATE);
    dts.doc("  ", documentation);
    const property = tsPropertyName(member.wire);
    dts.line(`  readonly ${property}${optional}: ${propertyType};${mapped("state", member.wire, property)}`);
    const errors = tsPropertyName(member.wire + "Errors");
    if (hasErrors) dts.line(`  readonly ${errors}${optional}: readonly string[];${mapped("state", member.wire, errors)}`);
    const pages = member.kind === "content" || member.kind === "content-list" || member.kind === "region-collection" ? member.pages : [];
    record(member, {
      key: "state", section: "state", declaration: `readonly ${property}${optional}: ${propertyType};`, documentation,
      imports: pages.map(page => ({ module: lowerFirst(page.name), type: referenceOf(page) })),
    });
    if (hasErrors) record(member, { key: "state-errors", section: "state", declaration: `readonly ${errors}${optional}: readonly string[];`, optional: true });
  }
  for (const command of commands) {
    const optional = developmentOnly(command) ? "?" : "";
    if (!hasArgument(command)) {
      const canState = `can${command.name}`;
      if (developmentOnly(command)) dts.doc("  ", DEVELOPMENT_ONLY_STATE);
      dts.line(`  readonly ${canState}${optional}: boolean;${mapped("commandState", canState, canState)}`);
      record(command, { key: "can-state", section: "state", declaration: `readonly ${canState}${optional}: boolean;` });
    }
    if (command.executionState) {
      const executing = `is${command.name}Executing`;
      if (developmentOnly(command)) dts.doc("  ", DEVELOPMENT_ONLY_STATE);
      dts.line(`  readonly ${executing}${optional}: boolean;${mapped("commandState", executing, executing)}`);
      record(command, { key: "executing-state", section: "state", declaration: `readonly ${executing}${optional}: boolean;` });
    }
  }
  dts.line("}");
  dts.line();
  if (interactions.length > 0) {
    dts.line(`export type ${shortName}InteractionContext = ${Ref("BridgeInteractionContext")};`);
    dts.line(`export interface ${shortName}Interactions {`);
    for (const { item, input, output } of interactions) {
      dts.doc("  ", item.doc);
      dts.line(`  readonly ${item.wire}: {${mapped("interaction", item.wire, item.wire)}`);
      dts.line(`    handle(handler: (input: ${emitter.type(input.root)}, context: ${shortName}InteractionContext) => ${emitter.type(output.root)} | Promise<${emitter.type(output.root)}>): () => void;`);
      dts.line("  };");
    }
    dts.line("}");
    dts.line();
  }
  if (requests.length > 0) {
    dts.line(`export type ${shortName}ViewRequestContext = ${Ref("BridgeViewRequestContext")};`);
    dts.line(`export interface ${shortName}ViewRequests {`);
    for (const { item, input } of requests) {
      dts.doc("  ", item.doc);
      dts.line(`  readonly ${item.wire}: { handle(handler: (input: ${emitter.type(input.root)}, context: ${shortName}ViewRequestContext) => void | Promise<void>): () => void };${mapped("request", item.wire, item.wire)}`);
    }
    dts.line("}");
    dts.line();
  }
  if (needsCheckedWriter) {
    dts.line(`export interface ${shortName}CheckedFields {`);
    for (const member of checked) dts.line(`  readonly ${tsPropertyName(member.wire)}: ${valueType(member)};${mapped("setter", member.wire, tsPropertyName(member.wire))}`);
    dts.line("}");
    dts.line();
  }
  for (const operation of operations)
    dts.line(`export interface ${shortName}${operation.name}Operation extends ${Ref(operation.isStream ? "BridgeStreamOperation" : "BridgeOperation")}<${typeArguments(operation)}> {}`);
  if (operations.length > 0) dts.line();
  // #1317: the names of the commands, which injectCommands and useCommands read from the client's type. Type-only.
  if (commands.length > 0) {
    dts.doc("", `The names of the ${shortName} ViewModel's commands, whose handles \`injectCommands\` and \`useCommands\` make.`);
    dts.line(`export type ${shortName}CommandName = ${commands.map(command => csQuote(command.wire)).join(" | ")};`);
    dts.line();
  }
  dts.doc("", `A connected ${shortName} ViewModel. Dispose it when its presentation ends.` + (view.doc === undefined ? "" : "\n\n" + view.doc));
  dts.line(`export interface ${clientName} extends ${Ref("ViewClient")}<${shortName}State> {${mapped(null, "", clientName)}`);
  if (commands.length > 0) {
    dts.line(`  /** Type-only: the client's commands, \`${shortName}CommandName\`, which \`injectCommands\` and \`useCommands\` offer. */`);
    dts.line(`  readonly [${Ref("viewCommands")}]?: ${shortName}CommandName;`);
  }
  if (interactions.length > 0) dts.line(`  readonly interactions: ${shortName}Interactions;`);
  if (requests.length > 0) dts.line(`  readonly requests: ${shortName}ViewRequests;`);
  if (queries.length > 0) {
    dts.line("  /** The rows of each query collection, which this presentation loads for its own range (`query-collections/1`). */");
    dts.line("  readonly queries: {");
    for (const { member, rows } of queries) {
      dts.doc("    ", member.doc);
      const declaration = `readonly ${tsPropertyName(member.wire)}: ${Ref("QueryCollection")}<${emitter.type(rows.root.element!)}>;`;
      dts.line(`    ${declaration}${mapped("state", member.wire, tsPropertyName(member.wire))}`);
      record(member, { key: "query", section: "queries", declaration, documentation: member.doc });
    }
    dts.line("  };");
  }
  if (usesUploads) {
    dts.doc("  ", UPLOAD_DOCUMENTATION);
    dts.line("  " + UPLOAD_DECLARATION);
  }
  for (const command of commands.filter(command => inputs.get(command)?.nodes.some(node => node.kind === "upload")))
    record(command, { key: "upload", section: "client", declaration: UPLOAD_DECLARATION, documentation: UPLOAD_DOCUMENTATION });
  for (const member of settable.filter(member => values.get(member)!.nodes.some(node => node.kind === "upload")))
    record(member, { key: "upload", section: "client", declaration: UPLOAD_DECLARATION, documentation: UPLOAD_DOCUMENTATION });
  for (const member of settable) {
    const setter = `Calls the public C# setter of \`${member.declaringType}.${member.name}\`.`;
    const documentation = member.doc === undefined ? setter : member.doc + "\n\n" + setter;
    dts.doc("  ", developmentOnly(member) ? withNote(documentation, DEVELOPMENT_ONLY_CALL) : documentation);
    dts.line(`  set${member.name}(value: ${valueType(member)}): Promise<${shortName}State>;${mapped("setter", member.wire, `set${member.name}`)}`);
    record(member, { key: "setter", section: "client", declaration: `set${member.name}(value: ${valueType(member)}): Promise<${OWNER}State>;` });
  }
  for (const member of checked) {
    // Neutral wording: the producer may be written in any language, so the text names no source type (G7).
    const documentation = member.doc ?? `Writes \`${member.wire}\` with an acknowledged field write, which reports whether the app committed it.`;
    dts.doc("  ", developmentOnly(member) ? withNote(documentation, DEVELOPMENT_ONLY_CALL) : documentation);
    dts.line(`  write${member.name}(value: ${valueType(member)}, options: ${Ref("FieldWriteOptions")}<${valueType(member)}>): Promise<${Ref("FieldWriteReceipt")}<${valueType(member)}>>;${mapped("setter", member.wire, `write${member.name}`)}`);
  }
  if (needsCheckedWriter) {
    dts.line("  /** The value and version of a checked field that a `write…` call is based on. */");
    dts.line(`  fieldBaseline<K extends keyof ${shortName}CheckedFields>(field: K): ${Ref("FieldBaseline")}<${shortName}CheckedFields[K]>;`);
  }
  if (hasFieldPaths) {
    dts.line("  /** A checked write of a field path below a member (field-paths/1); `{View}Fields` descriptors call it. */");
    dts.line(`  writePath<T>(member: string, path: readonly string[], write: ${Ref("BridgeFieldWriteRequest")}, decode: (wire: unknown) => T): Promise<${Ref("FieldWriteReceipt")}<T>>;`);
    dts.line("  /** The version a field path write is based on: the revision of the state the client shows. */");
    dts.line("  fieldPathVersion(): number;");
  }
  for (const command of commands) {
    const input = hasArgument(command) ? `${command.parameter}: ${inputType(command)}` : "";
    const comparableInput = hasArgument(command) ? `${PARAMETER}: ${inputType(command)}` : "";
    // A keyed command (#472) sends the key; the producer resolves it to the command's parameter.
    let unresolved = !command.keyed ? undefined
      : `Rejects with a \`BridgeError\` of kind \`rejected\` and reason \`unresolved-key\` when .NET finds no object for \`${command.parameter}\`.`;
    if (developmentOnly(command)) unresolved = withNote(unresolved, DEVELOPMENT_ONLY_COMMAND);
    const documentation = unresolved === undefined ? command.doc
      : (command.doc ?? "").trim().length === 0 ? unresolved : command.doc + "\n\n" + unresolved;
    dts.doc("  ", documentation);
    const reply = failures.has(command) ? `${Ref("BridgeOutcome")}<${OWNER}State, ${emitter.type(failures.get(command)!.root)}>` : `${OWNER}State`;
    const declaration = `${command.wire}(${input}): Promise<${reply}>;`;
    dts.line(`  ${declaration.replaceAll(OWNER, shortName)}${mapped("command", command.wire, command.wire)}`);
    record(command, { key: "command", section: "client", declaration, documentation, comparable: `${command.wire}(${comparableInput}): Promise<${reply}>;` });
    let availability = !command.keyed ? undefined
      : `Whether \`${command.wire}\` can run for \`${command.parameter}\`: false when .NET finds no object for it.`;
    if (developmentOnly(command) && hasArgument(command))
      availability = withNote(availability, "Development-only (`[RunicDevelopmentOnly]`): false in a trimmed or NativeAOT publish, which leaves the command out.");
    dts.doc("  ", availability);
    if (hasArgument(command)) {
      dts.line(`  can${command.name}(${input}): Promise<boolean>;${mapped("command", command.wire, `can${command.name}`)}`);
      record(command, {
        key: "can-method", section: "client", declaration: `can${command.name}(${input}): Promise<boolean>;`, documentation: availability,
        comparable: `can${command.name}(${comparableInput}): Promise<boolean>;`,
      });
    }
  }
  for (const { member, method, action } of regionMethods) {
    const documentation = action === "select"
      ? `Selects the region of \`${member.wire}\` with \`key\`. The departure guards of the region left decide; resolves with the request's outcome once the state includes it (\`region-collections/1\`).`
      : `Closes the region of \`${member.wire}\` with \`key\` and removes it. Its departure guards decide; resolves with the request's outcome once the state includes it (\`region-collections/1\`).`;
    dts.doc("  ", documentation);
    dts.line(`  ${method}(key: string): Promise<${Ref("RegionOutcome")}>;${mapped("state", member.wire, method)}`);
    record(member, { key: "region-" + action, section: "client", declaration: `${method}(key: string): Promise<${Ref("RegionOutcome")}>;`, documentation });
  }
  for (const operation of operations) {
    const command = operation.command;
    const input = hasArgument(command) ? `${command.parameter}: ${inputType(command)}, ` : "";
    dts.line("  /**");
    dts.line(`   * Starts \`${operation.name}\` as an operation that the page can observe, cancel and recover. \`options.requestId\` names it;`);
    dts.line("   * the default is a random id. A start with a request id that .NET already admitted returns that operation.");
    if (command.keyed) dts.line(`   * A new start is rejected with reason \`unresolved-key\` when .NET finds no object for \`${command.parameter}\`.`);
    if (developmentOnly(command)) dts.line("   * Development-only (`[RunicDevelopmentOnly]`): a trimmed or NativeAOT publish rejects it with reason `unavailable`.");
    dts.line("   */");
    dts.line(`  start${operation.name}(${input}options?: ${Ref("BridgeOperationStartOptions")}): Promise<${shortName}${operation.name}Operation>;${mapped("command", command.wire, `start${operation.name}`)}`);
    dts.line(`  /** Returns the \`${operation.name}\` operation started with \`requestId\`, for example after the page reloaded. */`);
    dts.line(`  recover${operation.name}(requestId: string): Promise<${shortName}${operation.name}Operation>;${mapped("command", command.wire, `recover${operation.name}`)}`);
    record(command, {
      key: "operation", section: "operations",
      declaration: `export interface ${OWNER}${operation.name}Operation extends ${Ref(operation.isStream ? "BridgeStreamOperation" : "BridgeOperation")}<${typeArguments(operation)}> {}`,
    });
    record(command, {
      key: "start", section: "client",
      declaration: `start${operation.name}(${input}options?: ${Ref("BridgeOperationStartOptions")}): Promise<${OWNER}${operation.name}Operation>;`,
      documentation: `Starts \`${operation.name}\` as an operation that the page can observe, cancel and recover. \`options.requestId\` names it;\n`
        + "the default is a random id. A start with a request id that .NET already admitted returns that operation."
        + (command.keyed ? `\nA new start is rejected with reason \`unresolved-key\` when .NET finds no object for \`${command.parameter}\`.` : ""),
      comparable: `start${operation.name}(${hasArgument(command) ? `${PARAMETER}: ${inputType(command)}, ` : ""}options?: ${Ref("BridgeOperationStartOptions")}): Promise<${OWNER}${operation.name}Operation>;`,
    });
    record(command, {
      key: "recover", section: "client",
      declaration: `recover${operation.name}(requestId: string): Promise<${OWNER}${operation.name}Operation>;`,
      documentation: `Returns the \`${operation.name}\` operation started with \`requestId\`, for example after the page reloaded.`,
    });
  }
  dts.line("}");
  dts.line();
  const connectAt = Ref(`connect${shortName}At`);
  const sharedReferences = context.sharedReferences.map(item => item.reference);
  for (const kind of view.kinds) {
    const functionName = upperFirst(kind);
    const referenceType = `${functionName}PageReference`;
    const cacheName = Ref(`page${functionName}References`);
    // A View with shared clients (#839): its reference is each shared reference, which the extends clause checks.
    dts.line((sharedReferences.length === 0 ? `export interface ${referenceType} {` : `export interface ${referenceType} extends ${sharedReferences.join(", ")} {`)
      + mapped(null, "", referenceType));
    dts.line(`  readonly kind: "${kind}";`);
    dts.line(`  connect(): Promise<${clientName}>;`);
    if (sharedReferences.length > 0) {
      dts.line("  /** Type-only: the shared page references (`[RunicSharedClient]`) whose components a `ViewRegistry` accepts for this kind. */");
      dts.line(`  readonly [${Ref("sharedPageReferences")}]?: ${sharedReferences.join(" | ")};`);
    }
    dts.line("}");
    dts.line();
    ts.line(`const ${cacheName} = ${Ref("viewReferences")}<${referenceType}>(id => ({ kind: "${kind}", connect: () => ${connectAt}(\`content\${id}\`) }));`);
    ts.line(`export function page${functionName}(id: string): ${referenceType} { return ${cacheName}(id); }`);
    ts.line();
  }
  const contentDecoder = (member: ContentBinding, ref: string) =>
    member.pages.map(page => `${ref}.kind === "${page.kind}" ? page${upperFirst(page.kind)}(${ref}.id)`).join(" : ")
    + ` : (() => { throw new ${Ref("BridgeError")}("failed", ${csQuote(`Unknown ${member.wire} kind.`)}); })()`;
  // A decoder is an expression of `wire`, or a block body.
  const memberDecoders: { member: string; decoder: string; block: boolean }[] = [];
  const developmentOnlyState = new Set<string>();
  if (hasValidation) memberDecoders.push({ member: "validation", decoder: `${Ref("decodeBridgeValidation")}(wire)`, block: false });
  for (const member of members) {
    if (developmentOnly(member)) { developmentOnlyState.add(member.wire); developmentOnlyState.add(member.wire + "Errors"); }
    if (values.has(member)) memberDecoders.push({ member: member.wire, decoder: emitter.decoder(values.get(member)!.root, "wire"), block: false });
    else if (member.kind === "query-collection") memberDecoders.push({ member: member.wire, decoder: `${Ref("decodeQueryCollectionState")}(wire)`, block: false });
    else if (member.kind === "content") {
      const decoder = `{ const reference = ${Ref("contentReference")}(wire); return ${contentDecoder(member, "reference")}; }`;
      memberDecoders.push({ member: member.wire, decoder: member.nullable ? `{ if (wire === null) return null; ${decoder.slice(2)}` : decoder, block: true });
    } else if (member.kind === "region-collection")
      memberDecoders.push({ member: member.wire, decoder: `${Ref("regionCollection")}(wire, item => { const reference = ${Ref("contentReference")}(item); return ${contentDecoder(member, "reference")}; })`, block: false });
    else if (member.kind === "content-list") {
      const decoder = `bridgeWire.array(wire, item => { const reference = ${Ref("contentReference")}(item); return ${contentDecoder(member, "reference")}; })`;
      memberDecoders.push({ member: member.wire, decoder: member.nullable ? `wire === null ? null : ${decoder}` : decoder, block: false });
    }
    if (hasErrors) memberDecoders.push({ member: member.wire + "Errors", decoder: "bridgeWire.array(wire, item => bridgeWire.string(item))", block: false });
  }
  for (const command of commands) {
    if (!hasArgument(command)) {
      memberDecoders.push({ member: `can${command.name}`, decoder: "bridgeWire.boolean(wire)", block: false });
      if (developmentOnly(command)) developmentOnlyState.add(`can${command.name}`);
    }
    if (command.executionState) {
      memberDecoders.push({ member: `is${command.name}Executing`, decoder: "bridgeWire.boolean(wire)", block: false });
      if (developmentOnly(command)) developmentOnlyState.add(`is${command.name}Executing`);
    }
  }
  // D072.13: a development-only member may be absent from the state.
  for (const entry of memberDecoders.filter(entry => developmentOnlyState.has(entry.member)))
    entry.decoder = entry.block ? `{ if (wire === undefined) return undefined; ${entry.decoder.slice(2)}` : `wire === undefined ? undefined : ${arrowBody(entry.decoder)}`;
  if (hasContent) {
    ts.line(`function ${Ref("contentReference")}(wire: unknown): { readonly kind: string; readonly id: string } {`);
    ts.line('  return bridgeWire.object(wire, value => ({ kind: bridgeWire.string(value["kind"]), id: bridgeWire.string(value["id"]) }));');
    ts.line("}");
  }
  ts.line(`const ${Ref("members")}: ${Ref("BridgeMemberDecoders")}<${shortName}State> = {`);
  for (const { member, decoder, block } of memberDecoders)
    ts.line(block ? `  ${tsLiteralKey(member)}: (wire: unknown) => ${decoder},` : `  ${tsLiteralKey(member)}: (wire: unknown) => ${arrowBody(decoder)},`);
  ts.line("};");
  ts.line(`const ${Ref("bridgeContract")} = "${fullName}:${view.fingerprint}";`);
  if (interactions.length > 0) {
    ts.line(`const ${Ref("interactionDefinitions")} = ${Ref("defineInteractions")}({`);
    for (const { item, input, output } of interactions) {
      const typedValue = `(value as ${emitter.type(output.root)})`;
      ts.line(`  ${item.wire}: { contract: "${item.contract}", decodeInput: (value: unknown) => ${arrowBody(emitter.decoder(input.root, "value"))}, encodeOutput: (value: unknown) => ${arrowBody(emitter.encoder(output.root, typedValue))} },`);
    }
    ts.line("});");
  }
  if (requests.length > 0) {
    ts.line(`const ${Ref("requestDefinitions")} = ${Ref("defineRequests")}({`);
    for (const { item, input } of requests)
      ts.line(`  ${item.wire}: { contract: "${item.contract}", decodeInput: (value: unknown) => ${arrowBody(emitter.decoder(input.root, "value"))} },`);
    ts.line("});");
  }
  if (needsCheckedWriter) ts.line(`const ${Ref("checkedFieldNames")} = [${checked.map(member => csQuote(member.wire)).join(", ")}];`);
  for (const operation of operations) {
    const failure = operation.decodeFailure === undefined ? "" : `, decodeFailure: value => ${arrowBody(operation.decodeFailure)}`;
    ts.line(`const ${Ref(operation.member + "Operation")}: ${Ref("BridgeOperationCodec")}<${typeArguments(operation)}> = { decode: value => ${arrowBody(operation.decodeResult)}${failure}${operation.isStream ? ", stream: true" : ""} };`);
  }
  ts.line();
  if (queries.length > 0) {
    ts.line(`const ${Ref("queryDefinitions")} = {`);
    for (const { member, rows } of queries)
      ts.line(`  ${tsLiteralKey(member.wire)}: ${Ref("defineQueryCollection")}<${emitter.type(rows.root.element!)}>(wire => ${arrowBody(emitter.decoder(rows.root.element!, "wire"))}),`);
    ts.line("};");
  }
  const hasDeltaFields = incremental.length > 0 || textFields.length > 0;
  if (hasDeltaFields) {
    ts.line(`const ${Ref("collectionDefinitions")} = ${Ref("defineCollections")}({`);
    for (const member of incremental) {
      const root = values.get(member)!.root;
      ts.line(`  ${tsLiteralKey(member.wire)}: ${Ref("defineCollection")}<${emitter.type(root.element!)}>(wire => ${arrowBody(emitter.decoder(root.element!, "wire"))}, item => String(${tsAccess("item", member.collectionKey!)})),`);
    }
    for (const member of textFields) {
      const root = values.get(member)!.root;
      ts.line(`  ${tsLiteralKey(member.wire)}: ${Ref("defineText")}(${root.kind === "string" && root.nullable ? "true" : ""}),`);
    }
    ts.line("});");
  }
  const connectOptions = `contract: ${Ref("bridgeContract")}, route, ${Shorthand("members")}`
    + (hasDeltaFields ? `, collections: ${Ref("collectionDefinitions")}` : "")
    + (needsCheckedWriter ? `, checkedFields: ${Ref("checkedFieldNames")}` : "")
    + (textMembers.length > 0 ? `, texts: [${textMembers.map(member => csQuote(member.wire)).join(", ")}]` : "")
    + (scopedCommands.length > 0 ? `, scopedCommands: [${scopedCommands.map(member => csQuote(member)).join(", ")}]` : "")
    + (interactions.length > 0 ? `, interactions: ${Ref("interactionDefinitions")}, handlers: options?.handlers` : "")
    + (requests.length > 0 ? `, requests: ${Ref("requestDefinitions")}` : "")
    + (operations.length > 0 ? `, operations: ${Ref("bridgeOperations")}` : "");
  // A View with interactions takes their handlers when it connects, so the producer can ask them from the first request.
  const optionsType = interactions.length > 0 ? `${shortName}ConnectOptions` : Ref("BridgeConnectOptions");
  if (interactions.length > 0) {
    ts.line(`/** Options of \`connect${shortName}\`. */`);
    ts.line(`export interface ${shortName}ConnectOptions extends ${Ref("BridgeConnectOptions")} {`);
    ts.line("  /**");
    ts.line("   * Interaction handlers, registered before the client opens the View, so .NET can ask them from the first");
    ts.line("   * request on. Handlers registered later through `interactions` replace them.");
    ts.line("   */");
    ts.line("  readonly handlers?: {");
    for (const { item, input, output } of interactions)
      ts.line(`    readonly ${item.wire}?: (input: ${emitter.type(input.root)}, context: ${shortName}InteractionContext) => ${emitter.type(output.root)} | Promise<${emitter.type(output.root)}>;`);
    ts.line("  };");
    ts.line("}");
  }
  ts.doc("", `Connects a ${shortName} client. It waits up to \`options.timeout\` milliseconds (default 5000) for the\nhost Bridge to connect, then rejects with a \`timeout\` BridgeError. \`options.signal\` abandons the connection.\nA \`using\` declaration disposes the client where the engine has \`Symbol.dispose\`.`
    + (interactions.length > 0 ? "\n`options.handlers` answers interactions from the first request on." : ""));
  // globalThis.Disposable cannot be shadowed by a named type of the same name.
  ts.line(`export function connect${shortName}(options?: ${optionsType}): Promise<${clientName} & globalThis.Disposable> { return ${connectAt}("${prefix}", options); }`);
  ts.line(`async function ${connectAt}(route: string, options?: ${optionsType}): Promise<${clientName} & globalThis.Disposable> {`);
  ts.line(`  const view = await ${Ref("connectView")}<${shortName}State>({ ${connectOptions}, timeout: options?.timeout, signal: options?.signal });`);
  ts.line(`  return ${Ref("disposable")}<${clientName}>({`);
  ts.line("    get snapshot() { return view.snapshot; },");
  ts.line("    subscribe: view.subscribe,");
  ts.line("    dispose: view.dispose,");
  if (usesUploads) ts.line(`    upload: ${Ref("upload")},`);
  if (interactions.length > 0) ts.line(`    interactions: view.interactions as unknown as ${shortName}Interactions,`);
  if (requests.length > 0) ts.line(`    requests: view.requests as unknown as ${shortName}ViewRequests,`);
  if (queries.length > 0)
    ts.line("    queries: { " + queries.map(({ member }) =>
      `${tsLiteralKey(member.wire)}: view.queryCollection(${csQuote(member.wire)}, ${Ref("queryDefinitions")}[${csQuote(member.wire)}])`).join(", ") + " },");
  if (needsCheckedWriter)
    ts.line(`    fieldBaseline<K extends keyof ${shortName}CheckedFields>(field: K) { return view.fieldBaseline(field) as ${Ref("FieldBaseline")}<${shortName}CheckedFields[K]>; },`);
  if (hasFieldPaths) {
    ts.line(`    writePath<T>(member: string, path: readonly string[], write: ${Ref("BridgeFieldWriteRequest")}, decode: (wire: unknown) => T) { return view.write<T>(member, { ...write, path }, decode); },`);
    ts.line("    fieldPathVersion() { return view.fieldPathVersion(); },");
  }
  const isInt = (member: ValueMember) => member.value.type.kind === "int32" && member.value.type.nullable !== true;
  for (const member of settable) {
    // A setter sends the member's value as an edit.
    const wire = csQuote(member.wire);
    if (isInt(member)) ts.line(`    async set${member.name}(value) { if (!Number.isSafeInteger(value)) throw new RangeError("Value must be an integer."); return view.set(${wire}, value); },`);
    else ts.line(`    async set${member.name}(value) { return view.set(${wire}, ${emitter.encoder(values.get(member)!.root, "value")}); },`);
  }
  for (const member of checked) {
    ts.line(`    async write${member.name}(value, options) {`);
    ts.line('      if (typeof options?.requestId !== "string" || options.requestId.trim().length === 0) throw new RangeError("A checked field requestId is required.");');
    ts.line("      const baseline = options.baseline;");
    const type = member.value.type;
    const check = isInt(member)
      ? 'typeof value !== "number" || !Number.isSafeInteger(value) || !baseline || typeof baseline.value !== "number" || !Number.isSafeInteger(baseline.value)'
      : type.kind === "boolean" && type.nullable !== true ? 'typeof value !== "boolean" || !baseline || typeof baseline.value !== "boolean"'
      : type.kind === "string" && type.nullable === true ? '(value !== null && typeof value !== "string") || !baseline || (baseline.value !== null && typeof baseline.value !== "string")'
      : type.kind === "string" ? 'typeof value !== "string" || !baseline || typeof baseline.value !== "string"' : "!baseline";
    ts.line(`      if (${check} || !Number.isSafeInteger(baseline.version) || baseline.version < 0) throw new RangeError("A checked field value and baseline are required.");`);
    const root = values.get(member)!.root;
    ts.line(`      return view.write<${valueType(member)}>(${csQuote(member.wire)}, { requestId: options.requestId, expectedVersion: baseline.version, expectedValue: ${emitter.encoder(root, "baseline.value")}, value: ${emitter.encoder(root, "value")} }, value => ${arrowBody(emitter.decoder(root, "value"))});`);
    ts.line("    },");
  }
  for (const command of commands) {
    const member = csQuote(command.wire);
    const parameter = command.parameter ?? "input";
    const input = inputs.has(command) ? emitter.encoder(inputs.get(command)!.root, parameter) : command.input?.untyped ? parameter : null;
    const argument = input === null ? "" : `, ${input}`;
    ts.line(failures.has(command)
      ? `    async ${command.wire}(${hasArgument(command) ? parameter : ""}) { return view.commandOutcome(${member}, value => ${arrowBody(emitter.decoder(failures.get(command)!.root, "value"))}${argument}); },`
      : `    async ${command.wire}(${hasArgument(command) ? parameter : ""}) { return view.command(${member}${argument}); },`);
    if (hasArgument(command)) ts.line(`    async can${command.name}(${parameter}) { return view.can(${member}${argument}); },`);
  }
  for (const { member, method, action } of regionMethods) ts.line(`    async ${method}(key) { return view.region(${csQuote(member.wire)}, "${action}", key); },`);
  for (const operation of operations) {
    const parameter = operation.hasInput ? `${operation.inputName}: ${operation.inputType}, ` : "";
    const member = csQuote(operation.member);
    ts.line(`    start${operation.name}(${parameter}options?: ${Ref("BridgeOperationStartOptions")}) { return view.start(${member}, options?.requestId ?? globalThis.crypto.randomUUID(), ${operation.hasInput ? operation.encodeInput : "undefined"}, ${Ref(operation.member + "Operation")}); },`);
    ts.line(`    recover${operation.name}(requestId: string) { return view.recover(${member}, requestId, ${Ref(operation.member + "Operation")}); },`);
  }
  ts.line("  });");
  ts.line("}");
  // The field descriptors ({View}Fields). A development-only member (D072.13) has none: a publish may leave it out.
  const fieldMembers = checked.filter(member => !developmentOnly(member));
  const rowFieldPaths = [...rowPaths].filter(([member, list]) => list.length > 0 && !developmentOnly(member));
  appendFieldDescriptors();

  const body = ts.toString();
  // Application-facing types and errors come from the package root, the runtime only generated code calls from its
  // /generated entry; only the protocol parts this View uses are imported.
  const publicImports: string[] = [];
  if (body.includes(`new ${Ref("BridgeError")}(`)) publicImports.push(Import("BridgeError"));
  if (hasValidation) publicImports.push("type " + Import("BridgeValidationState"));
  publicImports.push("type " + Import("BridgeConnectOptions"));
  publicImports.push("type " + Import("ViewClient"));
  if (sharedReferences.length > 0) publicImports.push("type " + Import("sharedPageReferences"));
  if (needsCheckedWriter || hasFieldPaths) publicImports.push(...["FieldBaseline", "FieldWriteOptions", "FieldWriteReceipt"].map(name => "type " + Import(name)));
  if (fieldMembers.length > 0 || rowFieldPaths.length > 0) publicImports.push("type " + Import("BridgeField"));
  if (rowFieldPaths.length > 0) publicImports.push("type " + Import("BridgeRowFields"));
  if (failures.size > 0) publicImports.push("type " + Import("BridgeOutcome"));
  if (operations.some(operation => !operation.isStream)) publicImports.push("type " + Import("BridgeOperation"));
  if (operations.length > 0) publicImports.push("type " + Import("BridgeOperationStartOptions"));
  if (operations.some(operation => operation.isStream)) publicImports.push("type " + Import("BridgeStreamOperation"));
  if (interactions.length > 0) publicImports.push("type " + Import("BridgeInteractionContext"));
  if (requests.length > 0) publicImports.push("type " + Import("BridgeViewRequestContext"));
  if (regionCollections.length > 0) publicImports.push("type " + Import("RegionCollection"), "type " + Import("RegionOutcome"));
  if (queries.length > 0) publicImports.push("type " + Import("QueryCollection"), "type " + Import("QueryCollectionState"));
  const runtimeImports = [Import("connectView"), Import("disposable"), Import("viewReferences"), "type " + Import("BridgeMemberDecoders")];
  if (operations.length > 0) runtimeImports.push("type " + Import("BridgeOperationCodec"));
  if (incremental.length > 0) runtimeImports.push(Import("defineCollection"));
  if (hasDeltaFields) runtimeImports.push(Import("defineCollections"));
  if (queries.length > 0) runtimeImports.push(Import("defineQueryCollection"), Import("decodeQueryCollectionState"));
  if (textFields.length > 0) runtimeImports.push(Import("defineText"));
  if (interactions.length > 0) runtimeImports.push(Import("defineInteractions"));
  if (requests.length > 0) runtimeImports.push(Import("defineRequests"));
  if (operations.length > 0) runtimeImports.push(Import("bridgeOperations"));
  if (hasValidation) runtimeImports.push(Import("decodeBridgeValidation"));
  if (regionCollections.length > 0) runtimeImports.push(Import("regionCollection"));
  if (hasValidation && fieldMembers.length > 0) runtimeImports.push(Import("fieldErrors"), Import("fieldValidating"));
  // In this order, so that a client without development-only members imports as before.
  if (fieldMembers.some(member => nestedPaths.has(member)) || rowFieldPaths.length > 0) runtimeImports.push(Import("fieldPath"));
  if (hasFieldPaths) runtimeImports.push("type " + Import("BridgeFieldWriteRequest"));
  if (rowFieldPaths.length > 0) runtimeImports.push(Import("rowFields"));
  if (usesUploads) runtimeImports.push(Import("upload"));
  // <view>.ts imports what its own code uses and re-exports the contract's types; the contract imports what its
  // declarations use.
  const contractBody = dts.toString().trimEnd() + "\n";
  const contractNames = [...declaredTopLevel(contractBody)];
  const code = withoutComments(body);
  const declarations = withoutComments(contractBody);
  const unmarked = (specifier: string) => specifier.replaceAll("type ", "").replace(/^[\u0003\u0004\u0005]+|[\u0003\u0004\u0005]+$/g, "");
  const uses = (text: string, specifier: string) => text.includes(Ref(unmarked(specifier)));
  // An import only the contract uses moves there; a type neither uses is dropped.
  const moved = (specifier: string) => !uses(body, specifier) && (uses(contractBody, specifier) || specifier.startsWith("type "));
  const keep = (list: string[]) => list.filter(specifier => !moved(specifier));
  const publicKept = keep(publicImports);
  const runtimeKept = keep(runtimeImports);
  const module = lowerFirst(shortName);
  const specifier = `./${module}.contract.js`;
  const out = new Builder();
  out.line("// <auto-generated />");
  out.line(publicKept.every(name => name.startsWith("type "))
    ? `import type { ${publicKept.map(name => name.slice(5)).join(", ")} } from "@runic-artifex/views";`
    : `import { ${publicKept.join(", ")} } from "@runic-artifex/views";`);
  out.line(`import { ${runtimeKept.join(", ")} } from "@runic-artifex/views/generated";`);
  // A namespace import of the decoder module lets every bundler drop unused decoders.
  if (body.includes("bridgeWire.")) out.line('import * as bridgeWire from "@runic-artifex/views/generated/wire";');
  for (const { module: imported, functions, references: types } of contentImports) {
    const used = [...functions, ...types.filter(reference => mentions(code, reference)).map(reference => "type " + reference)];
    out.line(`import { ${used.join(", ")} } from "${modules.specifier(imported, ".js")}";`);
  }
  for (const item of context.sharedReferences.filter(item => mentions(code, item.reference)))
    out.line(`import type { ${item.reference} } from "${modules.specifier(item.module, ".js")}";`);
  const contractImports = contractNames.filter(name => mentions(code, name));
  if (contractImports.length > 0) out.line(`import type { ${contractImports.join(", ")} } from "${specifier}";`);
  out.line(`export type { ${contractNames.join(", ")} } from "${specifier}";`);
  out.line(NAMED_TYPE_IMPORTS);
  out.line();
  out.append(body);

  const contract = new Builder();
  contract.line("// <auto-generated />");
  const publicTypes = ["BridgeValidationState", "ViewClient", "sharedPageReferences", "viewCommands", "FieldBaseline", "FieldWriteOptions",
    "FieldWriteReceipt", "BridgeOutcome", "BridgeOperation", "BridgeOperationStartOptions", "BridgeStreamOperation",
    "BridgeInteractionContext", "BridgeViewRequestContext", "RegionCollection", "RegionOutcome", "QueryCollection", "QueryCollectionState"];
  const runtimeTypes = ["BridgeFieldWriteRequest"];
  for (const [names, from] of [[publicTypes, "@runic-artifex/views"], [runtimeTypes, "@runic-artifex/views/generated"]] as const) {
    const used = names.filter(name => contractBody.includes(Ref(name)));
    if (used.length > 0) contract.line(`import type { ${used.map(Import).join(", ")} } from "${from}";`);
  }
  if (contractBody.includes("bridgeWire.")) contract.line('import type * as bridgeWire from "@runic-artifex/views/generated/wire";');
  for (const { module: imported, references: types } of contentImports) {
    const used = types.filter(reference => mentions(declarations, reference));
    if (used.length > 0) contract.line(`import type { ${used.join(", ")} } from "${modules.specifier(imported, ".contract.js")}";`);
  }
  for (const item of context.sharedReferences.filter(item => mentions(declarations, item.reference)))
    contract.line(`import type { ${item.reference} } from "${modules.specifier(item.module, ".js")}";`);
  contract.line(NAMED_TYPE_IMPORTS);
  contract.line();
  contract.append(contractBody);
  // Every own binding the declarations name is one of the types imported above.
  const unknown = references(contractBody).find(name => !publicTypes.includes(name) && !runtimeTypes.includes(name));
  if (unknown !== undefined) throw new Error(`${simpleName}: the generated client contract uses ${unknown}, which it does not import.`);

  modules.add(`${module}.ts`, out.toString(), fullName);
  modules.add(`${module}.contract.d.ts`, contract.toString(), fullName);
  const mockFieldPaths = [...nestedPaths, ...[...rowPaths].filter(([, list]) => list.length > 0)]
    .map(([member, list]) => ({ member: member.wire, paths: list.map(leaf => leaf.path) }));
  modules.add(`${module}.mock.ts`, emitMock(emitter, view, { values, inputs, results, failures, interactions, requests, queries, fieldPaths: mockFieldPaths }), fullName);

  // Field descriptors (F1): one BridgeField per checked member, which useField, injectField and createFieldController
  // bind an input to, and the descriptors of the field paths below members and rows (field-paths/1).
  function appendFieldDescriptors(): void {
    if (fieldMembers.length === 0 && rowFieldPaths.length === 0) return;
    ts.line();
    ts.doc("", `Descriptors of the ${shortName} ViewModel's settable members, for \`useField\`, \`injectField\` and\n\`createFieldController\`.`
      + (hasFieldPaths ? "\nFields below a member (`address.street`) and the cells of editable rows (`lines.at(key).quantity`) are\nchecked fields too (field-paths/1)." : ""));
    ts.line(`export const ${shortName}Fields: {`);
    for (const member of fieldMembers)
      ts.line(`  readonly ${tsPropertyName(member.wire)}: ${Ref("BridgeField")}<${clientName}, ${shortName}State, ${valueType(member)}>`
        + (nestedPaths.has(member) ? " & " + leafTypes(nestedPaths.get(member)!, []) : "") + ";");
    for (const [member, list] of rowFieldPaths)
      ts.line(`  readonly ${tsPropertyName(member.wire)}: ${Ref("BridgeRowFields")}<${leafTypes(list, [])}>;`);
    ts.line("} = {");
    for (const member of fieldMembers) {
      const wire = member.wire;
      const name = csQuote(wire);
      const root = values.get(member)!.root;
      ts.line(`  ${tsLiteralKey(wire)}: {`);
      ts.line(`    name: ${name}, ${fieldTraits(root, member.constraints)},`);
      ts.line(`    read: state => ${tsAccess("state", wire)},`);
      ts.line(`    write: (client, value) => client.set${member.name}(value),`);
      ts.line(`    checked: (client, value, options) => client.write${member.name}(value, options),`);
      ts.line(`    baseline: client => client.fieldBaseline(${name}),`);
      if (hasValidation) {
        ts.line(`    errors: state => ${Ref("fieldErrors")}(state.validation, ${name}${hasErrors ? ", " + tsAccess("state", wire + "Errors") : ""}),`);
        ts.line(`    validating: state => ${Ref("fieldValidating")}(state.validation, ${name}),`);
      }
      const nested = nestedPaths.get(member);
      if (nested)
        for (const line of leafValues(nested, [], wire, `container: state => ${tsAccess("state", wire)}`,
          root.nullable ? `NonNullable<${valueType(member)}>` : valueType(member)))
          ts.line("    " + line);
      ts.line("  },");
    }
    for (const [member, list] of rowFieldPaths) {
      const wire = member.wire;
      const row = `row: { key, rows: state => ${tsAccess("state", wire)}, keyOf: item => String(${tsAccess("item", member.collectionKey!)}) }`;
      ts.line(`  ${tsLiteralKey(wire)}: ${Ref("rowFields")}(${csQuote(wire)}, key => ({`);
      for (const line of leafValues(list, [], wire, row, emitter.type(values.get(member)!.root.element!))) ts.line("    " + line);
      ts.line("  })),");
    }
    ts.line("};");
  }

  // field-paths/1: the type of the descriptors below prefix, such as { readonly street: BridgeField<…> }.
  function leafTypes(list: readonly Leaf[], prefix: readonly string[]): string {
    const items = list.filter(leaf => leaf.path.length === prefix.length + 1 && startsWith(leaf.path, prefix)).map(leaf => {
      const children = list.some(other => other.path.length > leaf.path.length && startsWith(other.path, leaf.path));
      return `readonly ${tsPropertyName(leaf.path[leaf.path.length - 1]!)}: ${Ref("BridgeField")}<${clientName}, ${shortName}State, ${emitter.type(leaf.chain[leaf.chain.length - 1]!.type)}>`
        + (children ? " & " + leafTypes(list, leaf.path) : "");
    });
    return `{ ${items.join("; ")} }`;
  }

  // field-paths/1: the descriptors below prefix, one fieldPath per path; a path with paths below it spreads its
  // descriptor and adds theirs.
  function leafValues(list: readonly Leaf[], prefix: readonly string[], member: string, locate: string, containerType: string): string[] {
    const lines: string[] = [];
    for (const leaf of list.filter(leaf => leaf.path.length === prefix.length + 1 && startsWith(leaf.path, prefix))) {
      const node = leaf.chain[leaf.chain.length - 1]!.type;
      const type = emitter.type(node);
      let read = "value";
      leaf.chain.forEach((step, index) => {
        const optional = index > 0 && leaf.chain[index - 1]!.type.nullable;
        read = isTsIdentifier(step.wire) && step.wire !== "__proto__"
          ? `${read}${optional ? "?." : "."}${step.wire}`
          : `${read}${optional ? "?." : ""}[${csQuote(step.wire)}]`;
      });
      if (leaf.chain.slice(0, -1).some(step => step.type.nullable)) read += ` as ${type}`;
      const spec = `${Ref("fieldPath")}<${clientName}, ${shortName}State, ${type}, ${containerType}>({ member: ${csQuote(member)}, `
        + `path: [${leaf.path.map(segment => csQuote(segment)).join(", ")}], ${locate}, `
        + `read: (value: ${containerType}) => ${read}, `
        + `encode: (value: ${type}) => ${arrowBody(emitter.encoder(node, "value"))}, `
        + `decode: (wire: unknown) => ${arrowBody(emitter.decoder(node, "wire"))}, `
        + fieldTraits(node, leaf.constraints)
        + (hasValidation ? ", validation: state => state.validation" : "") + " })";
      const children = leafValues(list, leaf.path, member, locate, containerType);
      const key = tsLiteralKey(leaf.path[leaf.path.length - 1]!);
      if (children.length === 0) { lines.push(`${key}: ${spec},`); continue; }
      lines.push(`${key}: { ...${spec},`);
      for (const child of children) lines.push("  " + child);
      lines.push("},");
    }
    return lines;
  }

  // The leaves of field paths below an object node, found by following each path through its object members.
  function leaves(owner: Node, paths: readonly FieldPath[]): Leaf[] {
    return paths.map(item => {
      const chain: Node["members"] = [];
      let node = owner;
      for (const segment of item.path) {
        const step = node.members.find(candidate => candidate.wire === segment);
        if (!step) throw new Error(`${simpleName}: the field path ${item.path.join(".")} names no member ${segment}.`);
        chain.push(step);
        node = step.type;
      }
      return { path: item.path, chain, constraints: item.constraints };
    });
  }
}

const startsWith = (path: readonly string[], prefix: readonly string[]) => prefix.every((segment, index) => path[index] === segment);

/** A field's kind and the traits that go with it, from its wire type and its constraints. */
function fieldTraits(root: Node, constraints: Constraints | undefined): string {
  const kinds: Record<string, string> = {
    boolean: "boolean", int8: "number", uint8: "number", int16: "number", uint16: "number", int32: "number", uint32: "number", float32: "number", float64: "number",
    int64: "bigint", uint64: "bigint", "big-integer": "bigint", decimal: "decimal", "local-date": "date", "local-time": "time", "date-time": "datetime",
    "offset-date-time": "datetime", string: "text", uuid: "text", duration: "text", enum: "choice",
  };
  let traits = `kind: "${kinds[root.kind] ?? "value"}"`;
  if (root.nullable) traits += ", nullable: true";
  if (["int8", "uint8", "int16", "uint16", "int32", "uint32"].includes(root.kind)) traits += ", integer: true";
  if (root.kind === "offset-date-time") traits += ", offset: true";
  if (constraints) {
    const parts: string[] = [];
    const bound = (value: number | string) => (typeof value === "number" ? dotnetNumber(value) : csQuote(value));
    if (constraints.required) parts.push("required: true");
    if (constraints.minLength !== undefined) parts.push(`minLength: ${constraints.minLength}`);
    if (constraints.maxLength !== undefined) parts.push(`maxLength: ${constraints.maxLength}`);
    if (constraints.min !== undefined) parts.push(`min: ${bound(constraints.min)}`);
    if (constraints.max !== undefined) parts.push(`max: ${bound(constraints.max)}`);
    if (constraints.minExclusive) parts.push("minExclusive: true");
    if (constraints.maxExclusive) parts.push("maxExclusive: true");
    if (constraints.pattern !== undefined) parts.push(`pattern: ${relaxedQuote(constraints.pattern)}`);
    if (parts.length > 0) traits += `, constraints: { ${parts.join(", ")} }`;
  }
  if (root.kind === "enum") {
    const definition = root.definition as Extract<TypeDefinition, { kind: "enum" }>;
    const written = new Set<string>();
    traits += `, choices: [${definition.cases.filter(item => !written.has(item.value) && written.add(item.value)).map(item => csQuote(item.wire)).join(", ")}]`;
  }
  return traits;
}
