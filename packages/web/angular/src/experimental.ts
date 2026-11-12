// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
import { Component, input, output, signal } from "@angular/core";
import {
  defineHostedRegion,
  type HostedRegionSizing,
  type HostedRegionState,
  type HostedRegionStateChangeDetail,
  type HostedRegionTitleChangeDetail,
} from "@runic-artifex/views/experimental";

export type { HostedRegionSizing, HostedRegionState, RunicHostedRegionElement } from "@runic-artifex/views/experimental";

/** The `stateChange` output of {@link RunicHostedRegion}. */
export interface HostedRegionStateChange {
  readonly state: HostedRegionState;
  readonly previous: HostedRegionState;
}

/**
 * A region where the host application shows a hosted guest's Window: the component's element is
 * `<runic-hosted-region>` itself, and the page never gets a reference to its frame. Projected content shows while no
 * guest page shows in the region; `state()` tells which state it is in:
 *
 * ```html
 * <runic-hosted-region #check name="check" style="height: 420px" (stateChange)="onState($event)">
 *   @if (check.state() === "ended") { <button (click)="runAgain()">Run the check again</button> }
 *   @else { <p>Starting the check…</p> }
 * </runic-hosted-region>
 * ```
 */
@Component({
  selector: "runic-hosted-region",
  standalone: true,
  exportAs: "runicHostedRegion",
  host: {
    "[attr.name]": "name()",
    "[attr.sizing]": "sizing() ?? null",
    "[attr.label]": "label() ?? null",
    "(runicregionstatechange)": "stateChanged($event)",
    "(runicregiontitlechange)": "titleChanged($event)",
  },
  template: `<div slot="fallback" style="display: contents"><ng-content /></div>`,
})
export class RunicHostedRegion {
  /** The region's name, which the host application's policy embeds a guest's Window in. */
  readonly name = input.required<string>();
  /** `fill` (the default): the page's layout sets the height. `content-height`: the guest's page does. */
  readonly sizing = input<HostedRegionSizing | undefined>();
  /** The region's accessible name; without it, the guest page's title. */
  readonly label = input<string | undefined>();
  readonly stateChange = output<HostedRegionStateChange>();
  /** The guest page's title as the host passed it on, or `null`. */
  readonly titleChange = output<string | null>();
  readonly #state = signal<HostedRegionState>("waiting");
  /** What the region shows now. */
  readonly state = this.#state.asReadonly();

  constructor() {
    defineHostedRegion();
  }

  protected stateChanged(event: Event): void {
    const detail = (event as CustomEvent<HostedRegionStateChangeDetail>).detail;
    this.#state.set(detail.state);
    this.stateChange.emit({ state: detail.state, previous: detail.previous });
  }

  protected titleChanged(event: Event): void {
    this.titleChange.emit((event as CustomEvent<HostedRegionTitleChangeDetail>).detail.title);
  }
}
