// A generated View with two commands, `RefreshCommand` and `ArchiveCommand(long id)` with a declared failure, next to a
// setter and `canArchive(id)`, which are not commands (#1317).
import { bridgeFailure, bridgeSuccess, type BridgeOutcome, type ViewClient, type viewCommands } from "@runic-artifex/views";

export type OrdersState = { readonly title: string };
export type ArchiveFailure = { readonly $case: "missing" };
// The shapes the generator emits for the View's client.
export type OrdersCommandName = "refresh" | "archive";
export interface OrdersClient extends ViewClient<OrdersState> {
  readonly [viewCommands]?: OrdersCommandName;
  setTitle(value: string): Promise<OrdersState>;
  refresh(): Promise<OrdersState>;
  archive(id: bigint): Promise<BridgeOutcome<OrdersState, ArchiveFailure>>;
  canArchive(id: bigint): Promise<boolean>;
}

/** A client named `name` whose `refresh()` waits for `settle()`, and whose `archive(0n)` declares `missing`. */
export function ordersClient(name: string) {
  const calls: string[] = [];
  const runs: { resolve(): void; reject(error: unknown): void }[] = [];
  const state = { title: name };
  const client: OrdersClient = {
    snapshot: state,
    subscribe: listener => { listener(state); return () => {}; },
    dispose() {},
    async setTitle() { return state; },
    refresh() {
      calls.push(`${name}.refresh()`);
      return new Promise<OrdersState>((resolve, reject) => runs.push({ resolve: () => resolve(state), reject }));
    },
    async archive(id) {
      calls.push(`${name}.archive(${id})`);
      return id === 0n ? bridgeFailure({ $case: "missing" }) : bridgeSuccess(state);
    },
    async canArchive() { return true; },
  };
  return {
    client,
    calls,
    /** Ends the oldest `refresh()` run: rejects it with `error`, or resolves it. */
    settle(error?: unknown) {
      const run = runs.shift()!;
      if (error === undefined) run.resolve();
      else run.reject(error);
    },
  };
}
