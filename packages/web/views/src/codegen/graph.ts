// Value graphs and named types: BridgeTypeGraph's TypeScript half and TypeScriptNamedTypes, over the IR.
import type { ContractIr, Direction, ObjectMember, NameHints, TypeDefinition, TypeRef, Value } from "./ir.js";
import { Builder, NAME, WORD, csQuote, ordinal } from "./text.js";
import { declaredTopLevel } from "./bindings.js";

export interface Node {
  id: number;
  kind: string;
  nullable: boolean;
  direction: Direction;
  ref?: string;
  definition?: TypeDefinition;
  element?: Node;
  value?: Node;
  members: { wire: string; optional: boolean; absent: boolean; doc: string | undefined; type: Node }[];
  cases: { tag: string; type: Node }[];
  typeRef?: TypeRef;
}

/** A value's graph: its root and every node, in id order. */
export interface Graph { root: Node; nodes: Node[] }

/** Builds a value's graph with the node ids BridgeTypeGraph assigns: pre-order, named types expanded. */
export function graphOf(ir: ContractIr, value: Value): Graph {
  const nodes: Node[] = [];
  const build = (ref: TypeRef, objectRef?: string): Node => {
    const node: Node = { id: nodes.length, kind: ref.kind, nullable: ref.nullable === true, direction: value.direction, members: [], cases: [], typeRef: ref };
    nodes.push(node);
    if (ref.kind === "list") node.element = build(ref.element);
    else if (ref.kind === "map") node.value = build(ref.value);
    else if (ref.kind === "named" || objectRef !== undefined) {
      const id = objectRef ?? (ref as { ref: string }).ref;
      const definition = id === "" ? { kind: "object" as const, name: { source: "", base: "", qualifiers: [] }, members: [] } : ir.types[id];
      if (!definition) throw new Error(`Unknown type ${id}`);
      node.ref = id;
      node.definition = definition;
      node.kind = definition.kind;
      if (definition.kind === "object")
        for (const member of definition.members) node.members.push(memberNode(member));
      if (definition.kind === "union")
        for (const unionCase of definition.cases)
          node.cases.push({ tag: unionCase.tag, type: build({ kind: "named", ref: unionCase.object ?? "" } as TypeRef, unionCase.object ?? "") });
    }
    return node;
  };
  const memberNode = (member: ObjectMember) =>
    ({ wire: member.wire, optional: member.optional === true, absent: member.absent === true, doc: member.doc, type: build(member.type) });
  const root = build(value.type);
  return { root, nodes };
}

const fromPage = (node: Node) => node.direction !== "to-page";
const toPage = (node: Node) => node.direction !== "from-page";
const mayBeMissing = (member: Node["members"][number]) => member.absent || (member.optional && fromPage(member.type));
const jsonNull = (node: Node) => (node.typeRef as { jsonNull?: boolean }).jsonNull === true;

