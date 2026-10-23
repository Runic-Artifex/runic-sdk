<script lang="ts">
  import type { MenuBarRequest } from "@runic-artifex/views";
  import { useMenuBar } from "../../src/views/index.js";
  let { requests }: { requests: MenuBarRequest[] } = $props();
  const menuBar = useMenuBar({ onRequest: request => requests.push(request) });
</script>

<output>{menuBar.state.status} {menuBar.state.menus.map(menu => menu.label).join(",")}</output>
