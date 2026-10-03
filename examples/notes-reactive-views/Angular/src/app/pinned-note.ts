import { Component, input } from "@angular/core";
import type { PinnedNotePageReference } from "../../../Frontend/src/generated/pinnedNote.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";

@Component({
  selector: "notes-pinned-note",
  template: `@if (note.state(); as state) { <span>{{ state.label }}</span> } @else { <span>Connecting…</span> }`,
})
export class PinnedNoteComponent {
  readonly page = input.required<PinnedNotePageReference>();
  readonly note = injectView(this.page);
}
