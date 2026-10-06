import { useCommand, useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/react";
import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
import { CounterPage } from "./pages/CounterPage";
import { WelcomePage } from "./pages/WelcomePage";

const workspace = { connect: connectWorkspace };
const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;

export default function App() {
  const { state, client, error: connection } = useView(workspace);
  const navigate = useCommand((name: "showWelcome" | "showCounter") => client?.[name]());

  return <main>
    <header><h1>Runic Views</h1><p>Window/View starter · React</p></header>
    <nav aria-label="Main navigation">
      <button disabled={!client} onClick={() => void navigate.run("showWelcome")}>Welcome</button>
      <button disabled={!client} onClick={() => void navigate.run("showCounter")}>Counter</button>
    </nav>
    <ViewOutlet content={state?.main} registry={pages} fallback={<p>Connecting to the Window…</p>} />
    <p role="status">{String(navigate.error ?? connection ?? "Connected to the .NET Window.")}</p>
  </main>;
}
