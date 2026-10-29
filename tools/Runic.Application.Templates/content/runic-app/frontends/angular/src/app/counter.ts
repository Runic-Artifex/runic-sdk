import { Component, computed, input } from "@angular/core";
import { injectCommand, injectField, injectView } from "@runic-artifex/angular";
import { CounterFields, type CounterPageReference } from "../generated/counter.js";

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
  // Step and Label have public C# setters, so the generated CounterFields has a
  // field for each; injectField writes them and keeps what the user typed while
  // the writes are on their way. Count's setter is private: the template only
  // reads it. With @angular/forms, `[runicField]` from @runic-artifex/angular/forms
  // wires an element (and a FormControl) in one attribute.
  readonly step = injectField(this.counter, CounterFields.step);
  readonly label = injectField(this.counter, CounterFields.label);
  readonly error = computed(() => {
    const error = this.increment.error() ?? this.step.error() ?? this.label.error() ?? this.counter.error();
    return error === undefined ? undefined : String(error);
  });
}
