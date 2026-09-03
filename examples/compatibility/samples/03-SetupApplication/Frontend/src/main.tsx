import { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { setupBridge } from "./setup-bridge";
import type {
  FeatureId,
  SetupEvent,
  SetupSnapshot,
  SetupViewId,
} from "./application.bridge";
import "./styles.css";

const initialSnapshot: SetupSnapshot = {
  viewId: "Welcome",
  revision: 0,
  selectedFeatures: ["core"],
  canNavigateBack: false,
  canNavigateNext: true,
};
const views: SetupViewId[] = ["Welcome", "Destination", "Features", "Installing", "Complete"];

createRoot(document.querySelector("#app")!).render(<SetupApplication />);
globalThis.addEventListener("pagehide", () => void setupBridge.dispose(), { once: true });

function SetupApplication() {
  const [snapshot, setSnapshot] = useState<SetupSnapshot>(initialSnapshot);
  const [features, setFeatures] = useState<ReadonlyArray<FeatureId>>(["core"]);
  const [progress, setProgress] = useState({ completed: 0, total: 5, message: "Preparing" });
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<string>();

  useEffect(() => {
    const unsubscribe = setupBridge.subscribe(
      (event) => applyEvent(event, setSnapshot, setProgress, setError),
      (failure) => setError(failure.message),
    );
    void setupBridge.initialize()
      .then(async (value) => {
        setSnapshot(value);
        await setupBridge.uiReady();
        await setupBridge.uiRendered();
      })
      .catch((failure: Error) => setError(failure.message))
      .finally(() => setBusy(false));
    return unsubscribe;
  }, []);

  const nextTarget = useMemo(() => ({
    Welcome: "Destination",
    Destination: "Features",
    Features: "Installing",
    Installing: "Installing",
    Complete: "Complete",
  } satisfies Record<SetupViewId, SetupViewId>)[snapshot.viewId], [snapshot.viewId]);
  const backTarget = useMemo(() => ({
    Welcome: "Welcome",
    Destination: "Welcome",
    Features: "Destination",
    Installing: "Installing",
    Complete: "Complete",
  } satisfies Record<SetupViewId, SetupViewId>)[snapshot.viewId], [snapshot.viewId]);

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try { await action(); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "The command failed."); }
    finally { setBusy(false); }
  };
  const navigate = (target: SetupViewId) => run(async () => {
    const receipt = await setupBridge.dispatch({ _tag: "Navigate", target, expectedRevision: snapshot.revision });
    if (receipt._tag === "NavigationAccepted") setSnapshot(receipt.snapshot);
  });
  const selectDestination = () => run(async () => {
    const receipt = await setupBridge.dispatch({
      _tag: "SelectDestination",
      ...(snapshot.destination === undefined ? {} : { currentSelectionId: snapshot.destination.selectionId }),
    });
    if (receipt._tag === "DestinationSelected") {
      setSnapshot((current) => ({ ...current, destination: receipt.destination, revision: receipt.revision, canNavigateNext: true }));
    }
  });
  const startInstallation = () => run(async () => {
    if (snapshot.destination === undefined) throw new Error("Select a destination before installing.");
    setProgress({ completed: 0, total: 5, message: "Starting" });
    const receipt = await setupBridge.dispatch({
      _tag: "StartInstallation",
      destinationSelectionId: snapshot.destination.selectionId,
      selectedFeatures: features,
    });
    if (receipt._tag === "InstallationStarted") {
      setSnapshot((current) => current.viewId === "Complete" ? current : {
        ...current,
        viewId: "Installing",
        revision: receipt.revision,
        selectedFeatures: features,
        activeOperationId: receipt.operationId,
        canNavigateBack: false,
        canNavigateNext: false,
      });
    }
  });
  const reconnect = () => run(async () => setSnapshot(await setupBridge.reconnect()));
  const cancel = () => run(async () => {
    if (snapshot.activeOperationId !== undefined) await setupBridge.cancel(snapshot.activeOperationId);
  });

  return (
    <div className="shell">
      <header className="header">
        <div>
          <p className="eyebrow">Runic Toolkit reference vertical</p>
          <h1>Setup Application</h1>
        </div>
        <span className="status">Authoritative revision {snapshot.revision}</span>
      </header>
      <ol className="steps">
        {views.map((view) => <li className={snapshot.viewId === view ? "active" : ""} key={view}>{view}</li>)}
      </ol>
      <section className="content">
        {snapshot.viewId === "Welcome" && <>
          <h2>Install a neutral application</h2>
          <p>This wizard demonstrates domain commands and host-owned workflows without exposing ViewModels, filesystem paths, or generic property mutation.</p>
        </>}
        {snapshot.viewId === "Destination" && <>
          <h2>Choose a destination</h2>
          <p>The browser requests a selection. Only the native backend can choose and validate it; the frontend receives an opaque handle and display-safe metadata.</p>
          <div className="destination">
            <div><strong>{snapshot.destination?.displayName ?? "No destination selected"}</strong><br/><small>{snapshot.destination === undefined ? "A path is never sent over the bridge." : `${(snapshot.destination.availableBytes / 1_000_000_000).toFixed(1)} GB available`}</small></div>
            <button onClick={() => void selectDestination()} disabled={busy}>Select in native host</button>
          </div>
        </>}
        {snapshot.viewId === "Features" && <>
          <h2>Select features</h2>
          <p>React owns this transient selection. The backend validates it when the named <code>StartInstallation</code> command arrives.</p>
          <div className="features">
            {(["core", "desktop-shortcut", "examples"] as const).map((feature) => <label className="feature" key={feature}>
              <input type="checkbox" checked={features.includes(feature)} disabled={feature === "core" || busy} onChange={(event) => setFeatures((current) => event.currentTarget.checked ? [...current, feature] : current.filter((item) => item !== feature))}/>
              <span><strong>{feature}</strong><br/><small>{feature === "core" ? "Required application files" : "Optional host-validated component"}</small></span>
            </label>)}
          </div>
        </>}
        {snapshot.viewId === "Installing" && <>
          <h2>Installing</h2>
          <p>The start receipt returned an operation ID immediately. Progress and the terminal outcome arrive through the validated Effect Stream.</p>
          <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={progress.total} aria-valuenow={progress.completed}><span style={{ width: `${progress.completed / progress.total * 100}%` }}/></div>
          <p>{progress.message} · {progress.completed}/{progress.total}</p>
        </>}
        {snapshot.viewId === "Complete" && <>
          <h2>Installation complete</h2>
          <p>The backend advanced the revision, published a terminal operation event, and moved the application to its authoritative final view.</p>
        </>}
        {error && <p className="error">{error}</p>}
      </section>
      <footer className="footer">
        <div><button onClick={() => void reconnect()} disabled={busy}>Reconnect</button></div>
        <div>
          <button onClick={() => void navigate(backTarget)} disabled={busy || !snapshot.canNavigateBack}>Back</button>
          {snapshot.viewId === "Features"
            ? <button className="primary" onClick={() => void startInstallation()} disabled={busy}>Install</button>
            : snapshot.viewId === "Installing"
              ? <button onClick={() => void cancel()} disabled={busy || snapshot.activeOperationId === undefined}>Cancel operation</button>
              : snapshot.viewId !== "Complete" && <button className="primary" onClick={() => void navigate(nextTarget)} disabled={busy || !snapshot.canNavigateNext}>Next</button>}
        </div>
      </footer>
    </div>
  );
}

