import { computed, createApp, onUnmounted, ref } from "vue";
import { createVueMvvmAdapter } from "@webuitoolkit/mvvm-vue";

import {
  AdvancedTodoContract,
  commandEnabled,
  demoFromDocument,
  SimpleTodoContract,
  type AdvancedTodoState,
} from "../../shared/contracts";
import { connectTodo, reportStartupFailure } from "../../shared/runtime";

const framework = "Vue";
const demo = demoFromDocument();

try {
  const connection = await connectTodo(demo);
  const adapter = createVueMvvmAdapter(connection.projection);
  const todo = demo === "simple"
    ? new SimpleTodoContract(connection.projection)
    : new AdvancedTodoContract(connection.projection);
  const app = createApp(demo === "simple"
    ? simpleComponent(adapter, todo as SimpleTodoContract)
    : advancedComponent(adapter, todo as AdvancedTodoContract));
  app.mount("#app");
  globalThis.addEventListener("pagehide", () => {
    app.unmount();
    adapter.dispose();
    void connection.dispose();
  }, { once: true });
} catch (error) {
  reportStartupFailure(error);
}

function common(adapter: ReturnType<typeof createVueMvvmAdapter>) {
  const snapshot = adapter.state;
  return {
    framework,
    snapshot,
    connected: computed(() => snapshot.value.synchronized),
    status: computed(() => snapshot.value.synchronized
      ? `Connected · r${snapshot.value.revision}`
      : snapshot.value.phase),
  };
}

function simpleComponent(
  adapter: ReturnType<typeof createVueMvvmAdapter>,
  todo: SimpleTodoContract,
) {
  return {
    setup() {
      const title = ref("");
      const pending = ref(false);
      const snapshot = adapter.state;
      const items = computed(() => todo.items.from(snapshot.value));
      const completed = computed(() => items.value.filter((item) => item.isCompleted).length);

      async function add() {
        if (title.value.trim().length < 2) return;
        pending.value = true;
        try {
          await todo.newTitle.set(title.value);
          await todo.add.execute().completion;
          title.value = "";
        } finally {
          pending.value = false;
        }
      }

      onUnmounted(() => adapter.dispose());
      return {
        ...common(adapter),
        title,
        pending,
        items,
        completed,
        canAdd: computed(() =>
          !pending.value &&
          title.value.trim().length >= 2 &&
          commandEnabled(snapshot.value, todo.add.member)),
        add,
        todo,
      };
    },
    template: `
      <div class="app-shell simple-shell">
        <header class="mb-4">
          <div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
            <div><span class="framework-badge badge text-bg-success mb-2">{{ framework }}</span><h1 class="display-6 fw-semibold mb-1">Simple ToDo</h1><p class="text-secondary mb-0">One shared C# ViewModel, exposed as Vue computed refs.</p></div>
            <span class="badge" :class="connected ? 'text-bg-success' : 'text-bg-secondary'">{{ status }}</span>
          </div>
        </header>
        <section class="card hero-card"><div class="card-body p-4">
          <form class="input-group mb-4" @submit.prevent="add">
            <label class="visually-hidden" for="new-title">New task</label>
            <input id="new-title" class="form-control" v-model="title" maxlength="80" placeholder="What needs doing?">
            <button class="btn btn-primary" :disabled="!canAdd"><i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Add</button>
          </form>
          <div class="d-flex justify-content-between text-secondary small mb-3"><span>{{ items.length - completed }} remaining</span><span>{{ completed }} completed</span></div>
          <ul class="list-group list-group-flush">
            <li v-for="item in items" :key="item.id" class="list-group-item px-0 todo-row" :class="{ completed: item.isCompleted }">
              <button class="btn btn-sm btn-outline-primary rounded-circle" :aria-label="item.isCompleted ? 'Mark active' : 'Mark complete'" @click="() => todo.toggle.execute(item.id).completion"><i :class="item.isCompleted ? 'fa-solid fa-check' : 'fa-regular fa-circle'" aria-hidden="true"></i></button>
              <span class="todo-title">{{ item.title }}</span>
              <button class="btn btn-sm btn-outline-danger todo-actions" :aria-label="'Remove ' + item.title" @click="() => todo.remove.execute(item.id).completion"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
            </li>
          </ul>
        </div></section>
      </div>`,
  };
}

