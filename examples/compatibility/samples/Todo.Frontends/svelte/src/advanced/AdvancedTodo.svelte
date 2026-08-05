<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import type { SvelteMvvmStore } from "@runic-artifex/mvvm-svelte";
  import { toSvelteMvvmRune } from "@runic-artifex/mvvm-svelte/runes";
  import {
    type AdvancedTodoContract,
    type AdvancedTodoState,
  } from "../../../shared/contracts";
  import AppHeader from "../components/AppHeader.svelte";
  import { createAdvancedTodoStores } from "../todo-bindings.g";

  interface Props {
    model: SvelteMvvmStore;
    todo: AdvancedTodoContract;
  }

  const emptyState: AdvancedTodoState = {
    totalCount: 0,
    remainingCount: 0,
    completedCount: 0,
    isImporting: false,
    wizardStep: null,
    wizardIssues: [],
  };

  let { model, todo }: Props = $props();
  let title = $state("");
  let notes = $state("");
  let priority = $state("Normal");
  let query = $state("");
  let filter = $state("All");
  const initialModel = untrack(() => model);
  const initialTodo = untrack(() => todo);
  const bindings = createAdvancedTodoStores(initialModel, initialTodo);
  const modelState = toSvelteMvvmRune(initialModel);
  const itemsState = toSvelteMvvmRune(bindings.items);
  const diagnosticsState = toSvelteMvvmRune(bindings.diagnostics);
  const projectedState = toSvelteMvvmRune(bindings.state);
  const validationState = toSvelteMvvmRune(bindings.newTitleErrors);
  const addState = toSvelteMvvmRune(bindings.add);
  let items = $derived(itemsState.current);
  let diagnostics = $derived(diagnosticsState.current);
  let todoState = $derived(projectedState.current ?? emptyState);
  let validation = $derived(validationState.current);
  let connected = $derived(modelState.current.synchronized);
  let status = $derived(connected
    ? `Connected · r${modelState.current.revision}`
    : modelState.current.phase);
  let wizardReview = $derived(todoState.wizardStep === "todo.create.review");

  async function setDraft(): Promise<void> {
    await initialTodo.newTitle.set(title);
    await initialTodo.newNotes.set(notes);
    await initialTodo.newPriority.set(priority);
  }

  async function add(): Promise<void> {
    await setDraft();
    await bindings.add.execute().completion;
    if (initialTodo.newTitle.validation.length === 0) {
      title = "";
      notes = "";
      priority = "Normal";
    }
  }

  async function applyFilter(): Promise<void> {
    await initialTodo.query.set(query);
    await initialTodo.filter.set(filter);
    await initialTodo.applyFilter.execute().completion;
  }

  async function wizard(command: typeof initialTodo.wizardStart): Promise<void> {
    if (command === initialTodo.wizardStart || command === initialTodo.wizardNext) {
      await setDraft();
    }
    await command.execute().completion;
  }

  function submitAdvanced(event: SubmitEvent): void {
    event.preventDefault();
    void add();
  }

  function submitFilter(event: SubmitEvent): void {
    event.preventDefault();
    void applyFilter();
  }

  onDestroy(() => bindings.dispose());
</script>

