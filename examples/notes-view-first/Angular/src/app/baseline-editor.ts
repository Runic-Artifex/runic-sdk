import { Component, inject, input, signal } from "@angular/core";
import type { EditorPageReference, EditorView } from "../../../Frontend/src/generated/editor.js";
import { pageSignal } from "./bridge-signal";
import { WindowOperations } from "./window-operations";

@Component({
  selector: "notes-baseline-editor",
  template: `
    @if (editor.state(); as state) {
      <h2>Editor</h2>
      <label>Title <input [value]="state.title" (change)="changeTitle($event)" /></label>
      <label>Body <textarea [value]="state.body" (change)="changeBody($event)"></textarea></label>
      <button data-save [disabled]="!state.canSave" (click)="save()">Save</button>
      <p data-message role="status">{{ state.isDirty ? "Unsaved changes. " : "" }}{{ state.savedMessage }}</p>
    } @else { <p>Connecting…</p> }
    @if (error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (editor.error()) { <button (click)="editor.retry()">Retry editor</button> }
  `,
})
export class BaselineEditorComponent {
  readonly page = input.required<EditorPageReference>();
  readonly editor = pageSignal(this.page);
  readonly error = signal<string | undefined>(undefined);
  private readonly operations = inject(WindowOperations);

  changeTitle(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.run(view => view.setTitle(value));
  }
  changeBody(event: Event): void {
    const value = (event.target as HTMLTextAreaElement).value;
    this.run(view => view.setBody(value));
  }
  save(): void { this.run(view => view.save()); }

  private run(action: (view: EditorView) => Promise<unknown>): void {
    void this.operations.run(this.editor.view(), action)
      .then(() => this.error.set(undefined))
      .catch(cause => this.error.set(String(cause)));
  }
}
