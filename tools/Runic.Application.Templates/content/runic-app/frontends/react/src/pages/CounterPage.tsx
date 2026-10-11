import { useCommand, useView } from "@runic-artifex/react";
import type { CounterPageReference } from "../generated/counter.js";

const steps = [1, 2, 5, 10];

export function CounterPage({ page }: { page: CounterPageReference }) {
  const { state, client, error: connection } = useView(page);
  const increment = useCommand(() => client?.increment());
  // Step has a public C# setter, so the client has setStep for a two-way
  // binding. Count's setter is private: the page only reads it.
  const setStep = useCommand((step: number) => client?.setStep(step));
  const error = increment.error ?? setStep.error ?? connection;

  return <section>
    <h2>Counter View</h2>
    <p className="count">{state?.count ?? "…"}</p>
    <label>
      Step{" "}
      <select value={state?.step ?? 1} disabled={!client} onChange={event => void setStep.run(Number(event.currentTarget.value))}>
        {steps.map(step => <option key={step} value={step}>{step}</option>)}
      </select>
    </label>
    <button disabled={!client || increment.pending} onClick={() => void increment.run()}>Increment</button>
    {error !== undefined && <p role="alert">{String(error)}</p>}
  </section>;
}
