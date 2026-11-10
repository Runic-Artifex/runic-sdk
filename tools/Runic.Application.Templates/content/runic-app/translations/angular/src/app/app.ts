import { Component, computed } from "@angular/core";
import { injectBridgeConnection, injectCommand, injectView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/angular";
import { describeBridgeError } from "@runic-artifex/views";
import { languages, m } from "../translations";
import { connectWorkspace, type WorkspaceState } from "../generated/workspace.js";
import { CounterComponent } from "./counter";
import { WelcomeComponent } from "./welcome";

const pages = { counter: CounterComponent, welcome: WelcomeComponent } satisfies ViewRegistry<WorkspaceState["main"]>;
// While the Bridge reconnects, the clients keep the last state the Window sent.
const statusText: Record<BridgeConnectionState, () => string> = {
  connecting: m.status_connecting,
  connected: m.status_connected,
  reconnecting: m.status_stale,
  lost: m.status_stale,
  // The application closed the Window; a connection drop that follows is not an error.
  closed: m.status_closed,
};

@Component({
  selector: "runic-app",
  imports: [ViewOutlet],
  templateUrl: "./app.html"
})
export class AppComponent {
  readonly workspace = injectView({ connect: connectWorkspace });
  readonly state = this.workspace.state;
  readonly connection = injectBridgeConnection().state;
  readonly navigate = injectCommand((name: "showWelcome" | "showCounter") => this.workspace.client()?.[name]());
  // The application owns the language: the page asks for one, and every window follows.
  readonly choose = injectCommand((language: string) => this.workspace.client()?.chooseLanguage(language));
  readonly status = computed(() => {
    const error = this.navigate.error() ?? this.choose.error() ?? this.workspace.error();
    return error !== undefined ? describeBridgeError(error) : statusText[this.connection()]();
  });
  readonly pages = pages;
  readonly m = m;
  readonly languages = languages;
}
