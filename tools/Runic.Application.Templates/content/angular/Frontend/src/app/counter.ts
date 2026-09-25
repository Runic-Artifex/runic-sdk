import { Component, input, OnDestroy, OnInit, signal } from "@angular/core";
import type { CounterPageReference, CounterState, CounterView } from "../generated/counter.js";

@Component({
  selector: "runic-counter-view",
  templateUrl: "./counter.html"
})
export class CounterComponent implements OnInit, OnDestroy {
  readonly page = input.required<CounterPageReference>();
  readonly state = signal<CounterState | undefined>(undefined);
  readonly error = signal<string | undefined>(undefined);
  private view: CounterView | undefined;
  private unsubscribe = () => {};
  private active = true;

  ngOnInit(): void {
    void this.page().connect().then(client => {
      if (!this.active) { client.dispose(); return; }
      this.view = client;
      this.unsubscribe = client.subscribe(next => this.state.set(next));
    }).catch(cause => this.error.set(String(cause)));
  }

  ngOnDestroy(): void { this.active = false; this.unsubscribe(); this.view?.dispose(); }

  increment(): void {
    if (this.view) void this.view.increment().then(() => this.error.set(undefined)).catch(cause => this.error.set(String(cause)));
  }
}
