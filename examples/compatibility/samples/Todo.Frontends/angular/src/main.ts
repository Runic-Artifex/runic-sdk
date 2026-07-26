import "@angular/compiler";
import {
  Component,
  computed,
  provideZonelessChangeDetection,
  type OnDestroy,
} from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { AngularMvvmStore } from "@webuitoolkit/mvvm-angular";

import {
  AdvancedTodoContract,
  commandEnabled,
  demoFromDocument,
  SimpleTodoContract,
  type AdvancedTodoState,
  type TodoDemo,
} from "../../shared/contracts";
import { connectTodo, reportStartupFailure, type TodoConnection } from "../../shared/runtime";

let connection: TodoConnection;
let sampleStore: AngularMvvmStore;
let simpleTodo: SimpleTodoContract;
let advancedTodo: AdvancedTodoContract;
const demo = demoFromDocument();

@Component({
  selector: "todo-app",
  standalone: true,
  template: `
    @if (demo === "simple") {
      <div class="app-shell simple-shell">
        <header class="mb-4"><div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
          <div><span class="framework-badge badge text-bg-danger mb-2">Angular</span><h1 class="display-6 fw-semibold mb-1">Simple ToDo</h1><p class="text-secondary mb-0">One shared C# ViewModel surfaced through Angular signals.</p></div>
          <span class="badge" [class.text-bg-success]="connected()" [class.text-bg-secondary]="!connected()">{{ status() }}</span>
        </div></header>
        <section class="card hero-card"><div class="card-body p-4">
          <form class="input-group mb-4" (submit)="addSimple($event)">
            <label class="visually-hidden" for="new-title">New task</label>
            <input id="new-title" class="form-control" [value]="title" maxlength="80" placeholder="What needs doing?" (input)="title = inputValue($event)">
            <button class="btn btn-primary" [disabled]="pending || title.trim().length < 2 || !canSimpleAdd()"><i class="fa-solid fa-plus me-2" aria-hidden="true"></i>Add</button>
          </form>
          <div class="d-flex justify-content-between text-secondary small mb-3"><span>{{ simpleItems().length - completed() }} remaining</span><span>{{ completed() }} completed</span></div>
          <ul class="list-group list-group-flush">
            @for (item of simpleItems(); track item.id) {
              <li class="list-group-item px-0 todo-row" [class.completed]="item.isCompleted">
                <button class="btn btn-sm btn-outline-primary rounded-circle" [attr.aria-label]="item.isCompleted ? 'Mark active' : 'Mark complete'" (click)="simpleTodo.toggle.execute(item.id).completion"><i [class]="item.isCompleted ? 'fa-solid fa-check' : 'fa-regular fa-circle'" aria-hidden="true"></i></button>
                <span class="todo-title">{{ item.title }}</span>
                <button class="btn btn-sm btn-outline-danger todo-actions" [attr.aria-label]="'Remove ' + item.title" (click)="simpleTodo.remove.execute(item.id).completion"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
              </li>
            }
          </ul>
        </div></section>
      </div>
    } @else {
      <div class="app-shell">
        <header class="mb-4"><div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
          <div><span class="framework-badge badge text-bg-danger mb-2">Angular</span><h1 class="display-6 fw-semibold mb-1">Advanced ToDo</h1><p class="text-secondary mb-0">Persistence, filtering, validation, cancellation, and Flow through Angular signals.</p></div>
          <span class="badge" [class.text-bg-success]="connected()" [class.text-bg-secondary]="!connected()">{{ status() }}</span>
        </div></header>
        <div class="row g-4">
          <div class="col-lg-8">
            <section class="card workspace-card mb-4"><div class="card-body p-4">
              <h2 class="h5 mb-3">Quick add</h2>
              <form (submit)="addAdvanced($event)"><div class="row g-3">
                <div class="col-md-7"><input class="form-control" [class.is-invalid]="validation().length > 0" [value]="title" maxlength="120" placeholder="Task title" (input)="title = inputValue($event)">@if (validation().length) {<div class="invalid-feedback">{{ validation().join(" ") }}</div>}</div>
                <div class="col-md-3"><select class="form-select" [value]="priority" (change)="priority = inputValue($event)"><option>Low</option><option>Normal</option><option>High</option></select></div>
                <div class="col-md-2 d-grid"><button class="btn btn-primary" [disabled]="!canAdvancedAdd()">Add</button></div>
                <div class="col-12"><textarea class="form-control" [value]="notes" rows="2" placeholder="Notes (optional)" (input)="notes = inputValue($event)"></textarea></div>
              </div></form>
            </div></section>
            <section class="card workspace-card"><div class="card-body p-4">
              <form class="row g-2 mb-4" (submit)="applyFilter($event)">
                <div class="col-md-7"><input class="form-control" [value]="query" placeholder="Search title and notes" (input)="query = inputValue($event)"></div>
                <div class="col-md-3"><select class="form-select" [value]="filter" (change)="filter = inputValue($event)"><option>All</option><option>Active</option><option>Completed</option></select></div>
                <div class="col-md-2 d-grid"><button class="btn btn-outline-primary">Apply</button></div>
              </form>
              <div class="list-group list-group-flush">
                @for (item of advancedItems(); track item.id) {
                  <article class="list-group-item px-0 todo-row" [class.completed]="item.isCompleted">
                    <button class="btn btn-sm btn-outline-primary rounded-circle" aria-label="Toggle task" (click)="advancedTodo.toggle.execute(item.id).completion"><i [class]="item.isCompleted ? 'fa-solid fa-check' : 'fa-regular fa-circle'"></i></button>
                    <div><div class="d-flex gap-2 align-items-center"><strong class="todo-title">{{ item.title }}</strong><span class="badge" [class]="'badge priority-' + item.priority">{{ item.priority }}</span></div>@if (item.notes) {<div class="small text-secondary todo-notes">{{ item.notes }}</div>}</div>
                    <button class="btn btn-sm btn-outline-danger todo-actions" [attr.aria-label]="'Delete ' + item.title" (click)="advancedTodo.delete.execute(item.id).completion"><i class="fa-solid fa-trash"></i></button>
                  </article>
                } @empty {
                  <p class="text-secondary text-center my-4">No tasks match this view.</p>
                }
              </div>
            </div></section>
          </div>
          <aside class="col-lg-4">
            <section class="card workspace-card mb-4"><div class="card-body">
              <div class="summary-grid mb-3"><div class="summary-tile"><span class="summary-value">{{ state().totalCount }}</span><small>Total</small></div><div class="summary-tile"><span class="summary-value">{{ state().remainingCount }}</span><small>Active</small></div><div class="summary-tile"><span class="summary-value">{{ state().completedCount }}</span><small>Done</small></div></div>
              <div class="d-grid gap-2"><button class="btn btn-outline-primary" [disabled]="state().isImporting" (click)="advancedTodo.import.execute().completion"><i class="fa-solid fa-download me-2"></i>{{ state().isImporting ? "Importing…" : "Import starter tasks" }}</button>@if (state().isImporting) {<button class="btn btn-outline-danger" (click)="advancedTodo.cancelImport.execute().completion">Cancel import</button>}<button class="btn btn-outline-secondary" (click)="advancedTodo.clearCompleted.execute().completion">Clear completed</button></div>
            </div></section>
            <section class="card workspace-card mb-4"><div class="card-body">
              <h2 class="h5">Guided creation</h2>
              @if (state().wizardStep === null) {
                <button class="btn btn-primary" (click)="wizard(advancedTodo.wizardStart)">Start workflow</button>
              } @else {
                <p class="small text-secondary">{{ wizardReview() ? "Review the retained draft before saving." : "Enter task details, then continue." }}</p>
                @for (issue of state().wizardIssues; track issue) {<div class="alert alert-warning py-2">{{ issue }}</div>}
                <div class="d-flex flex-wrap gap-2">@if (!wizardReview()) {<button class="btn btn-primary" (click)="wizard(advancedTodo.wizardNext)">Next</button>} @else {<button class="btn btn-outline-secondary" (click)="wizard(advancedTodo.wizardBack)">Back</button><button class="btn btn-success" (click)="wizard(advancedTodo.wizardFinish)">Save</button>}<button class="btn btn-outline-danger" (click)="wizard(advancedTodo.wizardCancel)">Cancel</button></div>
              }
            </div></section>
            <section class="card workspace-card"><div class="card-body"><h2 class="h5">Diagnostics</h2><div class="diagnostic-list list-group list-group-flush">@for (entry of diagnostics(); track entry.at + $index) {<div class="list-group-item px-0"><div class="small"><span class="badge text-bg-light me-2">{{ entry.category }}</span><time>{{ formatTime(entry.at) }}</time></div><div>{{ entry.message }}</div></div>}</div></div></section>
          </aside>
        </div>
      </div>
    }
  `,
})
class TodoApplicationComponent implements OnDestroy {
  protected readonly demo: TodoDemo = demo;
  protected readonly simpleTodo = simpleTodo;
  protected readonly advancedTodo = advancedTodo;
  protected readonly snapshot = sampleStore.snapshot;
  protected readonly connected = computed(() => this.snapshot().synchronized);
  protected readonly status = computed(() => this.snapshot().synchronized
    ? `Connected · r${this.snapshot().revision}`
    : this.snapshot().phase);
  protected readonly simpleItems = computed(() =>
    this.simpleTodo.items.from(this.snapshot()));
  protected readonly completed = computed(() =>
    this.simpleItems().filter((item) => item.isCompleted).length);
  protected readonly advancedItems = computed(() =>
    this.advancedTodo.items.from(this.snapshot()));
  protected readonly diagnostics = computed(() =>
    this.advancedTodo.diagnostics.from(this.snapshot()));
  protected readonly state = computed<AdvancedTodoState>(() =>
    this.advancedTodo.state.from(this.snapshot()) ?? {
      totalCount: 0,
      remainingCount: 0,
      completedCount: 0,
      isImporting: false,
      wizardStep: null,
      wizardIssues: [],
    });
  protected readonly validation = computed(() =>
    this.snapshot().validation.get(this.advancedTodo.newTitle.member) ?? []);
  protected readonly wizardReview = computed(() =>
    this.state().wizardStep === "todo.create.review");
  protected title = "";
  protected notes = "";
  protected priority = "Normal";
  protected query = "";
  protected filter = "All";
  protected pending = false;
  protected inputValue(event: Event): string {
    return (event.currentTarget as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
  }

  protected canSimpleAdd(): boolean {
    return commandEnabled(this.snapshot(), this.simpleTodo.add.member);
  }

  protected canAdvancedAdd(): boolean {
    return commandEnabled(this.snapshot(), this.advancedTodo.add.member);
  }

  protected async addSimple(event: Event) {
    event.preventDefault();
    if (this.title.trim().length < 2) return;
    this.pending = true;
    try {
      await this.simpleTodo.newTitle.set(this.title);
      await this.simpleTodo.add.execute().completion;
      this.title = "";
    } finally {
      this.pending = false;
    }
  }

  private async setDraft() {
    await this.advancedTodo.newTitle.set(this.title);
    await this.advancedTodo.newNotes.set(this.notes);
    await this.advancedTodo.newPriority.set(this.priority);
  }

  protected async addAdvanced(event: Event) {
    event.preventDefault();
    await this.setDraft();
    await this.advancedTodo.add.execute().completion;
    if (this.advancedTodo.newTitle.validation.length === 0) {
      this.title = "";
      this.notes = "";
      this.priority = "Normal";
    }
  }

  protected async applyFilter(event: Event) {
    event.preventDefault();
    await this.advancedTodo.query.set(this.query);
    await this.advancedTodo.filter.set(this.filter);
    await this.advancedTodo.applyFilter.execute().completion;
  }

  protected async wizard(command: typeof this.advancedTodo.wizardStart) {
    if (command === this.advancedTodo.wizardStart ||
        command === this.advancedTodo.wizardNext) {
      await this.setDraft();
    }
    await command.execute().completion;
  }

  protected formatTime(value: string): string {
    return new Date(value).toLocaleTimeString();
  }

  public ngOnDestroy(): void {
    sampleStore.destroy();
  }
}

try {
  connection = await connectTodo(demo);
  sampleStore = new AngularMvvmStore(connection.projection);
  simpleTodo = new SimpleTodoContract(connection.projection);
  advancedTodo = new AdvancedTodoContract(connection.projection);
  const application = await bootstrapApplication(TodoApplicationComponent, {
    providers: [provideZonelessChangeDetection()],
  });
  globalThis.addEventListener("pagehide", () => {
    application.destroy();
    void connection.dispose();
  }, { once: true });
} catch (error) {
  reportStartupFailure(error);
}
