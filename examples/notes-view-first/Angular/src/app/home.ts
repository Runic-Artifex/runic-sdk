import { Component, input } from "@angular/core";
import type { HomePageReference } from "../../../Frontend/src/generated/home.js";
import { pageSignal } from "./bridge-signal";

@Component({
  selector: "notes-home",
  template: `
    @if (home.state(); as state) {
      <h1>{{ state.greeting }}</h1>
      <p class="muted">The sidebar stays mounted while this content changes.</p>
    } @else { <p>Connecting…</p> }
    @if (home.error(); as issue) {
      <p role="alert">{{ issue }}</p>
      <button (click)="home.retry()">Retry home</button>
    }
  `,
})
export class HomeComponent {
  readonly page = input.required<HomePageReference>();
  readonly home = pageSignal(this.page);
}
