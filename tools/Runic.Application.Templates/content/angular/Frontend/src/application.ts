import { Component, computed, provideZonelessChangeDetection, signal } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { injectApplicationBridge, provideApplicationBridge } from "@runic-artifex/angular";
import { counterBridge } from "./counter-bridge";
import type { CounterCommand, CounterEvent, CounterReceipt, CounterSnapshot } from "./counter-contract";

@Component({
  selector: "runic-toolkit-root",
  standalone: true,
  templateUrl: "./app.html",
})
class App {
  private readonly bridge = injectApplicationBridge<CounterCommand, CounterReceipt, CounterEvent, CounterSnapshot>();
  protected readonly snapshot = computed(() => this.bridge.snapshot() ?? { count: 0, history: [0], revision: 0 });
  protected readonly step = signal(1);
  protected readonly error = computed(() => this.bridge.error()?.message);

  public constructor() {
    void this.bridge.initialize();
  }

  protected setStep(event: Event): void {
    this.step.set((event.currentTarget as HTMLInputElement).valueAsNumber);
  }

  protected async increment(): Promise<void> {
    await this.bridge.dispatch({ _tag: "IncrementCounter", step: this.step() });
  }
}

export async function bootstrapCounterApplication(): Promise<void> {
  const angular = await bootstrapApplication(App, {
    providers: [
      provideZonelessChangeDetection(),
      provideApplicationBridge({
        controller: counterBridge,
        snapshotFromEvent: (event) => event._tag === "CounterChanged" ? event.snapshot : undefined,
      }),
    ],
  });
  globalThis.addEventListener("pagehide", () => {
    angular.destroy();
  }, { once: true });
}
