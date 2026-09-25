import { Component, input } from "@angular/core";
import type { EditorCompactPageReference } from "../../../Frontend/src/generated/editor.js";
import { pageSignal } from "./bridge-signal";

@Component({
  selector: "notes-compact",
  template: `
    @if (editor.state(); as state) {
      <h2>Compact View</h2><p class="muted">Contract: compact</p>
      <strong data-title>{{ state.title }}</strong><p data-body>{{ state.body || "Nothing written yet." }}</p>
      <p data-activation class="muted">Activated {{ state.activationCount }} × · deactivated {{ state.deactivationCount }} ×</p>
    } @else { <p>Connecting…</p> }
    @if (editor.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class CompactComponent {
  readonly page = input.required<EditorCompactPageReference>();
  readonly editor = pageSignal(this.page);
}
