import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { createRoot } from "react-dom/client";
import { createInjectedMvvmDevelopmentTools } from "@runic-artifex/mvvm";
import {
  ReactMvvmProvider,
  startReactMvvmApplication,
  useMvvmSnapshot,
} from "@runic-artifex/mvvm-react";
import { CounterContract } from "./counter-contract.g";
import { useCounterBindings } from "./counter-bindings.g";

const mock = import.meta.env.MODE === "mock"
  ? await import("./counter.mock")
  : undefined;
const development = createInjectedMvvmDevelopmentTools(
  CounterContract.memberMetadata,
);
const application = await startReactMvvmApplication({
  contract: CounterContract,
  ...(development === undefined ? {} : { inspector: development.inspector }),
  ...(mock === undefined
    ? {}
    : { channelFactory: mock.createCounterMockChannel }),
});
const root = createRoot(document.querySelector("#app")!);
root.render(
  <ReactMvvmProvider store={application.store}>
    <Counter />
  </ReactMvvmProvider>,
);
application.addCleanup(() => root.unmount());
if (development !== undefined) {
  application.addCleanup(() => development.dispose());
}

function Counter() {
  const snapshot = useMvvmSnapshot();
  const bindings = useCounterBindings(application.contract);
  const errors = bindings.stepErrors ?? [];
  return (
    <main className="container py-5" style={{ maxWidth: 720 }}>
      <div className="d-flex justify-content-between align-items-center mb-3">
        <span className="badge text-bg-primary">React + native C#</span>
        <span className={`badge ${snapshot.synchronized ? "text-bg-success" : "text-bg-secondary"}`}>
          {snapshot.synchronized ? `Connected · r${snapshot.revision}` : snapshot.phase}
        </span>
      </div>
      <section className="card border-0 shadow">
        <div className="card-body p-5">
          <p className="display-2 fw-semibold mb-2">{bindings.count ?? 0}</p>
          <p className="lead text-secondary">{bindings.summary}</p>
          <label className="form-label" htmlFor="step">Increment step</label>
          <input
            id="step"
            type="number"
            min="1"
            max="10"
            className={`form-control ${errors.length ? "is-invalid" : ""}`}
            value={bindings.step ?? 1}
            onChange={(event) => void application.contract.step.set(event.currentTarget.valueAsNumber)}
          />
          {errors.length > 0 && <div className="invalid-feedback">{errors.join(" ")}</div>}
          <button
            className="btn btn-primary w-100 mt-3"
            disabled={!bindings.increment.canExecute || bindings.increment.isRunning}
            onClick={() => void bindings.increment.execute().completion}
          >
            <i className="fa-solid fa-plus me-2" aria-hidden="true" />Increment in C#
          </button>
          <h2 className="h6 mt-4">History</h2>
          <div className="d-flex flex-wrap gap-2">
            {bindings.history.map((value, index) =>
              <span className="badge text-bg-light" key={index}>{value}</span>)}
          </div>
        </div>
      </section>
    </main>
  );
}
