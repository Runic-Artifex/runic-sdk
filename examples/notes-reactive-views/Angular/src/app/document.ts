import { Component, input } from "@angular/core";
import type { DocumentPageReference, DocumentState } from "../../../Frontend/src/generated/document.js";
import { injectCommand } from "../../../../../packages/web/angular/src/inject-command";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";
import { EditorComponent, MirrorEditorComponent } from "./editor";
import { CompactComponent } from "./compact";
import { PreviewComponent } from "./preview";
import { RunicViewOutlet, type ViewRegistry } from "../../../../../packages/web/angular/src/view-outlet";

const paneViews = { editor: EditorComponent, preview: PreviewComponent } satisfies ViewRegistry<DocumentState["currentPane"]>;
const mirrorViews = { ...paneViews, editor: MirrorEditorComponent } satisfies ViewRegistry<DocumentState["currentPane"]>;
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
            <button data-pane="editor" [attr.aria-current]="state.activePane === 'Editor' ? 'page' : null" (click)="show.run('showEditor')">Editor</button>
            <button data-pane="preview" [attr.aria-current]="state.activePane === 'Preview' ? 'page' : null" (click)="show.run('showPreview')">Preview</button>
          </div>
          <section id="document-pane" class="card"><runic-view-outlet [content]="state.currentPane" [registry]="paneViews" /></section>
        </div>
        @if (state.currentPane.kind === "editor") {
          <aside id="same-reference-editor" class="card"><runic-view-outlet [content]="state.currentPane" [registry]="mirrorViews" /></aside>
        }
        <aside id="compact-pane" class="card"><runic-view-outlet [content]="state.compactNote" [registry]="compactViews" /></aside>
      </div>
    } @else { <p>Connecting…</p> }
    @if (show.error() ?? document.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class DocumentComponent {
  readonly page = input.required<DocumentPageReference>();
  readonly document = injectView(this.page);
  readonly paneViews = paneViews;
  readonly mirrorViews = mirrorViews;
  readonly compactViews = compactViews;
  readonly show = injectCommand((name: "showEditor" | "showPreview") => this.document.client()?.[name]());
}
