import { Component, inject, input, signal } from "@angular/core";
import type { DocumentPageReference, DocumentView } from "../../../Frontend/src/generated/document.js";
import { pageSignal } from "./bridge-signal";
import { WindowOperations } from "./window-operations";
import { BaselineEditorComponent } from "./baseline-editor";
import { BoundEditorComponent } from "./bound-editor";
import { PreviewComponent } from "./preview";

@Component({
  selector: "notes-document",
  imports: [BaselineEditorComponent, BoundEditorComponent, PreviewComponent],
  template: `
    @if (document.state(); as state) {
      <h1>Document</h1>
      <p class="muted">This area has its own ViewModel and a nested Editor/Preview outlet.</p>
      <div class="tabs">
        <button data-pane="editor" [attr.aria-current]="state.activePane === 'Editor' ? 'page' : null"
          [disabled]="!state.canShowEditor" (click)="showEditor()">Editor</button>
        <button data-pane="preview" [attr.aria-current]="state.activePane === 'Preview' ? 'page' : null"
          [disabled]="!state.canShowPreview" (click)="showPreview()">Preview</button>
      </div>
      <section id="document-pane" class="card">
        @switch (state.currentPane.kind) {
          @case ("editor") {
            @if (operations.ordered) {
              <notes-bound-editor [page]="state.currentPane" />
            } @else {
              <notes-baseline-editor [page]="state.currentPane" />
            }
          }
          @case ("preview") { <notes-preview [page]="state.currentPane" /> }
          @default never(state.currentPane);
        }
      </section>
    } @else { <p>Connecting…</p> }
    @if (error() ?? document.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (document.error()) { <button (click)="document.retry()">Retry document</button> }
  `,
})
export class DocumentComponent {
  readonly page = input.required<DocumentPageReference>();
  readonly document = pageSignal(this.page);
  readonly operations = inject(WindowOperations);
  readonly error = signal<string | undefined>(undefined);

  showEditor(): void { this.run(view => view.showEditor()); }
  showPreview(): void { this.run(view => view.showPreview()); }

  private run(action: (view: DocumentView) => Promise<unknown>): void {
    void this.operations.run(this.document.view(), action)
      .then(() => this.error.set(undefined))
      .catch(cause => this.error.set(String(cause)));
  }
}