<div class="app-shell">
  <AppHeader
    title="Advanced ToDo"
    subtitle="Persistence, filtering, validation, cancellation, and Flow through Svelte stores."
    {connected}
    {status}
  />
  <div class="row g-4">
    <div class="col-lg-8">
      <section class="card workspace-card mb-4">
        <div class="card-body p-4">
          <h2 class="h5 mb-3">Quick add</h2>
          <form onsubmit={submitAdvanced}>
            <div class="row g-3">
              <div class="col-md-7">
                <label class="visually-hidden" for="advanced-title">Task title</label>
                <input
                  id="advanced-title"
                  class:is-invalid={validation.length > 0}
                  class="form-control"
                  bind:value={title}
                  maxlength="120"
                  placeholder="Task title"
                >
                {#if validation.length}
                  <div class="invalid-feedback">{validation.join(" ")}</div>
                {/if}
              </div>
              <div class="col-md-3">
                <label class="visually-hidden" for="advanced-priority">Priority</label>
                <select id="advanced-priority" class="form-select" bind:value={priority}>
                  <option>Low</option><option>Normal</option><option>High</option>
                </select>
              </div>
              <div class="col-md-2 d-grid">
                <button
                  class="btn btn-primary"
                  disabled={!modelState.current.synchronized ||
                    addState.current?.canExecute !== true ||
                    addState.current.isRunning}
                >Add</button>
              </div>
              <div class="col-12">
                <label class="visually-hidden" for="advanced-notes">Notes</label>
                <textarea
                  id="advanced-notes"
                  class="form-control"
                  bind:value={notes}
                  rows="2"
                  placeholder="Notes (optional)"
                ></textarea>
              </div>
            </div>
          </form>
        </div>
      </section>
      <section class="card workspace-card">
        <div class="card-body p-4">
          <form class="row g-2 mb-4" onsubmit={submitFilter}>
            <div class="col-md-7">
              <label class="visually-hidden" for="advanced-query">Search tasks</label>
              <input
                id="advanced-query"
                class="form-control"
                bind:value={query}
                placeholder="Search title and notes"
              >
            </div>
            <div class="col-md-3">
              <label class="visually-hidden" for="advanced-filter">Task status</label>
              <select id="advanced-filter" class="form-select" bind:value={filter}>
                <option>All</option><option>Active</option><option>Completed</option>
              </select>
            </div>
            <div class="col-md-2 d-grid">
              <button class="btn btn-outline-primary">Apply</button>
            </div>
          </form>
          <div class="list-group list-group-flush">
            {#each items as item (item.id)}
              <article
                class:completed={item.isCompleted}
                class="list-group-item px-0 todo-row"
              >
                <button
                  class="btn btn-sm btn-outline-primary rounded-circle"
                  aria-label={item.isCompleted ? "Mark active" : "Mark complete"}
                  onclick={() => initialTodo.toggle.execute(item.id).completion}
                >
                  <i
                    class={item.isCompleted ? "fa-solid fa-check" : "fa-regular fa-circle"}
                    aria-hidden="true"
                  ></i>
                </button>
                <div>
                  <div class="d-flex gap-2 align-items-center">
                    <strong class="todo-title">{item.title}</strong>
                    <span class={`badge priority-${item.priority}`}>{item.priority}</span>
                  </div>
                  {#if item.notes}
                    <div class="small text-secondary todo-notes">{item.notes}</div>
                  {/if}
                </div>
                <button
                  class="btn btn-sm btn-outline-danger todo-actions"
                  aria-label={`Delete ${item.title}`}
                  onclick={() => initialTodo.delete.execute(item.id).completion}
                >
                  <i class="fa-solid fa-trash" aria-hidden="true"></i>
                </button>
              </article>
            {/each}
            {#if items.length === 0}
              <p class="text-secondary text-center my-4">No tasks match this view.</p>
            {/if}
          </div>
        </div>
      </section>
    </div>
    <aside class="col-lg-4">
      <section class="card workspace-card mb-4">
        <div class="card-body">
          <div class="summary-grid mb-3">
            <div class="summary-tile">
              <span class="summary-value">{todoState.totalCount}</span><small>Total</small>
            </div>
            <div class="summary-tile">
              <span class="summary-value">{todoState.remainingCount}</span><small>Active</small>
            </div>
            <div class="summary-tile">
              <span class="summary-value">{todoState.completedCount}</span><small>Done</small>
            </div>
          </div>
          <div class="d-grid gap-2">
            <button
              class="btn btn-outline-primary"
              disabled={todoState.isImporting}
              onclick={() => initialTodo.import.execute().completion}
            >
              <i class="fa-solid fa-download me-2" aria-hidden="true"></i>
              {todoState.isImporting ? "Importing…" : "Import starter tasks"}
            </button>
            {#if todoState.isImporting}
              <button
                class="btn btn-outline-danger"
                onclick={() => initialTodo.cancelImport.execute().completion}
              >Cancel import</button>
            {/if}
            <button
              class="btn btn-outline-secondary"
              onclick={() => initialTodo.clearCompleted.execute().completion}
            >Clear completed</button>
          </div>
        </div>
      </section>
      <section class="card workspace-card mb-4">
        <div class="card-body">
          <h2 class="h5">Guided creation</h2>
          {#if todoState.wizardStep === null}
            <button
              class="btn btn-primary"
              onclick={() => wizard(initialTodo.wizardStart)}
            >Start workflow</button>
          {:else}
            <p class="small text-secondary">
              {wizardReview
                ? "Review the retained draft before saving."
                : "Enter task details, then continue."}
            </p>
            {#each todoState.wizardIssues as issue (issue)}
              <div class="alert alert-warning py-2">{issue}</div>
            {/each}
            <div class="d-flex flex-wrap gap-2">
              {#if !wizardReview}
                <button
                  class="btn btn-primary"
                  onclick={() => wizard(initialTodo.wizardNext)}
                >Next</button>
              {:else}
                <button
                  class="btn btn-outline-secondary"
                  onclick={() => wizard(initialTodo.wizardBack)}
                >Back</button>
                <button
                  class="btn btn-success"
                  onclick={() => wizard(initialTodo.wizardFinish)}
                >Save</button>
              {/if}
              <button
                class="btn btn-outline-danger"
                onclick={() => wizard(initialTodo.wizardCancel)}
              >Cancel</button>
            </div>
          {/if}
        </div>
      </section>
      <section class="card workspace-card">
        <div class="card-body">
          <h2 class="h5">Diagnostics</h2>
          <div class="diagnostic-list list-group list-group-flush">
            {#each diagnostics as entry, index (`${entry.at}-${index}`)}
              <div class="list-group-item px-0">
                <div class="small">
                  <span class="badge text-bg-light me-2">{entry.category}</span>
                  <time>{new Date(entry.at).toLocaleTimeString()}</time>
                </div>
                <div>{entry.message}</div>
              </div>
            {/each}
          </div>
        </div>
      </section>
    </aside>
  </div>
</div>
