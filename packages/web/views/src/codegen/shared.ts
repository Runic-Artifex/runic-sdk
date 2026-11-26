// Shared clients (#839, `[RunicSharedClient]` in .NET): one client type for the Views that implement a common interface,
// so one component renders all of them. Each View records the parts of its client that each member contributes, with
// its own name replaced by OWNER; the shared client takes the parts that every implementing View has alike.
import type { SharedClient, View } from "./ir.js";
import { Import, Ref } from "./bindings.js";
import { ContractIrDiagnostic, SHARED_CLIENT } from "./diagnostics.js";
import { NAMED_TYPE_IMPORTS, type Modules } from "./modules.js";
import { Builder, csQuote, lowerFirst, ordinal } from "./text.js";

/** Stands for the View's own name in a recorded declaration. */
export const OWNER = "\u000e";
/** Stands for a parameter name in a comparable declaration. */
export const PARAMETER = "\u000f";

/** A part of a generated client that one member or command contributes. */
export interface SharedPart {
  /** What the part is, such as `state`, `setter` or `command`. */
  readonly key: string;
  /** Where it goes: `state`, `client`, `queries` or `operations`. */
  readonly section: string;
  /** The declaration, with OWNER for the View's own name. */
  readonly declaration: string;
  /** The declaration without names that do not change its type, such as parameter names; default `declaration`. */
  readonly comparable?: string;
  readonly documentation?: string | undefined;
  /** Shared only when every implementing View has it alike, such as a member's validation errors. */
  readonly optional?: boolean;
  /** The page reference types it names, with their modules. */
  readonly imports?: readonly { module: string; type: string }[];
}

/** The parts of one View's client, by the member (`member:{wire}`) or command (`command:{wire}`) that contributes them. */
export class SharedParts {
  readonly byOwner = new Map<string, SharedPart[]>();

  record(owner: { readonly wire: string; readonly kind?: string }, part: SharedPart): void {
    const key = `${owner.kind === undefined ? "command" : "member"}:${owner.wire}`;
    const parts = this.byOwner.get(key) ?? [];
    parts.push(part);
    this.byOwner.set(key, parts);
  }
}

/**
 * Compares the implementing Views of a shared client and adds its module. `display` shows a declaration with the .NET
 * names of the named types it uses, for a RUNICBRIDGE036.
 */
