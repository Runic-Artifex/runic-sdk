import { Component, DestroyRef, effect, inject, input } from "@angular/core";
import { EditorFields, type EditorPageReference } from "../../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../../Frontend/src/save-failure.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectField } from "../../../../../packages/web/angular/src/inject-field";

@Component({
  selector: "notes-editor",
  template: `
    @if (editor.state(); as state) {
      <h2>Full editor</h2>
      <label>Title<input data-title [value]="title.text()" (input)="title.inputText($event)" (blur)="title.commit()"
        (compositionstart)="title.compositionStart()" (compositionend)="title.compositionEnd($event)" /></label>
      <label>Body<textarea data-body [value]="body.text()" (input)="body.inputText($event)" (blur)="body.commit()"
        (compositionstart)="body.compositionStart()" (compositionend)="body.compositionEnd($event)"></textarea></label>
      <button data-save [disabled]="!state.canSave" (click)="command.run('save')">Save</button>
      <button data-discard [disabled]="!state.canDiscard" (click)="command.run('discard')">Discard changes</button>
      <p data-message role="status">{{ state.savedMessage }}</p>
      <p data-activation class="muted">Activated {{ state.activationCount }} × · deactivated {{ state.deactivationCount }} ×</p>
    } @else { <p>Connecting…</p> }
    @if (command.failure(); as failure) { <p role="alert">{{ describeSaveFailure(failure) }}</p> }
    @else if (command.error() ?? title.error() ?? body.error() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class EditorComponent {
  // Registered before the page, so text that waits for its pause is written before the editor is released.
  private readonly flushOnDestroy = inject(DestroyRef).onDestroy(() => { void this.title.commit(); void this.body.commit(); });
  readonly page = input.required<EditorPageReference>();
  readonly handleInteractions = input(true);
  readonly editor = injectView(this.page);
  readonly describeSaveFailure = describeSaveFailure;
  // A field is written after a pause in typing or when it loses focus, not on every keystroke.
  readonly title = injectField(this.editor, EditorFields.title, { commit: "pause" });
  readonly body = injectField(this.editor, EditorFields.body, { commit: "pause" });
  // Save and Discard follow the fields' writes, and do not start after one that failed.
  readonly command = injectCommand(async (name: "save" | "discard") => {
    await Promise.all([this.title.commit(), this.body.commit()]);
    const view = this.editor.client();
    if (!view || this.title.error() !== undefined || this.body.error() !== undefined) return undefined;
    return name === "save" ? view.save() : view.discard();
  });

  constructor() {
    effect(onCleanup => {
      const view = this.editor.client();
      if (!view || !this.handleInteractions()) return;
      onCleanup(view.interactions.confirmDiscard.handle(async (request, { signal }) => {
        if (signal.aborted) throw signal.reason;
        return window.confirm(`Discard the ${request.bodyLength} unsaved characters in “${request.title}”?`);
      }));
    });
  }
}

@Component({
  selector: "notes-mirror-editor",
  imports: [EditorComponent],
  template: `<notes-editor [page]="page()" [handleInteractions]="false" />`,
})
export class MirrorEditorComponent {
  readonly page = input.required<EditorPageReference>();
}
