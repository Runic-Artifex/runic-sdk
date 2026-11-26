// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
import { Component, ElementRef, inject, input, output, signal } from "@angular/core";
import {
  defineHostedRegion,
  type HostedRegionSizing,
  type HostedRegionState,
  type HostedRegionStateChangeDetail,
  type HostedRegionTitleChangeDetail,
  type RunicHostedRegionElement,
} from "@runic-artifex/views/experimental";

export type { HostedRegionSizing, HostedRegionState, RunicHostedRegionElement } from "@runic-artifex/views/experimental";
// Trusted component mode (0.20): an Angular guest's component build.
export { runicComponentMount, type RunicComponentMountOptions } from "./component.js";

/**
 * The `stateChange` output of {@link RunicHostedRegion}, emitted when the state changed, or only whether the region is
 * stalled or the reason it failed.
 */
export interface HostedRegionStateChange {
  readonly state: HostedRegionState;
  readonly previous: HostedRegionState;
  /** Whether the region is `loading` and its frame has loaded without the guest's page connecting. */
  readonly stalled: boolean;
  /** The host application's reason for `failed`, or `null`. */
  readonly reason: string | null;
}

/**
 * A region where the host application shows a hosted guest's Window: the component's element is
 * `<runic-hosted-region>` itself, and the page never gets a reference to its frame. Projected content shows while no
 * guest page shows in the region; `state()`, `stalled()` and `failureReason()` tell how it goes. A projected element with
 * `slot="stalled"` shows in its place while the region is stalled, and one with `slot="failed"` while it is failed,
 * where `retry()` asks the host application to load it again:
 *
 * ```html
 * <runic-hosted-region #check="runicHostedRegion" name="check" style="height: 420px" (stateChange)="onState($event)">
 *   @if (check.state() === "ended") { <button (click)="runAgain()">Run the check again</button> }
 *   @else { <p>Starting the check…</p> }
 *   <p slot="stalled">The check is taking longer than usual…</p>
 *   <p slot="failed">The check could not start ({{ check.failureReason() }}). <button (click)="check.retry()">Try again</button></p>
 * </runic-hosted-region>
 * ```
 *
 * Like the element, a stalled region without `slot="stalled"` content shows its projected content for `loading`, and a
 * failed one without `slot="failed"` content its projected content for `failed`.
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
  // Stalled and failed content stays a child of the element, with its slot, and the element picks what shows: stalled,
  // then loading, then fallback; failed, then fallback. Without such content there is no such child, so the next shows.
  template: `<ng-content select="[slot=stalled]" /><ng-content select="[slot=failed]" /><div slot="fallback" style="display: contents"><ng-content /></div>`,
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
  readonly #element = inject<ElementRef<RunicHostedRegionElement>>(ElementRef).nativeElement;
  readonly #state = signal<HostedRegionState>("waiting");
  readonly #stalled = signal(false);
  readonly #failureReason = signal<string | null>(null);
  /** What the region shows now. */
  readonly state = this.#state.asReadonly();
  /** Whether the region is `loading` and its frame has loaded without the guest's page connecting. */
  readonly stalled = this.#stalled.asReadonly();
  /** The host application's reason for `failed`, or `null`. */
  readonly failureReason = this.#failureReason.asReadonly();

  constructor() {
    defineHostedRegion();
  }

  /**
   * Asks the host application to load a `failed` region again; it decides. Returns whether the request was sent: false
   * unless the region is `failed`.
   */
  retry(): boolean {
    return this.#element.retry();
  }

  protected stateChanged(event: Event): void {
    const { state, previous, stalled, reason } = (event as CustomEvent<HostedRegionStateChangeDetail>).detail;
    this.#state.set(state);
    this.#stalled.set(stalled);
    this.#failureReason.set(reason);
    this.stateChange.emit({ state, previous, stalled, reason });
  }

  protected titleChanged(event: Event): void {
    this.titleChange.emit((event as CustomEvent<HostedRegionTitleChangeDetail>).detail.title);
  }
}
