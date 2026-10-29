import { useCommand, useField, useView } from "@runic-artifex/react";
import { describeBridgeError } from "@runic-artifex/views";
import { CounterFields, type CounterPageReference } from "../generated/counter.js";

const steps = [1, 2, 5, 10];

export function CounterPage({ page }: { page: CounterPageReference }) {
  const counter = useView(page);
  const { state, client, error: connection } = counter;
  // Step and Label have public C# setters, so the generated CounterFields has a
  // field for each; useField writes them and keeps what the user typed while the
  // writes are on their way. Count's setter is private: the page only reads it.
  const step = useField(counter, CounterFields.step);
  const label = useField(counter, CounterFields.label);
  const increment = useCommand(() => client?.increment());
  const error = increment.error ?? step.error ?? label.error ?? connection;

  return <section>
    <h2>Counter View</h2>
    <p className="caption">{state?.label ?? "…"}</p>
    <p className="count">{state?.count ?? "…"}</p>
    <label>
      Step{" "}
      <select {...step.inputProps} disabled={!client}>
        {steps.map(value => <option key={value} value={value}>{value}</option>)}
      </select>
    </label>
    <label>
      Label{" "}
      <input {...label.inputProps} disabled={!client} />
    </label>
    {/* Increment waits until the Step it adds has been written. */}
    <button disabled={!client || increment.pending || step.editing} onClick={() => void increment.run()}>Increment</button>
    {error !== undefined && <p role="alert">{describeBridgeError(error)}</p>}
  </section>;
}
