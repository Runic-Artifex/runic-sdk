<script lang="ts" generics="TClient extends ViewClient">
  import type { FieldOptions, ViewClient } from "@runic-artifex/views";
  import { useField, useView, type BridgeField } from "../../src/views/index.js";
  type Fields = {
    readonly title: BridgeField<TClient, TClient["snapshot"], string>;
    readonly step: BridgeField<TClient, TClient["snapshot"], number>;
    readonly done: BridgeField<TClient, TClient["snapshot"], boolean>;
  };
  let { source, fields, options = {} }: { source: TClient; fields: Fields; options?: FieldOptions } = $props();
  const editor = useView(() => source);
  // The fields and options are read once, when the field is created.
  // svelte-ignore state_referenced_locally
  export const title = useField(() => editor, fields.title, options);
  // svelte-ignore state_referenced_locally
  export const step = useField(() => editor, fields.step);
  // svelte-ignore state_referenced_locally
  export const done = useField(() => editor, fields.done);
</script>

<input id="title" {@attach title.attach}>
<input id="step" inputmode="decimal" {@attach step.attach}>
<input id="done" type="checkbox" {@attach done.attach}>
<input id="bound" bind:value={title.value}>
