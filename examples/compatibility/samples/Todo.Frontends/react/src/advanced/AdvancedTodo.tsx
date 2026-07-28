import type { AdvancedTodoContract } from "../../../shared/contracts";
import { AppHeader } from "../components/AppHeader";
import { QuickAdd } from "./QuickAdd";
import { TodoSidebar } from "./TodoSidebar";
import { TodoWorkspace } from "./TodoWorkspace";
import { useAdvancedTodo } from "./useAdvancedTodo";

export function AdvancedTodo(
  { todo }: { readonly todo: AdvancedTodoContract },
) {
  const model = useAdvancedTodo(todo);
  return (
    <div className="app-shell">
      <AppHeader
        title="Advanced ToDo"
        subtitle="Persistence, filtering, validation, cancellation, and Flow over the same React adapter."
      />
      <div className="row g-4">
        <div className="col-lg-8">
          <QuickAdd model={model} />
          <TodoWorkspace model={model} />
        </div>
        <TodoSidebar model={model} />
      </div>
    </div>
  );
}
