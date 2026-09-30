<script lang="ts">
  import type { EditorPageReference } from "../../Frontend/src/generated/editor.js";
  import Editor from "./Editor.svelte";
  let { page }: { page: EditorPageReference } = $props();
</script>

<Editor {page} handleInteractions={false} />
