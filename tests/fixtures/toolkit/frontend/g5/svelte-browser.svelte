<script>
  export let store;
</script>

<main id="g5-view">
  <span id="amount">{String($store.properties.get(1))}</span>
  <span id="hostile">{String($store.properties.get(3))}</span>
  <span id="validation">{($store.validation.get(1) ?? []).join(",")}</span>
</main>
