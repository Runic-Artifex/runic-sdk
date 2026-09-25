import { Component, input, inject } from "@angular/core";
import type { ShellView } from "../../../Frontend/src/generated/shell.js";
import { bridgeSignal } from "./bridge-signal";
import { WindowOperations } from "./window-operations";
import { SidebarComponent } from "./sidebar";
import { HomeComponent } from "./home";
import { DocumentComponent } from "./document";
import { ConfirmNavigationComponent } from "./confirm-navigation";

@Component({
  selector: "notes-shell",
  imports: [SidebarComponent, HomeComponent, DocumentComponent, ConfirmNavigationComponent],
  template: `
    <header><strong>Composed Notes</strong><span>Plain web component · no ViewModel · {{ operations.ordered ? "ordered" : "baseline" }}</span></header>
    @if (state(); as current) {
      <div class="layout">
        <aside id="sidebar" aria-label="Workspace sidebar">
          <notes-sidebar [page]="current.sidebar" />
        </aside>
        <main id="main">
          @switch (current.main.kind) {
            @case ("home") { <notes-home [page]="current.main" /> }
            @case ("document") { <notes-document [page]="current.main" /> }
            @default never(current.main);
          }
        </main>
      </div>
      @if (current.dialog; as dialog) {
        <notes-confirm-navigation [page]="dialog" />
      }
    }
    <p id="status" class="status" role="status">Connected.</p>
  `,
})
export class ShellComponent {
  readonly shell = input.required<ShellView>();
  readonly state = bridgeSignal(this.shell);
  readonly operations = inject(WindowOperations);
}
