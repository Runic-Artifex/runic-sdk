import { Component, inject, input } from "@angular/core";
import { ReactiveFormsModule } from "@angular/forms";
import type { EditorPageReference } from "../../../Frontend/src/generated/editor.js";
import { bridgeTextForm } from "./bridge-text-form";
import { describeSaveFailure } from "../../../Frontend/src/save-failure.js";
import { editorFields } from "./editor-fields";
import { injectPage, WindowOperations } from "./window-operations";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";

@Component({
  selector: "notes-bound-editor",
  imports: [ReactiveFormsModule],
  template: `
    @if (editor.state(); as state) {
      @if (binding.form(); as form) {
        <h2>Editor</h2>
        <label>Title <input [formControl]="form.controls.title" /></label>
        <label>Body <textarea [formControl]="form.controls.body"></textarea></label>
        <button data-save [disabled]="!state.canSave" (click)="save.run()">Save</button>
        <p data-message role="status">{{ state.isDirty ? "Unsaved changes. " : "" }}{{ state.savedMessage }}</p>
      }
    } @else { <p>Connecting…</p> }
    @if (save.failure(); as failure) { <p role="alert">{{ describeSaveFailure(failure) }}</p> }
    @else if (save.error() ?? binding.error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (editor.error()) { <button (click)="editor.retry()">Retry editor</button> }
  `,
})
export class BoundEditorComponent {
  readonly page = input.required<EditorPageReference>();
  readonly editor = injectPage(this.page);
  private readonly operations = inject(WindowOperations);
  readonly describeSaveFailure = describeSaveFailure;
  readonly binding = bridgeTextForm(this.editor.client, editorFields, this.operations);
  readonly save = injectCommand(async () => {
    await this.binding.flush();
    return this.operations.dispatchProbe
      ? this.operations.runDispatched(this.editor.client(), view => view.save())
      : this.operations.run(this.editor.client(), view => view.save());
  });
}
