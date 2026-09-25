import { Component, DestroyRef, ElementRef, ViewChild, afterRenderEffect, inject, input, signal } from "@angular/core";
import type { ConfirmNavigationPageReference, ConfirmNavigationView } from "../../../Frontend/src/generated/confirmNavigation.js";
import { pageSignal } from "./bridge-signal";
import { WindowOperations } from "./window-operations";

@Component({
  selector: "notes-confirm-navigation",
  template: `
    <div id="modal" class="modal" (keydown)="keydown($event)">
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title" tabindex="-1">
        <h2 id="dialog-title">Unsaved changes</h2>
        <p data-message>{{ dialog.state()?.message ?? "Connecting…" }}</p>
        <div class="dialog-actions">
          <button #cancelButton data-cancel [disabled]="!dialog.state()?.canCancel" (click)="cancel()">Keep editing</button>
          <button #confirmButton data-confirm [disabled]="!dialog.state()?.canConfirm" (click)="confirm()">Discard changes</button>
        </div>
        @if (error() ?? dialog.error(); as issue) { <p role="alert">{{ issue }}</p> }
        @if (dialog.error()) { <button (click)="dialog.retry()">Retry dialog</button> }
      </div>
    </div>
  `,
})
export class ConfirmNavigationComponent {
  readonly page = input.required<ConfirmNavigationPageReference>();
  readonly dialog = pageSignal(this.page);
  readonly error = signal<string | undefined>(undefined);
  private readonly operations = inject(WindowOperations);
  private readonly destroyRef = inject(DestroyRef);
  private readonly previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  private focused = false;
  private cancelElement: HTMLButtonElement | undefined;
  private confirmElement: HTMLButtonElement | undefined;

  @ViewChild("cancelButton") set cancelButton(ref: ElementRef<HTMLButtonElement> | undefined) {
    this.cancelElement = ref?.nativeElement;
  }
  @ViewChild("confirmButton") set confirmButton(ref: ElementRef<HTMLButtonElement> | undefined) {
    this.confirmElement = ref?.nativeElement;
  }

  constructor() {
    this.destroyRef.onDestroy(() => this.previousFocus?.focus());
    afterRenderEffect({ write: () => {
      if (this.dialog.state()?.canCancel && this.cancelElement && !this.focused) {
        this.cancelElement.focus();
        this.focused = true;
      }
    } });
  }

  cancel(): void { this.run(view => view.cancel()); }
  confirm(): void { this.run(view => view.confirm()); }

  keydown(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.cancel();
    } else if (event.key === "Tab" && this.cancelElement && this.confirmElement) {
      if (event.shiftKey && document.activeElement === this.cancelElement) {
        event.preventDefault(); this.confirmElement.focus();
      } else if (!event.shiftKey && document.activeElement === this.confirmElement) {
        event.preventDefault(); this.cancelElement.focus();
      }
    }
  }

  private run(action: (view: ConfirmNavigationView) => Promise<unknown>): void {
    void this.operations.run(this.dialog.view(), action)
      .then(() => this.error.set(undefined))
      .catch(cause => this.error.set(String(cause)));
  }
}
