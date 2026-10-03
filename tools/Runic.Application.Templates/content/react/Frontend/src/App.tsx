import { useState } from "react";
import { useView } from "@runic-artifex/react";
import { connectWorkspace } from "./generated/workspace.js";
import { CounterPage } from "./pages/CounterPage";
import { WelcomePage } from "./pages/WelcomePage";

const workspace = { connect: connectWorkspace };

export default function App() {
  const { state, client, error: connection } = useView(workspace);
  const [error, setError] = useState<string>();

  async function run(command: () => Promise<unknown>) {
    try { await command(); setError(undefined); }
    catch (cause) { setError(String(cause)); }
  }

  const page = state?.main;
  return <main>
    <header><h1>Runic Views</h1><p>Window/View starter · React</p></header>
    <nav aria-label="Main navigation">
      <button disabled={!client} onClick={() => client && run(() => client.showWelcome())}>Welcome</button>
      <button disabled={!client} onClick={() => client && run(() => client.showCounter())}>Counter</button>
    </nav>
    {page?.kind === "counter" ? <CounterPage key={page.kind} page={page} />
      : page?.kind === "welcome" ? <WelcomePage key={page.kind} page={page} />
      : <p>Connecting to the Window…</p>}
    <p role="status">{error ?? (connection ? String(connection) : "Connected to the .NET Window.")}</p>
  </main>;
}
