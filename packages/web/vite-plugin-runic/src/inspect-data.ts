// Runic Inspect's data: what the overview of a selected element says, assembled from what the development client
// learned on the page (the Bridge calls an element made, the Views' states), the development source map
// (`/__runic/sources`) and the element's framework component. No DOM access here, so the assembly is testable on its
// own; inspect.ts reads the page and draws the overview.

import type { RunicSourceMap, RunicSourceMember, RunicSourceMemberKind, RunicSourceViewModel } from "./sources.js";

/**
 * A source location of the overview, with how "open in editor" opens it: `runic` posts the source map entry's `id` to
 * `/__runic/open`; `vite` opens a frontend file through Vite's `/__open-in-editor`.
 */
export interface RunicInspectLocation {
  readonly file: string;
  /** 1-based. */
  readonly line: number;
  /** 1-based. */
  readonly column: number;
  readonly editor: "runic" | "vite";
  /** The source map entry, for `editor: "runic"`. */
  readonly id?: string;
}

/** The element's framework component, as the framework's own development metadata gives it. */
export interface RunicInspectComponent {
  readonly framework: "react" | "vue" | "svelte" | "angular";
  readonly name: string;
  /** The components around it, innermost first. */
  readonly owners: readonly string[];
  /**
   * React: where the element's JSX is (from `_debugStack` through the module's source map); Svelte: where the element
   * is in the component; Vue: the component's file (line 1); Angular: the component class.
   */
  readonly location?: RunicInspectLocation;
}

/** A ViewModel the element is connected to. */
export interface RunicInspectViewModel {
  readonly route: string;
  /** The C# type: the source map's ViewModel id. */
  readonly id?: string;
  readonly name?: string;
  readonly contract?: string;
  readonly location?: RunicInspectLocation;
}

/**
 * How the overview knows a member belongs to the element: `call` (the element made a Bridge call to it), `text` (the
 * element's text is the member's value), `value` (a form control's value is the member's value). `text` and `value`
 * are matches, not proof.
 */
export type RunicInspectEvidence = "call" | "text" | "value";

/** A ViewModel member the element binds, with its current value and its C# member. */
export interface RunicInspectMember {
  readonly route: string;
  readonly viewModel?: string;
  readonly member: string;
  readonly kind: RunicSourceMemberKind;
  readonly evidence: RunicInspectEvidence;
  /** The member's current value in the View's state, shortened. */
  readonly value?: unknown;
  /** The C# member that runs, such as `Demo.CounterViewModel.Step.set`. */
  readonly symbol?: string;
  readonly location?: RunicInspectLocation;
}

/** A command the element calls, with its C# handler. */
export interface RunicInspectCommand {
  readonly route: string;
  readonly viewModel?: string;
  readonly member: string;
  /** The handler, such as `Demo.CounterViewModel.Increment()`, and the command property. */
  readonly symbol?: string;
  readonly property?: string;
  readonly location?: RunicInspectLocation;
  /** The value of the command's availability state (`canIncrement`) when the ViewModel has one. */
  readonly canExecute?: unknown;
  /** How many calls of it the element made in this page. */
  readonly calls: number;
}

/** A Bridge call the element made. Times are `performance.now()` milliseconds. */
export interface RunicInspectCall {
  readonly id: number;
  readonly kind: string;
  readonly request: string;
  readonly route: string;
  readonly member: string;
  readonly start: number;
  readonly duration?: number;
  readonly outcome?: string;
  readonly reason?: string;
  readonly revision?: number;
}

/** A text the ViewModel sent as a Runic Translations text reference, which the element shows. */
export interface RunicInspectText {
  readonly route: string;
  /** Where the reference is in the state: `status`, `items.2.label`. */
  readonly path: string;
  readonly catalog: string;
  readonly key: string;
  readonly locale?: string;
  readonly text: string;
  readonly arguments: unknown;
}

/** What Runic Inspect and `describe(element)` report for an element. */
export interface RunicInspectDescription {
  readonly element: {
    readonly tag: string;
    readonly id?: string;
    readonly classes: readonly string[];
    readonly role?: string;
    /** The element's text, whitespace collapsed, shortened. */
    readonly text: string;
  };
  readonly component?: RunicInspectComponent;
  readonly viewModels: readonly RunicInspectViewModel[];
  readonly members: readonly RunicInspectMember[];
  readonly commands: readonly RunicInspectCommand[];
  /** The element's recent Bridge calls, newest first. */
  readonly calls: readonly RunicInspectCall[];
  readonly texts: readonly RunicInspectText[];
  /** What the overview cannot tell, and why. */
  readonly notes: readonly string[];
}

/** A Bridge call kind as the call channel reports it, which the element was seen making. */
export interface LearnedBinding {
  readonly route: string;
  readonly contract: string;
  readonly kind: string;
  readonly member: string;
}

