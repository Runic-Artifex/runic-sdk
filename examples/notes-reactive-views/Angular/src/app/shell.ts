import { Component, input, signal } from "@angular/core";
import type { ShellView, ShellState } from "../../../Frontend/src/generated/shell.js";
import { bridgeSignal } from "./bridge-signal";
import { HomeComponent } from "./home";
import { DocumentComponent } from "./document";
import { PinnedNoteComponent } from "./pinned-note";
import { PinnedTaskComponent } from "./pinned-task";
import { RunicViewOutlet, type ViewRegistry } from "../../../../../packages/web/angular/src/view-outlet";

const mainViews = { home: HomeComponent, document: DocumentComponent } satisfies ViewRegistry<ShellState["main"]>;
const pinnedViews = { pinnedNote: PinnedNoteComponent, pinnedTask: PinnedTaskComponent } satisfies ViewRegistry<ShellState["pinned"][number]>;

@Component({
  selector: "notes-shell",
  imports: [RunicViewOutlet],
  template: `
    <header><strong>Reactive Notes</strong><span>Angular · two View contracts</span></header>
    @if (state(); as current) {
      <div class="layout">
        <nav aria-label="Main navigation">
          <button data-go="home" [attr.aria-current]="current.main.kind === 'home' ? 'page' : null" (click)="openHome()">Home</button>
          <button data-go="document" [attr.aria-current]="current.main.kind === 'document' ? 'page' : null" (click)="openDocument()">Document</button>
        </nav>
        <main id="main"><runic-view-outlet [content]="current.main" [registry]="mainViews" /></main>
      </div>
    }
    @if (state(); as current) {
      <section id="pinned" aria-label="Pinned Views">
        <button data-pinned-action="swap" (click)="swapPinned()">Reorder pinned</button>
        <button data-pinned-action="remove" (click)="removePinned()">Remove pinned note</button>
        <button data-pinned-action="restore" (click)="restorePinned()">Restore pinned note</button>
        @for (item of current.pinned; track item) {
          <span [attr.data-pin]="item.kind"><runic-view-outlet [content]="item" [registry]="pinnedViews" /></span>
        }
      </section>
    }
    <p id="status" class="status" role="status">{{ error() ?? "Connected." }}</p>
  `,
})
export class ShellComponent {
  readonly shell = input.required<ShellView>();
  readonly state = bridgeSignal(this.shell);
  readonly mainViews = mainViews;
  readonly pinnedViews = pinnedViews;
  readonly error = signal<string | undefined>(undefined);

  openHome(): void { void this.shell().openHome().catch(cause => this.error.set(String(cause))); }
  openDocument(): void { void this.shell().openDocument().catch(cause => this.error.set(String(cause))); }
  swapPinned(): void { void this.shell().swapPinned().catch(cause => this.error.set(String(cause))); }
  removePinned(): void { void this.shell().removePinned().catch(cause => this.error.set(String(cause))); }
  restorePinned(): void { void this.shell().restorePinned().catch(cause => this.error.set(String(cause))); }
}
