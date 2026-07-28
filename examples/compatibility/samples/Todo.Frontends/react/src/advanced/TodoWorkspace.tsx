import type { AdvancedTodoPresentation } from "./useAdvancedTodo";

export function TodoWorkspace(
  { model }: { readonly model: AdvancedTodoPresentation },
) {
  return (
    <section className="card workspace-card">
      <div className="card-body p-4">
        <form className="row g-2 mb-4" onSubmit={model.applyFilter}>
          <div className="col-md-7">
            <label className="visually-hidden" htmlFor="advanced-query">Search tasks</label>
            <input
              id="advanced-query"
              className="form-control"
              value={model.query}
              placeholder="Search title and notes"
              onChange={(event) => model.setQuery(event.currentTarget.value)}
            />
          </div>
          <div className="col-md-3">
            <label className="visually-hidden" htmlFor="advanced-filter">Task status</label>
            <select
              id="advanced-filter"
              className="form-select"
              value={model.filter}
              onChange={(event) => model.setFilter(event.currentTarget.value)}
            >
              <option>All</option><option>Active</option><option>Completed</option>
            </select>
          </div>
          <div className="col-md-2 d-grid">
            <button className="btn btn-outline-primary">Apply</button>
          </div>
        </form>
        <div className="list-group list-group-flush">
          {model.items.map((item) => (
            <article
              className={`list-group-item px-0 todo-row ${item.isCompleted ? "completed" : ""}`}
              key={item.id}
            >
              <button
                className="btn btn-sm btn-outline-primary rounded-circle"
                aria-label={item.isCompleted ? "Mark active" : "Mark complete"}
                onClick={() => void model.todo.toggle.execute(item.id).completion}
              >
                <i
                  className={`fa-${item.isCompleted ? "solid fa-check" : "regular fa-circle"}`}
                  aria-hidden="true"
                />
              </button>
              <div>
                <div className="d-flex gap-2 align-items-center">
                  <strong className="todo-title">{item.title}</strong>
                  <span className={`badge priority-${item.priority}`}>{item.priority}</span>
                </div>
                {item.notes &&
                  <div className="small text-secondary todo-notes">{item.notes}</div>}
              </div>
              <button
                className="btn btn-sm btn-outline-danger todo-actions"
                aria-label={`Delete ${item.title}`}
                onClick={() => void model.todo.delete.execute(item.id).completion}
              >
                <i className="fa-solid fa-trash" aria-hidden="true" />
              </button>
            </article>
          ))}
          {model.items.length === 0 &&
            <p className="text-secondary text-center my-4">No tasks match this view.</p>}
        </div>
      </div>
    </section>
  );
}