/** What inspect.ts read from the page for one element. */
export interface InspectInput {
  readonly element: RunicInspectDescription["element"] & {
    /** The element's own text (its text nodes), for matching state values. */
    readonly ownText: string;
    /** A form control's value. */
    readonly value?: string;
    /** Whether it is a control that makes Bridge calls when used (a button, link, form field). */
    readonly interactive: boolean;
  };
  readonly learned: readonly LearnedBinding[];
  readonly calls: readonly RunicInspectCall[];
  /** Each open route's latest state, from the state inspector channel. */
  readonly states: ReadonlyMap<string, unknown>;
  /** Each route's contract, from the Bridge calls seen. */
  readonly contracts: ReadonlyMap<string, string>;
  readonly sourceMap?: RunicSourceMap | undefined;
  /** Why there is no source map, for the notes. */
  readonly sourceMapStatus?: "missing" | "unavailable";
  readonly component?: RunicInspectComponent | undefined;
  /** Formats a text reference as the page shows it, with the locale it used. */
  readonly resolveText?: ((reference: TextReferenceLike) => { readonly text: string; readonly locale?: string } | undefined) | undefined;
}

/** A Runic Translations text reference on the wire (`@runic-artifex/views` `TextReference`). */
export interface TextReferenceLike {
  readonly version: number;
  readonly catalog: string;
  readonly contractFingerprint: string;
  readonly key: string;
  readonly arguments: Readonly<Record<string, unknown>>;
  readonly fallbackText?: string | null;
}

const maximumTextLength = 200;

/** Collapses whitespace and shortens text for the overview. */
export function normalizeText(text: string, maximum = maximumTextLength): string {
  const collapsed = text.replace(/\s+/gu, " ").trim();
  return collapsed.length > maximum ? `${collapsed.slice(0, maximum - 1)}…` : collapsed;
}

/** A JSON-safe, shortened copy of a state value for the overview. */
export function previewValue(value: unknown, depth = 0): unknown {
  if (value === null || typeof value === "boolean") return value;
  if (typeof value === "number") return Number.isFinite(value) ? value : String(value);
  if (typeof value === "string") return value.length > maximumTextLength ? `${value.slice(0, maximumTextLength - 1)}…` : value;
  if (typeof value === "bigint") return `${value}n`;
  if (value === undefined || typeof value === "function" || typeof value === "symbol") return undefined;
  if (value instanceof Date) return value.toISOString();
  if (depth >= 3) return Array.isArray(value) ? `[${value.length} items]` : "{…}";
  if (Array.isArray(value)) {
    const items = value.slice(0, 20).map((item) => previewValue(item, depth + 1) ?? null);
    return value.length > 20 ? [...items, `… ${value.length - 20} more`] : items;
  }
  const result: Record<string, unknown> = {};
  let count = 0;
  for (const [key, item] of Object.entries(value as Record<string, unknown>)) {
    if (count++ === 30) { result["…"] = "more"; break; }
    const preview = previewValue(item, depth + 1);
    if (preview !== undefined) result[key] = preview;
  }
  return result;
}

/** Whether a value has the shape of a text reference, as `@runic-artifex/views` checks it. */
export function isTextReference(value: unknown): value is TextReferenceLike {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as Record<string, unknown>;
  return Number.isInteger(candidate["version"]) && (candidate["version"] as number) >= 1
    && typeof candidate["catalog"] === "string" && typeof candidate["contractFingerprint"] === "string"
    && typeof candidate["key"] === "string" && candidate["key"].length > 0
    && typeof candidate["arguments"] === "object" && candidate["arguments"] !== null
    && (candidate["fallbackText"] === undefined || candidate["fallbackText"] === null || typeof candidate["fallbackText"] === "string");
}

/** The text references in a state, with their paths, at most 4 levels and 200 references deep. */
export function findTextReferences(state: unknown): { readonly path: string; readonly reference: TextReferenceLike }[] {
  const found: { path: string; reference: TextReferenceLike }[] = [];
  const visit = (value: unknown, path: string, depth: number): void => {
    if (found.length >= 200 || typeof value !== "object" || value === null) return;
    if (isTextReference(value)) {
      found.push({ path, reference: value });
      return;
    }
    if (depth >= 4) return;
    const entries = Array.isArray(value) ? value.slice(0, 100).map((item, index) => [String(index), item] as const)
      : Object.entries(value as Record<string, unknown>);
    for (const [key, item] of entries) visit(item, path === "" ? key : `${path}.${key}`, depth + 1);
  };
  visit(state, "", 0);
  return found;
}

/** The member kind a Bridge call kind reaches in the source map, in the order to look it up. */
export function memberKinds(callKind: string): readonly RunicSourceMemberKind[] {
  switch (callKind) {
    case "command": case "can": case "operation": return ["command"];
    case "set": case "write": return ["setter", "state"];
    default: return ["command", "state", "request", "interaction"];
  }
}

