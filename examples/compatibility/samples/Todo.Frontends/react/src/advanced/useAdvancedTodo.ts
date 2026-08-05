import { type FormEvent, useState } from "react";
import { useMvvmSnapshot } from "@runic-artifex/mvvm-react";

import {
  type AdvancedTodoContract,
  type AdvancedTodoItem,
  type AdvancedTodoState,
  type DiagnosticEntry,
} from "../../../shared/contracts";
import { useAdvancedTodoBindings } from "../todo-bindings.g";

const emptyState: AdvancedTodoState = {
  totalCount: 0,
  remainingCount: 0,
  completedCount: 0,
  isImporting: false,
  wizardStep: null,
  wizardIssues: [],
};

export function useAdvancedTodo(todo: AdvancedTodoContract) {
  const snapshot = useMvvmSnapshot();
  const bindings = useAdvancedTodoBindings(todo);
  const items: readonly AdvancedTodoItem[] = bindings.items;
  const diagnostics: readonly DiagnosticEntry[] = bindings.diagnostics;
  const state = bindings.state ?? emptyState;
  const validation = bindings.newTitleErrors ?? [];
  const [title, setTitle] = useState("");
  const [notes, setNotes] = useState("");
  const [priority, setPriority] = useState("Normal");
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState("All");

  async function setDraft(): Promise<void> {
    await todo.newTitle.set(title);
    await todo.newNotes.set(notes);
    await todo.newPriority.set(priority);
  }

  async function add(event: FormEvent): Promise<void> {
    event.preventDefault();
    await setDraft();
    await bindings.add.execute().completion;
    if (todo.newTitle.validation.length === 0) {
      setTitle("");
      setNotes("");
      setPriority("Normal");
    }
  }

  async function applyFilter(event: FormEvent): Promise<void> {
    event.preventDefault();
    await todo.query.set(query);
    await todo.filter.set(filter);
    await todo.applyFilter.execute().completion;
  }

  async function wizard(command: typeof todo.wizardStart): Promise<void> {
    if (command === todo.wizardStart || command === todo.wizardNext) {
      await setDraft();
    }
    await command.execute().completion;
  }

  return {
    todo,
    snapshot,
    bindings,
    items,
    diagnostics,
    state,
    validation,
    title,
    setTitle,
    notes,
    setNotes,
    priority,
    setPriority,
    query,
    setQuery,
    filter,
    setFilter,
    add,
    applyFilter,
    wizard,
    wizardOpen: state.wizardStep !== null,
    wizardReview: state.wizardStep === "todo.create.review",
  } as const;
}

export type AdvancedTodoPresentation = ReturnType<typeof useAdvancedTodo>;
