import { Component, DestroyRef, inject, signal } from "@angular/core";
import { connectShell, type ShellView } from "../../../Frontend/src/generated/shell.js";
import { ShellComponent } from "./shell";

@Component({
  selector: "app-root",
  imports: [ShellComponent],
  template: `
    @if (shell(); as connected) { <notes-shell [shell]="connected" /> }
    @else { <p id="status" role="status">{{ error() ?? "Connecting…" }}</p> }
  `,
})
export class App {
  private readonly destroyRef = inject(DestroyRef);
  private connected: ShellView | undefined;
  readonly shell = signal<ShellView | undefined>(undefined);
  readonly error = signal<string | undefined>(undefined);

  constructor() {
    this.destroyRef.onDestroy(() => this.connected?.dispose());
    void connectShell().then(view => {
      if (this.destroyRef.destroyed) { view.dispose(); return; }
      this.connected = view;
      this.shell.set(view);
    }).catch(cause => { if (!this.destroyRef.destroyed) this.error.set(String(cause)); });
  }
}
