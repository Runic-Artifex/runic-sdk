import type { AdvancedTodoPresentation } from "./useAdvancedTodo";

export function TodoSidebar(
  { model }: { readonly model: AdvancedTodoPresentation },
) {
  return (
    <aside className="col-lg-4">
      <section className="card workspace-card mb-4">
        <div className="card-body">
          <div className="summary-grid mb-3">
            <div className="summary-tile">
              <span className="summary-value">{model.state.totalCount}</span><small>Total</small>
            </div>
            <div className="summary-tile">
              <span className="summary-value">{model.state.remainingCount}</span><small>Active</small>
            </div>
            <div className="summary-tile">
              <span className="summary-value">{model.state.completedCount}</span><small>Done</small>
            </div>
          </div>
          <div className="d-grid gap-2">
            <button
              className="btn btn-outline-primary"
              onClick={() => void model.todo.import.execute().completion}
              disabled={model.state.isImporting}
            >
              <i className="fa-solid fa-download me-2" aria-hidden="true" />
              {model.state.isImporting ? "Importing…" : "Import starter tasks"}
            </button>
            {model.state.isImporting &&
              <button
                className="btn btn-outline-danger"
                onClick={() => void model.todo.cancelImport.execute().completion}
              >
                Cancel import
              </button>}
            <button
              className="btn btn-outline-secondary"
              onClick={() => void model.todo.clearCompleted.execute().completion}
            >
              Clear completed
            </button>
          </div>
        </div>
      </section>
      <section className="card workspace-card mb-4">
        <div className="card-body">
          <h2 className="h5">Guided creation</h2>
          {!model.wizardOpen
            ? <button
                className="btn btn-primary"
                onClick={() => void model.wizard(model.todo.wizardStart)}
              >
                Start workflow
              </button>
            : <>
                <p className="small text-secondary">
                  {model.wizardReview
                    ? "Review the retained draft before saving."
                    : "Enter task details, then continue."}
                </p>
                {model.state.wizardIssues.map((issue) =>
                  <div className="alert alert-warning py-2" key={issue}>{issue}</div>)}
                <div className="d-flex flex-wrap gap-2">
                  {!model.wizardReview &&
                    <button
                      className="btn btn-primary"
                      onClick={() => void model.wizard(model.todo.wizardNext)}
                    >
                      Next
                    </button>}
                  {model.wizardReview &&
                    <>
                      <button
                        className="btn btn-outline-secondary"
                        onClick={() => void model.wizard(model.todo.wizardBack)}
                      >
                        Back
                      </button>
                      <button
                        className="btn btn-success"
                        onClick={() => void model.wizard(model.todo.wizardFinish)}
                      >
                        Save
                      </button>
                    </>}
                  <button
                    className="btn btn-outline-danger"
                    onClick={() => void model.wizard(model.todo.wizardCancel)}
                  >
                    Cancel
                  </button>
                </div>
              </>}
        </div>
      </section>
      <section className="card workspace-card">
        <div className="card-body">
          <h2 className="h5">Diagnostics</h2>
          <div className="diagnostic-list list-group list-group-flush">
            {model.diagnostics.map((entry, index) =>
              <div className="list-group-item px-0" key={`${entry.at}-${index}`}>
                <div className="small">
                  <span className="badge text-bg-light me-2">{entry.category}</span>
                  <time>{new Date(entry.at).toLocaleTimeString()}</time>
                </div>
                <div>{entry.message}</div>
              </div>)}
          </div>
        </div>
      </section>
    </aside>
  );
}
