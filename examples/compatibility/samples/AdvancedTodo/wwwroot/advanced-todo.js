const byId = (id) => document.getElementById(id);
const call = (name, ...args) => globalThis.webui.call(name, ...args);
const node = (tag, className, text) => {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
};

let state;
let searchTimer;

function render(next) {
  state = next;
  byId("total-count").textContent = next.totalCount;
  byId("remaining-count").textContent = next.remainingCount;
  byId("completed-count").textContent = next.completedCount;
  byId("shown-count").textContent = `${next.items.length} shown`;
  byId("query").value = next.query;
  byId("filter").value = next.filter;
  byId("new-title").value = next.newTitle;
  byId("new-notes").value = next.newNotes;
  byId("new-priority").value = next.newPriority;
  byId("title-errors").textContent = next.validationMessages.join(" ");
  renderTasks(next.items);
  renderPlanner(next);
  renderActivity(next.diagnostics);
}

function renderTasks(items) {
  const target = byId("tasks");
  target.replaceChildren();
  if (items.length === 0) {
    const empty = node("div", "empty");
    empty.append(
      node("span", "empty-icon", "✓"),
      node("h3", "", state.totalCount === 0 ? "Nothing here yet" : "No matching tasks"),
      node("p", "", state.totalCount === 0
        ? "Add a task or import the starter set."
        : "Try another search or filter."),
    );
    target.append(empty);
    return;
  }

  for (const item of items) {
    const card = node("article", item.isCompleted ? "todo completed" : "todo");
    const check = node("button", "check", item.isCompleted ? "✓" : "");
    check.type = "button";
    check.ariaLabel = item.isCompleted ? "Mark active" : "Mark complete";
    check.addEventListener("click", () => invoke("todoToggle", item.id));

    const copy = node("div", "todo-copy");
    copy.append(node("div", "todo-title", item.title));
    if (item.notes) copy.append(node("p", "", item.notes));
    const meta = node("div", "meta");
    meta.append(
      node("span", `priority ${item.priority.toLowerCase()}`, item.priority),
      node("time", "", new Date(item.createdAt).toLocaleString([], {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      })),
    );
    copy.append(meta);

    const remove = node("button", "danger", "Delete");
    remove.type = "button";
    remove.addEventListener("click", () => invoke("todoDelete", item.id));
    card.append(check, copy, remove);
    target.append(card);
  }
}

function renderPlanner(next) {
  const target = byId("planner");
  target.replaceChildren();
  if (!next.wizardStep) {
    target.append(
      node("p", "", "Create a task through a typed Details → Review workflow with validation and retained Back navigation."),
    );
    const start = node("button", "primary", "Open planner");
    start.type = "button";
    start.addEventListener("click", () => invoke("todoWizardStart"));
    target.append(start);
    return;
  }

  const steps = node("div", "steps");
  steps.append(
    node("span", next.wizardStep.endsWith("details") ? "active" : "done", "1 Details"),
    node("span", next.wizardStep.endsWith("review") ? "active" : "", "2 Review"),
  );
  target.append(steps);

  if (next.wizardStep.endsWith("details")) {
    const form = node("form", "stack");
    const titleLabel = node("label", "", "Title");
    titleLabel.htmlFor = "wizard-title";
    const title = node("input");
    title.id = "wizard-title";
    title.value = next.newTitle;
    title.maxLength = 120;
    const notesLabel = node("label", "", "Notes");
    notesLabel.htmlFor = "wizard-notes";
    const notes = node("textarea");
    notes.id = "wizard-notes";
    notes.value = next.newNotes;
    const priorityLabel = node("label", "", "Priority");
    priorityLabel.htmlFor = "wizard-priority";
    const priority = node("select");
    priority.id = "wizard-priority";
    for (const value of ["Low", "Normal", "High"]) {
      const option = node("option", "", value);
      option.value = value;
      option.selected = value === next.newPriority;
      priority.append(option);
    }
    for (const issue of next.wizardIssues) {
      form.append(node("p", "validation", issue));
    }
    const proceed = node("button", "primary", "Continue to review");
    proceed.type = "submit";
    form.append(titleLabel, title, notesLabel, notes, priorityLabel, priority, proceed);
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      await invoke("todoWizardNext", title.value, notes.value, priority.value);
    });
    const cancel = node("button", "quiet", "Cancel");
    cancel.type = "button";
    cancel.addEventListener("click", () => invoke("todoWizardCancel"));
    target.append(form, cancel);
    return;
  }

  const review = node("div", "review");
  review.append(
    node("strong", "", next.newTitle),
    node("p", "", next.newNotes || "No notes"),
    node("span", `priority ${next.newPriority.toLowerCase()}`, next.newPriority),
  );
  const actions = node("div", "button-row");
  const back = node("button", "quiet", "Back");
  back.type = "button";
  back.addEventListener("click", () => invoke("todoWizardBack"));
  const finish = node("button", "primary", "Create task");
  finish.type = "button";
  finish.addEventListener("click", () => invoke("todoWizardFinish"));
  actions.append(back, finish);
  target.append(review, actions);
}

function renderActivity(entries) {
  const target = byId("activity");
  target.replaceChildren();
  for (const entry of entries.slice(0, 5)) {
    const item = node("li");
    item.append(
      node("time", "", new Date(entry.at).toLocaleTimeString()),
      node("span", "diagnostic-kind", entry.category),
      node("p", "", entry.message),
    );
    target.append(item);
  }
}

async function invoke(name, ...args) {
  try {
    render(JSON.parse(await call(name, ...args)));
  } catch (error) {
    console.error(`${name} failed`, error);
  }
}

byId("add-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  await invoke(
    "todoAdd",
    byId("new-title").value,
    byId("new-notes").value,
    byId("new-priority").value,
  );
});

const applyFilter = () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(
    () => invoke("todoFilter", byId("query").value, byId("filter").value),
    180,
  );
};
byId("query").addEventListener("input", applyFilter);
byId("filter").addEventListener("change", applyFilter);
byId("clear-completed").addEventListener("click", () => invoke("todoClearCompleted"));

byId("import").addEventListener("click", async () => {
  document.documentElement.classList.add("importing");
  try {
    await invoke("todoImport");
  } finally {
    document.documentElement.classList.remove("importing");
  }
});
byId("cancel-import").addEventListener("click", () => invoke("todoCancelImport"));

invoke("todoSnapshot");