function applyEvent(
  event: SetupEvent,
  setSnapshot: React.Dispatch<React.SetStateAction<SetupSnapshot>>,
  setProgress: React.Dispatch<React.SetStateAction<{ completed: number; total: number; message: string }>>,
  setError: React.Dispatch<React.SetStateAction<string | undefined>>,
) {
  if (event._tag === "SnapshotReplaced") setSnapshot(event.snapshot);
  else if (event._tag === "NavigationChanged") setSnapshot((current) => ({ ...current, viewId: event.viewId, revision: event.revision }));
  else if (event._tag === "OperationProgress") setProgress({ completed: event.completed, total: event.total, message: event.message ?? "Working" });
  else if (event._tag === "OperationCompleted") setSnapshot((current) => ({ ...current, viewId: "Complete", revision: event.revision, activeOperationId: undefined, canNavigateBack: false, canNavigateNext: false }));
  else if (event._tag === "OperationCancelled") setSnapshot((current) => ({ ...current, viewId: "Features", revision: event.revision, activeOperationId: undefined, canNavigateBack: true, canNavigateNext: true }));
  else if (event._tag === "OperationFailed") {
    setError(event.error);
    setSnapshot((current) => ({ ...current, viewId: "Features", revision: event.revision, activeOperationId: undefined, canNavigateBack: true, canNavigateNext: true }));
  }
}
