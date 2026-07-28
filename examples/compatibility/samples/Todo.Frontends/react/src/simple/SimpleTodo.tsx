import type { SimpleTodoContract } from "../../../shared/contracts";
import { AppHeader } from "../components/AppHeader";
import { useSimpleTodo } from "./useSimpleTodo";

export function SimpleTodo({ todo }: { readonly todo: SimpleTodoContract }) {
  const model = useSimpleTodo(todo);
  return (
    <div className="app-shell simple-shell">
      <AppHeader
        title="Simple ToDo"
        subtitle="One shared C# ViewModel, rendered through React hooks."
      />
      <section className="card hero-card">
        <div className="card-body p-4">
          <form className="input-group mb-4" onSubmit={model.submit}>
            <label className="visually-hidden" htmlFor="new-title">New task</label>
            <input
              id="new-title"
              className="form-control"
              value={model.title}
              maxLength={80}
              placeholder="What needs doing?"
              onChange={(event) => model.setTitle(event.currentTarget.value)}
            />
            <button
              className="btn btn-primary"
              disabled={model.pending || model.title.trim().length < 2 ||
                !model.snapshot.synchronized || !model.bindings.add.canExecute ||
                model.bindings.add.isRunning}
            >
              <i className="fa-solid fa-plus me-2" aria-hidden="true" />Add
            </button>
          </form>
          <div className="d-flex justify-content-between text-secondary small mb-3">
            <span>{model.items.length - model.completed} remaining</span>
            <span>{model.completed} completed</span>
          </div>
          <ul className="list-group list-group-flush">
            {model.items.map((item) => (
              <li
                className={`list-group-item px-0 todo-row ${item.isCompleted ? "completed" : ""}`}
                key={item.id}
              >
                <button
                  className="btn btn-sm btn-outline-primary rounded-circle"
                  aria-label={item.isCompleted ? "Mark active" : "Mark complete"}
                  onClick={() => void todo.toggle.execute(item.id).completion}
                >
                  <i
                    className={`fa-${item.isCompleted ? "solid fa-check" : "regular fa-circle"}`}
                    aria-hidden="true"
                  />
                </button>
                <span className="todo-title">{item.title}</span>
                <button
                  className="btn btn-sm btn-outline-danger todo-actions"
                  aria-label={`Remove ${item.title}`}
                  onClick={() => void todo.remove.execute(item.id).completion}
                >
                  <i className="fa-solid fa-trash" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
        </div>
      </section>
    </div>
  );
}
