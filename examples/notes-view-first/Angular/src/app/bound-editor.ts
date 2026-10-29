import { Component, DestroyRef, inject, input } from "@angular/core";
import { FormControl, FormGroup, ReactiveFormsModule } from "@angular/forms";
import { EditorFields, type EditorPageReference } from "../../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../../Frontend/src/save-failure.js";
import { injectPage, WindowOperations } from "./window-operations";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectField } from "../../../../../packages/web/angular/src/inject-field";
import { RunicFieldDirective } from "../../../../../packages/web/angular/src/forms";

@Component({
  selector: "notes-bound-editor",
  imports: [ReactiveFormsModule, RunicFieldDirective],
  template: `
    @if (editor.state(); as state) {
      <h2>Editor</h2>
      <form [formGroup]="form">
        <label>Title <input formControlName="title" [runicField]="title" /></label>
        <label>Body <textarea formControlName="body" [runicField]="body"></textarea></label>
      </form>
      <button data-save [disabled]="!state.canSave" (click)="save.run()">Save</button>
      <p data-message role="status">{{ state.isDirty ? "Unsaved changes. " : "" }}{{ state.savedMessage }}</p>
    } @else { <p>Connecting…</p> }
    @if (save.failure(); as failure) { <p role="alert">{{ describeSaveFailure(failure) }}</p> }
    @else if (save.error() ?? title.error() ?? body.error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (editor.error()) { <button (click)="editor.retry()">Retry editor</button> }
  `,
})
export class BoundEditorComponent {
  // Registered before the page, so text that waits for its pause is written before the editor is released.
  private readonly flushOnDestroy = inject(DestroyRef).onDestroy(() => { void this.title.commit(); void this.body.commit(); });
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectPage(this.page);
  private readonly operations = inject(WindowOperations);
  readonly describeSaveFailure = describeSaveFailure;
  // The fields' writes take their turn with the window's other operations. A field is written after a pause in
  // typing or when it loses focus; the form's controls follow the View through [runicField].
  readonly title = injectField(this.editor, this.operations.field(EditorFields.title), { commit: "pause" });
  readonly body = injectField(this.editor, this.operations.field(EditorFields.body), { commit: "pause" });
  readonly form = new FormGroup({
    title: new FormControl("", { nonNullable: true }),
    body: new FormControl("", { nonNullable: true }),
  });
  // Save waits for the fields' writes, and does not start after one that failed.
  readonly save = injectCommand(async () => {
    await Promise.all([this.title.commit(), this.body.commit()]);
    if (this.title.error() !== undefined || this.body.error() !== undefined) return undefined;
    return this.operations.dispatchProbe
      ? this.operations.runDispatched(this.editor.client(), view => view.save())
      : this.operations.run(this.editor.client(), view => view.save());
  });
}
