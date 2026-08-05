import { type FormEvent, useState } from "react";
import { useMvvmSnapshot } from "@runic-artifex/mvvm-react";

import {
  type SimpleTodoContract,
  type SimpleTodoItem,
} from "../../../shared/contracts";
import { useSimpleTodoBindings } from "../todo-bindings.g";

export function useSimpleTodo(todo: SimpleTodoContract) {
  const snapshot = useMvvmSnapshot();
  const bindings = useSimpleTodoBindings(todo);
  const items: readonly SimpleTodoItem[] = bindings.items;
  const [title, setTitle] = useState("");
  const [pending, setPending] = useState(false);

  async function submit(event: FormEvent): Promise<void> {
    event.preventDefault();
    if (title.trim().length < 2) return;
    setPending(true);
    try {
      await todo.newTitle.set(title);
      await bindings.add.execute().completion;
      setTitle("");
    } finally {
      setPending(false);
    }
  }

  return {
    todo,
    snapshot,
    bindings,
    items,
    title,
    setTitle,
    pending,
    completed: items.filter((item) => item.isCompleted).length,
    submit,
  } as const;
}
