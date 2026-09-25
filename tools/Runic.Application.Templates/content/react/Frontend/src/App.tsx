import { useEffect, useState } from "react";
import { connectWorkspace, type WorkspaceState, type WorkspaceView as WorkspaceClient } from "./generated/workspace.js";
import { CounterPage } from "./pages/CounterPage";
import { WelcomePage } from "./pages/WelcomePage";

export default function App() {
  const [workspace, setWorkspace] = useState<WorkspaceClient>();
  const [state, setState] = useState<WorkspaceState>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let active = true;
    let unsubscribe = () => {};
    let client: WorkspaceClient | undefined;
    void connectWorkspace().then(connected => {
      if (!active) { connected.dispose(); return; }
      client = connected;
      setWorkspace(connected);
      unsubscribe = connected.subscribe(next => setState(next));
    }).catch(cause => setError(String(cause)));
    return () => { active = false; unsubscribe(); client?.dispose(); };
  }, []);

  async function run(command: () => Promise<unknown>) {
    try { await command(); setError(undefined); }
    catch (cause) { setError(String(cause)); }
  }

  const page = state?.main;
  return <main>
    <header><h1>Runic Views</h1><p>Window/View starter · React</p></header>
    <nav aria-label="Main navigation">
      <button onClick={() => workspace && run(() => workspace.showWelcome())}>Welcome</button>
      <button onClick={() => workspace && run(() => workspace.showCounter())}>Counter</button>
    </nav>
    {page?.kind === "counter" ? <CounterPage key={page.kind} page={page} />
      : page?.kind === "welcome" ? <WelcomePage key={page.kind} page={page} />
      : <p>Connecting to the Window…</p>}
    <p role="status">{error ?? "Connected to the .NET Window."}</p>
  </main>;
}
