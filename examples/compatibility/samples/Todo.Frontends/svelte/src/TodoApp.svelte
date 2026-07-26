<script lang="ts">
  import { onDestroy } from "svelte";
  import {
    derivedMvvmCollection,
    derivedMvvmCommand,
    derivedMvvmProperty,
    derivedMvvmValidation,
    type SvelteMvvmStore,
  } from "@webuitoolkit/mvvm-svelte";
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

  export let demo: TodoDemo;
  export let model: SvelteMvvmStore;
  export let todo: SimpleTodoContract | AdvancedTodoContract;

  let title = "";
  let notes = "";
  let priority = "Normal";
  let query = "";
  let filter = "All";
  let pending = false;
  const simple = todo as SimpleTodoContract;
  const advanced = todo as AdvancedTodoContract;
  const simpleItemsStore = demo === "simple"
    ? derivedMvvmCollection(model, simple.items)
    : readable<readonly SimpleTodoItem[]>([]);
  const advancedItemsStore = demo === "advanced"
    ? derivedMvvmCollection(model, advanced.items)
    : readable<readonly AdvancedTodoItem[]>([]);
  const diagnosticsStore = demo === "advanced"
    ? derivedMvvmCollection(model, advanced.diagnostics)
    : readable<readonly DiagnosticEntry[]>([]);
  const stateStore = demo === "advanced"
    ? derivedMvvmProperty(model, advanced.state)
    : readable<AdvancedTodoState | undefined>(undefined);
  const validationStore = demo === "advanced"
    ? derivedMvvmValidation(model, advanced.newTitle)
    : readable<readonly string[]>([]);
  const simpleAddState = demo === "simple"
    ? derivedMvvmCommand(model, simple.add)
    : readable(undefined);
  const advancedAddState = demo === "advanced"
    ? derivedMvvmCommand(model, advanced.add)
    : readable(undefined);

  $: simpleItems = demo === "simple" ? $simpleItemsStore : [];
  $: completed = simpleItems.filter((item) => item.isCompleted).length;
  $: advancedItems = demo === "advanced" ? $advancedItemsStore : [];
  $: diagnostics = demo === "advanced" ? $diagnosticsStore : [];
  $: state = (demo === "advanced" ? $stateStore : undefined) ?? {
    totalCount: 0,
    remainingCount: 0,
    completedCount: 0,
    isImporting: false,
    wizardStep: null,
    wizardIssues: [],
  };
  $: validation = demo === "advanced" ? $validationStore : [];
  $: status = $model.synchronized ? `Connected · r${$model.revision}` : $model.phase;
  $: connected = $model.synchronized;
  $: wizardReview = state.wizardStep === "todo.create.review";

  async function addSimple() {
    if (title.trim().length < 2) return;
    pending = true;
    try {
      await simple.newTitle.set(title);
      await simple.add.execute().completion;
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
    await advanced.add.execute().completion;
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

  onDestroy(() => {
    model.dispose();
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
      <form class="input-group mb-4" on:submit|preventDefault={addSimple}>
        <label class="visually-hidden" for="new-title">New task</label>
        <input id="new-title" class="form-control" bind:value={title} maxlength="80" placeholder="What needs doing?">
        <button class="btn btn-primary" disabled={pending || title.trim().length < 2 || !$model.synchronized || $simpleAddState?.canExecute !== true || $simpleAddState.isExecuting}><i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Add</button>
      </form>
      <div class="d-flex justify-content-between text-secondary small mb-3"><span>{simpleItems.length - completed} remaining</span><span>{completed} completed</span></div>
      <ul class="list-group list-group-flush">
        {#each simpleItems as item (item.id)}
          <li class:completed={item.isCompleted} class="list-group-item px-0 todo-row">
            <button class="btn btn-sm btn-outline-primary rounded-circle" aria-label={item.isCompleted ? "Mark active" : "Mark complete"} on:click={() => simple.toggle.execute(item.id).completion}><i class={item.isCompleted ? "fa-solid fa-check" : "fa-regular fa-circle"} aria-hidden="true"></i></button>
            <span class="todo-title">{item.title}</span>
            <button class="btn btn-sm btn-outline-danger todo-actions" aria-label={`Remove ${item.title}`} on:click={() => simple.remove.execute(item.id).completion}><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
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
          <form on:submit|preventDefault={addAdvanced}><div class="row g-3">
            <div class="col-md-7"><input class:is-invalid={validation.length > 0} class="form-control" bind:value={title} maxlength="120" placeholder="Task title">{#if validation.length}<div class="invalid-feedback">{validation.join(" ")}</div>{/if}</div>
            <div class="col-md-3"><select class="form-select" bind:value={priority}><option>Low</option><option>Normal</option><option>High</option></select></div>
            <div class="col-md-2 d-grid"><button class="btn btn-primary" disabled={!$model.synchronized || $advancedAddState?.canExecute !== true || $advancedAddState.isExecuting}>Add</button></div>
            <div class="col-12"><textarea class="form-control" bind:value={notes} rows="2" placeholder="Notes (optional)"></textarea></div>
          </div></form>
        </div></section>
        <section class="card workspace-card"><div class="card-body p-4">
          <form class="row g-2 mb-4" on:submit|preventDefault={applyFilter}>
            <div class="col-md-7"><input class="form-control" bind:value={query} placeholder="Search title and notes"></div>
            <div class="col-md-3"><select class="form-select" bind:value={filter}><option>All</option><option>Active</option><option>Completed</option></select></div>
            <div class="col-md-2 d-grid"><button class="btn btn-outline-primary">Apply</button></div>
          </form>
          <div class="list-group list-group-flush">
            {#each advancedItems as item (item.id)}
              <article class:completed={item.isCompleted} class="list-group-item px-0 todo-row">
                <button class="btn btn-sm btn-outline-primary rounded-circle" aria-label="Toggle task" on:click={() => advanced.toggle.execute(item.id).completion}><i class={item.isCompleted ? "fa-solid fa-check" : "fa-regular fa-circle"}></i></button>
                <div><div class="d-flex gap-2 align-items-center"><strong class="todo-title">{item.title}</strong><span class={`badge priority-${item.priority}`}>{item.priority}</span></div>{#if item.notes}<div class="small text-secondary todo-notes">{item.notes}</div>{/if}</div>
                <button class="btn btn-sm btn-outline-danger todo-actions" aria-label={`Delete ${item.title}`} on:click={() => advanced.delete.execute(item.id).completion}><i class="fa-solid fa-trash"></i></button>
              </article>
            {/each}
            {#if advancedItems.length === 0}<p class="text-secondary text-center my-4">No tasks match this view.</p>{/if}
          </div>
        </div></section>
      </div>
      <aside class="col-lg-4">
        <section class="card workspace-card mb-4"><div class="card-body">
          <div class="summary-grid mb-3"><div class="summary-tile"><span class="summary-value">{state.totalCount}</span><small>Total</small></div><div class="summary-tile"><span class="summary-value">{state.remainingCount}</span><small>Active</small></div><div class="summary-tile"><span class="summary-value">{state.completedCount}</span><small>Done</small></div></div>
          <div class="d-grid gap-2"><button class="btn btn-outline-primary" disabled={state.isImporting} on:click={() => advanced.import.execute().completion}><i class="fa-solid fa-download me-2"></i>{state.isImporting ? "Importing…" : "Import starter tasks"}</button>{#if state.isImporting}<button class="btn btn-outline-danger" on:click={() => advanced.cancelImport.execute().completion}>Cancel import</button>{/if}<button class="btn btn-outline-secondary" on:click={() => advanced.clearCompleted.execute().completion}>Clear completed</button></div>
        </div></section>
        <section class="card workspace-card mb-4"><div class="card-body">
          <h2 class="h5">Guided creation</h2>
          {#if state.wizardStep === null}<button class="btn btn-primary" on:click={() => wizard(advanced.wizardStart)}>Start workflow</button>{:else}
            <p class="small text-secondary">{wizardReview ? "Review the retained draft before saving." : "Enter task details, then continue."}</p>
            {#each state.wizardIssues as issue}<div class="alert alert-warning py-2">{issue}</div>{/each}
            <div class="d-flex flex-wrap gap-2">{#if !wizardReview}<button class="btn btn-primary" on:click={() => wizard(advanced.wizardNext)}>Next</button>{:else}<button class="btn btn-outline-secondary" on:click={() => wizard(advanced.wizardBack)}>Back</button><button class="btn btn-success" on:click={() => wizard(advanced.wizardFinish)}>Save</button>{/if}<button class="btn btn-outline-danger" on:click={() => wizard(advanced.wizardCancel)}>Cancel</button></div>
          {/if}
        </div></section>
        <section class="card workspace-card"><div class="card-body"><h2 class="h5">Diagnostics</h2><div class="diagnostic-list list-group list-group-flush">{#each diagnostics as entry, index (`${entry.at}-${index}`)}<div class="list-group-item px-0"><div class="small"><span class="badge text-bg-light me-2">{entry.category}</span><time>{new Date(entry.at).toLocaleTimeString()}</time></div><div>{entry.message}</div></div>{/each}</div></div></section>
      </aside>
    </div>
  </div>
{/if}
