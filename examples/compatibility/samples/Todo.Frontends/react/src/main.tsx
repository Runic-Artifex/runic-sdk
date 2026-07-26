import { FormEvent, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  ReactMvvmProvider,
  createReactMvvmStore,
  useMvvmSnapshot,
} from "@webuitoolkit/mvvm-react";

import {
  AdvancedTodoContract,
  commandEnabled,
  demoFromDocument,
  SimpleTodoContract,
  type AdvancedTodoItem,
  type AdvancedTodoState,
  type DiagnosticEntry,
  type SimpleTodoItem,
} from "../../shared/contracts";
import { connectTodo, reportStartupFailure } from "../../shared/runtime";

const framework = "React";
const demo = demoFromDocument();

try {
  const connection = await connectTodo(demo);
  const store = createReactMvvmStore(connection.projection);
  const todo = demo === "simple"
    ? new SimpleTodoContract(connection.projection)
    : new AdvancedTodoContract(connection.projection);
  const root = createRoot(document.querySelector("#app")!);
  root.render(
    <ReactMvvmProvider store={store} ownsStore>
      {todo instanceof SimpleTodoContract
        ? <SimpleTodo todo={todo} />
        : <AdvancedTodo todo={todo} />}
    </ReactMvvmProvider>,
  );
  globalThis.addEventListener("pagehide", () => {
    root.unmount();
    void connection.dispose();
  }, { once: true });
} catch (error) {
  reportStartupFailure(error);
}

function Header({ title, subtitle }: { title: string; subtitle: string }) {
  const snapshot = useMvvmSnapshot();
  return (
    <header className="mb-4">
      <div className="d-flex flex-wrap gap-2 justify-content-between align-items-start">
        <div>
          <span className="framework-badge badge text-bg-primary mb-2">{framework}</span>
          <h1 className="display-6 fw-semibold mb-1">{title}</h1>
          <p className="text-secondary mb-0">{subtitle}</p>
        </div>
        <span className={`badge ${snapshot.synchronized ? "text-bg-success" : "text-bg-secondary"}`}>
          {snapshot.synchronized ? `Connected · r${snapshot.revision}` : snapshot.phase}
        </span>
      </div>
    </header>
  );
}