export const arrowBody = (expression: string) => (expression.trimStart().startsWith("{") ? `(${expression})` : expression);
const arrayElement = (type: string) => (/[ |&{(<]/.test(type) ? `(${type})` : type);
const propertyKey = (name: string) =>
  name.length > 0 && /[\p{L}_$]/u.test(name[0]!) && /^[\p{L}\p{Nd}_$]*$/u.test(name.slice(1)) ? name : csQuote(name);
const literalKey = (name: string) => (name === "__proto__" ? "[" + csQuote(name) + "]" : propertyKey(name));

export class Emitter {
  readonly named: NamedTypes;
  constructor(readonly ir: ContractIr) { this.named = new NamedTypes(this); }

  graph(value: Value): Graph { return graphOf(this.ir, value); }

  type(node: Node): string {
    let type: string;
    switch (node.kind) {
      case "boolean": type = "boolean"; break;
      case "int8": case "uint8": case "int16": case "uint16": case "int32": case "uint32": case "float32": case "float64": type = "number"; break;
      case "int64": case "uint64": case "big-integer": type = "bigint"; break;
      case "decimal": case "string": case "uuid": case "local-date": case "local-time": case "date-time": case "offset-date-time": case "duration": type = "string"; break;
      case "enum": case "object": case "union": type = this.named.reference(node); break;
      case "list": type = `readonly ${arrayElement(this.type(node.element!))}[]`; break;
      case "map": type = `Readonly<Record<string, ${this.type(node.value!)}>>`; break;
      case "custom": type = (node.typeRef as { typescript: { type: string } }).typescript.type; break;
      case "text-reference": type = "bridgeWire.TextReference"; break;
      case "blob": type = 'import("@runic-artifex/views").RunicBlob'; break;
      case "upload": type = 'import("@runic-artifex/views").BlobRef'; break;
      case "json": type = jsonNull(node) ? "unknown" : "NonNullable<unknown>"; break;
      default: throw new Error(`No TypeScript form exists for ${node.kind}.`);
    }
    return node.nullable && node.kind !== "json" ? `${type} | null` : type;
  }

  decoder(node: Node, expression: string): string {
    const ranges: Record<string, string> = {
      int8: "-128, 127", uint8: "0, 255", int16: "-32768, 32767", uint16: "0, 65535",
      int32: "-2147483648, 2147483647", uint32: "0, 4294967295",
    };
    let nonNull: string;
    switch (node.kind) {
      case "boolean": nonNull = `bridgeWire.boolean(${expression})`; break;
      case "int8": case "uint8": case "int16": case "uint16": case "int32": case "uint32":
        nonNull = `bridgeWire.integer(${expression}, ${ranges[node.kind]})`; break;
      case "float32": nonNull = `bridgeWire.float32(${expression})`; break;
      case "float64": nonNull = `bridgeWire.finiteNumber(${expression})`; break;
      case "int64": nonNull = `bridgeWire.bigint(${expression}, "-9223372036854775808", "9223372036854775807")`; break;
      case "uint64": nonNull = `bridgeWire.bigint(${expression}, "0", "18446744073709551615")`; break;
      case "big-integer": nonNull = `bridgeWire.bigint(${expression})`; break;
      case "decimal": nonNull = `bridgeWire.decimal(${expression})`; break;
      case "string": nonNull = `bridgeWire.string(${expression})`; break;
      case "uuid": nonNull = `bridgeWire.uuid(${expression})`; break;
      case "local-date": nonNull = `bridgeWire.localDate(${expression})`; break;
      case "local-time": nonNull = `bridgeWire.localTime(${expression})`; break;
      case "date-time": nonNull = `bridgeWire.dateTime(${expression})`; break;
      case "offset-date-time": nonNull = `bridgeWire.offsetDateTime(${expression})`; break;
      case "duration": nonNull = `bridgeWire.duration(${expression})`; break;
      case "enum": {
        const cases = (node.definition as Extract<TypeDefinition, { kind: "enum" }>).cases;
        nonNull = `bridgeWire.enumName<${this.named.reference(node)}>(${expression}, [${cases.map(item => csQuote(item.wire)).join(", ")}])`;
        break;
      }
      case "list": nonNull = `bridgeWire.array(${expression}, item => ${arrowBody(this.decoder(node.element!, "item"))})`; break;
      case "map": nonNull = `bridgeWire.stringRecord(${expression}, item => ${arrowBody(this.decoder(node.value!, "item"))})`; break;
      case "object": nonNull = `bridgeWire.object<${this.named.reference(node)}>(${expression}, value => ${this.decodeMembers(node, node, "value", null)})`; break;
      case "union": nonNull = this.decodeUnion(node, expression); break;
      case "custom": nonNull = (node.typeRef as { typescript: { decode: string } }).typescript.decode.replaceAll("$value", expression); break;
      case "text-reference": nonNull = `bridgeWire.textReference(${expression})`; break;
      case "json": nonNull = this.json(node, expression); break;
      case "blob": nonNull = `bridgeWire.blob(${expression})`; break;
      case "upload": nonNull = `bridgeWire.blobRef(${expression})`; break;
      default: throw new Error(`No TypeScript decoder exists for ${node.kind}.`);
    }
    return node.nullable && node.kind !== "json" ? `(${expression} === null ? null : ${nonNull})` : nonNull;
  }

  private json(node: Node, expression: string): string {
    return jsonNull(node) ? `bridgeWire.json(${expression})` : `bridgeWire.json(${expression}, false)`;
  }

  private decodeMembers(node: Node, declared: Node, value: string, discriminator: string | null): string {
    const fields: string[] = [];
    if (discriminator !== null) fields.push(`$case: ${csQuote(discriminator)}`);
    let omits = false;
    for (const member of node.members) {
      const access = value + "[" + csQuote(member.wire) + "]";
      const decoded = this.decoder(member.type, access);
      if (!mayBeMissing(member)) fields.push(literalKey(member.wire) + ": " + decoded);
      else if (member.type.nullable && !member.absent) fields.push(`${literalKey(member.wire)}: ${access} === undefined ? null : ${decoded}`);
      else {
        omits = true;
        fields.push(`...(${access} === undefined ? {} : { ${literalKey(member.wire)}: ${decoded} })`);
      }
    }
    const literal = "{ " + fields.join(", ") + (discriminator === null ? "}" : " }");
    return omits ? `(${literal} as ${this.named.reference(declared)})` : discriminator === null ? `(${literal})` : literal;
  }

  private decodeUnion(node: Node, expression: string): string {
    const value = `bridgeDecodedUnion${node.id}`;
    const cases = node.cases.map(item => `case ${csQuote(item.tag)}: return ${this.decodeMembers(item.type, node, value, item.tag)};`).join(" ");
    return `((): ${this.named.reference(node)} => { const ${value}: any = bridgeWire.union(${expression}); switch (${value}.$case) { ${cases} default: throw new RangeError("Unknown union case."); } })()`;
  }

  encoder(node: Node, expression: string): string {
    if (node.kind === "json") return this.json(node, expression);
    const value = node.nullable ? `bridgeNullable${node.id}` : expression;
    let nonNull: string;
    switch (node.kind) {
      case "int64": case "uint64": case "big-integer": nonNull = `${value}.toString()`; break;
      case "duration": nonNull = `bridgeWire.duration(${value})`; break;
      case "local-date": nonNull = `bridgeWire.localDate(${value})`; break;
      case "local-time": nonNull = `bridgeWire.localTime(${value})`; break;
      case "date-time": nonNull = `bridgeWire.dateTime(${value})`; break;
      case "offset-date-time": nonNull = `bridgeWire.offsetDateTime(${value})`; break;
      case "list": nonNull = `${value}.map(item => ${arrowBody(this.encoder(node.element!, "item"))})`; break;
      case "map": nonNull = `Object.fromEntries(Object.entries(${value}).map(([key, item]) => [key, ${this.encoder(node.value!, "item")}]))`; break;
      case "object":
        nonNull = "{ " + node.members.map(member => "[" + csQuote(member.wire) + "]: " + this.encodeMember(member, value + "[" + csQuote(member.wire) + "]")).join(", ") + " }";
        break;
      case "union": nonNull = this.encodeUnion(node, value); break;
      case "custom": nonNull = (node.typeRef as { typescript: { encode: string } }).typescript.encode.replaceAll("$value", value); break;
      case "text-reference": nonNull = `bridgeWire.textReference(${value})`; break;
      case "blob": nonNull = `bridgeWire.encodeBlob(${value})`; break;
      case "upload": nonNull = `bridgeWire.encodeBlobRef(${value})`; break;
      default: nonNull = value;
    }
    return node.nullable ? `(() => { const ${value} = ${expression}; return ${value} === null ? null : ${nonNull}; })()` : nonNull;
  }

  private encodeMember(member: Node["members"][number], expression: string): string {
    if (!mayBeMissing(member)) return this.encoder(member.type, expression);
    const local = `bridgeOptional${member.type.id}`;
    return `(() => { const ${local} = ${expression}; return ${local} === undefined ? undefined : ${this.encoder(member.type, local)}; })()`;
  }

  private encodeUnion(node: Node, expression: string): string {
    const unionValue = `bridgeUnion${node.id}`;
    const cases = node.cases.map(item => {
      const fields = item.type.members.map(member => "[" + csQuote(member.wire) + "]: " + this.encodeMember(member, unionValue + "[" + csQuote(member.wire) + "]")).join(", ");
      return `case ${csQuote(item.tag)}: return { "$case": ${csQuote(item.tag)}${fields.length === 0 ? "" : ", " + fields} };`;
    }).join(" ");
    return `(() => { const ${unionValue}: ${this.named.reference(node)} = ${expression}; bridgeWire.encodeUnion(${unionValue}); switch (${unionValue}.$case) { ${cases} default: throw new RangeError("Unknown union case."); } })()`;
  }

  /** BridgeTypeGraph.WithTypeScriptNote. */
  static withNote(documentation: string | undefined | null, node: Node): string | undefined {
    const note = node.kind === "int64" ? "A C# `long` is a `bigint`: a JavaScript `number` is exact only up to 2^53."
      : node.kind === "uint64" ? "A C# `ulong` is a `bigint`: a JavaScript `number` is exact only up to 2^53."
      : node.kind === "big-integer" ? "A C# `BigInteger` is a `bigint`." : null;
    return note === null ? documentation ?? undefined : documentation == null ? note : documentation + "\n\n" + note;
  }

  declaration(node: Node, name: string, fromPageOnly: boolean): string {
    const source = new Builder();
    const definition = node.definition!;
    switch (definition.kind) {
      case "enum": {
        let documentation = definition.doc ?? null;
        const cases = definition.cases.filter(item => item.doc !== undefined).map(item => `- \`${item.wire}\`: ${item.doc!.replaceAll("\n", " ")}`);
        if (cases.length > 0) documentation = (documentation === null ? "" : documentation + "\n\n") + cases.join("\n");
        source.doc("", documentation);
        source.line(`export type ${name} = ${definition.cases.length === 0 ? "never" : definition.cases.map(item => csQuote(item.wire)).join(" | ")};`);
        break;
      }
      case "object":
        source.doc("", definition.doc);
        source.line(`export interface ${name} {`);
        for (const member of node.members) {
          source.doc("  ", Emitter.withNote(member.doc, member.type));
          const optional = member.absent || (fromPageOnly && member.optional);
          source.line(`  readonly ${propertyKey(member.wire)}${optional ? "?" : ""}: ${this.type(member.type)};`);
        }
        source.line("}");
        break;
      case "union":
        source.doc("", definition.doc);
        source.line(`export type ${name} =`);
        node.cases.forEach((item, index) => {
          const tag = `{ readonly $case: ${csQuote(item.tag)} }`;
          source.append("  | ").append(item.type.members.length === 0 ? tag : `(${tag} & ${this.named.reference(item.type)})`);
          source.line(index === node.cases.length - 1 ? ";" : "");
        });
        break;
    }
    return source.toString();
  }
}

const MARKER = "\u0001";
const placeholder = /\u0001([0-9]+)\u0001/g;

const GLOBALS = new Set([
  "Array", "BigInt", "Boolean", "Date", "Error", "Function", "JSON", "Map", "Math", "Number", "Object",
  "Omit", "Partial", "Pick", "Promise", "RangeError", "Readonly", "ReadonlyArray", "Record", "Set",
  "String", "Symbol", "TypeError", "any", "bigint", "boolean", "never", "null", "number", "object",
  "string", "symbol", "undefined", "unknown", "void",
  "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
  "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "implements",
  "import", "in", "infer", "instanceof", "interface", "is", "keyof", "let", "new", "package", "private",
  "protected", "public", "readonly", "return", "static", "super", "switch", "this", "throw", "true",
  "try", "type", "typeof", "unique", "var", "while", "with", "yield",
]);

interface Entry {
  index: number; id: string; hints: NameHints; node: Node; definition?: string; toPage: boolean; baseName: string; name?: string;
  /** A written module, or the definition of a kept type, uses it, so types.ts declares it. */
  kept: boolean;
}

/**
 * TypeScriptNamedTypes: one exported type per IR type definition, named once every module is known. A use while the
 * emitter discards a module (a View a client library owns) declares nothing: such a type is declared only if a written
 * module uses it too.
 */
export class NamedTypes {
  private readonly byId = new Map<string, Entry>();
  readonly entries: Entry[] = [];
  discarding = false;
  constructor(private readonly emitter: Emitter) {}

  reference(node: Node): string {
    let entry = this.byId.get(node.ref!);
    if (!entry) {
      entry = { index: this.entries.length, id: node.ref!, hints: node.definition!.name, node, toPage: false, baseName: "", kept: false };
      this.byId.set(node.ref!, entry);
      this.entries.push(entry);
    }
    if (this.discarding) return `${MARKER}${entry.index}${MARKER}`;
    if (!entry.kept) {
      entry.kept = true;
      entry.node = node;
    }
    if (toPage(node)) entry.toPage = true;
    return `${MARKER}${entry.index}${MARKER}`;
  }

  /** A text with each named type as the simple name of its .NET type, such as `Note` for `Notes.Note`, for a message. */
  display(text: string): string {
    return text.replace(placeholder, (_, index: string) =>
      this.entries[Number(index)]!.hints.source.split("[")[0]!.split(/[.+]/).at(-1)!.split("`")[0]!);
  }

  private get kept(): Entry[] { return this.entries.filter(entry => entry.kept); }

  get any(): boolean { return this.entries.some(entry => entry.kept); }

  /** Builds every definition and assigns the names; the names `modules` declare or import are reserved. */
  complete(modules: readonly string[]): void {
    // A definition may reference further named types, which are kept from then on.
    for (let built = true; built;) {
      built = false;
      for (let index = 0; index < this.entries.length; index++) {
        const entry = this.entries[index]!;
        if (!entry.kept || entry.definition !== undefined) continue;
        entry.definition = this.emitter.declaration(entry.node, this.reference(entry.node), false);
        built = true;
      }
    }
    const entries = this.kept;
    for (let changed = true; changed;) {
      changed = false;
      for (const entry of entries.filter(item => item.toPage))
        for (const match of entry.definition!.matchAll(placeholder)) {
          const contained = this.entries[Number(match[1])]!;
          if (!contained.toPage) { contained.toPage = true; changed = true; }
        }
    }
    // A type that never reaches the page is declared with the optional members a page may leave out.
    for (const entry of entries.filter(item => !item.toPage))
      entry.definition = this.emitter.declaration(entry.node, this.reference(entry.node), true);
    for (const entry of entries)
      entry.baseName = entries.filter(other => other.hints.source === entry.hints.source).length > 1
        ? entry.hints.annotatedBase ?? entry.hints.base : entry.hints.base;
    const reserved = new Set(GLOBALS);
    for (const module of modules) for (const name of declaredNames(module)) reserved.add(name);
    const levels = new Map(entries.map(entry => [entry, 0]));
    const candidate = (entry: Entry, level: number) => entry.hints.qualifiers.slice(0, level).reverse().join("") + entry.baseName;
    for (let round = 0; round < 64; round++) {
      const groups = new Map<string, Entry[]>();
      for (const entry of entries) {
        const key = candidate(entry, levels.get(entry)!);
        groups.set(key, [...(groups.get(key) ?? []), entry]);
      }
      const conflicts = [...groups].filter(([key, group]) => group.length > 1 || reserved.has(key)).flatMap(([, group]) => group)
        .filter(entry => levels.get(entry)! < entry.hints.qualifiers.length);
      if (conflicts.length === 0) break;
      for (const entry of conflicts) levels.set(entry, levels.get(entry)! + 1);
    }
    const taken = new Set(reserved);
    const ordered = [...entries].sort((a, b) => levels.get(a)! - levels.get(b)! || ordinal(a.hints.source, b.hints.source));
    for (const entry of ordered) {
      let name = candidate(entry, levels.get(entry)!);
      for (let suffix = 2; taken.has(name); suffix++) name = candidate(entry, levels.get(entry)!) + suffix;
      taken.add(name);
      entry.name = name;
    }
  }

  resolve(text: string): string {
    return text.replace(placeholder, (_, index) => {
      const name = this.entries[Number(index)]!.name;
      if (name === undefined) throw new Error("Named TypeScript types have not been completed.");
      return name;
    });
  }

  used(text: string): string[] {
    return [...new Set([...text.matchAll(placeholder)].map(match => this.entries[Number(match[1])]!.name!))].sort(ordinal);
  }

  private valuesName(entry: Entry): string | null {
    const definition = entry.node.definition!;
    if (definition.kind !== "enum" || definition.cases.length === 0 || entry.name === undefined) return null;
    const name = entry.name + "Values";
    return this.entries.some(other => other.name === name) ? null : name;
  }

  usedValues(text: string): string[] {
    const declared = new Set(declaredNames(text));
    return [...new Set([...text.matchAll(placeholder)].map(match => this.valuesName(this.entries[Number(match[1])]!))
      .filter((name): name is string => name !== null && !declared.has(name)))].sort(ordinal);
  }

  module(): string {
    const source = new Builder();
    for (const entry of this.kept.sort((a, b) => ordinal(a.name!, b.name!))) {
      source.append(this.resolve(entry.definition!));
      const values = this.valuesName(entry);
      if (values !== null) {
        const definition = entry.node.definition as Extract<TypeDefinition, { kind: "enum" }>;
        const written = new Set<string>();
        const names = definition.cases.filter(item => !written.has(item.value) && written.add(item.value)).map(item => csQuote(item.wire));
        source.line(`/** The values of {@link ${entry.name}} that .NET writes, in declaration order. */`);
        source.line(`export const ${values} = [${names.join(", ")}] as const satisfies readonly ${entry.name}[];`);
      }
      source.line();
    }
    const module = source.toString().trimEnd() + "\n";
    return module.includes("bridgeWire.")
      ? 'import type * as bridgeWire from "@runic-artifex/views/generated/wire";\n\n' + module
      : module;
  }
}

// TypeScriptNamedTypes' scans; .NET's \b, like its \w, is Unicode.
const namedDeclaration = new RegExp(String.raw`(?<![${WORD}])(?:interface|type|function|const|let|class|enum)\s+(${NAME})`, "gu");
const namedImport = new RegExp(String.raw`(?<![${WORD}])import\s+(?:type\s+)?\{([^}]*)\}`, "gu");

function* declaredNames(module: string): Generator<string> {
  const code = module.replace(/\/\*[\s\S]*?\*\//g, " ");
  for (const match of code.matchAll(namedDeclaration)) yield match[1]!;
  yield* declaredTopLevel(code);
  for (const match of code.matchAll(namedImport))
    for (const raw of match[1]!.split(",").map(part => part.trim()).filter(part => part.length > 0)) {
      const name = raw.startsWith("type ") ? raw.slice(5).trim() : raw;
      const alias = name.indexOf(" as ");
      yield alias >= 0 ? name.slice(alias + 4).trim() : name;
    }
}

