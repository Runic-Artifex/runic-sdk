<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import type { SvelteMvvmStore } from "@webuitoolkit/mvvm-svelte";
  import { toSvelteMvvmRune } from "@webuitoolkit/mvvm-svelte/runes";
  import type { SimpleTodoContract } from "../../../shared/contracts";
  import AppHeader from "../components/AppHeader.svelte";
  import { createSimpleTodoStores } from "../todo-bindings.g";

  interface Props {
    model: SvelteMvvmStore;
    todo: SimpleTodoContract;
  }

  let { model, todo }: Props = $props();
  let title = $state("");
  let pending = $state(false);
  const initialModel = untrack(() => model);
  const initialTodo = untrack(() => todo);
  const bindings = createSimpleTodoStores(initialModel, initialTodo);
  const modelState = toSvelteMvvmRune(initialModel);
  const itemsState = toSvelteMvvmRune(bindings.items);
  const addState = toSvelteMvvmRune(bindings.add);
  let items = $derived(itemsState.current);
  let completed = $derived(items.filter((item) => item.isCompleted).length);
  let connected = $derived(modelState.current.synchronized);
  let status = $derived(connected
    ? `Connected · r${modelState.current.revision}`
    : modelState.current.phase);

  async function add(): Promise<void> {
    if (title.trim().length < 2) return;
    pending = true;
    try {
      await initialTodo.newTitle.set(title);
      await bindings.add.execute().completion;
      title = "";
    } finally {
      pending = false;
    }
  }

  function submit(event: SubmitEvent): void {
    event.preventDefault();
    void add();
  }

  onDestroy(() => bindings.dispose());
</script>

<div class="app-shell simple-shell">
  <AppHeader
    title="Simple ToDo"
    subtitle="One shared C# ViewModel exposed as a Svelte readable store."
    {connected}
    {status}
  />
  <section class="card hero-card">
    <div class="card-body p-4">
      <form class="input-group mb-4" onsubmit={submit}>
        <label class="visually-hidden" for="new-title">New task</label>
        <input
          id="new-title"
          class="form-control"
          bind:value={title}
          maxlength="80"
          placeholder="What needs doing?"
        >
        <button
          class="btn btn-primary"
          disabled={pending || title.trim().length < 2 ||
            !modelState.current.synchronized ||
            addState.current?.canExecute !== true ||
            addState.current.isRunning}
        >
          <i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Add
        </button>
      </form>
      <div class="d-flex justify-content-between text-secondary small mb-3">
        <span>{items.length - completed} remaining</span>
        <span>{completed} completed</span>
      </div>
      <ul class="list-group list-group-flush">
        {#each items as item (item.id)}
          <li class:completed={item.isCompleted} class="list-group-item px-0 todo-row">
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
            <span class="todo-title">{item.title}</span>
            <button
              class="btn btn-sm btn-outline-danger todo-actions"
              aria-label={`Remove ${item.title}`}
              onclick={() => initialTodo.remove.execute(item.id).completion}
            >
              <i class="fa-solid fa-trash" aria-hidden="true"></i>
            </button>
          </li>
        {/each}
      </ul>
    </div>
  </section>
</div>