/** Indexes of a source map for the lookups of an overview. */
export interface SourceIndex {
  readonly viewModels: ReadonlyMap<string, RunicSourceViewModel>;
  readonly members: ReadonlyMap<string, RunicSourceMember>;
  /** A ViewModel's state and command-state member names. */
  readonly stateMembers: ReadonlyMap<string, ReadonlySet<string>>;
  readonly map: RunicSourceMap;
}

export function indexSourceMap(map: RunicSourceMap): SourceIndex {
  const viewModels = new Map(map.viewModels.map((viewModel) => [viewModel.id, viewModel]));
  const members = new Map(map.members.map((member) => [member.id, member]));
  const stateMembers = new Map<string, Set<string>>();
  for (const member of map.members) {
    if (member.kind !== "state" && member.kind !== "commandState") continue;
    let names = stateMembers.get(member.viewModel);
    if (names === undefined) stateMembers.set(member.viewModel, names = new Set());
    names.add(member.member);
  }
  return { viewModels, members, stateMembers, map };
}

/**
 * The ViewModel of a route: by the contract its calls carried, else by the route `connect<Name>()` connects, else the
 * one ViewModel whose state members are exactly the route's state keys (a View presented as content connects a route
 * of its own, such as `content1`).
 */
export function viewModelForRoute(
  index: SourceIndex, route: string, contract: string | undefined, state: unknown,
): RunicSourceViewModel | undefined {
  if (contract !== undefined) {
    const byContract = index.viewModels.get(contract.split(":", 1)[0]!);
    if (byContract !== undefined) return byContract;
  }
  const byRoute = index.map.viewModels.filter((viewModel) => viewModel.route === route);
  if (byRoute.length === 1) return byRoute[0];
  if (typeof state !== "object" || state === null) return undefined;
  const keys = Object.keys(state);
  if (keys.length === 0) return undefined;
  const matches = index.map.viewModels.filter((viewModel) => {
    const names = index.stateMembers.get(viewModel.id);
    return names !== undefined && names.size === keys.length && keys.every((key) => names.has(key));
  });
  return matches.length === 1 ? matches[0] : undefined;
}

function runicLocation(entry: RunicSourceMember | RunicSourceViewModel): RunicInspectLocation | undefined {
  const location = entry.location;
  return location === undefined ? undefined
    : { file: location.file, line: location.line, column: location.column, editor: "runic", id: entry.id };
}

function capitalize(name: string): string {
  return name.length === 0 ? name : name[0]!.toUpperCase() + name.slice(1);
}

function stateValue(state: unknown, member: string): unknown {
  return typeof state === "object" && state !== null && Object.hasOwn(state, member)
    ? previewValue((state as Record<string, unknown>)[member]) : undefined;
}

