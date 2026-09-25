<script lang="ts">
  import type { Component } from "svelte";
  import type { ViewReference } from "./view-registry.js";

  let { content, registry }: {
    content: ViewReference | null | undefined;
    registry: Readonly<Record<string, Component<{ page: any }>>>;
  } = $props();
  let Resolved = $derived(content ? registry[content.kind] : undefined);
</script>

{#if content && Resolved}
  {#key content}<Resolved page={content} />{/key}
{:else if content}
  <p role="alert">No web component is registered for {content.kind}.</p>
{/if}
