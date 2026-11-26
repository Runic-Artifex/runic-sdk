<script lang="ts" module>
  /** How a region's loading goes: the argument of the `failed` snippet, and the second of the `fallback` snippet. */
  export interface HostedRegionStatus {
    /** Whether the region is `loading` and its frame has loaded without the guest's page connecting. */
    readonly stalled: boolean;
    /** The host application's reason for `failed`, or `null`. */
    readonly failureReason: string | null;
    /**
     * Asks the host application to load a `failed` region again; it decides. Returns whether the request was sent: false
     * unless the region is `failed`.
     */
    readonly retry: () => boolean;
  }
</script>

<script lang="ts">
  // Hosted regions (experimental, 0.18): a region where the host application shows a hosted guest's Window.
  import type { Snippet } from "svelte";
  import type { HTMLAttributes } from "svelte/elements";
  import {
    defineHostedRegion,
    type HostedRegionSizing,
    type HostedRegionState,
    type HostedRegionStateChangeDetail,
    type HostedRegionTitleChangeDetail,
    type RunicHostedRegionElement,
  } from "@runic-artifex/views/experimental";

  let { name, sizing, label, fallback, stalled, failed, onstatechange, ontitlechange, ...attributes }: Omit<HTMLAttributes<HTMLElement>, "title" | "children"> & {
    /** The region's name, which the host application's policy embeds a guest's Window in. */
    name: string;
    /** `fill` (the default): the page's layout sets the height. `content-height`: the guest's page does. */
    sizing?: HostedRegionSizing;
    /** The region's accessible name; without it, the guest page's title. */
    label?: string;
    /**
     * Rendered while no guest page shows in the region, unless `stalled` or `failed` shows instead, with the region's
     * state and status.
     */
    fallback?: Snippet<[HostedRegionState, HostedRegionStatus]>;
    /** Rendered in place of the `loading` fallback while the region is stalled; without it, the fallback shows. */
    stalled?: Snippet;
    /** Rendered while the region is failed, with its status; without it, the fallback shows. */
    failed?: Snippet<[HostedRegionStatus]>;
    /** The region's state changed, or only whether it is stalled or the reason it failed. */
    onstatechange?: (state: HostedRegionState, previous: HostedRegionState, detail: HostedRegionStateChangeDetail) => void;
    /** The guest page's title as the host passed it on, or `null`. */
    ontitlechange?: (title: string | null) => void;
  } = $props();

  defineHostedRegion();
  let element = $state<RunicHostedRegionElement>();
  let regionState = $state<HostedRegionState>("waiting");
  let isStalled = $state(false);
  let failureReason = $state<string | null>(null);

  /** Asks the host application to load a `failed` region again; returns whether the request was sent. */
  export function retry(): boolean {
    return element?.retry() ?? false;
  }

  const status: HostedRegionStatus = {
    get stalled() { return isStalled; },
    get failureReason() { return failureReason; },
    retry,
  };
  const stateChanged = (event: CustomEvent<HostedRegionStateChangeDetail>) => {
    regionState = event.detail.state;
    isStalled = event.detail.stalled;
    failureReason = event.detail.reason;
    onstatechange?.(event.detail.state, event.detail.previous, event.detail);
  };
  const titleChanged = (event: CustomEvent<HostedRegionTitleChangeDetail>) => ontitlechange?.(event.detail.title);
</script>

<!-- Each snippet goes to the element's slot of that name, and the element picks the one that shows: stalled, then
     loading, then fallback; failed, then fallback. A snippet that is not given has no slot child, so the next one shows. -->
<runic-hosted-region bind:this={element} {...attributes} {name} {sizing} {label} onrunicregionstatechange={stateChanged} onrunicregiontitlechange={titleChanged}>
  {#if fallback}<div slot="fallback" style="display: contents">{@render fallback(regionState, status)}</div>{/if}
  {#if stalled}<div slot="stalled" style="display: contents">{@render stalled()}</div>{/if}
  {#if failed}<div slot="failed" style="display: contents">{@render failed(status)}</div>{/if}
</runic-hosted-region>
