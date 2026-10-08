import { Component, inject, input } from "@angular/core";
import type { DocumentPageReference } from "../../../Frontend/src/generated/document.js";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectPage, WindowOperations } from "./window-operations";
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
          [disabled]="!state.canShowEditor" (click)="show.run('showEditor')">Editor</button>
        <button data-pane="preview" [attr.aria-current]="state.activePane === 'Preview' ? 'page' : null"
          [disabled]="!state.canShowPreview" (click)="show.run('showPreview')">Preview</button>
      </div>
      <section id="document-pane" class="card">
        @if (state.currentPane; as pane) {
          @switch (pane.kind) {
            @case ("editor") {
              @if (operations.ordered) {
                <notes-bound-editor [page]="pane" />
              } @else {
                <notes-baseline-editor [page]="pane" />
              }
            }
            @case ("preview") { <notes-preview [page]="pane" /> }
            @default never(pane);
          }
        }
      </section>
    } @else { <p>Connecting…</p> }
    @if (show.error() ?? document.error(); as issue) { <p role="alert">{{ issue }}</p> }
    @if (document.error()) { <button (click)="document.retry()">Retry document</button> }
  `,
})
export class DocumentComponent {
  readonly page = input.required<DocumentPageReference>();
  readonly document = injectPage(this.page);
  readonly operations = inject(WindowOperations);
  readonly show = injectCommand((name: "showEditor" | "showPreview") =>
    this.operations.run(this.document.client(), view => view[name]()));
}
