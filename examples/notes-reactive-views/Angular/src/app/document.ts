import { Component, input, signal } from "@angular/core";
import type { DocumentPageReference, DocumentState, DocumentView } from "../../../Frontend/src/generated/document.js";
import { pageSignal } from "./bridge-signal";
import { EditorComponent } from "./editor";
import { CompactComponent } from "./compact";
import { PreviewComponent } from "./preview";
import { RunicViewOutlet, type ViewRegistry } from "../../../../../packages/web/angular/src/view-outlet";

const paneViews = { editor: EditorComponent, preview: PreviewComponent } satisfies ViewRegistry<DocumentState["currentPane"]>;
const compactViews = { editorCompact: CompactComponent } satisfies ViewRegistry<DocumentState["compactNote"]>;

@Component({
  selector: "notes-document",
  imports: [RunicViewOutlet],
  template: `
    @if (document.state(); as state) {
      <h1>Document</h1>
      <p class="muted">The nested route swaps Editor and Preview. The compact View stays mounted.</p>
      <div class="document">
        <div>
          <div class="tabs">
            <button data-pane="editor" [attr.aria-current]="state.activePane === 'Editor' ? 'page' : null" (click)="showEditor()">Editor</button>
            <button data-pane="preview" [attr.aria-current]="state.activePane === 'Preview' ? 'page' : null" (click)="showPreview()">Preview</button>
          </div>
          <section id="document-pane" class="card"><runic-view-outlet [content]="state.currentPane" [registry]="paneViews" /></section>
        </div>
        @if (state.currentPane.kind === "editor") {
          <aside id="same-reference-editor" class="card"><runic-view-outlet [content]="state.currentPane" [registry]="paneViews" /></aside>
        }
        <aside id="compact-pane" class="card"><runic-view-outlet [content]="state.compactNote" [registry]="compactViews" /></aside>
      </div>
    } @else { <p>Connecting…</p> }
    @if (error() ?? document.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class DocumentComponent {
  readonly page = input.required<DocumentPageReference>();
  readonly document = pageSignal(this.page);
  readonly paneViews = paneViews;
  readonly compactViews = compactViews;
  readonly error = signal<string | undefined>(undefined);

  showEditor(): void { this.run(view => view.showEditor()); }
  showPreview(): void { this.run(view => view.showPreview()); }
  private run(action: (view: DocumentView) => Promise<unknown>): void {
    const view = this.document.view();
    if (!view) return;
    void action(view).then(() => this.error.set(undefined)).catch(cause => this.error.set(String(cause)));
  }
}
