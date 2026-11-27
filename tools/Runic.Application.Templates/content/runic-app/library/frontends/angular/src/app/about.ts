import { Component, computed, input } from "@angular/core";
import { injectCommand, injectView } from "@runic-artifex/angular";
import { describeBridgeError } from "@runic-artifex/views";
import { aboutHeading } from "runic-library-starter";
import type { AboutPageReference } from "runic-library-starter/generated";

// The RunicCoreLibrary library's View. Its client, AboutPageReference, and aboutHeading come from the
// library's frontend package, which the library's build generates and builds.
@Component({
  selector: "runic-about-view",
  templateUrl: "./about.html"
})
export class AboutComponent {
  readonly page = input.required<AboutPageReference>();
  readonly about = injectView(this.page);
  readonly state = this.about.state;
  readonly heading = computed(() => {
    const state = this.state();
    return state === undefined ? "Connecting…" : aboutHeading(state);
  });
  readonly toggle = injectCommand(() => this.about.client()?.toggleDetails());
  readonly error = computed(() => {
    const error = this.toggle.error() ?? this.about.error();
    return error === undefined ? undefined : describeBridgeError(error);
  });
}
