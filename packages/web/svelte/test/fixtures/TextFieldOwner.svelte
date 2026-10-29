<script lang="ts">
  import type { ViewSource } from "@runic-artifex/views";
  import type { ViewConnection } from "@runic-artifex/views/generated";
  import { useTextField, useView } from "../../src/views/index.js";
  type State = { readonly title: string };
  let { source }: { source: ViewSource<ViewConnection<State>> } = $props();
  const editor = useView(() => source);
  export const title = useTextField(() => editor, state => state.title, (client, value) => client.set("title", value));
</script>

<input aria-label="Title" bind:value={title.value} oncompositionstart={title.compositionStart} oncompositionend={title.compositionEnd}>
