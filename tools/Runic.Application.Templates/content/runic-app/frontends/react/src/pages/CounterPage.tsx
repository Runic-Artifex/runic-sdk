import { useCommand, useView } from "@runic-artifex/react";
import type { CounterPageReference } from "../generated/counter.js";

export function CounterPage({ page }: { page: CounterPageReference }) {
  const { state, client, error: connection } = useView(page);
  const increment = useCommand(() => client?.increment());
  const error = increment.error ?? connection;

  return <section>
    <h2>Counter View</h2>
    <p className="count">{state?.count ?? "…"}</p>
    <button disabled={!client || increment.pending} onClick={() => void increment.run()}>Increment</button>
    {error !== undefined && <p role="alert">{String(error)}</p>}
  </section>;
}
