import { Component, inject, input } from "@angular/core";
import type { SidebarPageReference } from "../../../Frontend/src/generated/sidebar.js";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectPage, WindowOperations } from "./window-operations";

@Component({
  selector: "notes-sidebar",
  template: `
    <h2>Workspace</h2>
    @if (sidebar.state(); as state) {
      <nav aria-label="Workspace navigation">
        <button data-go="home" [attr.aria-current]="state.selected === 'Home' ? 'page' : null"
          [disabled]="!state.canOpenHome" (click)="open.run('openHome')">Home</button>
        <button data-go="notes" [attr.aria-current]="state.selected === 'Notes' ? 'page' : null"
          [disabled]="!state.canOpenNotes" (click)="open.run('openNotes')">Notes</button>
      </nav>
    } @else { <p>Connecting…</p> }
    @if (open.error() ?? sidebar.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (sidebar.error()) { <button (click)="sidebar.retry()">Retry sidebar</button> }
  `,
})
export class SidebarComponent {
  readonly page = input.required<SidebarPageReference>();
  readonly sidebar = injectPage(this.page);
  private readonly operations = inject(WindowOperations);
  // A navigation completes only after the document's guard is answered in the dialog, a later
  // window command. Ordering its completion would queue that answer behind it, so it is
  // ordered by dispatch.
  readonly open = injectCommand((name: "openHome" | "openNotes") =>
    this.operations.runDispatched(this.sidebar.client(), view => view[name]()));
}
