import { Component, input, signal } from "@angular/core";
import type { EditorPageReference, EditorView } from "../../../Frontend/src/generated/editor.js";
import { EditorWrites } from "../../../Frontend/src/editor-writes.js";
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
  private readonly writes = new EditorWrites(cause => this.error.set(cause === undefined ? undefined : String(cause)));

  setTitle(event: Event): void { const value = (event.target as HTMLInputElement).value; this.write(view => view.setTitle(value)); }
  setBody(event: Event): void { const value = (event.target as HTMLTextAreaElement).value; this.write(view => view.setBody(value)); }
  save(): void { this.run(view => this.writes.run(() => view.save())); }
  private write(action: (view: EditorView) => Promise<unknown>): void {
    const view = this.editor.view();
    if (view) this.writes.enqueue(() => action(view));
  }
  private run(action: (view: EditorView) => Promise<unknown>): void {
    const view = this.editor.view();
    if (!view) return;
    void action(view).then(() => this.error.set(undefined)).catch(cause => this.error.set(String(cause)));
  }
}
