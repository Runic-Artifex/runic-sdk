import { Component, input } from "@angular/core";
import type { PinnedNotePageReference } from "../../../Frontend/src/generated/pinnedNote.js";
import { pageSignal } from "./bridge-signal";

@Component({
  selector: "notes-pinned-note",
  template: `@if (note.state(); as state) { <span>{{ state.label }}</span> } @else { <span>Connecting…</span> }`,
})
export class PinnedNoteComponent {
  readonly page = input.required<PinnedNotePageReference>();
  readonly note = pageSignal(this.page);
}
