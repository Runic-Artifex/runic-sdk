import { Component, computed, input } from "@angular/core";
import { injectView } from "@runic-artifex/angular";
import { describeBridgeError } from "@runic-artifex/views";
import type { WelcomePageReference } from "../generated/welcome.js";

@Component({
  selector: "runic-welcome-view",
  templateUrl: "./welcome.html"
})
export class WelcomeComponent {
  readonly page = input.required<WelcomePageReference>();
  readonly welcome = injectView(this.page);
  readonly state = this.welcome.state;
  readonly error = computed(() => {
    const error = this.welcome.error();
    return error === undefined ? undefined : describeBridgeError(error);
  });
}
