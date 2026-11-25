<script lang="ts" generics="TClient extends ViewClient">
  import type { ViewClient } from "@runic-artifex/views";
  import { useField, useView, type BridgeField } from "../../src/views/index.js";
  type Fields = {
    readonly title: BridgeField<TClient, TClient["snapshot"], string>;
    readonly step: BridgeField<TClient, TClient["snapshot"], number>;
    readonly done: BridgeField<TClient, TClient["snapshot"], boolean>;
  };
  let { source, fields }: { source: TClient; fields: Fields } = $props();
  const editor = useView(() => source);
  // Every field writes typed text on blur only (#1316).
  // svelte-ignore state_referenced_locally
  export const title = useField(() => editor, fields.title, { commit: "change" });
  // svelte-ignore state_referenced_locally
  export const step = useField(() => editor, fields.step, { commit: "change" });
  // svelte-ignore state_referenced_locally
  export const done = useField(() => editor, fields.done, { commit: "change" });
</script>

<input id="title" {@attach title.attach}>
<select id="step" {@attach step.attach}><option value="0">0</option><option value="2">2</option></select>
<input id="done" type="checkbox" {@attach done.attach}>
