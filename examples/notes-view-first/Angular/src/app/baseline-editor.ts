import { Component, inject, input, signal } from "@angular/core";
import type { EditorPageReference, EditorClient } from "../../../Frontend/src/generated/editor.js";
import { EditorWrites } from "../../../Frontend/src/editor-writes.js";
import { describeSaveFailure } from "../../../Frontend/src/save-failure.js";
import { injectPage, WindowOperations } from "./window-operations";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";

@Component({
  selector: "notes-baseline-editor",
  template: `
    @if (editor.state(); as state) {
      <h2>Editor</h2>
      <label>Title <input [value]="state.title" (change)="changeTitle($event)" /></label>
      <label>Body <textarea [value]="state.body" (change)="changeBody($event)"></textarea></label>
      <button data-save [disabled]="!state.canSave" (click)="save.run()">Save</button>
      <p data-message role="status">{{ state.isDirty ? "Unsaved changes. " : "" }}{{ state.savedMessage }}</p>
    } @else { <p>Connecting…</p> }
    @if (save.failure(); as failure) { <p role="alert">{{ describeSaveFailure(failure) }}</p> }
    @else if (save.error() ?? writeError() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (editor.error()) { <button (click)="editor.retry()">Retry editor</button> }
  `,
})
export class BaselineEditorComponent {
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectPage(this.page);
  readonly writeError = signal<unknown>(undefined);
  readonly describeSaveFailure = describeSaveFailure;
  private readonly operations = inject(WindowOperations);
  private readonly writes = new EditorWrites(cause => this.writeError.set(cause));
  readonly save = injectCommand(() => this.operations.run(this.editor.client(), view => this.writes.run(() => view.save())));

  changeTitle(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.write(view => view.setTitle(value));
  }
  changeBody(event: Event): void {
    const value = (event.target as HTMLTextAreaElement).value;
    this.write(view => view.setBody(value));
  }
  private write(action: (view: EditorClient) => Promise<unknown>): void {
    const view = this.editor.client();
    if (view) this.writes.enqueue(() => this.operations.run(view, action));
  }
}
