import { useState } from "react";
import { useView } from "@runic-artifex/react";
import type { CounterPageReference } from "../generated/counter.js";

export function CounterPage({ page }: { page: CounterPageReference }) {
  const { state, client, error: connection } = useView(page);
  const [error, setError] = useState<string>();
  const shown = error ?? (connection ? String(connection) : undefined);

  return <section>
    <h2>Counter View</h2>
    <p className="count">{state?.count ?? "…"}</p>
    <button disabled={!client} onClick={() => client && void client.increment().then(() => setError(undefined), cause => setError(String(cause)))}>Increment</button>
    {shown && <p role="alert">{shown}</p>}
  </section>;
}
