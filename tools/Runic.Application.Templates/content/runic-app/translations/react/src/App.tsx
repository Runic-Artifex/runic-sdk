import { useBridgeConnection, useCommand, useTextLocale, useView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/react";
import { describeBridgeError } from "@runic-artifex/views";
import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
import { CounterPage } from "./pages/CounterPage";
import { WelcomePage } from "./pages/WelcomePage";
import { languages, m } from "./translations";

const workspace = { connect: connectWorkspace };
const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
// While the Bridge reconnects, the clients keep the last state the Window sent.
const statusText: Record<BridgeConnectionState, () => string> = {
  connecting: m.status_connecting,
  connected: m.status_connected,
  reconnecting: m.status_stale,
  lost: m.status_stale,
  // The application closed the Window; a connection drop that follows is not an error.
  closed: m.status_closed,
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
      <h1>{m.app_title()}</h1><p>{m.app_subtitle({ frontend: "React" })}</p>
      {/* The application owns the language: the page asks for one, and every window follows. */}
      <label>{m.app_language()} <select value={state?.language ?? ""} disabled={!client}
        onChange={event => void choose.run(event.currentTarget.value)}>
        {languages.map(language => <option key={language.tag} value={language.tag}>{language.name}</option>)}
      </select></label>
    </header>
    {connection === "reconnecting" && <p role="status">{m.status_reconnecting()}</p>}
    {connection === "lost" && <p role="alert">{m.status_lost()}</p>}
    <nav aria-label={m.nav_label()}>
      <button disabled={!client} onClick={() => void navigate.run("showWelcome")}>{m.nav_welcome()}</button>
      <button disabled={!client} onClick={() => void navigate.run("showCounter")}>{m.nav_counter()}</button>
    </nav>
    <ViewOutlet content={state?.main} registry={pages} fallback={<p>{m.app_loading()}</p>} />
    <p role="status">{error !== undefined ? describeBridgeError(error) : statusText[connection]()}</p>
  </main>;
}
