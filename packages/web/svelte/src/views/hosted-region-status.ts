// A type module, not the component's module script: a consumer that resolves `*.svelte` through an ambient wildcard
// declaration sees only a component's default export.

/** How a region's loading goes: the argument of `RunicHostedRegion`'s `failed` snippet, and the second of `fallback`. */
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
