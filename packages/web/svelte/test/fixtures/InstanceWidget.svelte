<script lang="ts">
  import { getRunicInstance, useBridgeConnection, useText, useView, useWindowLocale } from "../../src/views/index.js";
  import type { InstanceCounter } from "./instance-counter.js";
  let { source }: { source: InstanceCounter } = $props();
  const view = useView(() => source);
  const text = useText(() => view.state?.status);
  const locale = useWindowLocale();
  const connection = useBridgeConnection();
  const page = getRunicInstance().page;
</script>

<p>{view.state?.count ?? "…"}|{text.current}|{locale.current ?? "-"}|{connection.state}|{page ? "page" : "guest"}</p>
