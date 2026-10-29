<script lang="ts">
  import type { ViewSource } from "@runic-artifex/views";
  import type { ViewConnection } from "@runic-artifex/views/generated";
  import { useTextField, useView } from "../../dist/index.js";
  type State = { readonly title: string };
  let { source, record }: { source: ViewSource<ViewConnection<State>>; record: (element: HTMLInputElement) => void } = $props();
  const editor = useView(() => source);
  const title = useTextField(() => editor, state => state.title, (client, value) => client.set("title", value), { coalesce: new URLSearchParams(location.search).get("coalesce") !== "0" });
</script>

<!-- The field stays enabled while its own writes are in flight. -->
<input id="title" aria-label="Title" data-editing={String(title.editing)} data-ready={String(editor.client !== undefined)}
  disabled={editor.client === undefined} bind:value={title.value} oncompositionstart={title.compositionStart}
  oncompositionend={title.compositionEnd} {@attach record}>
