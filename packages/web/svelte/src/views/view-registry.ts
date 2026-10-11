import type { Component } from "svelte";
import type { ViewReference } from "@runic-artifex/views";

export type { ViewReference } from "@runic-artifex/views";

/**
 * Compile-time check that every View kind has a component with a matching page prop. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds.
 */
export type ViewRegistry<R extends ViewReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: Component<{ page: Extract<NonNullable<R>, { readonly kind: K }> }>;
};
