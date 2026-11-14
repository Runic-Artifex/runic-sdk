<script lang="ts">
  // A page's own message that is edited while the page runs, next to component state that must survive the update.
  import { useTextLocale } from "../../src/views/index.js";
  import { edited } from "./edited-message.js";

  const textLocale = useTextLocale();
  let count = $state(0);
  export function increment(): void { count++; }
  const title = (): string => { void textLocale.current; return edited.text; };
</script>

<h1>{title()} {count}</h1>
