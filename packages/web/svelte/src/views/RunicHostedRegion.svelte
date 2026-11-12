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
  } from "@runic-artifex/views/experimental";

  let { name, sizing, label, fallback, onstatechange, ontitlechange, ...attributes }: Omit<HTMLAttributes<HTMLElement>, "title" | "children"> & {
    /** The region's name, which the host application's policy embeds a guest's Window in. */
    name: string;
    /** `fill` (the default): the page's layout sets the height. `content-height`: the guest's page does. */
    sizing?: HostedRegionSizing;
    /** The region's accessible name; without it, the guest page's title. */
    label?: string;
    /** Rendered while no guest page shows in the region, with the region's state. */
    fallback?: Snippet<[HostedRegionState]>;
    onstatechange?: (state: HostedRegionState, previous: HostedRegionState) => void;
    /** The guest page's title as the host passed it on, or `null`. */
    ontitlechange?: (title: string | null) => void;
  } = $props();

  defineHostedRegion();
  let state = $state<HostedRegionState>("waiting");
  const stateChanged = (event: CustomEvent<HostedRegionStateChangeDetail>) => {
    state = event.detail.state;
    onstatechange?.(event.detail.state, event.detail.previous);
  };
  const titleChanged = (event: CustomEvent<HostedRegionTitleChangeDetail>) => ontitlechange?.(event.detail.title);
</script>

<runic-hosted-region {...attributes} {name} {sizing} {label} onrunicregionstatechange={stateChanged} onrunicregiontitlechange={titleChanged}>
  {#if fallback}<div slot="fallback" style="display: contents">{@render fallback(state)}</div>{/if}
</runic-hosted-region>
