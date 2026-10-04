import { Component, computed, input, signal } from "@angular/core";
import { injectView } from "@runic-artifex/angular";
import type { CounterPageReference } from "../generated/counter.js";

@Component({
  selector: "runic-counter-view",
  templateUrl: "./counter.html"
})
export class CounterComponent {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
  readonly state = this.counter.state;
  readonly commandError = signal<string | undefined>(undefined);
  readonly error = computed(() => this.commandError()
    ?? (this.counter.error() === undefined ? undefined : String(this.counter.error())));

  increment(): void {
    const client = this.counter.client();
    if (client) void client.increment().then(() => this.commandError.set(undefined)).catch(cause => this.commandError.set(String(cause)));
  }
}
