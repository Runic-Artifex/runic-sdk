import { Component, inject, input, signal } from "@angular/core";
import type { SidebarPageReference, SidebarClient } from "../../../Frontend/src/generated/sidebar.js";
import { injectPage, WindowOperations } from "./window-operations";

@Component({
  selector: "notes-sidebar",
  template: `
    <h2>Workspace</h2>
    @if (sidebar.state(); as state) {
      <nav aria-label="Workspace navigation">
        <button data-go="home" [attr.aria-current]="state.selected === 'Home' ? 'page' : null"
          [disabled]="!state.canOpenHome" (click)="openHome()">Home</button>
        <button data-go="notes" [attr.aria-current]="state.selected === 'Notes' ? 'page' : null"
          [disabled]="!state.canOpenNotes" (click)="openNotes()">Notes</button>
      </nav>
    } @else { <p>Connecting…</p> }
    @if (error() ?? sidebar.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (sidebar.error()) { <button (click)="sidebar.retry()">Retry sidebar</button> }
  `,
})
export class SidebarComponent {
  readonly page = input.required<SidebarPageReference>();
  readonly sidebar = injectPage(this.page);
  readonly error = signal<string | undefined>(undefined);
  private readonly operations = inject(WindowOperations);

  openHome(): void { this.run(view => view.openHome()); }
  openNotes(): void { this.run(view => view.openNotes()); }

  private run(action: (view: SidebarClient) => Promise<unknown>): void {
    void this.operations.run(this.sidebar.client(), action)
      .then(() => this.error.set(undefined))
      .catch(cause => this.error.set(String(cause)));
  }
}
