// The experimental entry (docs/experimental.md, "npm packages"): importing it is the opt-in. Hosted regions (0.18): the
// .NET side is the experimental family RUNICDESK103.
export { default as RunicHostedRegion, type HostedRegionStatus } from "./views/RunicHostedRegion.svelte";
export type {
  HostedRegionSizing, HostedRegionState, HostedRegionStateChangeDetail, RunicHostedRegionElement,
} from "@runic-artifex/views/experimental";
