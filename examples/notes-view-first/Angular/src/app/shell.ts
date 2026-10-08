import { Component, input, inject } from "@angular/core";
import type { ShellClient } from "../../../Frontend/src/generated/shell.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";
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
          @if (current.main; as page) {
            @switch (page.kind) {
              @case ("home") { <notes-home [page]="page" /> }
              @case ("document") { <notes-document [page]="page" /> }
              @default never(page);
            }
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
  readonly shell = input.required<ShellClient>();
  readonly state = injectView(this.shell).state;
  readonly operations = inject(WindowOperations);
}
