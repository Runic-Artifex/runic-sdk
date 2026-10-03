import { Component, computed, input } from "@angular/core";
import { injectView } from "@runic-artifex/angular";
import type { WelcomePageReference } from "../generated/welcome.js";

@Component({
  selector: "runic-welcome-view",
  templateUrl: "./welcome.html"
})
export class WelcomeComponent {
  readonly page = input.required<WelcomePageReference>();
  readonly welcome = injectView(this.page);
  readonly state = this.welcome.state;
  readonly error = computed(() => this.welcome.error() === undefined ? undefined : String(this.welcome.error()));
}
