import {
  Component,
  computed,
  inject,
  provideZonelessChangeDetection,
} from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import type { NativeMvvmApplicationOptions } from "@runic-artifex/mvvm";
import {
  injectAngularMvvmApplication,
  provideAngularMvvmApplication,
  startAngularMvvmApplication,
} from "@runic-artifex/mvvm-angular";

import {
  injectCounterContract,
  provideCounterContract,
} from "./counter-bindings.g";
import { CounterContract } from "./counter-contract.g";

@Component({
  selector: "runic-toolkit-root",
  standalone: true,
  templateUrl: "./app.html",
})
class App {
  protected readonly application =
    injectAngularMvvmApplication<CounterContract>();
  protected readonly bindings = injectCounterContract();
  protected readonly snapshot = this.application.store.snapshot;
  protected readonly connected = computed(() => this.snapshot().synchronized);
  protected setStep(event: Event): void {
    void this.bindings.contract.step.set(
      (event.currentTarget as HTMLInputElement).valueAsNumber,
    );
  }
}

export async function bootstrapCounterApplication(
  options: Readonly<NativeMvvmApplicationOptions<CounterContract>>,
): Promise<void> {
  const application = await startAngularMvvmApplication(options);
  const angular = await bootstrapApplication(App, {
    providers: [
      provideZonelessChangeDetection(),
      ...provideAngularMvvmApplication(application),
      ...provideCounterContract(application.store, application.contract),
    ],
  });
  application.addCleanup(() => angular.destroy());
}
