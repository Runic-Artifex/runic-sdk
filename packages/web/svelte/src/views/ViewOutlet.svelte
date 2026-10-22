<script lang="ts" generics="R extends PageReference">
  import type { Component, Snippet } from "svelte";
  import type { PageReference } from "@runic-artifex/views";
  import type { ViewRegistry } from "./view-registry.js";

  let { content, registry, fallback, missing }: {
    content: R | null | undefined;
    registry: ViewRegistry<R>;
    /** Rendered while there is no content. */
    fallback?: Snippet;
    /**
     * Renders a reference whose kind has no registered component, for example localized text.
     * Defaults to an English `<p role="alert">`.
     */
    missing?: Snippet<[R]>;
  } = $props();
  let Resolved = $derived(content ? registry[content.kind as R["kind"]] as Component<{ page: R }> | undefined : undefined);
</script>

{#if content && Resolved}
  {#key content}<Resolved page={content} />{/key}
{:else if content && missing}
  {@render missing(content)}
{:else if content}
  <p role="alert">No web component is registered for {content.kind}.</p>
{:else}
  {@render fallback?.()}
{/if}
