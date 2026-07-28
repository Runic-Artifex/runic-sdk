<script setup lang="ts">
import { computed, ref } from "vue";
import { type VueMvvmAdapter } from "@webuitoolkit/mvvm-vue";

import {
  type AdvancedTodoContract,
  type AdvancedTodoState,
} from "../../shared/contracts";
import { useAdvancedTodoBindings } from "./todo-bindings.g";

const props = defineProps<{
  adapter: VueMvvmAdapter;
  todo: AdvancedTodoContract;
}>();

const framework = "Vue";
const snapshot = props.adapter.state;
const title = ref("");
const notes = ref("");
const priority = ref("Normal");
const query = ref("");
const filter = ref("All");
const bindings = useAdvancedTodoBindings(props.todo, props.adapter);
const items = bindings.items;
const diagnostics = bindings.diagnostics;
const projectedState = bindings.state;
const state = computed<AdvancedTodoState>(() =>
  projectedState.value ?? {
    totalCount: 0,
    remainingCount: 0,
    completedCount: 0,
    isImporting: false,
    wizardStep: null,
    wizardIssues: [],
  });
const projectedValidation = bindings.newTitleErrors;
const validation = computed(() => projectedValidation.value ?? []);
const connected = computed(() => snapshot.value.synchronized);
const status = computed(() => snapshot.value.synchronized
  ? `Connected · r${snapshot.value.revision}`
  : snapshot.value.phase);
const canAdd = computed(() =>
  snapshot.value.synchronized &&
  bindings.add.canExecute.value &&
  !bindings.add.isRunning.value);
const wizardOpen = computed(() => state.value.wizardStep !== null);
const wizardReview = computed(() => state.value.wizardStep === "todo.create.review");

async function setDraft() {
  await props.todo.newTitle.set(title.value);
  await props.todo.newNotes.set(notes.value);
  await props.todo.newPriority.set(priority.value);
}

async function add() {
  await setDraft();
  await bindings.add.execute().completion;
  if (props.todo.newTitle.validation.length === 0) {
    title.value = "";
    notes.value = "";
    priority.value = "Normal";
  }
}

async function applyFilter() {
  await props.todo.query.set(query.value);
  await props.todo.filter.set(filter.value);
  await props.todo.applyFilter.execute().completion;
}

async function wizard(command: typeof props.todo.wizardStart) {
  if (command === props.todo.wizardStart || command === props.todo.wizardNext) {
    await setDraft();
  }
  await command.execute().completion;
}

function formatTime(value: string) {
  return new Date(value).toLocaleTimeString();
}

function toggle(id: string) {
  void bindings.toggle.execute(id).completion;
}

function deleteTodo(id: string) {
  void bindings.delete.execute(id).completion;
}

function importTodos() {
  void bindings.import.execute().completion;
}

function cancelImport() {
  void bindings.cancelImport.execute().completion;
}

function clearCompleted() {
  void bindings.clearCompleted.execute().completion;
}

</script>

