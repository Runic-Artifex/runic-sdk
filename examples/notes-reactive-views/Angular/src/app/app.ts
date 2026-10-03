import { Component, computed } from "@angular/core";
import { connectShell } from "../../../Frontend/src/generated/shell.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";
import { ShellComponent } from "./shell";

@Component({
  selector: "app-root",
  imports: [ShellComponent],
  template: `
    @if (root.client(); as connected) { <notes-shell [shell]="connected" /> }
    @else { <p id="status" role="status">{{ error() ?? "Connecting…" }}</p> }
  `,
})
export class App {
  readonly root = injectView({ connect: connectShell });
  readonly error = computed(() => this.root.error() === undefined ? undefined : String(this.root.error()));
}