export function emitSharedClient(client: SharedClient, views: ReadonlyMap<string, View>, parts: ReadonlyMap<string, SharedParts>, modules: Modules,
  display: (declaration: string) => string = text => text): void {
  const name = client.name;
  const module = lowerFirst(name);
  const reference = `${name}PageReference`;
  const implementers = client.views.map(id => {
    const view = views.get(id);
    if (!view) throw new Error(`The shared client ${name} names the unknown View ${id}.`);
    return view;
  });
  const simpleName = (view: View) => view.id.split(/[.+]/).pop()!;
  // The parts each implementing View has for each member of the interface.
  const collected = implementers.map(view => new Map(client.members.map(member => {
    const published = member.published[view.id];
    const owner = published === undefined ? undefined : "member" in published ? `member:${published.member}` : `command:${published.command}`;
    const recorded = owner === undefined ? undefined : parts.get(view.id)?.byOwner.get(owner);
    if (recorded === undefined || recorded.length === 0)
      throw new ContractIrDiagnostic(SHARED_CLIENT,
        `${simpleName(view)} implements ${client.interface}.${member.name} of the shared client ${name}, but does not publish it.`, view.id);
    // The interface decides whether the shared client sets the member.
    const kept = recorded.filter(part => part.key !== "setter" || member.settable === true);
    if (member.settable && !kept.some(part => part.key === "setter"))
      throw new ContractIrDiagnostic(SHARED_CLIENT,
        `${simpleName(view)}: ${client.interface}.${member.name} of the shared client ${name} is settable, but the page cannot set it. Make the interface property get-only.`, view.id);
    return [member, kept] as const;
  })));
  // The parts every implementing View has alike; the declaration most of them have names the shared parameter.
  const shared: { member: (typeof client.members)[number]; part: SharedPart }[] = [];
  for (const member of client.members) {
    const keys = [...new Set(collected.flatMap(entry => entry.get(member)!.map(part => part.key)))];
    for (const key of keys) {
      const entries = collected.map((entry, index) => ({ view: implementers[index]!, part: entry.get(member)!.find(part => part.key === key) }));
      const groups = groupBy(entries, entry => entry.part === undefined ? undefined : entry.part.comparable ?? entry.part.declaration);
      if (groups.length === 1) {
        shared.push({ member, part: mostCommon(groupBy(entries, entry => entry.part!.declaration))[0]!.part! });
        continue;
      }
      if (entries.some(entry => entry.part?.optional === true)) continue;
      // The declaration most implementing Views have is the expected one; the first View that differs is reported at the
      // member that publishes it.
      const expected = mostCommon(groups);
      const differing = entries.find(entry => !expected.includes(entry))!;
      const others = expected.map(entry => simpleName(entry.view));
      const describe = (part: SharedPart | undefined, plural: boolean) => (plural ? "have" : "has")
        + (part === undefined ? " no such member" : ` \`${display(part.declaration.replaceAll(OWNER, name)).replace(/[\u0003\u0004\u0005]/g, "")}\``);
      const published = member.published[differing.view.id]!;
      const csharp = "member" in published
        ? { name: differing.view.members.find(item => item.wire === published.member)?.name ?? member.name, source: `${differing.view.id}/state/${published.member}` }
        : { name: `${differing.view.commands.find(item => item.wire === published.command)?.name ?? member.name}Command`, source: `${differing.view.id}/command/${published.command}` };
      throw new ContractIrDiagnostic(SHARED_CLIENT,
        `${simpleName(differing.view)}.${csharp.name}: every ViewModel that implements ${client.interface}.${member.name} of the shared client ${name} must publish it alike, but ${simpleName(differing.view)}'s client `
        + `${describe(differing.part, false)} where `
        + (others.length === 1 ? `${others[0]}'s client ` : `${others.slice(0, -1).join(", ")} and ${others.at(-1)}'s clients `)
        + `${describe(expected[0]!.part, others.length > 1)}. `
        + "Check the member's type and attributes, such as [JsonPropertyName], [RunicCommandInput], [RunicCommandKey] and [RunicFailure].",
        csharp.source);
    }
  }
  const kinds = implementers.flatMap(view => view.kinds);
  const contract = client.interface;
  const resolve = (text: string) => text.replaceAll(OWNER, name);
  const documentation = (member: (typeof client.members)[number], part: SharedPart) => {
    switch (part.key) {
      case "state": case "command": case "query": return member.doc ?? part.documentation;
      case "setter": return (member.doc === undefined ? "" : member.doc + "\n\n") + `Calls the public C# setter of \`${member.name}\` of the implementing ViewModel.`;
      default: return part.documentation;
    }
  };
  const append = (text: Builder, indent: string, section: string) => {
    const written = new Set<string>();
    for (const { member, part } of shared.filter(entry => entry.part.section === section)) {
      // A member shared by two commands, such as upload(), is declared once.
      if (written.has(part.declaration)) continue;
      written.add(part.declaration);
      text.doc(indent, documentation(member, part));
      text.line(indent + resolve(part.declaration));
    }
  };

  const body = new Builder();
  body.doc("", `The state that every ViewModel implementing \`${contract}\` publishes (\`[RunicSharedClient]\`).`);
  body.line(`export interface ${name}State {`);
  append(body, "  ", "state");
  body.line("}");
  body.line();
  if (shared.some(entry => entry.part.section === "operations")) {
    append(body, "", "operations");
    body.line();
  }
  body.doc("", (client.doc === undefined ? "" : client.doc + "\n\n")
    + `A connected View that implements \`${contract}\`: ${implementers.map(view => view.name).join(", ")}. Their own clients have these members too.`);
  body.line(`export interface ${name}Client extends ${Ref("ViewClient")}<${name}State> {`);
  if (shared.some(entry => entry.part.section === "queries")) {
    body.line("  /** The rows of each query collection, which this presentation loads for its own range (`query-collections/1`). */");
    body.line("  readonly queries: {");
    append(body, "    ", "queries");
    body.line("  };");
  }
  append(body, "  ", "client");
  body.line("}");
  body.line();
  const kindUnion = kinds.map(kind => csQuote(kind)).join(" | ");
  body.doc("", `The page reference of any View whose ViewModel implements \`${contract}\`. \`connect()\` returns the shared client;\n`
    + "each implementing ViewModel's own page reference extends this one, and a `ViewRegistry` accepts a component for it at each of its kinds.");
  body.line(`export interface ${reference} {`);
  body.line(`  readonly kind: ${kindUnion};`);
  body.line(`  connect(): Promise<${name}Client>;`);
  body.line("}");
  body.line();
  body.doc("", `Every kind of \`${reference}\`, for example to register one component for each with \`registryEntries\`.`);
  body.line(`export const ${module}Kinds: readonly ${reference}["kind"][] = [${kinds.map(kind => csQuote(kind)).join(", ")}];`);

  const text = body.toString();
  const publicTypes = ["ViewClient", "BridgeOutcome", "BridgeOperation", "BridgeStreamOperation", "BridgeOperationStartOptions",
    "RegionCollection", "RegionOutcome", "QueryCollection", "QueryCollectionState"];
  const out = new Builder();
  out.line("// <auto-generated />");
  out.line(`import type { ${publicTypes.filter(type => text.includes(Ref(type))).map(Import).join(", ")} } from "@runic-artifex/views";`);
  const imports = new Map<string, Set<string>>();
  for (const { part } of shared)
    for (const item of part.imports ?? []) imports.set(item.module, (imports.get(item.module) ?? new Set()).add(item.type));
  for (const [from, types] of [...imports].sort(([a], [b]) => ordinal(a, b)))
    out.line(`import type { ${[...types].sort(ordinal).join(", ")} } from "${modules.specifier(from, ".js")}";`);
  out.line(NAMED_TYPE_IMPORTS);
  out.line();
  out.append(text);
  // A client library's shared client is checked, but the library writes it.
  modules.discarding = client.owner !== undefined;
  try { modules.add(`${module}.ts`, out.toString(), client.id); }
  finally { modules.discarding = false; }
}

// Groups in first-appearance order.
function groupBy<T, K>(items: readonly T[], key: (item: T) => K): T[][] {
  const groups = new Map<K, T[]>();
  for (const item of items) groups.set(key(item), [...(groups.get(key(item)) ?? []), item]);
  return [...groups.values()];
}

// The largest group; the first of the largest on a tie.
const mostCommon = <T>(groups: T[][]): T[] => groups.reduce((best, group) => (group.length > best.length ? group : best));
