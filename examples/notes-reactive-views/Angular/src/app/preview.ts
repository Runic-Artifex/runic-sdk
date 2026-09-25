import { Component, input } from "@angular/core";
import type { PreviewPageReference } from "../../../Frontend/src/generated/preview.js";
import { pageSignal } from "./bridge-signal";

@Component({
  selector: "notes-preview",
  template: `
    @if (preview.state(); as state) { <h2 data-heading>{{ state.heading }}</h2><p data-body>{{ state.body }}</p> }
    @else { <p>Connecting…</p> }
    @if (preview.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class PreviewComponent {
  readonly page = input.required<PreviewPageReference>();
  readonly preview = pageSignal(this.page);
}
