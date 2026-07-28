<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import { type SvelteMvvmStore } from "@webuitoolkit/mvvm-svelte";
  import { toSvelteMvvmRune } from "@webuitoolkit/mvvm-svelte/runes";
  import { readable } from "svelte/store";
  import {
    AdvancedTodoContract,
    SimpleTodoContract,
    type AdvancedTodoItem,
    type AdvancedTodoState,
    type DiagnosticEntry,
    type SimpleTodoItem,
    type TodoDemo,
  } from "../../shared/contracts";
  import {
    createAdvancedTodoStores,
    createSimpleTodoStores,
  } from "./todo-bindings.g";

  interface Props {
    demo: TodoDemo;
    model: SvelteMvvmStore;
    todo: SimpleTodoContract | AdvancedTodoContract;
  }

  let { demo, model, todo }: Props = $props();
  let title = $state("");
  let notes = $state("");
  let priority = $state("Normal");
  let query = $state("");
  let filter = $state("All");
  let pending = $state(false);
  const initialDemo = untrack(() => demo);
  const initialModel = untrack(() => model);
  const initialContract = untrack(() => todo);
  const simple = initialContract as SimpleTodoContract;
  const advanced = initialContract as AdvancedTodoContract;
  const simpleBindings = createSimpleTodoStores(initialModel, simple);
  const advancedBindings = createAdvancedTodoStores(initialModel, advanced);
  const simpleItemsStore = initialDemo === "simple"
    ? simpleBindings.items
    : readable<readonly SimpleTodoItem[]>([]);
  const advancedItemsStore = initialDemo === "advanced"
    ? advancedBindings.items
    : readable<readonly AdvancedTodoItem[]>([]);
  const diagnosticsStore = initialDemo === "advanced"
    ? advancedBindings.diagnostics
    : readable<readonly DiagnosticEntry[]>([]);
  const stateStore = initialDemo === "advanced"
    ? advancedBindings.state
    : readable<AdvancedTodoState | undefined>(undefined);
  const validationStore = initialDemo === "advanced"
    ? advancedBindings.newTitleErrors
    : readable<readonly string[]>([]);
  const simpleAddState = initialDemo === "simple"
    ? simpleBindings.add
    : readable(undefined);
  const advancedAddState = initialDemo === "advanced"
    ? advancedBindings.add
    : readable(undefined);

  const modelState = toSvelteMvvmRune(initialModel);
  const simpleItemsState = toSvelteMvvmRune(simpleItemsStore);
  const advancedItemsState = toSvelteMvvmRune(advancedItemsStore);
  const diagnosticsState = toSvelteMvvmRune(diagnosticsStore);
  const projectedState = toSvelteMvvmRune(stateStore);
  const projectedValidation = toSvelteMvvmRune(validationStore);
  const simpleAdd = toSvelteMvvmRune(simpleAddState);
  const advancedAdd = toSvelteMvvmRune(advancedAddState);
  let simpleItems = $derived(demo === "simple" ? simpleItemsState.current : []);
  let completed = $derived(simpleItems.filter((item) => item.isCompleted).length);
  let advancedItems = $derived(demo === "advanced" ? advancedItemsState.current : []);
  let diagnostics = $derived(demo === "advanced" ? diagnosticsState.current : []);
  let todoState = $derived((demo === "advanced" ? projectedState.current : undefined) ?? {
    totalCount: 0,
    remainingCount: 0,
    completedCount: 0,
    isImporting: false,
    wizardStep: null,
    wizardIssues: [],
  });
  let validation = $derived(demo === "advanced" ? projectedValidation.current : []);
  let status = $derived(modelState.current.synchronized
    ? `Connected · r${modelState.current.revision}`
    : modelState.current.phase);
  let connected = $derived(modelState.current.synchronized);
  let wizardReview = $derived(todoState.wizardStep === "todo.create.review");

  async function addSimple() {
    if (title.trim().length < 2) return;
    pending = true;
    try {
      await simple.newTitle.set(title);
      await simpleBindings.add.execute().completion;
      title = "";
    } finally {
      pending = false;
    }
  }

  async function setDraft() {
    await advanced.newTitle.set(title);
    await advanced.newNotes.set(notes);
    await advanced.newPriority.set(priority);
  }

  async function addAdvanced() {
    await setDraft();
    await advancedBindings.add.execute().completion;
    if (advanced.newTitle.validation.length === 0) {
      title = "";
      notes = "";
      priority = "Normal";
    }
  }

  async function applyFilter() {
    await advanced.query.set(query);
    await advanced.filter.set(filter);
    await advanced.applyFilter.execute().completion;
  }

  async function wizard(command: typeof advanced.wizardStart) {
    if (command === advanced.wizardStart || command === advanced.wizardNext) {
      await setDraft();
    }
    await command.execute().completion;
  }

  function submitSimple(event: SubmitEvent) {
    event.preventDefault();
    void addSimple();
  }

  function submitAdvanced(event: SubmitEvent) {
    event.preventDefault();
    void addAdvanced();
  }

  function submitFilter(event: SubmitEvent) {
    event.preventDefault();
    void applyFilter();
  }

  onDestroy(() => {
    simpleBindings.dispose();
    advancedBindings.dispose();
  });
