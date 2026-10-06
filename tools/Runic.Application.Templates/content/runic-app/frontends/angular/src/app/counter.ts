import { Component, computed, input } from "@angular/core";
import { injectCommand, injectView } from "@runic-artifex/angular";
import type { CounterPageReference } from "../generated/counter.js";

@Component({
  selector: "runic-counter-view",
  templateUrl: "./counter.html"
})
export class CounterComponent {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
  readonly state = this.counter.state;
  readonly increment = injectCommand(() => this.counter.client()?.increment());
  readonly error = computed(() => {
    const error = this.increment.error() ?? this.counter.error();
    return error === undefined ? undefined : String(error);
  });
}
