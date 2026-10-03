import { Component, input } from "@angular/core";
import type { PreviewPageReference } from "../../../Frontend/src/generated/preview.js";
import { injectPage } from "./window-operations";

@Component({
  selector: "notes-preview",
  template: `
    @if (preview.state(); as state) {
      <h2>{{ state.heading }}</h2>
      <p>{{ state.excerpt }}</p>
    } @else { <p>Connecting…</p> }
    @if (preview.error(); as issue) {
      <p role="alert">{{ issue }}</p>
      <button (click)="preview.retry()">Retry preview</button>
    }
  `,
})
export class PreviewComponent {
  readonly page = input.required<PreviewPageReference>();
  readonly preview = injectPage(this.page);
}
