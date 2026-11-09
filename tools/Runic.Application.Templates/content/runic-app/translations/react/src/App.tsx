import { useBridgeConnection, useCommand, useTextLocale, useView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/react";
import { describeBridgeError } from "@runic-artifex/views";
import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
import { CounterPage } from "./pages/CounterPage";
import { WelcomePage } from "./pages/WelcomePage";
import { m } from "./translations";

const workspace = { connect: connectWorkspace };
const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
// While the Bridge reconnects, the clients keep the last state the Window sent.
const statusText: Record<BridgeConnectionState, string> = {
  connecting: "Connecting to the .NET Window…",
  connected: "Connected to the .NET Window.",
  reconnecting: "Showing the last state the .NET Window sent.",
  lost: "Showing the last state the .NET Window sent.",
  // The application closed the Window; a connection drop that follows is not an error.
  closed: "The application closed this window.",
};

export default function App() {
  const { state, client, error: connectError } = useView(workspace);
  const connection = useBridgeConnection().state;
  const navigate = useCommand((name: "showWelcome" | "showCounter") => client?.[name]());
  const choose = useCommand((language: string) => client?.chooseLanguage(language));
  const error = navigate.error ?? choose.error ?? connectError;
  // Renders the m.*() text again when the window's language changes.
  useTextLocale();

  return <main>
    <header>
      <h1>{m.app_title()}</h1><p>Window/View starter · React</p>
      {/* The application owns the language: the page asks for one, and every window follows. */}
      <label>{m.app_language()} <select value={state?.language ?? ""} disabled={!client}
        onChange={event => void choose.run(event.currentTarget.value)}>
        <option value="en">English</option>
        <option value="de">Deutsch</option>
      </select></label>
    </header>
    {connection === "reconnecting" && <p role="status">Reconnecting to the .NET Window…</p>}
    {connection === "lost" && <p role="alert">The connection to the .NET Window was lost. The page reconnects when the application is back.</p>}
    <nav aria-label="Main navigation">
      <button disabled={!client} onClick={() => void navigate.run("showWelcome")}>Welcome</button>
      <button disabled={!client} onClick={() => void navigate.run("showCounter")}>Counter</button>
    </nav>
    <ViewOutlet content={state?.main} registry={pages} fallback={<p>Connecting to the Window…</p>} />
    <p role="status">{error !== undefined ? describeBridgeError(error) : statusText[connection]}</p>
  </main>;
}
