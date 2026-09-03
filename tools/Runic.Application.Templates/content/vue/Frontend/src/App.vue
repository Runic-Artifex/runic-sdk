<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import { counterBridge } from "./counter-bridge";
import type { CounterSnapshot } from "./application.bridge";

const snapshot = ref<CounterSnapshot>({ count: 0, history: [0], revision: 0 });
const step = ref(1);
const error = ref<string>();
let unsubscribe = () => undefined;
onMounted(() => {
  unsubscribe = counterBridge.subscribe(
    (event) => { snapshot.value = event.snapshot; },
    (failure) => { error.value = failure.message; },
  );
  void counterBridge.initialize().then(
    (value) => { snapshot.value = value; },
    (failure) => { error.value = failure.message; },
  );
});
onUnmounted(() => unsubscribe());
async function increment(): Promise<void> {
  const receipt = await counterBridge.dispatch({ _tag: "IncrementCounter", step: step.value });
  if (receipt && typeof receipt === "object" && "snapshot" in receipt) {
    snapshot.value = receipt.snapshot as CounterSnapshot;
  }
}
</script>

<template>
  <main class="container py-5" style="max-width: 720px">
    <div class="d-flex justify-content-between align-items-center mb-3">
      <span class="badge text-bg-success">Vue + native C#</span>
      <span class="badge text-bg-success">
        Connected · r{{ snapshot.revision }}
      </span>
    </div>
    <section class="card border-0 shadow"><div class="card-body p-5">
      <p class="display-2 fw-semibold mb-2">{{ snapshot.count }}</p>
      <p class="lead text-secondary">{{ snapshot.history.length - 1 }} named command(s)</p>
      <label class="form-label" for="step">Increment step</label>
      <input id="step" type="number" min="1" max="10" class="form-control"
        v-model.number="step">
      <div v-if="error" class="text-danger mt-2">{{ error }}</div>
      <button class="btn btn-primary w-100 mt-3"
        @click="increment">
        <i class="fa-solid fa-plus me-2" aria-hidden="true" />Increment in C#
      </button>
      <h2 class="h6 mt-4">History</h2>
      <div class="d-flex flex-wrap gap-2">
        <span v-for="(value, index) in snapshot.history" :key="`${index}-${value}`"
          class="badge text-bg-light">{{ value }}</span>
      </div>
    </div></section>
  </main>
</template>
