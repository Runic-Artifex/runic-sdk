import type { AdvancedTodoPresentation } from "./useAdvancedTodo";

export function QuickAdd({ model }: { readonly model: AdvancedTodoPresentation }) {
  return (
    <section className="card workspace-card mb-4">
      <div className="card-body p-4">
        <h2 className="h5 mb-3">Quick add</h2>
        <form onSubmit={model.add}>
          <div className="row g-3">
            <div className="col-md-7">
              <label className="visually-hidden" htmlFor="advanced-title">Task title</label>
              <input
                id="advanced-title"
                className={`form-control ${model.validation.length ? "is-invalid" : ""}`}
                value={model.title}
                maxLength={120}
                placeholder="Task title"
                onChange={(event) => model.setTitle(event.currentTarget.value)}
              />
              {model.validation.length > 0 &&
                <div className="invalid-feedback">{model.validation.join(" ")}</div>}
            </div>
            <div className="col-md-3">
              <label className="visually-hidden" htmlFor="advanced-priority">Priority</label>
              <select
                id="advanced-priority"
                className="form-select"
                value={model.priority}
                onChange={(event) => model.setPriority(event.currentTarget.value)}
              >
                <option>Low</option><option>Normal</option><option>High</option>
              </select>
            </div>
            <div className="col-md-2 d-grid">
              <button
                className="btn btn-primary"
                disabled={!model.snapshot.synchronized ||
                  !model.bindings.add.canExecute ||
                  model.bindings.add.isRunning}
              >
                Add
              </button>
            </div>
            <div className="col-12">
              <label className="visually-hidden" htmlFor="advanced-notes">Notes</label>
              <textarea
                id="advanced-notes"
                className="form-control"
                value={model.notes}
                rows={2}
                placeholder="Notes (optional)"
                onChange={(event) => model.setNotes(event.currentTarget.value)}
              />
            </div>
          </div>
        </form>
      </div>
    </section>
  );
}
