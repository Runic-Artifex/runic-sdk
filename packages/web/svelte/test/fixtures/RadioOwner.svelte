<script lang="ts" generics="TClient extends ViewClient">
  import type { FieldOptions, ViewClient } from "@runic-artifex/views";
  import { useField, useView, type BridgeField } from "../../src/views/index.js";
  let { source, field, options = {} }: { source: TClient; field: BridgeField<TClient, TClient["snapshot"], number>; options?: FieldOptions } = $props();
  const editor = useView(() => source);
  // The field and options are read once, when the field is created.
  // svelte-ignore state_referenced_locally
  export const step = useField(() => editor, field, options);
</script>

{#each ["1", "2", "3"] as option (option)}
  <input type="radio" name="step" value={option} {@attach step.attach}>
{/each}
