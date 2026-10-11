import { Component, computed } from "@angular/core";
import { injectCommand, injectView, ViewOutlet, type ViewRegistry } from "@runic-artifex/angular";
import { connectWorkspace, type WorkspaceState } from "../generated/workspace.js";
import { CounterComponent } from "./counter";
import { WelcomeComponent } from "./welcome";

const pages = { counter: CounterComponent, welcome: WelcomeComponent } satisfies ViewRegistry<WorkspaceState["main"]>;

@Component({
  selector: "runic-app",
  imports: [ViewOutlet],
  templateUrl: "./app.html"
})
export class AppComponent {
  readonly workspace = injectView({ connect: connectWorkspace });
  readonly state = this.workspace.state;
  readonly navigate = injectCommand((name: "showWelcome" | "showCounter") => this.workspace.client()?.[name]());
  readonly status = computed(() => String(this.navigate.error() ?? this.workspace.error() ?? "Connected to the .NET Window."));
  readonly pages = pages;
}
