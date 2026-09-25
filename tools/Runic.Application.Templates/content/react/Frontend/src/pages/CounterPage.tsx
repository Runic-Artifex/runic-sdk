import { useEffect, useState } from "react";
import type { CounterPageReference, CounterState, CounterView } from "../generated/counter.js";

export function CounterPage({ page }: { page: CounterPageReference }) {
  const [view, setView] = useState<CounterView>();
  const [state, setState] = useState<CounterState>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let active = true;
    let unsubscribe = () => {};
    let client: CounterView | undefined;
    void page.connect().then(connected => {
      if (!active) { connected.dispose(); return; }
      client = connected;
      setView(connected);
      unsubscribe = connected.subscribe(next => setState(next));
    }).catch(cause => setError(String(cause)));
    return () => { active = false; unsubscribe(); client?.dispose(); };
  }, [page]);

  return <section>
    <h2>Counter View</h2>
    <p className="count">{state?.count ?? "…"}</p>
    <button disabled={!view} onClick={() => view && void view.increment().catch(cause => setError(String(cause)))}>Increment</button>
    {error && <p role="alert">{error}</p>}
  </section>;
}