function SimpleTodo({ todo }: { todo: SimpleTodoContract }) {
  const snapshot = useMvvmSnapshot();
  const items: readonly SimpleTodoItem[] = todo.items.from(snapshot);
  const [title, setTitle] = useState("");
  const [pending, setPending] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (title.trim().length < 2) return;
    setPending(true);
    try {
      await todo.newTitle.set(title);
      await todo.add.execute().completion;
      setTitle("");
    } finally {
      setPending(false);
    }
  }

  const completed = items.filter((item) => item.isCompleted).length;
  return (
    <div className="app-shell simple-shell">
      <Header
        title="Simple ToDo"
        subtitle="One shared C# ViewModel, rendered through React hooks."
      />
      <section className="card hero-card">
        <div className="card-body p-4">
          <form className="input-group mb-4" onSubmit={submit}>
            <label className="visually-hidden" htmlFor="new-title">New task</label>
            <input
              id="new-title"
              className="form-control"
              value={title}
              maxLength={80}
              placeholder="What needs doing?"
              onChange={(event) => setTitle(event.currentTarget.value)}
            />
            <button
              className="btn btn-primary"
              disabled={pending || title.trim().length < 2 || !commandEnabled(snapshot, todo.add.member)}
            >
              <i className="fa-solid fa-plus me-2" aria-hidden="true" />Add
            </button>
          </form>
          <div className="d-flex justify-content-between text-secondary small mb-3">
            <span>{items.length - completed} remaining</span>
            <span>{completed} completed</span>
          </div>
          <ul className="list-group list-group-flush">
            {items.map((item) => (
              <li className={`list-group-item px-0 todo-row ${item.isCompleted ? "completed" : ""}`} key={item.id}>
                <button
                  className="btn btn-sm btn-outline-primary rounded-circle"
                  aria-label={item.isCompleted ? "Mark active" : "Mark complete"}
                  onClick={() => void todo.toggle.execute(item.id).completion}
                >
                  <i className={`fa-${item.isCompleted ? "solid fa-check" : "regular fa-circle"}`} aria-hidden="true" />
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

function AdvancedTodo({ todo }: { todo: AdvancedTodoContract }) {
  const snapshot = useMvvmSnapshot();
  const items: readonly AdvancedTodoItem[] = todo.items.from(snapshot);
  const diagnostics: readonly DiagnosticEntry[] = todo.diagnostics.from(snapshot);
  const state: AdvancedTodoState = todo.state.from(snapshot) ?? {
    totalCount: 0,
    remainingCount: 0,
    completedCount: 0,
    isImporting: false,
    wizardStep: null,
    wizardIssues: [],
  };
  const validation = snapshot.validation.get(todo.newTitle.member) ?? [];
  const [title, setTitle] = useState("");
  const [notes, setNotes] = useState("");
  const [priority, setPriority] = useState("Normal");
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState("All");

  async function setDraft() {
    await todo.newTitle.set(title);
    await todo.newNotes.set(notes);
    await todo.newPriority.set(priority);
  }

  async function add(event: FormEvent) {
    event.preventDefault();
    await setDraft();
    await todo.add.execute().completion;
    if (todo.newTitle.validation.length === 0) {
      setTitle("");
      setNotes("");
      setPriority("Normal");
    }
  }

  async function applyFilter(event: FormEvent) {
    event.preventDefault();
    await todo.query.set(query);
    await todo.filter.set(filter);
    await todo.applyFilter.execute().completion;
  }

  async function wizard(command: typeof todo.wizardStart) {
    if (command === todo.wizardStart || command === todo.wizardNext) {
      await setDraft();
    }
    await command.execute().completion;
  }

  const wizardOpen = state.wizardStep !== null;
  const wizardReview = state.wizardStep === "todo.create.review";
  return (
    <div className="app-shell">
      <Header
        title="Advanced ToDo"
        subtitle="Persistence, filtering, validation, cancellation, and Flow over the same React adapter."
      />
      <div className="row g-4">
        <div className="col-lg-8">
          <section className="card workspace-card mb-4">
            <div className="card-body p-4">
              <h2 className="h5 mb-3">Quick add</h2>
              <form onSubmit={add}>
                <div className="row g-3">
                  <div className="col-md-7">
                    <input className={`form-control ${validation.length ? "is-invalid" : ""}`} value={title} maxLength={120} placeholder="Task title" onChange={(event) => setTitle(event.currentTarget.value)} />
                    {validation.length > 0 && <div className="invalid-feedback">{validation.join(" ")}</div>}
                  </div>
                  <div className="col-md-3">
                    <select className="form-select" value={priority} onChange={(event) => setPriority(event.currentTarget.value)}>
                      <option>Low</option><option>Normal</option><option>High</option>
                    </select>
                  </div>
                  <div className="col-md-2 d-grid">
                    <button className="btn btn-primary" disabled={!commandEnabled(snapshot, todo.add.member)}>Add</button>
                  </div>
                  <div className="col-12">
                    <textarea className="form-control" value={notes} rows={2} placeholder="Notes (optional)" onChange={(event) => setNotes(event.currentTarget.value)} />
                  </div>
                </div>
              </form>
            </div>
          </section>
          <section className="card workspace-card">
            <div className="card-body p-4">
              <form className="row g-2 mb-4" onSubmit={applyFilter}>
                <div className="col-md-7"><input className="form-control" value={query} placeholder="Search title and notes" onChange={(event) => setQuery(event.currentTarget.value)} /></div>
                <div className="col-md-3"><select className="form-select" value={filter} onChange={(event) => setFilter(event.currentTarget.value)}><option>All</option><option>Active</option><option>Completed</option></select></div>
                <div className="col-md-2 d-grid"><button className="btn btn-outline-primary">Apply</button></div>
              </form>
              <div className="list-group list-group-flush">
                {items.map((item) => (
                  <article className={`list-group-item px-0 todo-row ${item.isCompleted ? "completed" : ""}`} key={item.id}>
                    <button className="btn btn-sm btn-outline-primary rounded-circle" aria-label="Toggle task" onClick={() => void todo.toggle.execute(item.id).completion}><i className={`fa-${item.isCompleted ? "solid fa-check" : "regular fa-circle"}`} /></button>
                    <div><div className="d-flex gap-2 align-items-center"><strong className="todo-title">{item.title}</strong><span className={`badge priority-${item.priority}`}>{item.priority}</span></div>{item.notes && <div className="small text-secondary todo-notes">{item.notes}</div>}</div>
                    <button className="btn btn-sm btn-outline-danger todo-actions" aria-label={`Delete ${item.title}`} onClick={() => void todo.delete.execute(item.id).completion}><i className="fa-solid fa-trash" /></button>
                  </article>
                ))}
                {items.length === 0 && <p className="text-secondary text-center my-4">No tasks match this view.</p>}
              </div>
            </div>
          </section>
        </div>
        <aside className="col-lg-4">
          <section className="card workspace-card mb-4"><div className="card-body">
            <div className="summary-grid mb-3">
              <div className="summary-tile"><span className="summary-value">{state.totalCount}</span><small>Total</small></div>
              <div className="summary-tile"><span className="summary-value">{state.remainingCount}</span><small>Active</small></div>
              <div className="summary-tile"><span className="summary-value">{state.completedCount}</span><small>Done</small></div>
            </div>
            <div className="d-grid gap-2">
              <button className="btn btn-outline-primary" onClick={() => void todo.import.execute().completion} disabled={state.isImporting}><i className="fa-solid fa-download me-2" />{state.isImporting ? "Importing…" : "Import starter tasks"}</button>
              {state.isImporting && <button className="btn btn-outline-danger" onClick={() => void todo.cancelImport.execute().completion}>Cancel import</button>}
              <button className="btn btn-outline-secondary" onClick={() => void todo.clearCompleted.execute().completion}>Clear completed</button>
            </div>
          </div></section>
          <section className="card workspace-card mb-4"><div className="card-body">
            <h2 className="h5">Guided creation</h2>
            {!wizardOpen ? <button className="btn btn-primary" onClick={() => void wizard(todo.wizardStart)}>Start workflow</button> : <>
              <p className="small text-secondary">{wizardReview ? "Review the retained draft before saving." : "Enter task details, then continue."}</p>
              {state.wizardIssues.map((issue) => <div className="alert alert-warning py-2" key={issue}>{issue}</div>)}
              <div className="d-flex flex-wrap gap-2">
                {!wizardReview && <button className="btn btn-primary" onClick={() => void wizard(todo.wizardNext)}>Next</button>}
                {wizardReview && <><button className="btn btn-outline-secondary" onClick={() => void wizard(todo.wizardBack)}>Back</button><button className="btn btn-success" onClick={() => void wizard(todo.wizardFinish)}>Save</button></>}
                <button className="btn btn-outline-danger" onClick={() => void wizard(todo.wizardCancel)}>Cancel</button>
              </div>
            </>}
          </div></section>
          <section className="card workspace-card"><div className="card-body">
            <h2 className="h5">Diagnostics</h2>
            <div className="diagnostic-list list-group list-group-flush">
              {diagnostics.map((entry, index) => <div className="list-group-item px-0" key={`${entry.at}-${index}`}><div className="small"><span className="badge text-bg-light me-2">{entry.category}</span><time>{new Date(entry.at).toLocaleTimeString()}</time></div><div>{entry.message}</div></div>)}
            </div>
          </div></section>
        </aside>
      </div>
    </div>
  );
}
