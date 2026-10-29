<script lang="ts">
  import type { ViewSource } from "@runic-artifex/views";
  import { FieldError, useField, useView } from "../../dist/index.js";
  import { Fields, type Client } from "./field-fields.js";
  let { source }: { source: ViewSource<Client> } = $props();
  const form = useView(() => source);
  const title = useField(() => form, Fields.title, { commit: "pause", checked: true });
  const step = useField(() => form, Fields.step);
  const done = useField(() => form, Fields.done);
</script>

<input id="title" aria-label="Title" data-ready={String(form.client !== undefined)} data-editing={String(title.editing)}
  data-error={title.error instanceof FieldError ? title.error.kind : ""} {@attach title.attach}>
<input id="step" type="number" aria-label="Step" data-editing={String(step.editing)} {@attach step.attach}>
<input id="done" type="checkbox" aria-label="Done" data-editing={String(done.editing)} {@attach done.attach}>
