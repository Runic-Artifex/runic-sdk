import { Component, DestroyRef, inject, input } from "@angular/core";
import { EditorFields, type EditorPageReference } from "../../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../../Frontend/src/save-failure.js";
import { injectPage, WindowOperations } from "./window-operations";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectField } from "../../../../../packages/web/angular/src/inject-field";

// The variant without forms: the fields are bound by hand in the template.
@Component({
  selector: "notes-baseline-editor",
  template: `
    @if (editor.state(); as state) {
      <h2>Editor</h2>
      <label>Title <input [value]="title.text()" (input)="title.inputText($event)" (blur)="title.commit()"
        (compositionstart)="title.compositionStart()" (compositionend)="title.compositionEnd($event)" /></label>
      <label>Body <textarea [value]="body.text()" (input)="body.inputText($event)" (blur)="body.commit()"
        (compositionstart)="body.compositionStart()" (compositionend)="body.compositionEnd($event)"></textarea></label>
      <button data-save [disabled]="!state.canSave" (click)="save.run()">Save</button>
      <p data-message role="status">{{ state.isDirty ? "Unsaved changes. " : "" }}{{ state.savedMessage }}</p>
    } @else { <p>Connecting…</p> }
    @if (save.failure(); as failure) { <p role="alert">{{ describeSaveFailure(failure) }}</p> }
    @else if (save.error() ?? title.error() ?? body.error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (editor.error()) { <button (click)="editor.retry()">Retry editor</button> }
  `,
})
export class BaselineEditorComponent {
  // Registered before the page, so text that waits for its pause is written before the editor is released.
  private readonly flushOnDestroy = inject(DestroyRef).onDestroy(() => { void this.title.commit(); void this.body.commit(); });
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectPage(this.page);
  readonly describeSaveFailure = describeSaveFailure;
  private readonly operations = inject(WindowOperations);
  // A field is written after a pause in typing or when it loses focus, not on every keystroke.
  readonly title = injectField(this.editor, this.operations.field(EditorFields.title), { commit: "pause" });
  readonly body = injectField(this.editor, this.operations.field(EditorFields.body), { commit: "pause" });
  // Save waits for the fields' writes, and does not start after one that failed.
  readonly save = injectCommand(async () => {
    await Promise.all([this.title.commit(), this.body.commit()]);
    if (this.title.error() !== undefined || this.body.error() !== undefined) return undefined;
    return this.operations.run(this.editor.client(), view => view.save());
  });
}
