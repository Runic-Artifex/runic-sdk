<script setup lang="ts">
import { computed, ref } from "vue";
import { type VueMvvmAdapter } from "@runic-artifex/mvvm-vue";

import { type SimpleTodoContract } from "../../shared/contracts";
import { useSimpleTodoBindings } from "./todo-bindings.g";

const props = defineProps<{
  adapter: VueMvvmAdapter;
  todo: SimpleTodoContract;
}>();

const framework = "Vue";
const snapshot = props.adapter.state;
const title = ref("");
const pending = ref(false);
const bindings = useSimpleTodoBindings(props.todo, props.adapter);
const items = bindings.items;
const completed = computed(() => items.value.filter((item) => item.isCompleted).length);
const connected = computed(() => snapshot.value.synchronized);
const status = computed(() => snapshot.value.synchronized
  ? `Connected · r${snapshot.value.revision}`
  : snapshot.value.phase);
const canAdd = computed(() =>
  !pending.value &&
  title.value.trim().length >= 2 &&
  snapshot.value.synchronized &&
  bindings.add.canExecute.value &&
  !bindings.add.isRunning.value);

async function add() {
  if (title.value.trim().length < 2) return;
  pending.value = true;
  try {
    await props.todo.newTitle.set(title.value);
    await bindings.add.execute().completion;
    title.value = "";
  } finally {
    pending.value = false;
  }
}

function toggle(id: string) {
  void bindings.toggle.execute(id).completion;
}

function remove(id: string) {
  void bindings.remove.execute(id).completion;
}

</script>

<template>
  <div class="app-shell simple-shell">
    <header class="mb-4">
      <div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
        <div>
          <span class="framework-badge badge text-bg-success mb-2">{{ framework }}</span>
          <h1 class="display-6 fw-semibold mb-1">Simple ToDo</h1>
          <p class="text-secondary mb-0">One shared C# ViewModel, exposed as Vue computed refs.</p>
        </div>
        <span class="badge" :class="connected ? 'text-bg-success' : 'text-bg-secondary'">{{ status }}</span>
      </div>
    </header>
    <section class="card hero-card">
      <div class="card-body p-4">
        <form class="input-group mb-4" @submit.prevent="add">
          <label class="visually-hidden" for="new-title">New task</label>
          <input id="new-title" v-model="title" class="form-control" maxlength="80" placeholder="What needs doing?">
          <button class="btn btn-primary" :disabled="!canAdd">
            <i class="fa-solid fa-plus me-2" aria-hidden="true" />Add
          </button>
        </form>
        <div class="d-flex justify-content-between text-secondary small mb-3">
          <span>{{ items.length - completed }} remaining</span>
          <span>{{ completed }} completed</span>
        </div>
        <ul class="list-group list-group-flush">
          <li
            v-for="item in items"
            :key="item.id"
            class="list-group-item px-0 todo-row"
            :class="{ completed: item.isCompleted }"
          >
            <button
              class="btn btn-sm btn-outline-primary rounded-circle"
              :aria-label="item.isCompleted ? 'Mark active' : 'Mark complete'"
              @click="toggle(item.id)"
            >
              <i :class="item.isCompleted ? 'fa-solid fa-check' : 'fa-regular fa-circle'" aria-hidden="true" />
            </button>
            <span class="todo-title">{{ item.title }}</span>
            <button
              class="btn btn-sm btn-outline-danger todo-actions"
              :aria-label="'Remove ' + item.title"
              @click="remove(item.id)"
            >
              <i class="fa-solid fa-trash" aria-hidden="true" />
            </button>
          </li>
        </ul>
      </div>
    </section>
  </div>
</template>