</script>

{#if demo === "simple"}
  <div class="app-shell simple-shell">
    <header class="mb-4">
      <div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
        <div><span class="framework-badge badge text-bg-danger mb-2">Svelte</span><h1 class="display-6 fw-semibold mb-1">Simple ToDo</h1><p class="text-secondary mb-0">One shared C# ViewModel exposed as a Svelte readable store.</p></div>
        <span class:badge={true} class:text-bg-success={connected} class:text-bg-secondary={!connected}>{status}</span>
      </div>
    </header>
    <section class="card hero-card"><div class="card-body p-4">
      <form class="input-group mb-4" onsubmit={submitSimple}>
        <label class="visually-hidden" for="new-title">New task</label>
        <input id="new-title" class="form-control" bind:value={title} maxlength="80" placeholder="What needs doing?">
        <button class="btn btn-primary" disabled={pending || title.trim().length < 2 || !modelState.current.synchronized || simpleAdd.current?.canExecute !== true || simpleAdd.current.isRunning}><i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Add</button>
      </form>
      <div class="d-flex justify-content-between text-secondary small mb-3"><span>{simpleItems.length - completed} remaining</span><span>{completed} completed</span></div>
      <ul class="list-group list-group-flush">
        {#each simpleItems as item (item.id)}
          <li class:completed={item.isCompleted} class="list-group-item px-0 todo-row">
            <button class="btn btn-sm btn-outline-primary rounded-circle" aria-label={item.isCompleted ? "Mark active" : "Mark complete"} onclick={() => simple.toggle.execute(item.id).completion}><i class={item.isCompleted ? "fa-solid fa-check" : "fa-regular fa-circle"} aria-hidden="true"></i></button>
            <span class="todo-title">{item.title}</span>
            <button class="btn btn-sm btn-outline-danger todo-actions" aria-label={`Remove ${item.title}`} onclick={() => simple.remove.execute(item.id).completion}><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
          </li>
        {/each}
      </ul>
    </div></section>
  </div>
{:else}
  <div class="app-shell">
    <header class="mb-4"><div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
      <div><span class="framework-badge badge text-bg-danger mb-2">Svelte</span><h1 class="display-6 fw-semibold mb-1">Advanced ToDo</h1><p class="text-secondary mb-0">Persistence, filtering, validation, cancellation, and Flow through Svelte stores.</p></div>
      <span class:badge={true} class:text-bg-success={connected} class:text-bg-secondary={!connected}>{status}</span>
    </div></header>
    <div class="row g-4">
      <div class="col-lg-8">
        <section class="card workspace-card mb-4"><div class="card-body p-4">
          <h2 class="h5 mb-3">Quick add</h2>
          <form onsubmit={submitAdvanced}><div class="row g-3">
            <div class="col-md-7"><label class="visually-hidden" for="advanced-title">Task title</label><input id="advanced-title" class:is-invalid={validation.length > 0} class="form-control" bind:value={title} maxlength="120" placeholder="Task title">{#if validation.length}<div class="invalid-feedback">{validation.join(" ")}</div>{/if}</div>
            <div class="col-md-3"><label class="visually-hidden" for="advanced-priority">Priority</label><select id="advanced-priority" class="form-select" bind:value={priority}><option>Low</option><option>Normal</option><option>High</option></select></div>
            <div class="col-md-2 d-grid"><button class="btn btn-primary" disabled={!modelState.current.synchronized || advancedAdd.current?.canExecute !== true || advancedAdd.current.isRunning}>Add</button></div>
            <div class="col-12"><label class="visually-hidden" for="advanced-notes">Notes</label><textarea id="advanced-notes" class="form-control" bind:value={notes} rows="2" placeholder="Notes (optional)"></textarea></div>
          </div></form>
        </div></section>
        <section class="card workspace-card"><div class="card-body p-4">
          <form class="row g-2 mb-4" onsubmit={submitFilter}>
            <div class="col-md-7"><label class="visually-hidden" for="advanced-query">Search tasks</label><input id="advanced-query" class="form-control" bind:value={query} placeholder="Search title and notes"></div>
            <div class="col-md-3"><label class="visually-hidden" for="advanced-filter">Task status</label><select id="advanced-filter" class="form-select" bind:value={filter}><option>All</option><option>Active</option><option>Completed</option></select></div>
            <div class="col-md-2 d-grid"><button class="btn btn-outline-primary">Apply</button></div>
          </form>
          <div class="list-group list-group-flush">
            {#each advancedItems as item (item.id)}
              <article class:completed={item.isCompleted} class="list-group-item px-0 todo-row">
                <button class="btn btn-sm btn-outline-primary rounded-circle" aria-label="Toggle task" onclick={() => advanced.toggle.execute(item.id).completion}><i class={item.isCompleted ? "fa-solid fa-check" : "fa-regular fa-circle"} aria-hidden="true"></i></button>
                <div><div class="d-flex gap-2 align-items-center"><strong class="todo-title">{item.title}</strong><span class={`badge priority-${item.priority}`}>{item.priority}</span></div>{#if item.notes}<div class="small text-secondary todo-notes">{item.notes}</div>{/if}</div>
                <button class="btn btn-sm btn-outline-danger todo-actions" aria-label={`Delete ${item.title}`} onclick={() => advanced.delete.execute(item.id).completion}><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
              </article>
            {/each}
            {#if advancedItems.length === 0}<p class="text-secondary text-center my-4">No tasks match this view.</p>{/if}
          </div>
        </div></section>
      </div>
      <aside class="col-lg-4">
        <section class="card workspace-card mb-4"><div class="card-body">
          <div class="summary-grid mb-3"><div class="summary-tile"><span class="summary-value">{todoState.totalCount}</span><small>Total</small></div><div class="summary-tile"><span class="summary-value">{todoState.remainingCount}</span><small>Active</small></div><div class="summary-tile"><span class="summary-value">{todoState.completedCount}</span><small>Done</small></div></div>
          <div class="d-grid gap-2"><button class="btn btn-outline-primary" disabled={todoState.isImporting} onclick={() => advanced.import.execute().completion}><i class="fa-solid fa-download me-2" aria-hidden="true"></i>{todoState.isImporting ? "Importing…" : "Import starter tasks"}</button>{#if todoState.isImporting}<button class="btn btn-outline-danger" onclick={() => advanced.cancelImport.execute().completion}>Cancel import</button>{/if}<button class="btn btn-outline-secondary" onclick={() => advanced.clearCompleted.execute().completion}>Clear completed</button></div>
        </div></section>
        <section class="card workspace-card mb-4"><div class="card-body">
          <h2 class="h5">Guided creation</h2>
          {#if todoState.wizardStep === null}<button class="btn btn-primary" onclick={() => wizard(advanced.wizardStart)}>Start workflow</button>{:else}
            <p class="small text-secondary">{wizardReview ? "Review the retained draft before saving." : "Enter task details, then continue."}</p>
            {#each todoState.wizardIssues as issue}<div class="alert alert-warning py-2">{issue}</div>{/each}
            <div class="d-flex flex-wrap gap-2">{#if !wizardReview}<button class="btn btn-primary" onclick={() => wizard(advanced.wizardNext)}>Next</button>{:else}<button class="btn btn-outline-secondary" onclick={() => wizard(advanced.wizardBack)}>Back</button><button class="btn btn-success" onclick={() => wizard(advanced.wizardFinish)}>Save</button>{/if}<button class="btn btn-outline-danger" onclick={() => wizard(advanced.wizardCancel)}>Cancel</button></div>
          {/if}
        </div></section>
        <section class="card workspace-card"><div class="card-body"><h2 class="h5">Diagnostics</h2><div class="diagnostic-list list-group list-group-flush">{#each diagnostics as entry, index (`${entry.at}-${index}`)}<div class="list-group-item px-0"><div class="small"><span class="badge text-bg-light me-2">{entry.category}</span><time>{new Date(entry.at).toLocaleTimeString()}</time></div><div>{entry.message}</div></div>{/each}</div></div></section>
      </aside>
    </div>
  </div>
{/if}
