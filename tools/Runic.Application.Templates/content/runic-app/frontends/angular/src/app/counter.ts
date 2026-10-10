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
  readonly steps = [1, 2, 5, 10];
  readonly increment = injectCommand(() => this.counter.client()?.increment());
  // Step has a public C# setter, so the client has setStep for a two-way
  // binding. Count's setter is private: the template only reads it.
  readonly setStep = injectCommand((step: number) => this.counter.client()?.setStep(step));
  readonly error = computed(() => {
    const error = this.increment.error() ?? this.setStep.error() ?? this.counter.error();
    return error === undefined ? undefined : String(error);
  });
}
