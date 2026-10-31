// The development state inspector's channel on a registered symbol, which copies of this package and
// `@runic-artifex/vite-plugin-runic/client` share without a dependency (specs/application, "Shared globals"). The
// development client sets `requested` and adds a listener; every copy then reports each route's exposed state, a
// closed route, and a command the producer answered `superseded`. Without a reader nothing is reported or kept, so a
// production page pays one property read per published state.
const inspectorKey = Symbol.for("runic.views.inspector");

/** What the inspector channel reports. A listener ignores kinds and fields it does not know. */
export type ViewInspectorEvent =
  | {
    readonly kind: "state"; readonly route: string; readonly revision: number | undefined; readonly epoch: number | undefined;
    /** How many presentations of the page show the route. */
    readonly presentations: number;
    /** The exposed state; listeners must not change it. */
    readonly state: unknown;
  }
  | { readonly kind: "closed"; readonly route: string }
  | {
    readonly kind: "superseded"; readonly route: string; readonly member: string;
    /** The epoch the command was issued for, and the reply's revision. */
    readonly epoch: number | undefined; readonly revision: number | undefined;
  };

interface InspectorChannel {
  readonly listeners?: Set<(event: ViewInspectorEvent) => void>;
  requested?: boolean;
}

function readers(): Set<(event: ViewInspectorEvent) => void> | undefined {
  const channel = (globalThis as unknown as Record<symbol, InspectorChannel | undefined>)[inspectorKey];
  const listeners = channel?.requested === true ? channel.listeners : undefined;
  return listeners instanceof Set && listeners.size > 0 ? listeners : undefined;
}

/** @internal Reports to the state inspector when a reader asked for it; `create` runs only then. */
export function reportToInspector(create: () => ViewInspectorEvent): void {
  const listeners = readers();
  if (listeners === undefined) return;
  const event = create();
  for (const listener of [...listeners]) {
    try { listener(event); } catch (error) { console.error(error); }
  }
}
