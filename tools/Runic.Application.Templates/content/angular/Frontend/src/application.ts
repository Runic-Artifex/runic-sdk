import {
  Component,
  DestroyRef,
  inject,
  provideZonelessChangeDetection,
  signal,
} from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { counterBridge } from "./counter-bridge";
import type { CounterSnapshot } from "./counter-contract";

@Component({
  selector: "runic-toolkit-root",
  standalone: true,
  templateUrl: "./app.html",
})
class App {
  protected readonly snapshot = signal<CounterSnapshot>({ count: 0, history: [0], revision: 0 });
  protected readonly step = signal(1);
  protected readonly error = signal<string | undefined>(undefined);

  public constructor() {
    const unsubscribe = counterBridge.subscribe(
      (event) => this.snapshot.set(event.snapshot),
      (failure) => this.error.set(failure.message),
    );
    inject(DestroyRef).onDestroy(unsubscribe);
    void counterBridge.initialize().then(
      (value) => this.snapshot.set(value),
      (failure) => this.error.set(failure.message),
    );
  }

  protected setStep(event: Event): void {
    this.step.set((event.currentTarget as HTMLInputElement).valueAsNumber);
  }

  protected async increment(): Promise<void> {
    const receipt = await counterBridge.dispatch({ _tag: "IncrementCounter", step: this.step() });
    if (receipt && typeof receipt === "object" && "snapshot" in receipt) {
      this.snapshot.set(receipt.snapshot as CounterSnapshot);
    }
  }
}

export async function bootstrapCounterApplication(): Promise<void> {
  const angular = await bootstrapApplication(App, {
    providers: [provideZonelessChangeDetection()],
  });
  globalThis.addEventListener("pagehide", () => {
    angular.destroy();
    void counterBridge.dispose();
  }, { once: true });
}