/** Assembles the overview of an element from what was read for it. */
export function assembleDescription(input: InspectInput): RunicInspectDescription {
  const index = input.sourceMap === undefined ? undefined : indexSourceMap(input.sourceMap);
  const viewModels = new Map<string, RunicInspectViewModel>();
  const resolved = new Map<string, RunicSourceViewModel | undefined>();
  const viewModelOf = (route: string): RunicSourceViewModel | undefined => {
    if (!resolved.has(route)) {
      resolved.set(route, index === undefined ? undefined
        : viewModelForRoute(index, route, input.contracts.get(route), input.states.get(route)));
    }
    return resolved.get(route);
  };
  const useRoute = (route: string): RunicSourceViewModel | undefined => {
    const viewModel = viewModelOf(route);
    if (!viewModels.has(route)) {
      const contract = input.contracts.get(route) ?? viewModel?.contract;
      const location = viewModel === undefined ? undefined : runicLocation(viewModel);
      viewModels.set(route, {
        route,
        ...(viewModel === undefined ? {} : { id: viewModel.id, name: viewModel.name }),
        ...(contract === undefined ? {} : { contract }),
        ...(location === undefined ? {} : { location }),
      });
    }
    return viewModel;
  };
  const sourceMember = (viewModel: RunicSourceViewModel | undefined, kinds: readonly RunicSourceMemberKind[], member: string) => {
    if (index === undefined || viewModel === undefined) return undefined;
    for (const kind of kinds) {
      const entry = index.members.get(`${viewModel.id}/${kind}/${member}`);
      if (entry !== undefined) return entry;
    }
    return undefined;
  };

  const members: RunicInspectMember[] = [];
  const commands: RunicInspectCommand[] = [];
  const memberKeys = new Set<string>();
  const addMember = (member: RunicInspectMember): void => {
    const key = `${member.route}\u0000${member.member}`;
    if (memberKeys.has(key)) return;
    memberKeys.add(key);
    members.push(member);
  };

  // What the element's calls show: exact.
  for (const binding of input.learned) {
    const viewModel = useRoute(binding.route);
    const kinds = memberKinds(binding.kind);
    const entry = sourceMember(viewModel, kinds, binding.member);
    const state = input.states.get(binding.route);
    const location = entry === undefined ? undefined : runicLocation(entry);
    if (kinds[0] === "command") {
      if (commands.some((command) => command.route === binding.route && command.member === binding.member)) continue;
      const canName = `can${capitalize(binding.member)}`;
      const canExecute = stateValue(state, canName);
      commands.push({
        route: binding.route,
        ...(viewModel === undefined ? {} : { viewModel: viewModel.id }),
        member: binding.member,
        ...(entry === undefined ? {} : { symbol: entry.symbol }),
        ...(entry?.property === undefined ? {} : { property: entry.property }),
        ...(location === undefined ? {} : { location }),
        ...(canExecute === undefined ? {} : { canExecute }),
        calls: input.calls.filter((call) => call.route === binding.route && call.member === binding.member
          && memberKinds(call.kind)[0] === "command").length,
      });
    } else {
      const value = stateValue(state, binding.member);
      addMember({
        route: binding.route,
        ...(viewModel === undefined ? {} : { viewModel: viewModel.id }),
        member: binding.member,
        kind: entry?.kind ?? kinds[0]!,
        evidence: "call",
        ...(value === undefined ? {} : { value }),
        ...(entry === undefined ? {} : { symbol: entry.symbol }),
        ...(location === undefined ? {} : { location }),
      });
    }
  }

  // What the element shows: a state value as its text or a control's value. A match, so only for short text.
  const ownText = normalizeText(input.element.ownText);
  for (const [route, state] of input.states) {
    if (typeof state !== "object" || state === null) continue;
    for (const [member, raw] of Object.entries(state as Record<string, unknown>)) {
      if (typeof raw !== "string" && typeof raw !== "number" && typeof raw !== "bigint") continue;
      const text = String(raw);
      if (text.trim().length === 0) continue;
      const evidence: RunicInspectEvidence | undefined = ownText.length > 0 && normalizeText(text) === ownText ? "text"
        : input.element.value !== undefined && input.element.value === text ? "value" : undefined;
      if (evidence === undefined) continue;
      const viewModel = useRoute(route);
      const entry = sourceMember(viewModel, ["state", "commandState"], member);
      const location = entry === undefined ? undefined : runicLocation(entry);
      addMember({
        route,
        ...(viewModel === undefined ? {} : { viewModel: viewModel.id }),
        member,
        kind: entry?.kind ?? "state",
        evidence,
        value: previewValue(raw),
        ...(entry === undefined ? {} : { symbol: entry.symbol }),
        ...(location === undefined ? {} : { location }),
      });
    }
  }

  // Texts the ViewModel sent as references that the element shows.
  const texts: RunicInspectText[] = [];
  const elementText = input.element.text;
  if (elementText.length > 0) {
    for (const [route, state] of input.states) {
      for (const { path, reference } of findTextReferences(state)) {
        const formatted = input.resolveText?.(reference);
        const text = formatted?.text ?? reference.fallbackText ?? undefined;
        if (text === undefined || normalizeText(text).length === 0 || !elementText.includes(normalizeText(text))) continue;
        useRoute(route);
        texts.push({
          route, path, catalog: reference.catalog, key: reference.key,
          ...(formatted?.locale === undefined ? {} : { locale: formatted.locale }),
          text: normalizeText(text), arguments: previewValue(reference.arguments),
        });
      }
    }
  }

  const notes: string[] = [];
  if (input.sourceMapStatus === "missing") {
    notes.push("No Bridge source map, so no C# locations: build the .NET project (`dotnet runic dev` passes the map to Vite), or check runic({ sourceMap }).");
  } else if (input.sourceMapStatus === "unavailable") {
    notes.push("This development server does not serve the Bridge source map (/__runic/sources), so no C# locations.");
  }
  if (input.element.interactive && input.learned.length === 0) {
    notes.push("No Bridge call from this element yet: use it once outside Runic Inspect, and the overview shows the command or setter it calls.");
  }
  if (input.component === undefined) {
    notes.push("No framework component found for this element (React, Vue, Svelte or Angular in development mode).");
  }

  return {
    element: {
      tag: input.element.tag,
      ...(input.element.id === undefined ? {} : { id: input.element.id }),
      classes: input.element.classes,
      ...(input.element.role === undefined ? {} : { role: input.element.role }),
      text: input.element.text,
    },
    ...(input.component === undefined ? {} : { component: input.component }),
    viewModels: [...viewModels.values()],
    members,
    commands,
    calls: input.calls,
    texts,
    notes,
  };
}
