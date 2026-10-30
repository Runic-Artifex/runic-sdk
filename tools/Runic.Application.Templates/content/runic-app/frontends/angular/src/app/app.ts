import { Component, computed } from "@angular/core";
import { injectBridgeConnection, injectCommand, injectView, ViewOutlet, type BridgeConnectionState, type ViewRegistry } from "@runic-artifex/angular";
import { describeBridgeError } from "@runic-artifex/views";
import { connectWorkspace, type WorkspaceState } from "../generated/workspace.js";
import { CounterComponent } from "./counter";
import { WelcomeComponent } from "./welcome";

const pages = { counter: CounterComponent, welcome: WelcomeComponent } satisfies ViewRegistry<WorkspaceState["main"]>;
// While the Bridge reconnects, the clients keep the last state the Window sent.
const statusText: Record<BridgeConnectionState, string> = {
  connecting: "Connecting to the .NET Window…",
  connected: "Connected to the .NET Window.",
  reconnecting: "Showing the last state the .NET Window sent.",
  lost: "Showing the last state the .NET Window sent.",
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
  readonly status = computed(() => {
    const error = this.navigate.error() ?? this.workspace.error();
    return error !== undefined ? describeBridgeError(error) : statusText[this.connection()];
  });
  readonly pages = pages;
}
