<script lang="ts">
  import type { SvelteMvvmStore } from "@runic-artifex/mvvm-svelte";
  import {
    type AdvancedTodoContract,
    type SimpleTodoContract,
    type TodoDemo,
  } from "../../shared/contracts";
  import AdvancedTodo from "./advanced/AdvancedTodo.svelte";
  import SimpleTodo from "./simple/SimpleTodo.svelte";

  interface Props {
    demo: TodoDemo;
    model: SvelteMvvmStore;
    todo: SimpleTodoContract | AdvancedTodoContract;
  }

  let { demo, model, todo }: Props = $props();
</script>

{#if demo === "simple"}
  <SimpleTodo model={model} todo={todo as SimpleTodoContract} />
{:else}
  <AdvancedTodo model={model} todo={todo as AdvancedTodoContract} />
{/if}
