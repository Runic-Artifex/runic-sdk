<script lang="ts" generics="R extends ViewReference">
  import type { Component, Snippet } from "svelte";
  import type { ViewReference, ViewRegistry } from "./view-registry.js";

  let { content, registry, fallback }: {
    content: R | null | undefined;
    registry: ViewRegistry<R>;
    /** Rendered while there is no content. */
    fallback?: Snippet;
  } = $props();
  let Resolved = $derived(content ? registry[content.kind as R["kind"]] as Component<{ page: R }> | undefined : undefined);
</script>

{#if content && Resolved}
  {#key content}<Resolved page={content} />{/key}
{:else if content}
  <p role="alert">No web component is registered for {content.kind}.</p>
{:else}
  {@render fallback?.()}
{/if}
