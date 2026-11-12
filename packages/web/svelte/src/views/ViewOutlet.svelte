<script lang="ts" generics="R extends PageReference">
  import type { Component, Snippet } from "svelte";
  import type { PageReference } from "@runic-artifex/views";
  import { missingViewMessage, reportMissingView } from "@runic-artifex/views/bindings";
  import { isDevelopment } from "./development.js";
  import { getRunicInstance } from "./instance.js";
  import type { ViewRegistry } from "./view-registry.js";

  let { content, registry, fallback, missing }: {
    content: R | null | undefined;
    registry: ViewRegistry<R>;
    /** Rendered while there is no content. */
    fallback?: Snippet;
    /**
     * Renders a reference whose kind has no registered component, for example localized text. Without it, a
     * development build renders an English `<p role="alert">`, and a production build renders nothing and reports
     * the kind through the page's error reporting.
     */
    missing?: Snippet<[R]>;
  } = $props();
  let Resolved = $derived(content ? registry[content.kind as R["kind"]] as Component<{ page: R }> | undefined : undefined);
  const development = isDevelopment();
  // A missing kind is reported to the component's Runic instance.
  const instance = getRunicInstance();
  $effect.pre(() => {
    const kind = content && !Resolved && !missing && !development ? content.kind : undefined;
    if (kind !== undefined) instance.run(() => reportMissingView(kind));
  });
</script>

{#if content && Resolved}
  {#key content}<Resolved page={content} />{/key}
{:else if content && missing}
  {@render missing(content)}
{:else if content && development}
  <p role="alert">{missingViewMessage(content.kind)}</p>
{:else if !content}
  {@render fallback?.()}
{/if}
