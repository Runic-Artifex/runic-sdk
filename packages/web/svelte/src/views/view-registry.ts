import type { Component } from "svelte";
import type { ViewReference } from "@runic-artifex/views";

export type { ViewReference } from "@runic-artifex/views";

/** Compile-time check that every View kind has a component with a matching page prop. */
export type ViewRegistry<R extends ViewReference> = {
  readonly [K in R["kind"]]: Component<{ page: Extract<R, { readonly kind: K }> }>;
};
