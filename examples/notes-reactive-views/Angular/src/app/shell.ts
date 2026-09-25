import { Component, input, signal } from "@angular/core";
import type { ShellView, ShellState } from "../../../Frontend/src/generated/shell.js";
import { bridgeSignal } from "./bridge-signal";
import { HomeComponent } from "./home";
import { DocumentComponent } from "./document";
import { RunicViewOutlet, type ViewRegistry } from "../../../../../packages/web/angular/src/view-outlet";

const mainViews = { home: HomeComponent, document: DocumentComponent } satisfies ViewRegistry<ShellState["main"]>;

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
    <p id="status" class="status" role="status">{{ error() ?? "Connected." }}</p>
  `,
})
export class ShellComponent {
  readonly shell = input.required<ShellView>();
  readonly state = bridgeSignal(this.shell);
  readonly mainViews = mainViews;
  readonly error = signal<string | undefined>(undefined);

  openHome(): void { void this.shell().openHome().catch(cause => this.error.set(String(cause))); }
  openDocument(): void { void this.shell().openDocument().catch(cause => this.error.set(String(cause))); }
}
