// What a framework binding's DevTools labels show for a View client (#1041): in the development build only, each
// connected client's `dispose` function carries a description function on a registered symbol, which every copy of
// this package on the page shares. The production build does not contain this module and describes nothing.
const describeKey = Symbol.for("runic.views.describe");

/** A connected View client as the development tools describe it. */
export interface ViewDescription {
  /** The C# view model's name without its namespace, such as `EditorViewModel`. */
  readonly view: string;
  /** The route, such as `editor` or `content1`. */
  readonly route: string;
  /** The View contract, `{C# view model}:{fingerprint}`. */
  readonly contract: string;
  /** The revision of the route's latest state; `undefined` before the first state. */
  readonly revision: number | undefined;
  /** How many presentations of the page show the route. */
  readonly presentations: number;
  /** The client was disposed, or .NET released its View or the Bridge session changed. */
  readonly released: boolean;
}

/** The C# view model's name of a contract `Demo.EditorViewModel:3F2A…`, or `route` without one. */
export function viewName(contract: string, route: string): string {
  return contract.split(":", 1)[0]!.split(".").pop() || route;
}

/** @internal Gives a client's `dispose` function its description. */
export function describeWith(dispose: () => void, describe: () => ViewDescription): void {
  Object.defineProperty(dispose, describeKey, { value: describe });
}

/** @internal The description of a connected client of the development build of any copy of this package. */
export function describe(client: unknown): ViewDescription | undefined {
  const dispose = typeof client === "object" && client !== null ? (client as { dispose?: unknown }).dispose : undefined;
  const read = typeof dispose === "function" ? (dispose as unknown as Record<symbol, unknown>)[describeKey] : undefined;
  if (typeof read !== "function") return undefined;
  try { return read() as ViewDescription; } catch { return undefined; }
}
