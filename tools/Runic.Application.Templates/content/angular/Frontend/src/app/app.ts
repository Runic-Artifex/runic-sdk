import { Component, OnDestroy, OnInit, signal } from "@angular/core";
import { RunicViewOutlet, type ViewRegistry } from "@runic-artifex/angular";
import { connectWorkspace, type WorkspaceState, type WorkspaceView } from "../generated/workspace.js";
import { CounterComponent } from "./counter";
import { WelcomeComponent } from "./welcome";

const pages = { counter: CounterComponent, welcome: WelcomeComponent } satisfies ViewRegistry<WorkspaceState["main"]>;

@Component({
  selector: "runic-app",
  imports: [RunicViewOutlet],
  templateUrl: "./app.html"
})
export class AppComponent implements OnInit, OnDestroy {
  readonly state = signal<WorkspaceState | undefined>(undefined);
  readonly workspace = signal<WorkspaceView | undefined>(undefined);
  readonly error = signal<string | undefined>(undefined);
  readonly pages = pages;
  private unsubscribe = () => {};
  private active = true;

  ngOnInit(): void {
    void connectWorkspace().then(client => {
      if (!this.active) { client.dispose(); return; }
      this.workspace.set(client);
      this.unsubscribe = client.subscribe(next => this.state.set(next));
    }).catch(cause => this.error.set(String(cause)));
  }

  ngOnDestroy(): void {
    this.active = false;
    this.unsubscribe();
    this.workspace()?.dispose();
  }

  showWelcome(): void {
    const client = this.workspace();
    if (client) void client.showWelcome().then(() => this.error.set(undefined)).catch(cause => this.error.set(String(cause)));
  }

  showCounter(): void {
    const client = this.workspace();
    if (client) void client.showCounter().then(() => this.error.set(undefined)).catch(cause => this.error.set(String(cause)));
  }
}
