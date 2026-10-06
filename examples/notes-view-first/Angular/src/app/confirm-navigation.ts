import { Component, DestroyRef, ElementRef, ViewChild, afterRenderEffect, inject, input } from "@angular/core";
import type { ConfirmNavigationPageReference } from "../../../Frontend/src/generated/confirmNavigation.js";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectPage, WindowOperations } from "./window-operations";

@Component({
  selector: "notes-confirm-navigation",
  template: `
    <div id="modal" class="modal" (keydown)="keydown($event)">
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title" tabindex="-1">
        <h2 id="dialog-title">Unsaved changes</h2>
        <p data-message>{{ dialog.state()?.message ?? "Connecting…" }}</p>
        <div class="dialog-actions">
          <button #cancelButton data-cancel [disabled]="!dialog.state()?.canCancel" (click)="answer.run('cancel')">Keep editing</button>
          <button #confirmButton data-confirm [disabled]="!dialog.state()?.canConfirm" (click)="answer.run('confirm')">Discard changes</button>
        </div>
        @if (answer.error() ?? dialog.error(); as issue) { <p role="alert">{{ issue }}</p> }
        @if (dialog.error()) { <button (click)="dialog.retry()">Retry dialog</button> }
      </div>
    </div>
  `,
})
export class ConfirmNavigationComponent {
  readonly page = input.required<ConfirmNavigationPageReference>();
  readonly dialog = injectPage(this.page);
  private readonly operations = inject(WindowOperations);
  readonly answer = injectCommand((name: "cancel" | "confirm") =>
    this.operations.run(this.dialog.client(), view => view[name]()));
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

  keydown(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      void this.answer.run("cancel");
    } else if (event.key === "Tab" && this.cancelElement && this.confirmElement) {
      if (event.shiftKey && document.activeElement === this.cancelElement) {
        event.preventDefault(); this.confirmElement.focus();
      } else if (!event.shiftKey && document.activeElement === this.confirmElement) {
        event.preventDefault(); this.cancelElement.focus();
      }
    }
  }
}
