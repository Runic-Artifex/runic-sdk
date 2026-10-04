import { Component, computed, signal } from "@angular/core";
import { injectView, RunicViewOutlet, type ViewRegistry } from "@runic-artifex/angular";
import { connectWorkspace, type WorkspaceClient, type WorkspaceState } from "../generated/workspace.js";
import { CounterComponent } from "./counter";
import { WelcomeComponent } from "./welcome";

const pages = { counter: CounterComponent, welcome: WelcomeComponent } satisfies ViewRegistry<WorkspaceState["main"]>;

@Component({
  selector: "runic-app",
  imports: [RunicViewOutlet],
  templateUrl: "./app.html"
})
export class AppComponent {
  readonly workspace = injectView({ connect: connectWorkspace });
  readonly state = this.workspace.state;
  readonly commandError = signal<string | undefined>(undefined);
  readonly error = computed(() => this.commandError()
    ?? (this.workspace.error() === undefined ? undefined : String(this.workspace.error())));
  readonly pages = pages;

  showWelcome(): void { this.run(client => client.showWelcome()); }
  showCounter(): void { this.run(client => client.showCounter()); }

  private run(command: (client: WorkspaceClient) => Promise<unknown>): void {
    const client = this.workspace.client();
    if (client) void command(client).then(() => this.commandError.set(undefined)).catch(cause => this.commandError.set(String(cause)));
  }
}
