// Type-checked with the generated clients: named enum types are literal
// unions, so a switch over every case is exhaustive.
import type { CodegenShapeState } from "./obj/bridge-frontend/generated/codegenShape.js";
import type { OptionalMemberClient, OptionalMemberState, OptionalRequest } from "./obj/bridge-frontend/generated/optionalMember.js";
import type { DocumentedLayout, DocumentedState } from "./obj/bridge-frontend/generated/documented.js";
import type { NamingClient, NamingState } from "./obj/bridge-frontend/generated/naming.js";
import type { KeyedReactiveClient } from "./obj/bridge-frontend/generated/keyedReactive.js";
import type { KeyedReactiveMockDefinition } from "./obj/bridge-frontend/generated/keyedReactive.mock.js";
import type { KeyedToolkitClient } from "./obj/bridge-frontend/generated/keyedToolkit.js";
import type { KeyedPlainClient } from "./obj/bridge-frontend/generated/keyedPlain.js";
import type { NavDiscardPrompt, NavigationOutcome, ReactiveNavEditorClient } from "./obj/bridge-frontend/generated/reactiveNavEditor.js";
import type { NavTabsClient, NavTabsState } from "./obj/bridge-frontend/generated/navTabs.js";

export function describeLayout(state: DocumentedState): string {
  const layout: DocumentedLayout = state.layout;
  switch (layout) {
    case "List": return `one row each: ${state.item.title}`;
    case "Grid": return `a grid: ${state.item.title}`;
    default: {
      const unreachable: never = layout;
      return unreachable;
    }
  }
}

// A generic DTO member declared T is nullable only where the C# type
// argument is: CodegenSlot<string> versus CodegenSlot<string?>.
export function requiredValue(state: CodegenShapeState): string {
  return state.required.value;
}

export const optionalSlot: CodegenShapeState["optional"] = {
  value: null, fallback: null, items: [null, "item"], values: [null], map: { key: null }, inner: { value: null },
};

export const requiredSlot: CodegenShapeState["required"] = {
  // @ts-expect-error CodegenSlot<string> does not accept null.
  value: null, fallback: null, items: [], values: [], map: { key: "m" }, inner: { value: "i" },
};

// Any JSON value is unknown; a non-nullable JsonNode cannot hold JSON null.
export const json: CodegenShapeState["json"] = { any: null, node: null, required: { nested: [1, "two"] } };

export const nullJson: CodegenShapeState["json"] = {
  // @ts-expect-error A JsonNode is never JSON null.
  any: [], node: {}, required: null,
};

// A value the page only sends may leave out what .NET fills in; one it receives has every member.
export const minimalRequest: OptionalRequest = { kind: "build" };
export function submitMinimal(client: OptionalMemberClient): Promise<OptionalMemberState> {
  return client.submit({ kind: "build", extra: undefined }).then(() => client.configure({ name: "settings" }));
}
export function summaryRuns(state: OptionalMemberState): number {
  return state.summary.runs;
}
// @ts-expect-error A member without a .NET default is required.
export const missingKind: OptionalRequest = { label: "no kind" };

// Names next to the reserved client members stay ordinary members: a state
// property named New and a NewItem command (a NewCommand is RUNICBRIDGE004).
export function newState(state: NamingState): string {
  return state.new;
}

export function createItem(client: NamingClient): Promise<NamingState> {
  return client.newItem();
}

// W250-034/035: an editor's result commands return a typed NavigationOutcome, and the web View answers its
// ReactiveLeaveConfirmation interaction; the signal aborts when the navigation that asked is superseded.
export function answerLeaveConfirmation(
  view: ReactiveNavEditorClient,
  ask: (prompt: NavDiscardPrompt, signal: AbortSignal) => Promise<boolean>,
): () => void {
  return view.interactions.confirmDiscard.handle((prompt, { signal }) => ask(prompt, signal));
}

export async function closeEditor(view: ReactiveNavEditorClient): Promise<string> {
  const operation = await view.startClose();
  const settled = await operation.outcome();
  if (!settled.ok) return "failed";
  const outcome: NavigationOutcome = settled.value;
  if (outcome.kind === "Rejected") return `kept: ${outcome.rejection ?? "unknown"}`;
  // #385: a committed navigation reports how the arrival hooks of the page it made current ended.
  const arrival: "None" | "Completed" | "Failed" | "Cancelled" = outcome.arrival;
  return arrival === "Failed" ? "load failed" : outcome.kind;
}

// #472: a [RunicCommandKey] command takes the resolver's key, typed as its parameter (the goldens pin the names).
type Same<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
export const keyedSignatures: [
  Same<Parameters<KeyedReactiveClient["delete"]>, [itemId: bigint]>,
  Same<Parameters<KeyedReactiveClient["canArchive"]>, [itemId: bigint]>,
  Same<Parameters<KeyedReactiveClient["startRename"]>[0], bigint>,
  Same<Parameters<KeyedToolkitClient["select"]>, [name: string]>,
  Same<Parameters<KeyedToolkitClient["open"]>, [id: bigint]>,
  Same<Parameters<KeyedPlainClient["pick"]>, [slot: number]>,
  Same<Parameters<NonNullable<NonNullable<KeyedReactiveMockDefinition["resolveKey"]>["delete"]>>[1], bigint>,
] = [true, true, true, true, true, true, true];

// #648: a NavigationSelection slot is the shown tab's index, or null; the frontend shows that tab's region slot.
export function shownTab(state: NavTabsState): NavTabsState["inbox"] | NavTabsState["archive"] {
  const index: number | null = state.tabs;
  const slots = [state.inbox, state.archive] as const;
  return index === null ? null : slots[index] ?? null;
}

export async function selectTab(view: NavTabsClient, index: number): Promise<boolean> {
  const operation = await view.startSelectTab(index);
  const settled = await operation.outcome();
  return settled.ok && settled.value.kind === "Committed";
}