function advancedComponent(
  adapter: ReturnType<typeof createVueMvvmAdapter>,
  todo: AdvancedTodoContract,
) {
  return {
    setup() {
      const snapshot = adapter.state;
      const title = ref("");
      const notes = ref("");
      const priority = ref("Normal");
      const query = ref("");
      const filter = ref("All");
      const items = computed(() => todo.items.from(snapshot.value));
      const diagnostics = computed(() => todo.diagnostics.from(snapshot.value));
      const state = computed<AdvancedTodoState>(() =>
        todo.state.from(snapshot.value) ?? {
          totalCount: 0,
          remainingCount: 0,
          completedCount: 0,
          isImporting: false,
          wizardStep: null,
          wizardIssues: [],
        });
      const validation = computed(() =>
        snapshot.value.validation.get(todo.newTitle.member) ?? []);

      async function setDraft() {
        await todo.newTitle.set(title.value);
        await todo.newNotes.set(notes.value);
        await todo.newPriority.set(priority.value);
      }
      async function add() {
        await setDraft();
        await todo.add.execute().completion;
        if (todo.newTitle.validation.length === 0) {
          title.value = "";
          notes.value = "";
          priority.value = "Normal";
        }
      }
      async function applyFilter() {
        await todo.query.set(query.value);
        await todo.filter.set(filter.value);
        await todo.applyFilter.execute().completion;
      }
      async function wizard(command: typeof todo.wizardStart) {
        if (command === todo.wizardStart || command === todo.wizardNext) {
          await setDraft();
        }
        await command.execute().completion;
      }

      onUnmounted(() => {
        adapter.dispose();
      });
      return {
        ...common(adapter),
        title,
        notes,
        priority,
        query,
        filter,
        items,
        diagnostics,
        state,
        validation,
        todo,
        add,
        applyFilter,
        wizard,
        canAdd: computed(() => commandEnabled(snapshot.value, todo.add.member)),
        wizardOpen: computed(() => state.value.wizardStep !== null),
        wizardReview: computed(() => state.value.wizardStep === "todo.create.review"),
        formatTime: (value: string) => new Date(value).toLocaleTimeString(),
      };
    },
    template: `
      <div class="app-shell">
        <header class="mb-4"><div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
          <div><span class="framework-badge badge text-bg-success mb-2">{{ framework }}</span><h1 class="display-6 fw-semibold mb-1">Advanced ToDo</h1><p class="text-secondary mb-0">Persistence, filtering, validation, cancellation, and Flow through Vue.</p></div>
          <span class="badge" :class="connected ? 'text-bg-success' : 'text-bg-secondary'">{{ status }}</span>
        </div></header>
        <div class="row g-4">
          <div class="col-lg-8">
            <section class="card workspace-card mb-4"><div class="card-body p-4">
              <h2 class="h5 mb-3">Quick add</h2>
              <form @submit.prevent="add"><div class="row g-3">
                <div class="col-md-7"><input class="form-control" :class="{ 'is-invalid': validation.length }" v-model="title" maxlength="120" placeholder="Task title"><div v-if="validation.length" class="invalid-feedback">{{ validation.join(' ') }}</div></div>
                <div class="col-md-3"><select class="form-select" v-model="priority"><option>Low</option><option>Normal</option><option>High</option></select></div>
                <div class="col-md-2 d-grid"><button class="btn btn-primary" :disabled="!canAdd">Add</button></div>
                <div class="col-12"><textarea class="form-control" v-model="notes" rows="2" placeholder="Notes (optional)"></textarea></div>
              </div></form>
            </div></section>
            <section class="card workspace-card"><div class="card-body p-4">
              <form class="row g-2 mb-4" @submit.prevent="applyFilter">
                <div class="col-md-7"><input class="form-control" v-model="query" placeholder="Search title and notes"></div>
                <div class="col-md-3"><select class="form-select" v-model="filter"><option>All</option><option>Active</option><option>Completed</option></select></div>
                <div class="col-md-2 d-grid"><button class="btn btn-outline-primary">Apply</button></div>
              </form>
              <div class="list-group list-group-flush">
                <article v-for="item in items" :key="item.id" class="list-group-item px-0 todo-row" :class="{ completed: item.isCompleted }">
                  <button class="btn btn-sm btn-outline-primary rounded-circle" aria-label="Toggle task" @click="() => todo.toggle.execute(item.id).completion"><i :class="item.isCompleted ? 'fa-solid fa-check' : 'fa-regular fa-circle'"></i></button>
                  <div><div class="d-flex gap-2 align-items-center"><strong class="todo-title">{{ item.title }}</strong><span class="badge" :class="'priority-' + item.priority">{{ item.priority }}</span></div><div v-if="item.notes" class="small text-secondary todo-notes">{{ item.notes }}</div></div>
                  <button class="btn btn-sm btn-outline-danger todo-actions" :aria-label="'Delete ' + item.title" @click="() => todo.delete.execute(item.id).completion"><i class="fa-solid fa-trash"></i></button>
                </article>
                <p v-if="items.length === 0" class="text-secondary text-center my-4">No tasks match this view.</p>
              </div>
            </div></section>
          </div>
          <aside class="col-lg-4">
            <section class="card workspace-card mb-4"><div class="card-body">
              <div class="summary-grid mb-3"><div class="summary-tile"><span class="summary-value">{{ state.totalCount }}</span><small>Total</small></div><div class="summary-tile"><span class="summary-value">{{ state.remainingCount }}</span><small>Active</small></div><div class="summary-tile"><span class="summary-value">{{ state.completedCount }}</span><small>Done</small></div></div>
              <div class="d-grid gap-2"><button class="btn btn-outline-primary" :disabled="state.isImporting" @click="() => todo.import.execute().completion"><i class="fa-solid fa-download me-2"></i>{{ state.isImporting ? 'Importing…' : 'Import starter tasks' }}</button><button v-if="state.isImporting" class="btn btn-outline-danger" @click="() => todo.cancelImport.execute().completion">Cancel import</button><button class="btn btn-outline-secondary" @click="() => todo.clearCompleted.execute().completion">Clear completed</button></div>
            </div></section>
            <section class="card workspace-card mb-4"><div class="card-body">
              <h2 class="h5">Guided creation</h2>
              <button v-if="!wizardOpen" class="btn btn-primary" @click="wizard(todo.wizardStart)">Start workflow</button>
              <template v-else><p class="small text-secondary">{{ wizardReview ? 'Review the retained draft before saving.' : 'Enter task details, then continue.' }}</p><div v-for="issue in state.wizardIssues" :key="issue" class="alert alert-warning py-2">{{ issue }}</div><div class="d-flex flex-wrap gap-2"><button v-if="!wizardReview" class="btn btn-primary" @click="wizard(todo.wizardNext)">Next</button><template v-else><button class="btn btn-outline-secondary" @click="wizard(todo.wizardBack)">Back</button><button class="btn btn-success" @click="wizard(todo.wizardFinish)">Save</button></template><button class="btn btn-outline-danger" @click="wizard(todo.wizardCancel)">Cancel</button></div></template>
            </div></section>
            <section class="card workspace-card"><div class="card-body"><h2 class="h5">Diagnostics</h2><div class="diagnostic-list list-group list-group-flush"><div v-for="(entry, index) in diagnostics" :key="entry.at + index" class="list-group-item px-0"><div class="small"><span class="badge text-bg-light me-2">{{ entry.category }}</span><time>{{ formatTime(entry.at) }}</time></div><div>{{ entry.message }}</div></div></div></div></section>
          </aside>
        </div>
      </div>`,
  };
}
