import { Component, effect, input, signal } from "@angular/core";
import type { EditorPageReference, EditorClient } from "../../../Frontend/src/generated/editor.js";
import { EditorWrites } from "../../../Frontend/src/editor-writes.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";

@Component({
  selector: "notes-editor",
  template: `
    @if (editor.state(); as state) {
      <h2>Full editor</h2>
      <label>Title<input data-title [value]="state.title" (change)="setTitle($event)" /></label>
      <label>Body<textarea data-body [value]="state.body" (change)="setBody($event)"></textarea></label>
      <button data-save [disabled]="!state.canSave" (click)="command.run('save')">Save</button>
      <button data-discard [disabled]="!state.canDiscard" (click)="command.run('discard')">Discard changes</button>
      <p data-message role="status">{{ state.savedMessage }}</p>
      <p data-activation class="muted">Activated {{ state.activationCount }} × · deactivated {{ state.deactivationCount }} ×</p>
    } @else { <p>Connecting…</p> }
    @if (command.error() ?? writeError() ?? editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class EditorComponent {
  readonly page = input.required<EditorPageReference>();
  readonly handleInteractions = input(true);
  readonly editor = injectView(this.page);
  readonly writeError = signal<unknown>(undefined);
  private readonly writes = new EditorWrites(cause => this.writeError.set(cause));
  readonly command = injectCommand((name: "save" | "discard") => {
    const view = this.editor.client();
    return view && this.writes.run(() => view[name]());
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

  setTitle(event: Event): void { const value = (event.target as HTMLInputElement).value; this.write(view => view.setTitle(value)); }
  setBody(event: Event): void { const value = (event.target as HTMLTextAreaElement).value; this.write(view => view.setBody(value)); }
  private write(action: (view: EditorClient) => Promise<unknown>): void {
    const view = this.editor.client();
    if (view) this.writes.enqueue(() => action(view));
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