<template>
  <div class="app-shell">
    <header class="mb-4">
      <div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
        <div>
          <span class="framework-badge badge text-bg-success mb-2">{{ framework }}</span>
          <h1 class="display-6 fw-semibold mb-1">Advanced ToDo</h1>
          <p class="text-secondary mb-0">Persistence, filtering, validation, cancellation, and Flow through Vue.</p>
        </div>
        <span class="badge" :class="connected ? 'text-bg-success' : 'text-bg-secondary'">{{ status }}</span>
      </div>
    </header>
    <div class="row g-4">
      <div class="col-lg-8">
        <section class="card workspace-card mb-4">
          <div class="card-body p-4">
            <h2 class="h5 mb-3">Quick add</h2>
            <form @submit.prevent="add">
              <div class="row g-3">
                <div class="col-md-7">
                  <label class="visually-hidden" for="advanced-title">Task title</label>
                  <input
                    id="advanced-title"
                    v-model="title"
                    class="form-control"
                    :class="{ 'is-invalid': validation.length }"
                    maxlength="120"
                    placeholder="Task title"
                  >
                  <div v-if="validation.length" class="invalid-feedback">{{ validation.join(" ") }}</div>
                </div>
                <div class="col-md-3">
                  <label class="visually-hidden" for="advanced-priority">Priority</label>
                  <select id="advanced-priority" v-model="priority" class="form-select">
                    <option>Low</option><option>Normal</option><option>High</option>
                  </select>
                </div>
                <div class="col-md-2 d-grid"><button class="btn btn-primary" :disabled="!canAdd">Add</button></div>
                <div class="col-12">
                  <label class="visually-hidden" for="advanced-notes">Notes</label>
                  <textarea id="advanced-notes" v-model="notes" class="form-control" rows="2" placeholder="Notes (optional)" />
                </div>
              </div>
            </form>
          </div>
        </section>
        <section class="card workspace-card">
          <div class="card-body p-4">
            <form class="row g-2 mb-4" @submit.prevent="applyFilter">
              <div class="col-md-7">
                <label class="visually-hidden" for="advanced-query">Search tasks</label>
                <input id="advanced-query" v-model="query" class="form-control" placeholder="Search title and notes">
              </div>
              <div class="col-md-3">
                <label class="visually-hidden" for="advanced-filter">Completion filter</label>
                <select id="advanced-filter" v-model="filter" class="form-select">
                  <option>All</option><option>Active</option><option>Completed</option>
                </select>
              </div>
              <div class="col-md-2 d-grid"><button class="btn btn-outline-primary">Apply</button></div>
            </form>
            <div class="list-group list-group-flush">
              <article
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
                <div>
                  <div class="d-flex gap-2 align-items-center">
                    <strong class="todo-title">{{ item.title }}</strong>
                    <span class="badge" :class="'priority-' + item.priority">{{ item.priority }}</span>
                  </div>
                  <div v-if="item.notes" class="small text-secondary todo-notes">{{ item.notes }}</div>
                </div>
                <button
                  class="btn btn-sm btn-outline-danger todo-actions"
                  :aria-label="'Delete ' + item.title"
                  @click="deleteTodo(item.id)"
                >
                  <i class="fa-solid fa-trash" aria-hidden="true" />
                </button>
              </article>
              <p v-if="items.length === 0" class="text-secondary text-center my-4">No tasks match this view.</p>
            </div>
          </div>
        </section>
      </div>
      <aside class="col-lg-4">
        <section class="card workspace-card mb-4">
          <div class="card-body">
            <div class="summary-grid mb-3">
              <div class="summary-tile"><span class="summary-value">{{ state.totalCount }}</span><small>Total</small></div>
              <div class="summary-tile"><span class="summary-value">{{ state.remainingCount }}</span><small>Active</small></div>
              <div class="summary-tile"><span class="summary-value">{{ state.completedCount }}</span><small>Done</small></div>
            </div>
            <div class="d-grid gap-2">
              <button class="btn btn-outline-primary" :disabled="state.isImporting" @click="importTodos">
                <i class="fa-solid fa-download me-2" aria-hidden="true" />{{ state.isImporting ? "Importing…" : "Import starter tasks" }}
              </button>
              <button v-if="state.isImporting" class="btn btn-outline-danger" @click="cancelImport">Cancel import</button>
              <button class="btn btn-outline-secondary" @click="clearCompleted">Clear completed</button>
            </div>
          </div>
        </section>
        <section class="card workspace-card mb-4">
          <div class="card-body">
            <h2 class="h5">Guided creation</h2>
            <button v-if="!wizardOpen" class="btn btn-primary" @click="wizard(todo.wizardStart)">Start workflow</button>
            <template v-else>
              <p class="small text-secondary">{{ wizardReview ? "Review the retained draft before saving." : "Enter task details, then continue." }}</p>
              <div v-for="issue in state.wizardIssues" :key="issue" class="alert alert-warning py-2">{{ issue }}</div>
              <div class="d-flex flex-wrap gap-2">
                <button v-if="!wizardReview" class="btn btn-primary" @click="wizard(todo.wizardNext)">Next</button>
                <template v-else>
                  <button class="btn btn-outline-secondary" @click="wizard(todo.wizardBack)">Back</button>
                  <button class="btn btn-success" @click="wizard(todo.wizardFinish)">Save</button>
                </template>
                <button class="btn btn-outline-danger" @click="wizard(todo.wizardCancel)">Cancel</button>
              </div>
            </template>
          </div>
        </section>
        <section class="card workspace-card">
          <div class="card-body">
            <h2 class="h5">Diagnostics</h2>
            <div class="diagnostic-list list-group list-group-flush">
              <div v-for="(entry, index) in diagnostics" :key="entry.at + index" class="list-group-item px-0">
                <div class="small">
                  <span class="badge text-bg-light me-2">{{ entry.category }}</span>
                  <time>{{ formatTime(entry.at) }}</time>
                </div>
                <div>{{ entry.message }}</div>
              </div>
            </div>
          </div>
        </section>
      </aside>
    </div>
  </div>
</template>
