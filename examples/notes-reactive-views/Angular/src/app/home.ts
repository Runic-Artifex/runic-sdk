import { Component, input } from "@angular/core";
import type { HomePageReference } from "../../../Frontend/src/generated/home.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";

@Component({
  selector: "notes-home",
  template: `
    @if (home.state(); as state) { <h1>{{ state.greeting }}</h1><p>Open the document to see two Views of one Editor ViewModel.</p> }
    @else { <p>Connecting…</p> }
    @if (home.error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class HomeComponent {
  readonly page = input.required<HomePageReference>();
  readonly home = injectView(this.page);
}
