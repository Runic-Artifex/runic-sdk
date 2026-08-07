import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { counterBridge } from "./counter-bridge";
import type { CounterSnapshot } from "./counter-contract";

const root = createRoot(document.querySelector("#app")!);
root.render(<Counter />);
globalThis.addEventListener("pagehide", () => void counterBridge.dispose(), { once: true });

function Counter() {
  const [snapshot, setSnapshot] = useState<CounterSnapshot>({ count: 0, history: [0], revision: 0 });
  const [step, setStep] = useState(1);
  const [error, setError] = useState<string>();
  useEffect(() => {
    const unsubscribe = counterBridge.subscribe(
      (event) => setSnapshot(event.snapshot),
      (failure) => setError(failure.message),
    );
    void counterBridge.initialize().then(setSnapshot, (failure) => setError(failure.message));
    return unsubscribe;
  }, []);
  const increment = async () => {
    setError(undefined);
    const receipt = await counterBridge.dispatch({ _tag: "IncrementCounter", step });
    if (receipt && typeof receipt === "object" && "snapshot" in receipt) setSnapshot(receipt.snapshot as CounterSnapshot);
  };
  return (
    <main className="container py-5" style={{ maxWidth: 720 }}>
      <div className="d-flex justify-content-between align-items-center mb-3">
        <span className="badge text-bg-primary">React + native C#</span>
        <span className="badge text-bg-success">
          Connected · r{snapshot.revision}
        </span>
      </div>
      <section className="card border-0 shadow">
        <div className="card-body p-5">
          <p className="display-2 fw-semibold mb-2">{snapshot.count}</p>
          <p className="lead text-secondary">{snapshot.history.length - 1} named command(s)</p>
          <label className="form-label" htmlFor="step">Increment step</label>
          <input
            id="step"
            type="number"
            min="1"
            max="10"
            className="form-control"
            value={step}
            onChange={(event) => setStep(event.currentTarget.valueAsNumber)}
          />
          {error && <div className="text-danger mt-2">{error}</div>}
          <button
            className="btn btn-primary w-100 mt-3"
            onClick={() => void increment()}
          >
            <i className="fa-solid fa-plus me-2" aria-hidden="true" />Increment in C#
          </button>
          <h2 className="h6 mt-4">History</h2>
          <div className="d-flex flex-wrap gap-2">
            {snapshot.history.map((value, index) =>
              <span className="badge text-bg-light" key={`${index}-${value}`}>{value}</span>)}
          </div>
        </div>
      </section>
    </main>
  );
}
