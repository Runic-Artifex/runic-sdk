import { Component, effect, input } from "@angular/core";
import type { DocumentPageReference, DocumentState } from "../../../Frontend/src/generated/document.js";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";
import { EditorComponent, MirrorEditorComponent } from "./editor";
import { CompactComponent } from "./compact";
import { PreviewComponent } from "./preview";
import { describeSaveFailure } from "../../../Frontend/src/save-failure.js";
import { ViewOutlet, type ViewRegistry } from "../../../../../packages/web/angular/src/view-outlet";

const paneViews = { editor: EditorComponent, preview: PreviewComponent } satisfies ViewRegistry<DocumentState["currentPane"]>;
const mirrorViews = { ...paneViews, editor: MirrorEditorComponent } satisfies ViewRegistry<DocumentState["currentPane"]>;
const compactViews = { editorCompact: CompactComponent } satisfies ViewRegistry<DocumentState["compactNote"]>;

@Component({
  selector: "notes-document",
  imports: [ViewOutlet],
  template: `
    @if (document.state(); as state) {
      <h1>Document</h1>
      <p class="muted">A child region swaps Editor and Preview. The compact View stays mounted.</p>
      <div class="document">
        <div>
          <div class="tabs">
            <button data-pane="editor" [attr.aria-current]="state.activePane === 'Editor' ? 'page' : null" (click)="show.run('showEditor')">Editor</button>
            <button data-pane="preview" [attr.aria-current]="state.activePane === 'Preview' ? 'page' : null" (click)="show.run('showPreview')">Preview</button>
          </div>
          <section id="document-pane" class="card"><runic-view-outlet [content]="state.currentPane" [registry]="paneViews" /></section>
          <div class="tabs">
            <button data-save-close (click)="saveAndClose.run()">Save and close</button>
            <button data-close (click)="close.run()">Close</button>
          </div>
          <p data-document-status class="muted" role="status">{{ state.status }}</p>
          @if (saveAndClose.failure(); as failure) { <p data-close-failure role="alert">{{ describeSaveFailure(failure) }}</p> }
        </div>
        @if (state.currentPane?.kind === "editor") {
          <aside id="same-reference-editor" class="card"><runic-view-outlet [content]="state.currentPane" [registry]="mirrorViews" /></aside>
        }
        <aside id="compact-pane" class="card"><runic-view-outlet [content]="state.compactNote" [registry]="compactViews" /></aside>
      </div>
    } @else { <p>Connecting…</p> }
    @if (show.error() ?? close.error() ?? saveAndClose.error() ?? document.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class DocumentComponent {
  readonly page = input.required<DocumentPageReference>();
  readonly document = injectView(this.page);
  readonly paneViews = paneViews;
  readonly mirrorViews = mirrorViews;
  readonly compactViews = compactViews;
  readonly show = injectCommand((name: "showEditor" | "showPreview") => this.document.client()?.[name]());
  readonly close = injectCommand(() => this.document.client()?.close());
  readonly saveAndClose = injectCommand(() => this.document.client()?.saveAndClose());
  readonly describeSaveFailure = describeSaveFailure;

  constructor() {
    // The leave guard asks here when this View's Close or Save and close leaves
    // unsaved edits. Answering false keeps the document and its edits.
    effect(onCleanup => {
      const view = this.document.client();
      if (!view) return;
      onCleanup(view.interactions.confirmLeave.handle(async (request, { signal }) => {
        if (signal.aborted) throw signal.reason;
        return window.confirm(`Close “${request.title}” and lose its unsaved changes?`);
      }));
    });
  }
}
