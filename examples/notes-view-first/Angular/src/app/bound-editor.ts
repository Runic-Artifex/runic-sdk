import { Component, inject, input, signal } from "@angular/core";
import { ReactiveFormsModule } from "@angular/forms";
import type { EditorPageReference } from "../../../Frontend/src/generated/editor.js";
import { pageSignal } from "./bridge-signal";
import { bridgeTextForm } from "./bridge-text-form";
import { editorFields } from "./editor-fields";
import { WindowOperations } from "./window-operations";

@Component({
  selector: "notes-bound-editor",
  imports: [ReactiveFormsModule],
  template: `
    @if (editor.state(); as state) {
      @if (binding.form(); as form) {
        <h2>Editor</h2>
        <label>Title <input [formControl]="form.controls.title" /></label>
        <label>Body <textarea [formControl]="form.controls.body"></textarea></label>
        <button data-save [disabled]="!state.canSave" (click)="save()">Save</button>
        <p data-message role="status">{{ state.isDirty ? "Unsaved changes. " : "" }}{{ state.savedMessage }}</p>
      }
    } @else { <p>Connecting…</p> }
    @if (error() ?? binding.error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (editor.error()) { <button (click)="editor.retry()">Retry editor</button> }
  `,
})
export class BoundEditorComponent {
  readonly page = input.required<EditorPageReference>();
  readonly editor = pageSignal(this.page);
  private readonly operations = inject(WindowOperations);
  readonly binding = bridgeTextForm(this.editor.view, editorFields, this.operations);
  readonly error = signal<string | undefined>(undefined);

  save(): void {
    void this.binding.flush().then(() => this.operations.dispatchProbe
      ? this.operations.runDispatched(this.editor.view(), view => view.save())
      : this.operations.run(this.editor.view(), view => view.save()))
      .then(() => this.error.set(undefined))
      .catch(cause => this.error.set(String(cause)));
  }
}
