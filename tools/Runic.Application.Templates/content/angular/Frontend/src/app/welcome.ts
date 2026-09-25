import { Component, input, OnDestroy, OnInit, signal } from "@angular/core";
import type { WelcomePageReference, WelcomeState, WelcomeView } from "../generated/welcome.js";

@Component({
  selector: "runic-welcome-view",
  templateUrl: "./welcome.html"
})
export class WelcomeComponent implements OnInit, OnDestroy {
  readonly page = input.required<WelcomePageReference>();
  readonly state = signal<WelcomeState | undefined>(undefined);
  readonly error = signal<string | undefined>(undefined);
  private view: WelcomeView | undefined;
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
}
