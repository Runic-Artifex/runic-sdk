import type { Component } from "svelte";
import type { PageReference, ViewRegistryPage } from "@runic-artifex/views";

/**
 * Compile-time check that every View kind has a component with a matching page prop. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds. A component whose `page` is a shared
 * page reference (`[RunicSharedClient]`), such as `ResultPagePageReference`, registers for each of its kinds.
 */
export type ViewRegistry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: PageComponent<ViewRegistryPage<R, K>>;
};

// One component type per page type the entry accepts.
type PageComponent<P> = P extends unknown ? Component<{ page: P }> : never;
