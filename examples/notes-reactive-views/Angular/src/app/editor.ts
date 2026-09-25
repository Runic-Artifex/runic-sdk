import { Component, input, signal } from "@angular/core";
import type { EditorPageReference, EditorView } from "../../../Frontend/src/generated/editor.js";
import { pageSignal } from "./bridge-signal";

@Component({
  selector: "notes-editor",
  template: `
    @if (editor.state(); as state) {
      <h2>Full editor</h2>
      <label>Title<input data-title [value]="state.title" (change)="setTitle($event)" /></label>
      <label>Body<textarea data-body [value]="state.body" (change)="setBody($event)"></textarea></label>
      <button data-save [disabled]="!state.canSave" (click)="save()">Save</button>
      <p data-message role="status">{{ state.savedMessage }}</p>
      <p data-activation class="muted">Activated {{ state.activationCount }} × · deactivated {{ state.deactivationCount }} ×</p>
    } @else { <p>Connecting…</p> }
    @if (error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class EditorComponent {
  readonly page = input.required<EditorPageReference>();
  readonly editor = pageSignal(this.page);
  readonly error = signal<string | undefined>(undefined);

  setTitle(event: Event): void { this.run(view => view.setTitle((event.target as HTMLInputElement).value)); }
  setBody(event: Event): void { this.run(view => view.setBody((event.target as HTMLTextAreaElement).value)); }
  save(): void { this.run(view => view.save()); }
  private run(action: (view: EditorView) => Promise<unknown>): void {
    const view = this.editor.view();
    if (!view) return;
    void action(view).then(() => this.error.set(undefined)).catch(cause => this.error.set(String(cause)));
  }
}
